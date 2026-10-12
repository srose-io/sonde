# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project follows
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Fixed

- The probe ships Harmony's per-runtime builds (`harmony/net{8,9,10}.0/`) and loads the host's,
  instead of whichever single copy the consuming project's target framework happened to pull in;
  a .NET 8 host given the net9.0 copy could not load it and the probe stayed dormant.
- The Harmony resolver is registered when the probe assembly loads, not only by the startup
  hook, so a host that reaches the listener another way can still hook. A Status request no
  longer loads Harmony at all.
- Test suites run on the CI cell's runtime (`SondeTestTfm`), and the end-to-end suite pins the
  samples to that runtime through a runtimeconfig override rather than guessing from the
  environment; the first run on the 9 cell had silently tested .NET 10.

## [0.1.0] - 2026-10-12

The first release: Sonde extracted from the in-house probe it was born as, with every scar a
test, four packages, a CLI, an MCP server, samples, an end-to-end suite and a docs site.

### Added

- Docs: README with the sixty-second demo (driven by the real CLI in the e2e suite) and a
  recording, `docs/architecture.md`, `docs/protocol.md`, `docs/script-surface.md`,
  `docs/threat-model.md`, and a docfx site with the API reference published to GitHub Pages.
- `samples/MinimalWeb` and `samples/Worker`, each with `scripts/*.cs` evals, and the
  `Sonde.EndToEnd` suite that launches them with the probe and runs every script: the
  environment route, the `dotnet watch` runtimeconfig route, a child that must not inherit the
  probe, a second listener that must stay dormant, and an idle-cost measurement written to
  `artifacts/idle-cost.json`.
- `Sonde.Mcp` (command `sonde-mcp`): a stdio Model Context Protocol server with tools
  `sonde_list_targets`, `sonde_hello`, `sonde_status`, `sonde_eval` and the
  `sonde://script-surface` resource (ADR 0003).
- `Sonde`: the client library. `ScriptCompiler` compiles a script against the target's loaded
  assemblies and their siblings on disk; `ProbeClient` moves frames over a Unix socket or named
  pipe; `Launch` builds the environment and the `dotnet watch` runtimeconfig template;
  `SondeClient.EvalAsync` runs the whole hello, compile, run flow and answers with an
  `EvalOutcome`.
- `Sonde.Probe`: the in-process agent. A startup hook that scrubs its own environment, stays
  dormant without `SONDE_ENDPOINT`, passes SDK tooling through, never steals a live socket, runs
  each script in a collectible load context, refuses a second eval while one runs, and exposes
  `g.Services`, `g.Vars`, `g.Log`, `g.Hook`/`g.Unhook`, `g.Tap`/`g.Tapped`. Wire protocol:
  length-prefixed JSON frames, kinds `hello`, `status`, `run`.
- Repository scaffolding: solution, shared build settings, analyzers with warnings as errors,
  central package versions, CI matrix (Linux, macOS, Windows × .NET 8, 9, 10), release workflow,
  issue and pull request templates, contributor guide, code of conduct, security policy.

[Unreleased]: https://github.com/srose-io/sonde/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/srose-io/sonde/releases/tag/v0.1.0
