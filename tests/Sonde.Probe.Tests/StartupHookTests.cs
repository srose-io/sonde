using System.Net.Sockets;

namespace Sonde.Probe.Tests;

/// <summary>The charter's non-negotiables, one test each. The names are the scars.</summary>
[Collection("process-global")]
public class StartupHookTests
{
    [Fact]
    public void Scrub_RemovesStartupHooksAndEndpointFromOwnEnvironment()
    {
        Environment.SetEnvironmentVariable(Bootstrap.StartupHooksVariable, "/x/Sonde.Probe.dll");
        Environment.SetEnvironmentVariable(Bootstrap.EndpointVariable, "/x/sonde.sock");

        Bootstrap.ScrubEnvironment();

        Assert.Null(Environment.GetEnvironmentVariable(Bootstrap.StartupHooksVariable));
        Assert.Null(Environment.GetEnvironmentVariable(Bootstrap.EndpointVariable));
        // A child started now inherits neither.
        var childEnvironment = new System.Diagnostics.ProcessStartInfo("true").Environment;
        Assert.False(childEnvironment.ContainsKey(Bootstrap.StartupHooksVariable));
        Assert.False(childEnvironment.ContainsKey(Bootstrap.EndpointVariable));
        Bootstrap.ScrubEnvironment(); // idempotent
    }

    [Theory]
    [InlineData("/nix/store/x-dotnet-sdk-9.0.312/share/dotnet/sdk/9.0.312/dotnet.dll", true)]
    [InlineData("/home/u/.dotnet/sdk/10.0.401/DotnetTools/dotnet-watch/10.0.401/tools/net10.0/any/dotnet-watch.dll", true)]
    [InlineData("/home/u/.dotnet/sdk/10.0.401/MSBuild.dll", true)]
    [InlineData("/home/u/.dotnet/sdk/10.0.401/Roslyn/bincore/VBCSCompiler.dll", true)]
    [InlineData("C:\\Program Files\\dotnet\\sdk\\9.0.312\\dotnet.dll", true)]
    [InlineData("/srv/app/bin/Debug/net10.0/MyApp.dll", false)]
    [InlineData("/home/u/code/sonde/samples/MinimalWeb/bin/Release/net9.0/MinimalWeb.dll", false)]
    [InlineData("/opt/tools/releases/abc/tool.dll", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Hook_IsANoOpForSdkTooling(string? entryAssemblyLocation, bool tooling)
    {
        Assert.Equal(tooling, Bootstrap.IsSdkTooling(entryAssemblyLocation));
        if (tooling)
            Assert.Equal(HookAction.PassThrough, Bootstrap.Plan(entryAssemblyLocation, "/x/sonde.sock"));
    }

    [Fact]
    public void Hook_IsDormantWithNoEndpointVariable()
    {
        Assert.Equal(HookAction.Dormant, Bootstrap.Plan("/srv/app/MyApp.dll", null));
        Assert.Equal(HookAction.Dormant, Bootstrap.Plan("/srv/app/MyApp.dll", ""));
        Assert.Equal(HookAction.Listen, Bootstrap.Plan("/srv/app/MyApp.dll", "/x/sonde.sock"));
    }

    [Fact]
    public void Hook_PassThroughLeavesTheEnvironmentForTheChild()
    {
        Environment.SetEnvironmentVariable(Bootstrap.StartupHooksVariable, "/x/Sonde.Probe.dll");
        Environment.SetEnvironmentVariable(Bootstrap.EndpointVariable, "/x/sonde.sock");
        try
        {
            var action = Bootstrap.Initialize("/usr/share/dotnet/sdk/9.0.312/dotnet.dll", "/x/sonde.sock");
            Assert.Equal(HookAction.PassThrough, action);
            // The app this tool launches must still see both.
            Assert.Equal("/x/Sonde.Probe.dll", Environment.GetEnvironmentVariable(Bootstrap.StartupHooksVariable));
            Assert.Equal("/x/sonde.sock", Environment.GetEnvironmentVariable(Bootstrap.EndpointVariable));
        }
        finally
        {
            Bootstrap.ScrubEnvironment();
        }
    }

    [Fact]
    public void Hook_RecordsTheEndpointBeforeScrubbingIt()
    {
        // The host reads its own endpoint from the probe, never from the environment.
        SondeHost.RecordEndpoint("/x/recorded.sock");
        Bootstrap.ScrubEnvironment();
        Assert.Null(Environment.GetEnvironmentVariable(Bootstrap.EndpointVariable));
        Assert.Equal("/x/recorded.sock", SondeHost.Endpoint);
    }

    [Fact]
    public void Hook_NeverThrows()
    {
        // An endpoint path longer than a Unix socket allows cannot be bound. The hook must return
        // normally: the failure is logged on the listener thread, never thrown before Main.
        var impossible = Path.Combine(Path.GetTempPath(), new string('x', 200), "sonde.sock");
        Environment.SetEnvironmentVariable(Bootstrap.EndpointVariable, impossible);
        try
        {
            var exception = Record.Exception(StartupHook.Initialize);
            Assert.Null(exception);
            Assert.Null(Environment.GetEnvironmentVariable(Bootstrap.EndpointVariable));
        }
        finally
        {
            Bootstrap.ScrubEnvironment();
        }
    }

    [Fact]
    public async Task Listener_DoesNotStealASocketThatStillAnswers()
    {
        if (OperatingSystem.IsWindows())
            return;
        var path = TempSocketPath();
        using var owner = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        owner.Bind(new UnixDomainSocketEndPoint(path));
        owner.Listen(16);
        try
        {
            Assert.True(await Listener.IsListeningAsync(path));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Listener.ServeAsync(path, cts.Token); // returns at once instead of binding

            Assert.False(cts.IsCancellationRequested, "ServeAsync should have returned without waiting for cancellation");
            Assert.True(File.Exists(path));
            Assert.True(await Listener.IsListeningAsync(path), "the original listener still owns the path");
        }
        finally
        {
            owner.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Listener_ReplacesAStaleSocketFile()
    {
        if (OperatingSystem.IsWindows())
            return;
        var path = TempSocketPath();
        // What a process that died without cleaning up leaves behind: a path nothing answers on.
        File.WriteAllBytes(path, []);
        Assert.False(await Listener.IsListeningAsync(path));

        using var cts = new CancellationTokenSource();
        var serving = Listener.ServeAsync(path, cts.Token);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!await Listener.IsListeningAsync(path) && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            Assert.True(await Listener.IsListeningAsync(path), "the probe bound the stale path");
        }
        finally
        {
            await cts.CancelAsync();
            await serving;
            File.Delete(path);
        }
    }

    [Fact]
    public void Harmony_IsAtLeast_2_4_2_AndIsTheBuildForThisRuntime()
    {
        // Force Harmony to load through the probe's resolver, then inspect what was loaded.
        var g = new ScriptGlobals([], CancellationToken.None);
        var handle = g.Hook<HookProbe>(nameof(HookProbe.Touch), postfix: _ => { });
        g.Unhook(handle);

        var harmony = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "0Harmony");
        var informational = harmony.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        var effective = Version.TryParse(informational?.Split('+', '-')[0], out var parsed) ? parsed : harmony.GetName().Version;
        Assert.NotNull(effective);
        Assert.True(effective >= new Version(2, 4, 2), $"Lib.Harmony {effective} is older than 2.4.2, which throws on CoreCLR 10.");

        var expected = Path.Combine("harmony", $"net{Environment.Version.Major}.0", "0Harmony.dll");
        Assert.EndsWith(expected, harmony.Location, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(8, "harmony/net8.0/0Harmony.dll")]
    [InlineData(9, "harmony/net9.0/0Harmony.dll")]
    [InlineData(10, "harmony/net10.0/0Harmony.dll")]
    [InlineData(11, "harmony/net10.0/0Harmony.dll")]
    public void HarmonyLocator_PicksTheHostsMajorOrTheNewestBelowIt(int major, string expected)
    {
        var shipped = new HashSet<string> { "/p/harmony/net8.0/0Harmony.dll", "/p/harmony/net9.0/0Harmony.dll", "/p/harmony/net10.0/0Harmony.dll" };
        Assert.Equal("/p/" + expected, HarmonyLocator.Pick("/p", major, shipped.Contains));
    }

    [Fact]
    public void HarmonyLocator_FallsBackToABareCopyThenNull()
    {
        Assert.Equal("/p/0Harmony.dll", HarmonyLocator.Pick("/p", 9, path => path == "/p/0Harmony.dll"));
        Assert.Null(HarmonyLocator.Pick("/p", 9, _ => false));
    }

    [Fact]
    public void Probe_ShipsHarmonyPerRuntimeAndNothingElse()
    {
        // The agent references only the shared framework plus Harmony; the DI abstractions it
        // compiles against come from the host at runtime and must not be shipped. Harmony ships
        // as its own per-runtime builds so a host on 8, 9 or 10 loads the one made for it.
        var dir = Path.GetDirectoryName(typeof(SondeHost).Assembly.Location)!;
        foreach (var major in new[] { 8, 9, 10 })
            Assert.True(File.Exists(Path.Combine(dir, "harmony", $"net{major}.0", "0Harmony.dll")), $"harmony/net{major}.0/0Harmony.dll must ship beside Sonde.Probe.dll");
        Assert.False(File.Exists(Path.Combine(dir, "Microsoft.Extensions.DependencyInjection.Abstractions.dll")) && !File.Exists(Path.Combine(dir, "Microsoft.Extensions.DependencyInjection.dll")),
            "the DI abstractions must not ship with the probe");
        var referenced = typeof(SondeHost).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        var allowed = new[] { "System.", "Microsoft.Extensions.DependencyInjection.Abstractions", "0Harmony", "netstandard", "mscorlib" };
        foreach (var name in referenced)
            Assert.True(allowed.Any(prefix => name!.StartsWith(prefix, StringComparison.Ordinal)), $"Sonde.Probe references {name}");
    }

    public sealed class HookProbe
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void Touch()
        {
        }
    }

    private static string TempSocketPath() =>
        Path.Combine(Path.GetTempPath(), "sonde-test-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
}

/// <summary>Tests that touch process-wide probe state run serially.</summary>
[CollectionDefinition("process-global", DisableParallelization = true)]
public sealed class ProcessGlobalFixture;
