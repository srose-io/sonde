# Sonde

Sonde runs C# inside a live .NET process: compile a script out of process, send it in through
a startup-hook agent, run it against the host's real services, get JSON back. No restart after
the one that loads the agent. Read `docs/decisions/0001-charter.md` before anything else: it
holds the decisions already made, the non-negotiables, and what "gold standard" means for
this repo.

This is an open-source project for strangers to use and contribute to. Every change is judged
by how it reads to someone who has never seen Loom.

## Layout

- `src/Sonde.Probe/` — the in-process agent (`net8.0`). References only the shared framework,
  the DI abstractions (compile-time only, ADR 0002) and Lib.Harmony. Loaded through
  `DOTNET_STARTUP_HOOKS`. The `StartupHook` type stays in the global namespace (the runtime
  looks for it by name). `Protocol/` is the wire contract.
- `src/Sonde/` — the client library: `ScriptCompiler` (Roslyn), `ProbeClient` (frames),
  `Launch` (environment, endpoint naming, the runtimeconfig template for `dotnet watch`),
  `Targets`, `SondeClient` (the whole eval flow, answering with an `EvalOutcome`).
- `src/Sonde.Cli/` — the `sonde` dotnet tool (package `Sonde.Cli`; the id `sonde` would collide
  with the `Sonde` library on NuGet).
- `src/Sonde.Mcp/` — the `sonde-mcp` MCP server (ADR 0003). Embeds `docs/script-surface.md`.
- `tests/` — one xunit project per package plus `Sonde.EndToEnd` (launches `samples/` as real
  processes and evals into them; this is the real proof). `Sonde.Tests/LoopbackFixture.cs`
  starts the listener in-process and is linked into the CLI and MCP suites.
- `samples/` — `MinimalWeb` (ASP.NET) and `Worker` (no ASP.NET), each with `scripts/*.cs` the
  e2e suite runs. They target `net8.0` and roll forward.
- `docs/` — `architecture.md`, `protocol.md`, `script-surface.md`, `threat-model.md`,
  `decisions/` (ADRs), `assets/` (icon, demo tape and GIF). `docfx.json` builds the site.
- `scripts/update-public-api.sh <csproj>` — appends undeclared public symbols to
  `PublicAPI.Unshipped.txt` from the build's RS0016 errors.
- Build output is under `artifacts/` (`UseArtifactsOutput`): `artifacts/bin/<Project>/<config>[_<tfm>]/`.

## Rules

- **Nothing in the startup hook may throw.** Wrap everything; a throw there kills the host before
  `Main`. A test asserts the hook is a no-op with no endpoint set.
- **The agent never opens a network listener.** Local socket or named pipe only.
- **No `Thread.Abort`, no synchronous waits on the eval thread's result from inside a hook.**
- **A design change is an ADR** in `docs/decisions/` before the code, numbered, with status.
  A behaviour change is a `CHANGELOG.md` line in the same commit.
- **Public API changes update `PublicAPI.Unshipped.txt`** in the same commit.
- **Warnings are errors.** Do not suppress; fix or justify in a comment.
- **Tests before behaviour** (`test-driven-development`). The e2e suite is the gate for anything
  touching the agent, the protocol or the compiler.
- Work on `main`; small commits with messages a contributor can read in `git log --oneline`.
- `docs/responses/` is for working notes to Spencer and is not part of the product. Keep it out of
  the README and the docs site.

## Verify

`.loom/verify.json` gates done-claims (`verified-development`). The `full` tier builds, runs the
unit suites and the e2e suite on this machine. `dotnet test` once at a time.

## Relationship to Loom

Loom (`/home/slowdata/sroot/code/loom`, same node) holds the reference implementation under
`tools/loom-probe/` and `src/loom.api/Services/ProcessEval/`. Read it; do not copy it blindly.
Loom-specific naming (`LOOM_PROBE_*`, `[Loom.Probe]`), its routing, its admin gate and its audit
stay in Loom. Once Sonde 0.1 is on NuGet, Loom switches to it — that migration is a Loom change,
planned from the Loom thread, not from here.
