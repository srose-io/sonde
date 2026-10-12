using System.Text;
using System.Text.Json.Nodes;

namespace Sonde.Tests;

public class LaunchTests
{
    [Fact]
    public void ApplyEnvironment_SetsBothVariables()
    {
        var env = new Dictionary<string, string?>();
        Launch.ApplyEnvironment(env, "/tmp/a.sock", "/p/Sonde.Probe.dll");
        Assert.Equal("/p/Sonde.Probe.dll", env[Launch.StartupHooksVariable]);
        Assert.Equal("/tmp/a.sock", env[Launch.EndpointVariable]);
        Assert.False(env.ContainsKey(Launch.UserRuntimeConfigVariable));
    }

    [Fact]
    public void ApplyEnvironment_AppendsToExistingHooksWithThePathSeparator_Once()
    {
        var env = new Dictionary<string, string?> { [Launch.StartupHooksVariable] = "/other/Hook.dll" };
        Launch.ApplyEnvironment(env, "/tmp/a.sock", "/p/Sonde.Probe.dll");
        Assert.Equal("/other/Hook.dll" + Path.PathSeparator + "/p/Sonde.Probe.dll", env[Launch.StartupHooksVariable]);
        Launch.ApplyEnvironment(env, "/tmp/a.sock", "/p/Sonde.Probe.dll");
        Assert.Equal("/other/Hook.dll" + Path.PathSeparator + "/p/Sonde.Probe.dll", env[Launch.StartupHooksVariable]);
    }

    [Fact]
    public void ApplyEnvironment_NamesTheRuntimeConfigTemplateWhenGiven()
    {
        var env = new Dictionary<string, string?>();
        Launch.ApplyEnvironment(env, "/tmp/a.sock", "/p/Sonde.Probe.dll", "/t/app.runtimeconfig.template.json");
        Assert.Equal("/t/app.runtimeconfig.template.json", env[Launch.UserRuntimeConfigVariable]);
    }

    [Fact]
    public void ApplyEnvironment_DefaultsToTheProbeBesideTheLibrary()
    {
        var env = new Dictionary<string, string?>();
        Launch.ApplyEnvironment(env, "/tmp/a.sock");
        Assert.Equal(ProbeAssembly.Path, env[Launch.StartupHooksVariable]);
        Assert.True(File.Exists(ProbeAssembly.Path));
    }

    [Fact]
    public void WriteRuntimeConfigTemplate_NamesTheProbeInStartupHooks()
    {
        var dir = NewTempDirectory();
        var path = Launch.WriteRuntimeConfigTemplate(dir, "app", "/p/Sonde.Probe.dll");
        Assert.NotNull(path);
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("/p/Sonde.Probe.dll", json["configProperties"]!["STARTUP_HOOKS"]!.GetValue<string>());
    }

    [Fact]
    public void WriteRuntimeConfigTemplate_MergesOverTheProjectsOwnTemplate_WithoutDuplicating()
    {
        var project = NewTempDirectory();
        File.WriteAllText(Path.Combine(project, "runtimeconfig.template.json"),
            """{"configProperties":{"System.GC.Server":true,"STARTUP_HOOKS":"/p/Sonde.Probe.dll"}}""");
        var dir = NewTempDirectory();
        var path = Launch.WriteRuntimeConfigTemplate(dir, "app", "/p/Sonde.Probe.dll", project)!;
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.True(json["configProperties"]!["System.GC.Server"]!.GetValue<bool>());
        Assert.Equal("/p/Sonde.Probe.dll", json["configProperties"]!["STARTUP_HOOKS"]!.GetValue<string>());
    }

    [Fact]
    public void WriteRuntimeConfigTemplate_AppendsToTheProjectsOwnHooks()
    {
        var project = NewTempDirectory();
        File.WriteAllText(Path.Combine(project, "runtimeconfig.template.json"),
            """{"configProperties":{"STARTUP_HOOKS":"/other/Hook.dll"}}""");
        var path = Launch.WriteRuntimeConfigTemplate(NewTempDirectory(), "app", "/p/Sonde.Probe.dll", project)!;
        var hooks = JsonNode.Parse(File.ReadAllText(path))!["configProperties"]!["STARTUP_HOOKS"]!.GetValue<string>();
        Assert.Equal("/other/Hook.dll" + Path.PathSeparator + "/p/Sonde.Probe.dll", hooks);
    }

    [Fact]
    public void EndpointFor_IsReadableWhenItFits()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("sonde-my-app", Launch.EndpointFor("my-app", "C:\\x", "C:\\t"));
            return;
        }
        Assert.Equal("/home/u/.sonde/my-app.sock", Launch.EndpointFor("my-app", "/home/u/.sonde", "/tmp"));
    }

    [Fact]
    public void EndpointFor_NeverExceedsTheSocketPathLimit()
    {
        if (OperatingSystem.IsWindows())
            return;
        var deepHome = "/" + string.Join("/", Enumerable.Repeat("deep", 20)) + "/.sonde";
        var longName = new string('n', 150);

        var hashedInHome = Launch.EndpointFor(longName, "/home/u/.sonde", "/tmp");
        Assert.StartsWith("/home/u/.sonde/", hashedInHome, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(hashedInHome) <= Launch.MaxSocketPathBytes);

        var hashedInTemp = Launch.EndpointFor("app", deepHome, "/tmp");
        Assert.StartsWith("/tmp/sonde/", hashedInTemp, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(hashedInTemp) <= Launch.MaxSocketPathBytes);

        // Stable: the same inputs give the same endpoint, so a launch and an eval agree.
        Assert.Equal(hashedInHome, Launch.EndpointFor(longName, "/home/u/.sonde", "/tmp"));
    }

    [Fact]
    public void EndpointFor_SanitisesTheName()
    {
        var endpoint = Launch.EndpointFor("my app/v2", "/home/u/.sonde", "/tmp");
        Assert.DoesNotContain(' ', endpoint);
        Assert.EndsWith("my-app-v2.sock", endpoint, StringComparison.Ordinal);
    }

    private static string NewTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sonde-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
