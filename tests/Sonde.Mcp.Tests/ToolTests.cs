using System.Text.Json.Nodes;
using Sonde.Tests;

namespace Sonde.Mcp.Tests;

public class ToolTests(LoopbackFixture loopback) : IClassFixture<LoopbackFixture>
{
    [Fact]
    public async Task HelloDescribesTheTarget()
    {
        var json = JsonNode.Parse(await SondeTools.HelloAsync(loopback.Endpoint, CancellationToken.None))!;
        Assert.Equal(Environment.ProcessId, json["processId"]!.GetValue<int>());
    }

    [Fact]
    public async Task StatusReportsTheProbe()
    {
        var json = JsonNode.Parse(await SondeTools.StatusAsync(loopback.Endpoint, CancellationToken.None))!;
        Assert.Equal(Environment.ProcessId, json["processId"]!.GetValue<int>());
        Assert.Null(json["runningEval"]);
    }

    [Fact]
    public async Task EvalAnswersWithAnOutcomeAndResult()
    {
        var json = JsonNode.Parse(await SondeTools.EvalAsync(loopback.Endpoint, "g.Log(\"hi\"); return 6 * 7;", CancellationToken.None))!;
        Assert.Equal("completed", json["outcome"]!.GetValue<string>());
        Assert.Equal(42, json["result"]!.GetValue<int>());
        Assert.Equal("hi", json["logs"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task ACompileErrorIsAnOutcomeNotAThrow()
    {
        var json = JsonNode.Parse(await SondeTools.EvalAsync(loopback.Endpoint, "return nope;", CancellationToken.None))!;
        Assert.Equal("compileFailed", json["outcome"]!.GetValue<string>());
        Assert.Equal("CS0103", json["diagnostics"]![0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnUnreachableTargetIsAnOutcomeNotAThrow()
    {
        var missing = OperatingSystem.IsWindows() ? "sonde-nothing" : Path.Combine(Path.GetTempPath(), "sonde-nothing.sock");
        var json = JsonNode.Parse(await SondeTools.HelloAsync(missing, CancellationToken.None))!;
        Assert.Equal("unreachable", json["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task ListTargetsAnswersAnArray()
    {
        var home = Path.Combine(Path.GetTempPath(), "sonde-mcp-home-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable(Launch.HomeVariable, home);
        var json = JsonNode.Parse(await SondeTools.ListTargetsAsync(CancellationToken.None))!;
        if (OperatingSystem.IsWindows())
        {
            // Pipes are machine-wide, so the fixtures' own test pipes show up; each must be live.
            Assert.All(json.AsArray(), row => Assert.True(row!["listening"]!.GetValue<bool>()));
            return;
        }
        Assert.Empty(json.AsArray());
    }

    [Fact]
    public void TheScriptSurfaceResourceIsTheDoc()
    {
        var text = SondeResources.ScriptSurface();
        Assert.Contains("g.Hook", text, StringComparison.Ordinal);
        Assert.Contains("g.Tap", text, StringComparison.Ordinal);
    }
}
