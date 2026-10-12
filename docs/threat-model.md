# Threat model

**Sonde is remote code execution by design, and has no authorisation of its own.** A script sent
to a probe runs inside the host process with every right that process has: its memory, its
connections, its credentials, its filesystem access, its network. Nothing in Sonde checks who
sent the script. This page is about what *does* bound that, and what you are responsible for.

## The boundary is the endpoint

A probe only ever listens on a local endpoint:

- **POSIX:** a Unix domain socket file. Whoever can `connect()` to the file can run code in the
  process. The file is created with the process's `umask`, in `~/.sonde` by default, so by
  default that is the user the process runs as. There is no TCP listener and no way to add one.
- **Windows:** a named pipe with the default security descriptor, which admits the same user and
  administrators.

So the question "who can run code in this process?" has the answer "whoever can open the
endpoint", which is the same set of people who could already attach a debugger, read the
process's memory with `dotnet-dump`, or replace its binaries. Sonde does not widen that set. It
does make the capability convenient, which is the point and also the risk.

**What you own:** the endpoint's location and permissions. Keep `SONDE_HOME` on a filesystem
with sane permissions. Do not put the socket in a world-writable directory with a predictable
name. Do not bind-mount it into a container you do not trust.

## A probe only exists when you asked for one

The hook does nothing unless `SONDE_ENDPOINT` is set in the process's environment at start.
A process that merely has `DOTNET_STARTUP_HOOKS` pointing at the probe (because it inherited it)
scrubs the variable and continues with no listener, no thread, no socket. That is tested.

**What you own:** which processes you launch with the endpoint set. A production service
should not be one of them unless you have decided that the people who can reach its endpoint
should be able to run code in it.

## Children never inherit the probe

The first thing the hook does is remove both variables from the process's own environment.
A child process the host starts later (a build, a helper, a shell) does not get the probe, does
not try to bind the same endpoint, and does not become a second door into the host. The host
reads its endpoint from `SondeHost.Endpoint`. That is tested, in-process and end to end.

A second process that is nonetheless given the same endpoint (a copy-pasted launch command)
finds the socket answering and refuses to bind, logging `[Sonde] another probe already listens`.
It does not unlink a live socket. Only a stale file nothing answers on is replaced.

## A script cannot be stopped

.NET Core has no `Thread.Abort`. A script that ignores its cancellation token runs until it
finishes or the host exits. The probe will not start a second eval while it runs, reports it in
`status`, and counts its load context in `liveContexts`. A script that spins forever has pinned
one thread-pool thread for the life of the process.

**What you own:** honour `ct` in anything that might take a while, and treat `sonde eval` on a
process you care about the way you would treat attaching a debugger to it.

## What a script can do that is worse than reading state

Everything. A hook (`g.Hook`) rewrites what a method returns for every caller until unhooked. A
script can call `Environment.Exit`, open sockets, write files, read secrets out of
configuration. Sonde does not sandbox the script and could not: it runs as host code, by design.

## Hosting Sonde for other people

If you route evals to processes on behalf of other people (a dashboard, a fleet tool, an agent
platform), **you** are the authorisation layer. The reference host that Sonde was extracted from
does three things above it, and so should you:

1. **Identity and gating.** Only an authenticated administrator may send a script, and only to
   a process whose definition opted in.
2. **An audit trail.** Every eval is written to a log with who sent it, when, to which process,
   and a hash (or the text) of the script.
3. **No network exposure of the endpoint.** Your service talks to the probe over the local
   socket on the same machine, and exposes only its own authenticated API.

`SECURITY.md` has the disclosure route. "A script can do anything" is the product, not a
vulnerability; a way to reach a probe you should not be able to reach, or a probe that listens
when it should be dormant, or the agent taking a host down, is.
