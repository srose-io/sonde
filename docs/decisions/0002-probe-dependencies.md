# ADR 0002: What the probe targets and depends on

**Status:** accepted · **Date:** 2026-10-12

## Context

`Sonde.Probe` is loaded into other people's processes through `DOTNET_STARTUP_HOOKS`. It has to
load into any supported host (.NET 8, 9 and 10; a web app or a worker service) without the host
referencing it, and it must not drag a second copy of any assembly the host already has, or
scripts see "type X is not type X".

The reference implementation targeted `net9.0` and took its dependency-injection types from the
ASP.NET Core shared framework, which every host it was used on carried. A worker service does not
carry that framework.

## Decision

- **Target `net8.0`**, the lowest supported host. A startup hook built for 8 loads into 9 and 10
  hosts; one built for 9 does not load into an 8 host. The probe never needs anything newer than
  the 8 BCL.
- **Reference `Microsoft.Extensions.DependencyInjection.Abstractions` with `ExcludeAssets="runtime"`.**
  The probe compiles against `IServiceProvider`, `IServiceScope` and `IServiceScopeFactory` but
  ships no copy of the assembly. At runtime it binds to whichever copy the host loaded: the ASP.NET
  shared framework's in a web app, the app-local NuGet copy in a worker service. A host with no DI
  at all never resolves the types, and every member that would touch them is reached only by a
  script that asks for `g.Services`.
- **Ship Harmony's own per-runtime builds with the probe** under `harmony/net8.0/`,
  `harmony/net9.0/` and `harmony/net10.0/` beside `Sonde.Probe.dll`, and pick the host's at load
  time (`HarmonyLocator`: the host's major, else the newest below it). The `Lib.Harmony` package
  reference is private to the probe (`PrivateAssets="all"`), so nothing about Harmony flows to a
  consumer through NuGet; a `buildTransitive` targets file copies the folder into the consumer's
  output. The first CI run showed why: a net9.0 consumer (the CLI, the test suites) received the
  package's net9.0 Harmony beside the probe, and a .NET 8 host could not load it, so the hook
  logged `initialize failed` and the probe stayed dormant. The host never references Harmony; a
  `Default` load-context `Resolving` handler, registered from the probe's module initializer,
  answers only for `0Harmony` and only when nothing else resolved it.
- **Nothing else.** A test (`Probe_ShipsHarmonyPerRuntimeAndNothingElse`) asserts the referenced
  assembly list is the shared framework, the DI abstractions and Harmony, and that the three
  Harmony builds are on disk under `harmony/`.

## Consequences

- The probe's own code is limited to the .NET 8 API surface. Fine: it is small and uses nothing new.
- The DI assembly version the probe compiled against (8.0.x) is older than the host's in a 9 or 10
  host; the runtime unifies upward, which is the normal case for any .NET library.
- A host that uses no DI container (a bare console app) gets a probe that answers Hello, Status and
  scripts that do not touch `g.Services`; `g.Services` throws with a message naming
  `SondeHost.Register`.
