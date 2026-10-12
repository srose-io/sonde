# Architecture

Sonde is two halves and a wire between them.

```
 your shell / an agent                              the target process
 ┌───────────────────────────┐                      ┌──────────────────────────────┐
 │ sonde CLI · sonde-mcp     │                      │ Sonde.Probe (startup hook)   │
 │   └ Sonde (client lib)    │   local socket/pipe  │   ├ listener thread          │
 │       ├ ScriptCompiler ───┼── run(assembly) ────▶│   ├ EvalRunner (collectible  │
 │       │   (Roslyn)        │◀── result JSON ──────┼───│   AssemblyLoadContext)     │
 │       └ ProbeClient       │                      │   ├ ScriptGlobals  g.*       │
 └───────────────────────────┘                      │   ├ Hooks (Harmony)          │
                                                    │   └ Taps (DiagnosticListener)│
                                                    └──────────────────────────────┘
```

**The probe** is a small assembly the .NET runtime loads into the target before `Main` because
`DOTNET_STARTUP_HOOKS` names it. It opens a local socket, waits, and runs whatever compiled
script arrives against the process's real objects. It carries no compiler.

**The client** compiles the script out of process with Roslyn, against the exact assemblies the
target reports it has loaded, and sends the bytes over. Sixteen services on one box share one
compiler instead of carrying 60 MB of Roslyn each.

## What the CLR allows, and what it does not

This shape is forced by the runtime, not chosen for taste.

| Mechanism | Needs opt-in at start? | Runs code? | Notes |
|---|---|---|---|
| Startup hook (`DOTNET_STARTUP_HOOKS`) | An environment variable only; no source change | Yes | What `dotnet watch` hot reload uses. **This is Sonde.** |
| Roslyn scripting in-process | A source change and 60-100 MB of Roslyn per host | Yes | Simplest, heaviest. |
| Harmony / MonoMod detours | No, but needs a way to run code first | n/a | Prefix/postfix any method. Misses call sites the JIT already inlined. **Sonde's `g.Hook`.** |
| `DiagnosticListener` / `EventListener` | No | Observe only | EF Core SQL, HttpClient, ASP.NET requests, no patching. **Sonde's `g.Tap`.** |
| Hot reload (`MetadataUpdater.ApplyUpdate`) | An environment variable | Edits method bodies | Needs the baseline compilation; a project of its own. Out of scope. |
| ClrMD / `dotnet-dump` | No | No | Read-only heap and stacks. Good for a process that did not opt in. |
| Profiler attach (`ICorProfiler`) | No | Native, then managed | The only true no-restart injection. Needs a native shim per platform. Out of scope. |
| Debugger func-eval | No | Expressions only, process paused | No lambdas, no new types. |

So: **a process must start with the probe in it.** There is no attach-time managed code
injection into a CLR that did not opt in, short of writing a native profiler. Sonde says so
plainly and does not chase it. After that one start, the process never restarts for debugging
again.

## The startup hook

`StartupHook.Initialize()` runs before the host's `Main`, on the main thread, and a throw there
kills the host before it logs anything. Everything the hook does is inside one `try`, and the
decision it makes is a pure function (`Bootstrap.Plan`) of two inputs:

1. **The entry assembly's path.** If it is under an SDK version directory
   (`…/sdk/9.0.312/dotnet.dll`, `dotnet-watch`, `MSBuild.dll`, `VBCSCompiler.dll`), this process
   is tooling that will launch the real app as a child. The hook does nothing and leaves the
   environment alone so the child gets the probe.
2. **`SONDE_ENDPOINT`.** Unset: the hook scrubs the environment and returns. No thread, no
   socket, no cost. Set: the hook records the endpoint, scrubs the environment, and starts.

**Scrubbing the environment** means removing `DOTNET_STARTUP_HOOKS` and `SONDE_ENDPOINT` from
the process's own environment block as the first thing after the decision. The runtime has
already read them. Any child the host later starts, a `dotnet build` from a dev server, a
helper process, a test host, would otherwise inherit them, load the probe, and fight for the
same socket. The host reads its own endpoint from `SondeHost.Endpoint`, never from the
environment.

**Starting** registers a resolver so `0Harmony.dll` loads from the probe's own directory, wires
the DI-root capture, and starts one background thread named `sonde-listener` that sleeps 250 ms
(letting the host settle) and then binds. If the endpoint's file exists and something still
answers on it, the probe logs `[Sonde] another probe already listens` and stays dormant rather
than stealing it; only a stale file left by a dead process is replaced.

## Delivering the hook under `dotnet watch`

`dotnet watch run` rewrites `DOTNET_STARTUP_HOOKS` for the app it launches, keeping only its own
hot-reload hook, so the environment route alone never reaches an app under watch. The runtime
also reads startup hooks from the app's `runtimeconfig.json` (`configProperties.STARTUP_HOOKS`)
and merges both lists. Sonde therefore writes a `runtimeconfig.template.json` naming the probe
and points the build at it through the `UserRuntimeConfig` MSBuild property, which MSBuild reads
from the environment. `sonde env` and `sonde run` set all three variables, so both routes are
live at once, and the end-to-end suite proves each on its own.

## Finding the DI root

A startup hook runs before the host has built anything, so there is no `IServiceProvider` to
capture yet. Three paths, first one wins:

1. **A Harmony postfix on `ServiceProvider`'s constructor** records the first container the
   host builds. Works for web hosts and plain generic hosts alike (the Worker sample proves it).
2. **The first ASP.NET request.** A `DiagnosticListener` observer takes `IServiceScopeFactory`
   from the request's `RequestServices`. The fallback if the patch could not be applied.
3. **`SondeHost.Register(app.Services)`** from the host's own code. One line, for the host that
   wants to be explicit or whose container is not the standard one.

Until one of them fires, `g.Services` throws with a message naming all three.

## Running a script

The compiler wraps the script as the body of
`public static async Task<object?> Run(ScriptGlobals g, CancellationToken ct)` inside a
class named `SondeScript`, with a `#line` directive so errors report the script's own lines.
It references every assembly the target reported, every managed sibling in those directories
(so a script can name a framework assembly the host has not loaded yet), and `Sonde.Probe.dll`
for `ScriptGlobals`. References are cached by path, size and mtime.

The probe loads the assembly into a **collectible `AssemblyLoadContext`** whose resolver
answers every reference with the host's already-loaded instance, so a type in the script is the
same `Type` the host uses. It invokes `Run` on a thread-pool thread and waits up to the timeout.
On return it serialises the result (before unloading, since the value may be of a type defined
in the script), unloads the context, and answers.

**One eval at a time.** A second `run` while one is in flight is refused with `busySinceUtc`.

**A runaway script cannot be stopped.** .NET Core has no `Thread.Abort`. A script that ignores
its cancellation token holds its thread, its load context and the eval slot until it finishes
or the host restarts. The probe reports it under `status.runningEval` and `liveContexts`, and
the docs say so wherever a script is written.

## Hooks

`g.Hook` patches a method with Harmony. One Harmony patch per target method dispatches to every
handle registered on it; the last handle removed unpatches. The patch methods are chosen by
whether the target returns a value, because Harmony rejects a `__result` parameter on a void
target. Harmony 2.4.2 or later is required: earlier releases throw on .NET 9 or refuse .NET 10.
A small non-virtual target the JIT already inlined into its callers is not intercepted there;
`g.Hook` warns when the target's IL is tiny and non-virtual.

## Taps

`g.Tap` subscribes to `DiagnosticListener.AllListeners`, which replays every existing listener to
a new subscriber, so it catches a listener created before or after the tap. Events are kept in
a ring buffer with a two-level JSON summary of each payload.

## What is where

| Project | Role | Depends on |
|---|---|---|
| `Sonde.Probe` | The agent | The shared framework, `Lib.Harmony`, the DI abstractions (compile-time only; see ADR 0002) |
| `Sonde` | Compiler, client, launch helpers, `SondeClient` | `Sonde.Probe`, `Microsoft.CodeAnalysis.CSharp` |
| `Sonde.Cli` | The `sonde` tool | `Sonde`, `System.CommandLine` |
| `Sonde.Mcp` | The `sonde-mcp` server | `Sonde`, `ModelContextProtocol` |

Decisions and their reasons are in [`decisions/`](decisions/0001-charter.md).
