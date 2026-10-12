using System.Text.Json.Nodes;

namespace Sonde.Probe.Protocol;

// The wire contract between a probe and its client. This folder depends on nothing but the
// shared framework and System.Text.Json, so a client in any language can be written from
// docs/protocol.md and these shapes.

/// <summary>What a request asks the probe to do.</summary>
public enum ProbeRequestKind
{
    /// <summary>Describe the process: runtime, loaded assemblies, probe version.</summary>
    Hello,

    /// <summary>Report the probe's state: running eval, live contexts, hooks and taps.</summary>
    Status,

    /// <summary>Load a compiled script assembly, run its entry method, return the result.</summary>
    Run,
}

/// <summary>One request per connection. <see cref="Kind"/> selects which fields are read.</summary>
public sealed record ProbeRequest
{
    /// <summary>What to do.</summary>
    public ProbeRequestKind Kind { get; init; }

    /// <summary>Run only: the compiled script assembly, base64-encoded.</summary>
    public string? AssemblyBase64 { get; init; }

    /// <summary>Run only: the script's entry type (the class the compiler wraps the script in).</summary>
    public string? EntryTypeName { get; init; }

    /// <summary>Run only: the static method to invoke on the entry type.</summary>
    public string EntryMethodName { get; init; } = "Run";

    /// <summary>Run only: how long the probe waits for the script before answering with <see cref="RunResponse.TimedOut"/>.</summary>
    public int TimeoutMs { get; init; } = 10_000;

    /// <summary>Run only: how deep the result serialiser walks an object graph.</summary>
    public int MaxDepth { get; init; } = ResultJson.DefaultMaxDepth;
}

/// <summary>The answer to a <see cref="ProbeRequestKind.Hello"/>.</summary>
public sealed record HelloResponse
{
    /// <summary>The host's runtime, e.g. <c>.NET 9.0.4</c>.</summary>
    public string RuntimeVersion { get; init; } = "";

    /// <summary>The host's process id.</summary>
    public int ProcessId { get; init; }

    /// <summary>The host's entry assembly name, if the runtime reports one.</summary>
    public string? EntryAssembly { get; init; }

    /// <summary>File paths of the host's loaded assemblies (dynamic and in-memory ones skipped).
    /// A client compiles scripts against these.</summary>
    public IReadOnlyList<string> LoadedAssemblyPaths { get; init; } = [];

    /// <summary>Names of loaded assemblies that have no file path (a single-file bundle). A client
    /// resolves these from the shared framework directory.</summary>
    public IReadOnlyList<string> LoadedAssemblyNames { get; init; } = [];

    /// <summary>True once the probe has found the host's dependency-injection root.</summary>
    public bool DiRootCaptured { get; init; }

    /// <summary>Collectible load contexts not yet unloaded (a timed-out script holds one open).</summary>
    public int LiveContexts { get; init; }

    /// <summary>The probe's own version, so a stale agent is visible.</summary>
    public string SondeVersion { get; init; } = "";
}

/// <summary>The eval in progress, if any, on a <see cref="StatusResponse"/>.</summary>
public sealed record RunningEvalInfo
{
    /// <summary>When the running eval started.</summary>
    public DateTimeOffset StartedAtUtc { get; init; }
}

/// <summary>The answer to a <see cref="ProbeRequestKind.Status"/>.</summary>
public sealed record StatusResponse
{
    /// <summary>The host's process id.</summary>
    public int ProcessId { get; init; }

    /// <summary>True once the probe has found the host's dependency-injection root.</summary>
    public bool DiRootCaptured { get; init; }

    /// <summary>Collectible load contexts not yet unloaded.</summary>
    public int LiveContexts { get; init; }

    /// <summary>The eval in progress, or null.</summary>
    public RunningEvalInfo? RunningEval { get; init; }

    /// <summary>Descriptions of the active method hooks.</summary>
    public IReadOnlyList<string> Hooks { get; init; } = [];

    /// <summary>Names of the DiagnosticListeners being tapped.</summary>
    public IReadOnlyList<string> Taps { get; init; } = [];
}

/// <summary>The answer to a <see cref="ProbeRequestKind.Run"/>.</summary>
public sealed record RunResponse
{
    /// <summary>The script's return value, serialised with depth and size caps. Null when the script returned null or nothing.</summary>
    public JsonNode? Result { get; init; }

    /// <summary>Lines the script wrote with <c>g.Log</c>.</summary>
    public IReadOnlyList<string> Logs { get; init; } = [];

    /// <summary>The script's exception rendered as text (type, message, stack), or null.</summary>
    public string? Exception { get; init; }

    /// <summary>Wall time of the script.</summary>
    public long ElapsedMs { get; init; }

    /// <summary>True when the timeout tripped before the script returned. The script's thread may
    /// still be running: a runaway eval cannot be killed.</summary>
    public bool TimedOut { get; init; }

    /// <summary>Collectible load contexts not yet unloaded.</summary>
    public int LiveContexts { get; init; }

    /// <summary>Set when the request was refused because an eval is already running. One eval at a time.</summary>
    public DateTimeOffset? BusySinceUtc { get; init; }

    /// <summary>A protocol-level error that is not a script exception: entry type not found, malformed request.</summary>
    public string? ProtocolError { get; init; }
}
