using Sonde.Probe;

namespace Sonde.Tests;

public class SondeClientTests(LoopbackFixture loopback) : IClassFixture<LoopbackFixture>
{
    private SondeClient Client => new(loopback.Endpoint);

    [Fact]
    public async Task HelloDescribesThisProcess()
    {
        // This test host has no DI container and no DI abstractions assembly on disk: the probe
        // must answer anyway, and only load those types when a script asks for g.Services.
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "Microsoft.Extensions.DependencyInjection.Abstractions.dll")),
            "this suite proves the no-DI host; keep the DI abstractions out of its output");
        var hello = await Client.HelloAsync();
        Assert.False(hello.DiRootCaptured);
        Assert.Equal(Environment.ProcessId, hello.ProcessId);
        Assert.Contains(hello.LoadedAssemblyPaths, p => p.EndsWith("Sonde.Probe.dll", StringComparison.Ordinal));
        Assert.StartsWith("0.1.0", hello.SondeVersion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusReportsNoRunningEval()
    {
        var status = await Client.StatusAsync();
        Assert.Equal(Environment.ProcessId, status.ProcessId);
        Assert.Null(status.RunningEval);
    }

    [Fact]
    public async Task EvalCompilesRunsAndReturnsJson()
    {
        var result = await Client.EvalAsync("g.Log(\"hi\"); return new { Sum = 1 + 2, Name = \"x\" };");
        Assert.Equal(EvalOutcome.Completed, result.Outcome);
        Assert.Equal(3, result.Result!["Sum"]!.GetValue<int>());
        Assert.Equal(["hi"], result.Logs);
        Assert.Equal(0, result.LiveContexts);
    }

    [Fact]
    public async Task EvalReportsCompileErrorsInScriptLines()
    {
        var result = await Client.EvalAsync("return nope;");
        Assert.Equal(EvalOutcome.CompileFailed, result.Outcome);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(1, error.Line);
        Assert.Equal("CS0103", error.Id);
    }

    [Fact]
    public async Task EvalReportsAScriptException()
    {
        var result = await Client.EvalAsync("throw new InvalidOperationException(\"boom\");");
        Assert.Equal(EvalOutcome.ScriptThrew, result.Outcome);
        Assert.Contains("boom", result.Exception, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvalReportsATimeout()
    {
        var result = await Client.EvalAsync("await Task.Delay(2000, CancellationToken.None); return 1;", new EvalOptions { TimeoutMs = 200 });
        Assert.Equal(EvalOutcome.TimedOut, result.Outcome);
        // Let the runaway finish so later tests are not refused as busy.
        await Task.Delay(2200);
    }

    [Fact]
    public async Task EvalSeesTheProbesGlobalsAndVars()
    {
        var token = Guid.NewGuid().ToString("N");
        await Client.EvalAsync($"g.Vars[\"loop\"] = \"{token}\"; return null;");
        var result = await Client.EvalAsync("return g.Vars[\"loop\"];");
        Assert.Equal(token, result.Result!.GetValue<string>());
        Assert.Equal(token, SondeHost.Vars["loop"]);
    }

    [Fact]
    public async Task UnreachableEndpointIsAnOutcomeNotAThrow()
    {
        var missing = OperatingSystem.IsWindows() ? "sonde-nothing-here" : Path.Combine(Path.GetTempPath(), "sonde-nothing-here.sock");
        var result = await new SondeClient(missing).EvalAsync("return 1;");
        Assert.Equal(EvalOutcome.Unreachable, result.Outcome);
        Assert.Contains("sonde env", result.Error, StringComparison.Ordinal);
        await Assert.ThrowsAsync<SondeUnreachableException>(() => new SondeClient(missing).HelloAsync());
    }

    [Fact]
    public void OptionsAreClamped()
    {
        Assert.Equal(EvalOptions.MinTimeoutMs, new EvalOptions { TimeoutMs = 1 }.ClampedTimeoutMs);
        Assert.Equal(EvalOptions.MaxTimeoutMs, new EvalOptions { TimeoutMs = int.MaxValue }.ClampedTimeoutMs);
        Assert.Equal(1, new EvalOptions { MaxDepth = -4 }.ClampedMaxDepth);
        Assert.Equal(EvalOptions.MaxMaxDepth, new EvalOptions { MaxDepth = 99 }.ClampedMaxDepth);
    }
}
