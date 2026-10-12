# The wire protocol

This is the contract between a Sonde probe (inside the target process) and any client. It is
small enough to implement in an afternoon in any language; the .NET client is `Sonde.ProbeClient`
and the shapes are `Sonde.Probe.Protocol.*`.

## Transport

- **POSIX:** a Unix domain socket at the path in `SONDE_ENDPOINT`. The probe creates the file;
  its permissions are the user's default (`umask`), which is the security boundary. See
  [the threat model](threat-model.md).
- **Windows:** a named pipe, `\\.\pipe\<name>`, where `<name>` is `SONDE_ENDPOINT`.
- The probe never listens on TCP, and never will.

**One request and one response per connection.** The client connects, writes one frame, reads
one frame, and closes. The probe closes its end after answering. There is no session, so a
client may open as many connections as it likes, in any order, from any number of processes;
the probe serialises evals itself (see Busy below).

## Framing

A frame is a 4-byte **little-endian signed 32-bit length**, then exactly that many bytes of
**UTF-8 JSON**. The length is of the body only. A body may not exceed 128 MiB
(`134217728` bytes); a probe that reads a larger length closes the connection.

A Hello request on the wire (71 bytes of body):

```
47 00 00 00                                  length = 71
7b 22 6b 69 6e 64 22 3a 22 68 65 6c 6c 6f    {"kind":"hello
22 2c ...                                    ",...
```

```json
{"kind":"hello","entryMethodName":"Run","timeoutMs":10000,"maxDepth":6}
```

The three trailing fields are the request's defaults, which the .NET client always sends; a
client in another language may omit them. The test `Framing_IsFourByteLittleEndianLengthThenUtf8Json`
asserts these exact bytes.

## JSON conventions

- Property names are camelCase.
- Enums are camelCase strings (`"hello"`, not `0`).
- Null properties are omitted by the probe; a client should treat a missing property as null.
- Unknown properties are ignored by both sides, so fields can be added without a version bump.

## Request

```json
{
  "kind": "hello" | "status" | "run",
  "assemblyBase64": "...",     // run only: the compiled script assembly
  "entryTypeName": "SondeScript", // run only: the type to find in it
  "entryMethodName": "Run",    // run only, default "Run": the public static method to invoke
  "timeoutMs": 10000,          // run only, default 10000
  "maxDepth": 6                // run only, default 6: how deep the result is serialised
}
```

### `hello`

Describes the process, and tells a compiler what to compile against.

```json
{
  "runtimeVersion": ".NET 9.0.4",
  "processId": 12345,
  "entryAssembly": "MinimalWeb",
  "loadedAssemblyPaths": ["/usr/share/dotnet/shared/Microsoft.NETCore.App/9.0.4/System.Private.CoreLib.dll", "..."],
  "loadedAssemblyNames": [],
  "diRootCaptured": true,
  "liveContexts": 0,
  "sondeVersion": "0.1.0"
}
```

| Field | Meaning |
|---|---|
| `loadedAssemblyPaths` | File paths of every non-dynamic assembly the process has loaded. Compile against these, and against their siblings on disk so the script can name a framework assembly the host has not touched yet. |
| `loadedAssemblyNames` | Names of loaded assemblies that have no file (a single-file bundle). Resolve them from the runtime directory. |
| `diRootCaptured` | Whether `g.Services` will work. |
| `liveContexts` | Collectible load contexts not yet unloaded. A script that timed out holds one until it ends. |
| `sondeVersion` | The probe's informational version, so a stale agent is visible. |

### `status`

```json
{
  "processId": 12345,
  "diRootCaptured": true,
  "liveContexts": 0,
  "runningEval": { "startedAtUtc": "2026-10-12T10:00:00Z" },   // omitted when idle
  "hooks": ["MinimalWeb.CounterService.Increment()"],
  "taps": ["Microsoft.EntityFrameworkCore"]
}
```

### `run`

The client compiles the script to a .NET assembly whose `entryTypeName` has a public static
`entryMethodName` with one of these signatures (the probe picks by parameter count):

```csharp
static object? Run()
static object? Run(ScriptGlobals g)
static object? Run(ScriptGlobals g, CancellationToken ct)   // what the Sonde compiler emits
```

A `Task` or `Task<T>` return value is awaited. The probe loads the assembly into a collectible
`AssemblyLoadContext`, resolves its references to the host's already-loaded assemblies (so types
are the host's own, not copies), invokes the method on a thread-pool thread, waits up to
`timeoutMs`, serialises the return value, and unloads the context.

```json
{
  "result": { "...": "..." },   // the return value as JSON; omitted when null
  "logs": ["a line from g.Log"],
  "exception": "System.InvalidOperationException: ...\n   at ...",   // when the script threw
  "elapsedMs": 12,
  "timedOut": false,
  "liveContexts": 0,
  "busySinceUtc": "2026-10-12T10:00:00Z",   // when refused: an eval is already running
  "protocolError": "Entry type 'X' not found in the compiled script."   // when the request was malformed
}
```

Exactly one of these is true of a `run` response:

| Case | What is set |
|---|---|
| The script returned | `result` (unless it returned null), `logs`, `elapsedMs` |
| The script threw | `exception`, `logs` |
| The timeout tripped first | `timedOut: true`, `logs`, `elapsedMs`. The script is still running inside the host. |
| An eval was already running | `busySinceUtc`. Nothing was loaded. |
| The request was malformed | `protocolError`. Nothing ran. |

**Busy.** One eval at a time per process. A second `run` while one is in flight is refused
with `busySinceUtc` rather than queued, so a client always knows what is running. A script
that timed out keeps the slot until its thread actually finishes, because there is no way to
stop it; `status` shows it under `runningEval`.

## Result serialisation

The return value becomes JSON by walking it: scalars (numbers, strings, bools, enums as strings,
`Guid`, dates, `TimeSpan`, `Uri`, `Version`) directly; dictionaries as objects; other enumerables
as arrays; everything else as an object of its public properties and fields. Caps:

| Cap | Value | Marker |
|---|---|---|
| Depth | `maxDepth` (default 6) | `"<max-depth TypeName>"` |
| String length | 10 000 chars | `"…(truncated N chars)"` appended |
| Collection items | 100 | `"<truncated, +more items>"` as the last item; `"…": "(+N more)"` for dictionaries |
| Object members | 200 | silently stops |
| Reference cycle | | `"<cycle TypeName>"` |
| A getter that threw | | `"<error: message>"` |
| A value that will not serialise | | its `ToString()` |

A `JsonNode`, `JsonElement` or `JsonDocument` returned by a script passes through as data.

## Versioning

The protocol is unversioned in 0.x. A field is only ever added, never renamed or removed; a
client ignores what it does not know. A breaking change will add a `protocolVersion` to `hello`
first.
