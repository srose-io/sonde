# Security

Sonde is remote code execution by design and **has no authorisation of its own**. A script sent to
a Sonde probe runs with the full rights of the host process. The boundary is the local endpoint:
a Unix domain socket or a named pipe that only a user who can open it can reach, and a process
only listens when it was launched with `SONDE_ENDPOINT` set. There is no network listener, ever.
Anything that routes evals to other people (a hosted dashboard, a fleet tool) must add its own
identity, gating and audit above Sonde; Sonde does not and will not.

Read `docs/threat-model.md` for what that means in practice: what the socket's filesystem
permissions protect, why the agent scrubs its own environment so child processes never inherit
it, and why a runaway script cannot be stopped without restarting the host.

## Reporting a vulnerability

Please do not open a public issue for a security problem. Use GitHub's private vulnerability
reporting on this repository (Security → Report a vulnerability). You will get an acknowledgement
within a week and a fix or a reasoned answer within 90 days; we will credit you in the release
notes unless you ask otherwise.

In scope: anything that lets a process reach a probe it should not be able to (a world-readable
socket, a predictable pipe name another user can bind first), the agent taking down a host, or the
hook running when it should be dormant. Out of scope: "a script can do anything", which is the
product.

## Supported versions

The latest minor release receives fixes. Older releases do not.
