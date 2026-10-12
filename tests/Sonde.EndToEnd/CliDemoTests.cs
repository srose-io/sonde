using System.Diagnostics;
using System.Text;

namespace Sonde.EndToEnd;

/// <summary>Drives the README's sixty-second demo through the real CLI: `sonde run`, then `sonde eval`.</summary>
[Trait("Category", "EndToEnd")]
public sealed class CliDemoTests : IAsyncLifetime
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "sonde-demo-home-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly StringBuilder _runOutput = new();
    private Process _run = null!;
    private string _endpoint = null!;

    private string RunOutput
    {
        get
        {
            lock (_runOutput)
            {
                return _runOutput.ToString();
            }
        }
    }

    private static string CliDll =>
        Path.Combine(Repo.Root, "artifacts", "bin", "Sonde.Cli", Repo.Configuration + "_net9.0", "Sonde.Cli.dll");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_home);
        _endpoint = Launch.EndpointFor("web", _home, Path.GetTempPath());

        // sonde run --name web -- dotnet MinimalWeb.dll
        var start = Cli("run", "--name", "web", "--", "dotnet", Repo.SampleDll("MinimalWeb"));
        start.WorkingDirectory = Path.GetDirectoryName(Repo.SampleDll("MinimalWeb"));
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        _run = Process.Start(start)!;
        _run.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_runOutput) _runOutput.AppendLine(e.Data); };
        _run.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_runOutput) _runOutput.AppendLine(e.Data); };
        _run.BeginOutputReadLine();
        _run.BeginErrorReadLine();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await ProbeClient.IsListeningAsync(_endpoint, TimeSpan.FromMilliseconds(300)))
        {
            Assert.False(_run.HasExited, "sonde run exited early:\n" + RunOutput);
            Assert.True(DateTime.UtcNow < deadline, "the app launched by sonde run never answered:\n" + RunOutput);
            await Task.Delay(100);
        }
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (!_run.HasExited)
                _run.Kill(entireProcessTree: true);
            await _run.WaitForExitAsync();
        }
        finally
        {
            _run.Dispose();
        }
    }

    [Fact]
    public async Task SondeEval_InlineScript_PrintsTheValue()
    {
        var (code, output, _) = await RunCliAsync("eval", "web", "return g.Resolve<Microsoft.Extensions.Configuration.IConfiguration>()[\"Greeting\"];");
        Assert.Equal(0, code);
        Assert.Equal("\"configured hello\"", output.Trim());
    }

    [Fact]
    public async Task SondeEval_ScriptFile_HooksAMethod()
    {
        var (code, output, _) = await RunCliAsync("eval", "web", "@" + Path.Combine(Repo.SampleProject("MinimalWeb"), "scripts", "hook-method.cs"));
        Assert.Equal(0, code);
        Assert.Contains("\"hooked\":", output, StringComparison.Ordinal);
        Assert.Contains("CounterService.Increment", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SondeLs_ShowsTheTargetUp()
    {
        var (code, output, _) = await RunCliAsync("ls");
        Assert.Equal(0, code);
        Assert.Contains("up    web", output, StringComparison.Ordinal);
    }

    private ProcessStartInfo Cli(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(CliDll);
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        start.Environment[Launch.HomeVariable] = _home;
        return start;
    }

    private async Task<(int Code, string Out, string Err)> RunCliAsync(params string[] args)
    {
        using var process = Process.Start(Cli(args))!;
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) error.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        return (process.ExitCode, output.ToString(), error.ToString());
    }
}
