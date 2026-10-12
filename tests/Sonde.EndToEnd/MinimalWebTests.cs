
namespace Sonde.EndToEnd;

/// <summary>Launches the web sample once and runs every script in its scripts/ folder: this is the README demo.</summary>
[Trait("Category", "EndToEnd")]
public sealed class MinimalWebTests : IAsyncLifetime
{
    private SampleProcess _app = null!;

    public async Task InitializeAsync() => _app = await SampleProcess.LaunchAsync("MinimalWeb", web: true);

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task LaunchedWithEnvironment_ProbeAnswersHello()
    {
        var hello = await _app.Client.HelloAsync();
        Assert.Equal(_app.Pid, hello.ProcessId);
        Assert.Equal("MinimalWeb", hello.EntryAssembly);
        Assert.Contains(hello.LoadedAssemblyPaths, p => p.EndsWith("Microsoft.AspNetCore.dll", StringComparison.Ordinal));
        if (SampleProcess.RequestedRuntimeMajor is { } major)
            Assert.Contains($".NET {major}.", hello.RuntimeVersion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Script_ReadConfig()
    {
        var result = await Eval("read-config");
        Assert.Equal("configured hello", result.Result!.GetValue<string>());
    }

    [Fact]
    public async Task Script_ResolveService_ReadsPrivateState()
    {
        await _app.GetAsync("/count");
        await _app.GetAsync("/count");
        var result = await Eval("resolve-service");
        Assert.Equal("MinimalWeb.CounterService", result.Result!["Type"]!.GetValue<string>());
        Assert.True(result.Result["Count"]!.GetValue<int>() >= 2);
    }

    [Fact]
    public async Task Script_HookMethod_RewritesAResultThenRestores()
    {
        var result = await Eval("hook-method");
        var before = result.Result!["before"]!.GetValue<int>();
        Assert.Equal(before + 1 + 1000, result.Result["hooked"]!.GetValue<int>());
        Assert.Equal(before + 2, result.Result["after"]!.GetValue<int>());
        Assert.Contains("CounterService.Increment", result.Result["description"]!.GetValue<string>(), StringComparison.Ordinal);

        var status = await _app.Client.StatusAsync();
        Assert.Empty(status.Hooks);
    }

    [Fact]
    public async Task Script_TapHttp_SeesTheRequest()
    {
        await _app.Client.EvalAsync($"g.Vars[\"baseUrl\"] = \"{_app.BaseUrl}\"; return null;");
        var result = await Eval("tap-http");
        Assert.Equal("hello from MinimalWeb", result.Result!["body"]!.GetValue<string>());
        var names = result.Result["eventNames"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Contains(names, n => n.Contains("System.Net.Http", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Script_VarsPersist_AcrossEvals()
    {
        var first = await Eval("vars-persist");
        Assert.Equal("nothing stored", first.Result!.GetValue<string>());
        await _app.Client.EvalAsync("g.Vars[\"marker\"] = \"kept\"; return null;");
        var second = await Eval("vars-persist");
        Assert.Equal("kept", second.Result!.GetValue<string>());
    }

    [Fact]
    public async Task ChildProcessOfProbedHost_DoesNotInheritTheProbe()
    {
        var result = await _app.Client.EvalAsync("""
            var child = new System.Diagnostics.ProcessStartInfo("dotnet", "--version");
            return new {
                Hooks = Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS"),
                Endpoint = Environment.GetEnvironmentVariable("SONDE_ENDPOINT"),
                ChildSeesHooks = child.Environment.ContainsKey("DOTNET_STARTUP_HOOKS"),
                ChildSeesEndpoint = child.Environment.ContainsKey("SONDE_ENDPOINT"),
            };
            """);
        Assert.Equal(EvalOutcome.Completed, result.Outcome);
        Assert.Null(result.Result!["Hooks"]);
        Assert.Null(result.Result["Endpoint"]);
        Assert.False(result.Result["ChildSeesHooks"]!.GetValue<bool>());
        Assert.False(result.Result["ChildSeesEndpoint"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CompileErrorsComeBackInScriptLines()
    {
        var result = await _app.Client.EvalAsync("var x = 1;\nreturn y;");
        Assert.Equal(EvalOutcome.CompileFailed, result.Outcome);
        Assert.Equal(2, Assert.Single(result.Diagnostics).Line);
    }

    private async Task<EvalResult> Eval(string script)
    {
        var result = await _app.Client.EvalAsync(Repo.Script("MinimalWeb", script));
        Assert.True(result.Outcome == EvalOutcome.Completed, $"{script}: {result.Outcome} {result.Exception}{string.Join("; ", result.Diagnostics)}{result.Error}");
        return result;
    }
}
