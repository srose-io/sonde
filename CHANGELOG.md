# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project follows
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

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
