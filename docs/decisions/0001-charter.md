# ADR 0001: Charter

**Status:** accepted · **Date:** 2026-10-12 · **Decided by:** Spencer Rose

## What Sonde is

Sonde runs C# inside a live .NET process. An agent or a person writes a script, Sonde compiles
it, sends it into the process, runs it against the process's real objects — its DI container,
its caches, its connections — and returns the result as JSON. The process never restarts for
debugging after the one start that loads Sonde.

A *sonde* is a probe sent into something to take readings. That is the whole product.

Three things a script can do:

1. **Read and call.** Resolve any service from the host's `IServiceProvider`, walk private state,
   call methods, return a projection.
2. **Hook.** Prefix or postfix any method with Harmony, keep the hook across calls, remove it later.
3. **Tap.** Ring-buffer a `DiagnosticListener` (EF Core SQL, HttpClient, ASP.NET requests) with
   no patching.

## Where it comes from

Sonde is extracted from Loom (`/home/slowdata/sroot/code/loom`), where it shipped as `Loom.Probe`
in October 2026 and was proven on the Loom API itself, on headless nodes, on .NET 9 and .NET 10
ASP.NET services, under `dotnet watch`, and on Windows named pipes. The Loom copy is the reference
implementation until Sonde 0.1 ships; then Loom consumes Sonde from NuGet and keeps only its own
routing, gating and audit.

Reference material in the Loom repo (same node, read-only from here):

- `tools/loom-probe/` — the in-process agent (startup hook, listener, collectible ALC runner,
  Harmony hooks, taps, result JSON), with its README and 38 tests in `tools/loom-probe.tests/`.
- `src/loom.api/Services/ProcessEval/` — the out-of-process compiler (`ScriptCompiler.cs`), the
  client (`ProbeClient.cs`), launch wiring (`ProbeLaunch.cs`: env, runtimeconfig template for
  `dotnet watch`), status cache.
- `docs/responses/dotnet-eval-tunnel.md` — the design survey (what the CLR allows and what it
  does not).
- `docs/plans/2026-10-11-dotnet-eval-tunnel.md`, `2026-10-11-probe-attach.md`,
  `2026-10-12-probe-node-opt-out.md` and `docs/responses/*probe*-proofs.md` — what was built,
  what broke, what was proven.

## Decisions already made

| Decision | Choice | Why |
|---|---|---|
| Name | **Sonde** | Free on NuGet and GitHub (checked 2026-10-12). Short, says what it does. |
| Licence | MIT | Lowest friction for contributors and for commercial hosts embedding it. |
| Audience | Agents first, people second | The CLI and MCP server are the front door; a human REPL is a nice-to-have. |
| Architecture | In-process agent + out-of-process compiler | Roslyn stays out of the target. Sixteen services share one compiler. Carried over from Loom, proven. |
| Attach model | Start with the hook; no live injection | The CLR has no attach-time managed code injection short of a native profiler. We say so plainly and do not chase it. |
| Targets | .NET 8, 9, 10 on Linux, macOS, Windows | The probe binds to the host's own framework at runtime; the CI matrix proves every pair. |
| Transport | Unix domain socket on POSIX, named pipe on Windows, length-prefixed JSON frames | Local only, filesystem-permissioned. No network listener in the agent, ever. |
| Security stance | Sonde is remote code execution by design and **has no authorisation of its own** | A local socket your user can open is the boundary. Hosts that route evals (Loom) add identity, gating and audit above it. `SECURITY.md` says this in the first paragraph. |
| Repo shape | Single repo, several packages | `Sonde.Probe` (agent), `Sonde` (compiler + client library), `sonde` (dotnet tool CLI), `Sonde.Mcp` (MCP server). One version across all four. |

## Non-negotiables carried from Loom's scars

Each of these cost a day. They are requirements, not suggestions, and each gets a test.

- **Scrub the environment at startup.** The hook removes `DOTNET_STARTUP_HOOKS` and the endpoint
  variable from its own process so children never inherit the probe. Twelve listeners on one
  socket is what happens otherwise.
- **Never steal a live socket.** If something already answers at the endpoint, do not bind; log and
  stay dormant.
- **Pass through SDK tooling.** `dotnet.dll`, `dotnet-watch`, MSBuild workers: detect by entry
  assembly and do nothing.
- **`dotnet watch` rewrites startup hooks** for the app it launches. Deliver the hook through a
  runtimeconfig template plus the user-runtimeconfig variable as well, and test both routes.
- **Dormant unless told.** No endpoint variable means no listener, no thread, no cost.
- **Nothing in the hook may throw.** A startup hook that throws crashes the host before `Main`.
- **Collectible `AssemblyLoadContext` per eval**, unloaded after. Report live contexts.
- **No `Thread.Abort`.** A runaway script holds the eval slot until the host restarts. Refuse a
  second eval while one runs, pass a cancellation token, say so in the docs.
- **Harmony 2.4.2 or later.** 2.4.1 throws `PlatformNotSupportedException` on CoreCLR 10.
- **Compile against the host's loaded assemblies and their siblings on disk.** Framework
  assemblies the host has not loaded yet are still resolvable.
- **Idle cost is a number in the README** (Loom measured ~14 MB and one parked thread). Keep it
  measured in CI, not asserted.

## What "gold standard" means here

A stranger should be able to go from the README to a working eval on their own app in five
minutes, and from a fork to a merged PR in an afternoon without asking anyone anything.

- README with a 60-second demo (install tool → launch app with Sonde → run a script → JSON back),
  a GIF, the idle-cost number, and the attach caveat above the fold.
- `docs/` with architecture, the wire protocol as a spec, the script surface, a security threat
  model, and an ADR per decision (this file is the first).
- `CONTRIBUTING.md` (how to build, test, run the samples, what a good PR looks like), a code of
  conduct, `SECURITY.md` with a disclosure route, issue and PR templates, `CHANGELOG.md` in
  Keep-a-Changelog form, SemVer.
- CI on GitHub Actions: build + test matrix across OS × .NET version, a real end-to-end job that
  launches a sample app and evals into it, package build on every PR, publish to NuGet on a tag.
  Reproducible builds, SourceLink, deterministic, symbols published.
- Analyzers on, warnings as errors, `.editorconfig`, nullable everywhere, public API tracked
  (`PublicAPI.Shipped.txt`).
- `samples/` with a minimal web app and a worker service, each with a `scripts/` folder of evals
  that double as the e2e tests.
- Every public type has XML docs; the docs site is generated from them.

## Out of scope for 1.0

Hot reload (`MetadataUpdater.ApplyUpdate`), profiler-based live attach, a web UI, any
authentication or multi-user story, non-CoreCLR runtimes (Mono, NativeAOT).

## Still Spencer's to decide

- **Where it lives on GitHub**: `srose-io/sonde` (personal, simplest) or a new org (`sonde-dev`)
  so maintainers can be added without personal-account entanglement. Ask before the first push.
- **Publish timing**: push the repo public from the first commit, or build to 0.1 in private and
  publish with a working demo. Recommendation: private until the demo works, then public with
  a release.
- **NuGet publishing account**: whose API key signs releases.
