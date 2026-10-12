# The script surface

A Sonde script is the body of one method. Sonde wraps it as

```csharp
public static async Task<object?> Run(ScriptGlobals g, CancellationToken ct)
{
    // your script
}
```

so it is statements ending in `return x;`. `await` works. The usings you get for free are `System`,
`System.Collections`, `System.Collections.Generic`, `System.Linq`, `System.Reflection`,
`System.Text`, `System.Threading`, `System.Threading.Tasks` and `Sonde.Probe`. Anything the host
process could reference, the script can reference: Sonde compiles against every assembly the host
has loaded and every managed sibling beside them on disk.

The return value comes back as JSON: depth-capped (6 levels by default), long strings and large
collections truncated with a marker, reference cycles broken, and `ToString()` as the fallback
for a value that refuses to serialise. Return a projection (`new { a.Id, a.Name }`) rather than a
whole object graph.

The host's internals are not visible to the compiler. Use reflection for private state:

```csharp
var cache = g.Resolve<IMemoryCache>();
var entries = cache.GetType().GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cache);
return entries;
```

## `g`: what a script can reach

| Member | What it does |
|---|---|
| `g.Services` | The host's root `IServiceProvider`. Throws with a hint if Sonde has not found it yet (see below). |
| `g.Resolve<T>()` | `g.Services.GetRequiredService<T>()`. |
| `g.Scope()` | A fresh `IServiceScope`. Dispose it (`using var scope = g.Scope();`). |
| `g.Vars` | A `ConcurrentDictionary<string, object?>` that lives for the life of the process. Assign in one eval, read in the next. |
| `g.Log(string)` | A line returned alongside the result. |
| `g.Cancellation` | Trips at the eval timeout. Also passed as `ct`. A script that ignores it cannot be stopped from outside. |
| `g.Hook(MethodBase, prefix, postfix)` | Intercepts a method (see Hooks). Returns a `HookHandle`. |
| `g.Hook<T>("Name", prefix, postfix)` | The same, finding `T`'s method by name, public or private, instance or static. |
| `g.Unhook(handle)` / `g.UnhookAll()` | Removes one hook, or every hook in the process. |
| `g.Tap("ListenerName", capacity)` | Ring-buffers a `DiagnosticListener`'s events (see Taps). |
| `g.Tapped("ListenerName", since)` | The captured events, optionally only those after `since`. |
| `g.Untap("ListenerName")` | Stops the tap and drops its buffer. |

## Examples

Read configuration:

```csharp
return g.Resolve<IConfiguration>()["Logging:LogLevel:Default"];
```

Call a service and project the answer:

```csharp
var orders = g.Resolve<IOrderRepository>();
var late = await orders.FindLateAsync(ct);
return late.Select(o => new { o.Id, o.CustomerName, DaysLate = (DateTime.UtcNow - o.Due).Days });
```

Keep state across evals:

```csharp
g.Vars["baseline"] = GC.GetTotalMemory(false);
return "recorded";
```

```csharp
return GC.GetTotalMemory(false) - (long)g.Vars["baseline"]!;
```

## Hooks

`g.Hook` patches a method with [Harmony](https://github.com/pardeike/Harmony) so a prefix runs
before it and a postfix after. The hook stays until `g.Unhook`, `g.UnhookAll`, or the process
ends. The prefix and postfix receive a `HookContext`:

| Member | Meaning |
|---|---|
| `Instance` | The target's `this`, or null for a static method. |
| `Args` | The call's arguments. |
| `Result` | In a postfix, what the method returned; set it to return something else. In a prefix, the value to return when `Skip` is set. |
| `Skip` | Set in a prefix to skip the original method. |

Watch every call for a while:

```csharp
var handle = g.Hook<PricingService>("Quote",
    prefix: c => g.Vars["lastQuoteArgs"] = c.Args.ToArray());
g.Vars["quoteHook"] = handle;
return handle.Description;
```

Rewrite a result to reproduce a bug without touching source:

```csharp
g.Hook<FeatureFlags>("IsEnabled", postfix: c => { if ((string)c.Args[0]! == "new-checkout") c.Result = true; });
return "forced on";
```

Remove it:

```csharp
g.Unhook((HookHandle)g.Vars["quoteHook"]!);
return g.UnhookAll;
```

Two limits. A small non-virtual method the JIT already inlined into its callers is not
intercepted at those call sites; `g.Hook` logs a warning when the target looks like one. And a
hook runs inside the host on the host's threads: keep it short and never block.

## Taps

`g.Tap` subscribes to a named `DiagnosticListener` and keeps its last `capacity` events (1000 by
default) with a two-level summary of each payload. Reading never clears the buffer. Names you
will want:

| Listener | What it carries |
|---|---|
| `Microsoft.EntityFrameworkCore` | Every SQL command EF Core runs, with its text and parameters. |
| `HttpHandlerDiagnosticListener` | Every `HttpClient` request and response. |
| `Microsoft.AspNetCore` | Every inbound request. |

```csharp
g.Tap("Microsoft.EntityFrameworkCore");
return "tapping";
```

```csharp
return g.Tapped("Microsoft.EntityFrameworkCore", since: DateTimeOffset.UtcNow.AddMinutes(-2))
    .Where(e => e.EventName.EndsWith("CommandExecuted"))
    .Select(e => e.Payload?["Command"]?["CommandText"]);
```

## Finding the DI root

Sonde finds the host's root `IServiceProvider` three ways, first one wins: a patch on the
container's constructor when the host builds it; the first ASP.NET request's `RequestServices`;
or the host calling `SondeHost.Register(app.Services)` after it is built. If `g.Services` throws
"No DI root captured yet", send the app one request, or add that one line.
