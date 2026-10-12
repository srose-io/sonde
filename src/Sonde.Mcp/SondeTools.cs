using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace Sonde.Mcp;

/// <summary>The tools an agent sees. Each answers JSON text; an eval that did not run is not an error, it is an outcome.</summary>
[McpServerToolType]
internal static class SondeTools
{
    public const string Instructions =
        "Sonde runs C# inside a live .NET process that was started with the Sonde probe. " +
        "Call sonde_list_targets to see which processes answer, sonde_hello to learn what one has loaded, " +
        "then sonde_eval with C# statements ending in `return x;`. Read the sonde://script-surface resource " +
        "for what `g` offers (services, hooks, taps, persistent vars). A script runs with the host's full " +
        "rights and cannot be stopped once started; honour the cancellation token `ct` in long scripts.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [McpServerTool(Name = "sonde_list_targets", ReadOnly = true, Idempotent = true)]
    [Description("List the processes launched with the Sonde probe on this machine and whether each still answers.")]
    public static async Task<string> ListTargetsAsync(CancellationToken cancellationToken)
    {
        var targets = await Targets.ListAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(targets, Json);
    }

    [McpServerTool(Name = "sonde_hello", ReadOnly = true, Idempotent = true)]
    [Description("Describe the process behind a target: runtime, process id, entry assembly, loaded assemblies, whether its DI root was found, and the probe version.")]
    public static async Task<string> HelloAsync(
        [Description("A name given when the process was launched, or its endpoint (socket path or pipe name).")] string target,
        CancellationToken cancellationToken)
    {
        try
        {
            var hello = await new SondeClient(Targets.Resolve(target)).HelloAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(hello, Json);
        }
        catch (SondeUnreachableException ex)
        {
            return JsonSerializer.Serialize(new { outcome = "unreachable", error = ex.Message }, Json);
        }
    }

    [McpServerTool(Name = "sonde_status", ReadOnly = true, Idempotent = true)]
    [Description("Report a target's probe state: the eval in progress if any, live script contexts, active hooks and taps.")]
    public static async Task<string> StatusAsync(
        [Description("A name given when the process was launched, or its endpoint.")] string target,
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await new SondeClient(Targets.Resolve(target)).StatusAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(status, Json);
        }
        catch (SondeUnreachableException ex)
        {
            return JsonSerializer.Serialize(new { outcome = "unreachable", error = ex.Message }, Json);
        }
    }

    [McpServerTool(Name = "sonde_eval", Destructive = true)]
    [Description("Compile C# and run it inside the target process against its real services. The answer is JSON with an outcome (completed, scriptThrew, timedOut, compileFailed, busy, unreachable, protocolError), the result, the script's log lines, and timing.")]
    public static async Task<string> EvalAsync(
        [Description("A name given when the process was launched, or its endpoint.")] string target,
        [Description("C# statements ending in `return x;`. `g` is the script surface (see sonde://script-surface); `ct` is the cancellation token.")] string script,
        CancellationToken cancellationToken,
        [Description("Milliseconds to wait for the script. Default 10000, max 300000.")] int? timeoutMs = null,
        [Description("How many levels deep the result is serialised. Default 6, max 32.")] int? maxDepth = null)
    {
        var options = new EvalOptions
        {
            TimeoutMs = timeoutMs ?? EvalOptions.DefaultTimeoutMs,
            MaxDepth = maxDepth ?? Probe.ResultJson.DefaultMaxDepth,
        };
        var result = await new SondeClient(Targets.Resolve(target)).EvalAsync(script, options, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, Json);
    }
}

/// <summary>The resources an agent can read.</summary>
[McpServerResourceType]
internal static class SondeResources
{
    public const string ScriptSurfaceUri = "sonde://script-surface";

    [McpServerResource(UriTemplate = ScriptSurfaceUri, Name = "Sonde script surface", MimeType = "text/markdown")]
    [Description("What a script can reach through `g`: services, scopes, persistent vars, logging, method hooks and DiagnosticListener taps, with examples.")]
    public static string ScriptSurface()
    {
        using var stream = typeof(SondeResources).Assembly.GetManifestResourceStream("script-surface.md")
            ?? throw new InvalidOperationException("script-surface.md is not embedded in Sonde.Mcp.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
