namespace Sonde.EndToEnd;

/// <summary>The worker has no ASP.NET: the DI root must come from the container's constructor.</summary>
[Trait("Category", "EndToEnd")]
public sealed class WorkerTests : IAsyncLifetime
{
    private SampleProcess _app = null!;

    public async Task InitializeAsync() => _app = await SampleProcess.LaunchAsync("Worker");

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task DiRootIsCapturedWithoutAnyRequest()
    {
        var hello = await _app.Client.HelloAsync();
        Assert.Equal("Worker", hello.EntryAssembly);
        Assert.True(hello.DiRootCaptured, "the Harmony patch on ServiceProvider's constructor should have captured the root");
    }

    [Fact]
    public async Task Script_DiRootCaptured()
    {
        var result = await Eval("di-root-captured");
        Assert.Equal("Worker", result.Result!["ApplicationName"]!.GetValue<string>());
    }

    [Fact]
    public async Task Script_ReadPrivateState()
    {
        await Task.Delay(300);
        var result = await Eval("read-private-state");
        Assert.True(result.Result!["ticks"]!.GetValue<long>() > 0);
        Assert.True(result.Result["running"]!.GetValue<bool>());
    }

    [Fact]
    public async Task SecondListenerOnSameEndpoint_StaysDormant()
    {
        // A second process given the same endpoint (a child that inherited it, say) must not
        // steal the socket: the first keeps answering, the second says so and goes quiet.
        await using var second = await SampleProcess.LaunchAsync("Worker", endpoint: _app.Endpoint, readyTimeout: TimeSpan.FromSeconds(5));
        await Task.Delay(1500);
        var hello = await _app.Client.HelloAsync();
        Assert.Equal(_app.Pid, hello.ProcessId);
        Assert.Contains("another probe already listens", second.StandardError, StringComparison.Ordinal);
    }

    private async Task<EvalResult> Eval(string script)
    {
        var result = await _app.Client.EvalAsync(Repo.Script("Worker", script));
        Assert.True(result.Outcome == EvalOutcome.Completed, $"{script}: {result.Outcome} {result.Exception}{string.Join("; ", result.Diagnostics)}{result.Error}");
        return result;
    }
}
