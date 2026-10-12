# Contributing to Sonde

Thanks for looking. The aim is that you can go from a fork to a merged pull request in an
afternoon without asking anyone anything. If something here is wrong or missing, that is a bug:
open an issue or fix it in the same PR.

## Build and test

You need the .NET SDK 9 or later (`global.json` rolls forward to newer majors). Nothing else.

```bash
dotnet build Sonde.sln -warnaserror        # warnings are errors everywhere
dotnet test Sonde.sln --filter "Category!=EndToEnd"   # the unit suites
dotnet test tests/Sonde.EndToEnd           # launches the samples and evals into them
dotnet pack Sonde.sln -o artifacts/package # the four packages
```

The end-to-end suite is the real proof for anything that touches the agent, the protocol or the
compiler. It builds `samples/` itself and launches each sample with the probe, so it needs a few
seconds and a free Unix socket (or named pipe on Windows).

Build output goes under `artifacts/` (not `bin/` and `obj/` in each project).

## Layout

| Path | What |
|---|---|
| `src/Sonde.Probe` | The in-process agent, loaded through `DOTNET_STARTUP_HOOKS`. Targets `net8.0`, references only the shared framework and Lib.Harmony. |
| `src/Sonde` | The client library: Roslyn compiler, protocol client, launch helpers, the session type. |
| `src/Sonde.Cli` | The `sonde` dotnet tool. |
| `src/Sonde.Mcp` | The `sonde-mcp` Model Context Protocol server. |
| `tests/` | One xunit project per package plus `Sonde.EndToEnd`. |
| `samples/` | A minimal web app and a worker service, each with `scripts/*.cs` evals that the e2e suite runs. |
| `docs/` | Architecture, the protocol spec, the script surface, the threat model, and `decisions/` (ADRs). |

## What a good pull request looks like

- **Small and on one subject.** One behaviour change, with its test, its CHANGELOG line and (if
  the public API moved) its `PublicAPI.Unshipped.txt` entry, in one PR.
- **Tests first.** Write the failing test, watch it fail for the right reason, then make it pass.
  The scars in the charter (`docs/decisions/0001-charter.md`) each have a named test; do not weaken
  one to get green.
- **A design change is an ADR.** Anything that changes the protocol, the script surface, the launch
  model or a package boundary gets `docs/decisions/NNNN-<slug>.md` (status, context, decision,
  consequences) in the same PR, before the code.
- **Public API is tracked.** The `Microsoft.CodeAnalysis.PublicApiAnalyzers` package fails the build
  when a public member is added without a line in `PublicAPI.Unshipped.txt`. Every public type and
  member has an XML doc comment; the docs site is generated from them.
- **No warnings, no suppressions.** Fix the warning or justify a per-line suppression in a comment.
  Repo-wide analyzer tuning lives in `.editorconfig` with a reason per line.
- **Commit messages a stranger can read** in `git log --oneline`.

## The rules the agent lives by

`src/Sonde.Probe` runs inside other people's processes, so it has rules that the rest of the repo
does not:

- Nothing in the startup hook may throw. A throw there kills the host before `Main`.
- The agent never opens a network listener. Unix domain socket or named pipe only.
- No `Thread.Abort` (there is none on .NET Core), and no synchronous wait on an eval from inside
  a hook.
- Everything it logs goes to stderr with a `[Sonde]` prefix and nowhere else.

## Releasing (maintainers)

Bump `VersionPrefix` in `Directory.Build.props`, move `[Unreleased]` in `CHANGELOG.md` to the
version, move `PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt`, commit, tag `vX.Y.Z`, push
the tag. The `Release` workflow packs, publishes to NuGet and creates the GitHub release.

## Code of conduct

This project follows the [Contributor Covenant](CODE_OF_CONDUCT.md).
