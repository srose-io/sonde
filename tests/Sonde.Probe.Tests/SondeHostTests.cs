using Microsoft.Extensions.DependencyInjection;

namespace Sonde.Probe.Tests;

[Collection("process-global")]
public class SondeHostTests
{
    [Fact]
    public async Task RegisterIsFirstWriterWins_AndScriptsResolveFromThatRoot()
    {
        // The root is process-wide, so one test owns the whole story: null ignored, first
        // writer wins, later writers ignored, scripts and scopes resolve from the first.
        var first = new ServiceCollection().AddSingleton(new Marker("one")).BuildServiceProvider();
        SondeHost.Register(null!);
        SondeHost.Register(first);
        SondeHost.Register(new ServiceCollection().AddSingleton(new Marker("two")).BuildServiceProvider());

        Assert.True(SondeHost.DiRootCaptured);
        Assert.Equal("one", SondeHost.Services.GetRequiredService<Marker>().Name);
        using (var scope = SondeHost.CreateScope())
        {
            Assert.Equal("one", scope.ServiceProvider.GetRequiredService<Marker>().Name);
        }

        var response = await EvalRunner.RunAsync(TestScripts.RunRequest(
            "using var scope = g.Scope(); " +
            "return g.Resolve<Sonde.Probe.Tests.Marker>().Name + \"/\" + ((Sonde.Probe.Tests.Marker)scope.ServiceProvider.GetService(typeof(Sonde.Probe.Tests.Marker))!).Name;"));
        Assert.Null(response.Exception);
        Assert.Equal("one/one", response.Result!.GetValue<string>());
    }
}

public sealed record Marker(string Name);
