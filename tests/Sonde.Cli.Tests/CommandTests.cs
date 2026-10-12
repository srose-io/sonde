using System.CommandLine;
using System.Text.Json.Nodes;
using Sonde.Tests;

namespace Sonde.Cli.Tests;

public class CommandTests(LoopbackFixture loopback) : IClassFixture<LoopbackFixture>
{
    private static async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await SondeCommand.Parse(args).InvokeAsync(new InvocationConfiguration { Output = output, Error = error });
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task HelpListsEveryCommandWithASentence()
    {
        var (code, output, _) = await RunAsync("--help");
        Assert.Equal(0, code);
        Assert.Contains("Usage:\n  sonde [command]", output.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        foreach (var command in new[] { "env", "run", "hello", "status", "eval", "ls" })
            Assert.Contains(command, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnvPrintsShellExportsAndWritesTheTemplate()
    {
        var home = NewHome();
        var (code, output, error) = await RunAsync("env", "--name", "demo");
        Assert.Equal(0, code);
        Assert.Contains("export DOTNET_STARTUP_HOOKS='", output, StringComparison.Ordinal);
        Assert.Contains("Sonde.Probe.dll", output, StringComparison.Ordinal);
        Assert.Contains("export SONDE_ENDPOINT='", output, StringComparison.Ordinal);
        Assert.Contains("export UserRuntimeConfig='", output, StringComparison.Ordinal);
        Assert.Contains("# endpoint:", error, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(home, "templates", "demo.runtimeconfig.template.json")));
    }

    [Theory]
    [InlineData("powershell", "$env:SONDE_ENDPOINT = '")]
    [InlineData("dotenv", "SONDE_ENDPOINT=")]
    [InlineData("json", "\"SONDE_ENDPOINT\":")]
    public async Task EnvRendersOtherFormats(string format, string marker)
    {
        NewHome();
        var (code, output, _) = await RunAsync("env", "--name", "demo", "--format", format);
        Assert.Equal(0, code);
        Assert.Contains(marker, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelloAndStatusPrintJson()
    {
        var (code, output, _) = await RunAsync("hello", loopback.Endpoint);
        Assert.Equal(0, code);
        Assert.Equal(Environment.ProcessId, JsonNode.Parse(output)!["processId"]!.GetValue<int>());

        (code, output, _) = await RunAsync("status", loopback.Endpoint);
        Assert.Equal(0, code);
        Assert.Equal(Environment.ProcessId, JsonNode.Parse(output)!["processId"]!.GetValue<int>());
    }

    [Fact]
    public async Task EvalPrintsTheResultAndLogsGoToStderr()
    {
        var (code, output, error) = await RunAsync("eval", loopback.Endpoint, "g.Log(\"note\"); return new[] { 1, 2, 3 };");
        Assert.Equal(ExitCode.Ok, code);
        Assert.Equal(3, JsonNode.Parse(output)!.AsArray().Count);
        Assert.Contains("log: note", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvalReadsAScriptFile()
    {
        var file = Path.Combine(Path.GetTempPath(), "sonde-cli-" + Guid.NewGuid().ToString("N")[..8] + ".cs");
        await File.WriteAllTextAsync(file, "return 40 + 2;");
        var (code, output, _) = await RunAsync("eval", loopback.Endpoint, "@" + file);
        Assert.Equal(ExitCode.Ok, code);
        Assert.Equal("42", output.Trim());
    }

    [Fact]
    public async Task EvalJsonPrintsTheWholeResult()
    {
        var (code, output, _) = await RunAsync("eval", loopback.Endpoint, "return 1;", "--json");
        Assert.Equal(ExitCode.Ok, code);
        var json = JsonNode.Parse(output)!;
        Assert.Equal("completed", json["outcome"]!.GetValue<string>());
        Assert.Equal(1, json["result"]!.GetValue<int>());
    }

    [Fact]
    public async Task EvalExitCodesFollowTheOutcome()
    {
        var (code, _, error) = await RunAsync("eval", loopback.Endpoint, "return nope;");
        Assert.Equal(ExitCode.NotRun, code);
        Assert.Contains("did not compile", error, StringComparison.Ordinal);
        Assert.Contains("script.cs(1,", error, StringComparison.Ordinal);

        (code, _, error) = await RunAsync("eval", loopback.Endpoint, "throw new Exception(\"bang\");");
        Assert.Equal(ExitCode.ScriptFailed, code);
        Assert.Contains("bang", error, StringComparison.Ordinal);

        var missing = OperatingSystem.IsWindows() ? "sonde-nothing" : Path.Combine(Path.GetTempPath(), "sonde-nothing.sock");
        (code, _, error) = await RunAsync("eval", missing, "return 1;");
        Assert.Equal(ExitCode.NotRun, code);
        Assert.Contains("No probe answered", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ExitCodeMappingCoversEveryOutcome()
    {
        foreach (var outcome in Enum.GetValues<EvalOutcome>())
            Assert.Contains(SondeCommand.ExitCodeFor(outcome), new[] { ExitCode.Ok, ExitCode.ScriptFailed, ExitCode.NotRun });
    }

    [Fact]
    public async Task LsShowsWhatAnswers()
    {
        if (OperatingSystem.IsWindows())
            return;
        var home = NewHome();
        File.WriteAllBytes(Path.Combine(home, "dead.sock"), []);
        var (code, output, _) = await RunAsync("ls");
        Assert.Equal(0, code);
        Assert.Contains("down  dead", output, StringComparison.Ordinal);

        (code, output, _) = await RunAsync("ls", "--json");
        Assert.Equal(0, code);
        var row = Assert.Single(JsonNode.Parse(output)!.AsArray());
        Assert.False(row!["listening"]!.GetValue<bool>());
    }

    [Fact]
    public void TargetsResolveNamesToEndpointsAndPassPathsThrough()
    {
        NewHome();
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("sonde-x", Targets.Resolve("sonde-x"));
            Assert.Equal("sonde-demo", Targets.Resolve("demo"));
            return;
        }
        Assert.Equal("/tmp/a.sock", Targets.Resolve("/tmp/a.sock"));
        Assert.Equal(Launch.EndpointFor("demo"), Targets.Resolve("demo"));
        Assert.EndsWith("/demo.sock", Targets.Resolve("demo"), StringComparison.Ordinal);
    }

    private static string NewHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "sonde-home-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable(Launch.HomeVariable, home);
        return home;
    }
}
