# ADR 0003: The MCP surface

**Status:** accepted · **Date:** 2026-10-12

## Context

Agents are Sonde's first audience. The Model Context Protocol is how an agent discovers and calls
tools, so `Sonde.Mcp` is a front door on a par with the CLI. The question is what shape the tools
take: one tool per protocol request, or a few coarse ones; errors as MCP errors or as data.

## Decision

- **Transport:** stdio only, through the official `ModelContextProtocol` package (2.2.0 at the
  time of writing). A client runs `sonde-mcp` as a child process. No HTTP: Sonde has no
  authorisation of its own, and an HTTP listener would invite someone to put it on a network.
- **Four tools, named with a `sonde_` prefix** so they sort together in a client's tool list:
  `sonde_list_targets`, `sonde_hello`, `sonde_status`, `sonde_eval`. They mirror the CLI verbs
  one to one, so a person reading the CLI docs understands the tools and vice versa.
- **Every tool answers JSON text**, the same shapes the CLI's `--json` prints. An eval that did not
  run (did not compile, no probe, busy) is an *outcome* in that JSON, not an MCP error: an agent
  reads the diagnostics and fixes its script rather than seeing a failed call.
- **One resource, `sonde://script-surface`,** serving `docs/script-surface.md` verbatim (embedded
  at build time), so the agent can read what `g` offers without leaving the protocol.
- **Tool annotations:** the three read tools are marked read-only and idempotent; `sonde_eval` is
  marked destructive, because it is.
- **Server instructions** carry the one-paragraph orientation an agent needs before its first
  call, including that a script cannot be stopped once started.

## Consequences

- Adding a protocol request means adding a CLI verb and a tool together, and a test for each.
- The MCP package's API has moved between releases; the version is pinned centrally, and the
  stdio round-trip test in `tests/Sonde.Mcp.Tests` is what catches a breaking bump.
