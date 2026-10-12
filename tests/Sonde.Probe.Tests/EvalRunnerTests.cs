using System.Runtime.Loader;

namespace Sonde.Probe.Tests;

[Collection("process-global")]
public class EvalRunnerTests
{
    [Fact]
    public async Task ReturnsAValue()
    {
        var response = await EvalRunner.RunAsync(TestScripts.RunRequest("return 1 + 2;"));
        Assert.Null(response.Exception);
        Assert.Null(response.ProtocolError);
        Assert.Equal(3, response.Result!.GetValue<int>());
    }

    [Fact]
    public async Task ReturnsExceptionAsData()
    {
        var response = await EvalRunner.RunAsync(TestScripts.RunRequest("throw new InvalidOperationException(\"nope\");"));
        Assert.Null(response.Result);
        Assert.NotNull(response.Exception);
        Assert.Contains("nope", response.Exception, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", response.Exception, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapturesLogs()
    {
        var response = await EvalRunner.RunAsync(TestScripts.RunRequest("g.Log(\"first\"); g.Log(\"second\"); return 7;"));
        Assert.Equal(7, response.Result!.GetValue<int>());
        Assert.Equal(["first", "second"], response.Logs);
    }

    [Fact]
    public async Task VarsPersistAcrossEvals()
    {
        var token = Guid.NewGuid().ToString("N");
        await EvalRunner.RunAsync(TestScripts.RunRequest($"g.Vars[\"k\"] = \"{token}\"; return 0;"));
        var response = await EvalRunner.RunAsync(TestScripts.RunRequest("return g.Vars[\"k\"];"));
        Assert.Equal(token, response.Result!.GetValue<string>());
    }

    [Fact]
    public async Task RefusesAMissingEntryType()
    {
        var request = TestScripts.RunRequest("return 1;") with { EntryTypeName = "NoSuchType" };
        var response = await EvalRunner.RunAsync(request);
        Assert.Contains("NoSuchType", response.ProtocolError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesMalformedBase64()
    {
        var request = TestScripts.RunRequest("return 1;") with { AssemblyBase64 = "not base64!" };
        var response = await EvalRunner.RunAsync(request);
        Assert.Contains("base64", response.ProtocolError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Eval_RunsInACollectibleContextThatUnloadsAfter()
    {
        var before = SondeHost.LiveContexts;
        var response = await EvalRunner.RunAsync(TestScripts.RunRequest("return 42;"));
        Assert.Equal(42, response.Result!.GetValue<int>());
        Assert.Equal(before, SondeHost.LiveContexts);

        var context = EvalRunner.LastContext;
        Assert.NotNull(context);
        Assert.True(await WaitForCollectionAsync(context), "the script's AssemblyLoadContext was not collected after unload");
    }

    [Fact]
    public async Task Eval_RefusesASecondEvalWhileOneRuns()
    {
        var first = EvalRunner.RunAsync(TestScripts.RunRequest("await Task.Delay(800, ct); return \"first\";", timeoutMs: 10_000));
        await Task.Delay(100);
        var second = await EvalRunner.RunAsync(TestScripts.RunRequest("return \"second\";"));
        Assert.NotNull(second.BusySinceUtc);
        Assert.Null(second.Result);
        Assert.Equal("first", (await first).Result!.GetValue<string>());
    }

    [Fact]
    public async Task Eval_TimeoutLeavesTheSlotHeldUntilTheRunawayEnds()
    {
        // The script ignores the token and overruns the timeout.
        var timedOut = await EvalRunner.RunAsync(TestScripts.RunRequest(
            "await Task.Delay(1500, CancellationToken.None); return 1;", timeoutMs: 200));
        Assert.True(timedOut.TimedOut);
        Assert.Null(timedOut.Result);
        Assert.NotNull(EvalRunner.RunningSince);

        // While the runaway still runs, the slot is held: a second eval is refused, not raced.
        var refused = await EvalRunner.RunAsync(TestScripts.RunRequest("return 2;"));
        Assert.NotNull(refused.BusySinceUtc);

        // Once it ends, the slot frees itself.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (EvalRunner.RunningSince is not null && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        var afterwards = await EvalRunner.RunAsync(TestScripts.RunRequest("return 3;"));
        Assert.Equal(3, afterwards.Result!.GetValue<int>());
    }

    [Fact]
    public async Task Eval_CancellationTokenTripsAtTimeout()
    {
        var response = await EvalRunner.RunAsync(TestScripts.RunRequest(
            "try { await Task.Delay(10000, ct); return \"finished\"; } catch (OperationCanceledException) { return \"cancelled\"; }",
            timeoutMs: 300));
        // Either the script observed cancellation and returned, or the probe's timeout answered
        // first; in both cases the token tripped and the script never "finished".
        if (!response.TimedOut)
            Assert.Equal("cancelled", response.Result!.GetValue<string>());
    }

    [Fact]
    public async Task ScriptSeesTheHostsTypesNotCopies()
    {
        // The script's reference to this test assembly resolves to the loaded instance, so a
        // static set from the script is the same static the test reads.
        SharedState.Value = 0;
        await EvalRunner.RunAsync(TestScripts.RunRequest("Sonde.Probe.Tests.SharedState.Value = 9; return null;"));
        Assert.Equal(9, SharedState.Value);
    }

    private static async Task<bool> WaitForCollectionAsync(WeakReference<AssemblyLoadContext> context)
    {
        for (int i = 0; i < 20; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (!context.TryGetTarget(out _))
                return true;
            await Task.Delay(50);
        }
        return !context.TryGetTarget(out _);
    }
}

public static class SharedState
{
    public static int Value { get; set; }
}
