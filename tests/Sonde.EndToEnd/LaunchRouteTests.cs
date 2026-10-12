namespace Sonde.EndToEnd;

[Trait("Category", "EndToEnd")]
public sealed class LaunchRouteTests
{
    [Fact]
    [Trait("Speed", "Slow")]
    public async Task LaunchedUnderDotnetWatch_ProbeArrivesViaRuntimeConfigTemplate()
    {
        // dotnet watch rewrites DOTNET_STARTUP_HOOKS for the app it launches, so the hook must
        // arrive through the runtimeconfig template instead, and land in the app, not the watcher.
        await using var app = await SampleProcess.LaunchUnderWatchAsync("Worker", web: false);
        var hello = await app.Client.HelloAsync();
        Assert.Equal("Worker", hello.EntryAssembly);
        Assert.NotEqual(app.Pid, hello.ProcessId); // the watcher is the parent; the app answered
    }

    [Fact]
    public async Task LaunchedWithoutTheEndpoint_NothingListens()
    {
        await using var app = await SampleProcess.LaunchAsync("Worker", probe: false);
        Assert.False(app.Process.HasExited);
        Assert.DoesNotContain("[Sonde]", app.StandardError, StringComparison.Ordinal);
    }
}
