using Sonde.Probe;

namespace Sonde.Tests;

/// <summary>
/// Starts the real listener inside the test process on a temp endpoint, so the client can be
/// driven end to end (frames, compile, run) without launching a second process.
/// </summary>
public sealed class LoopbackFixture : IAsyncLifetime
{
    private readonly CancellationTokenSource _lifetime = new();
    private Task _serving = Task.CompletedTask;

    public string Endpoint { get; } = OperatingSystem.IsWindows()
        ? "sonde-test-" + Guid.NewGuid().ToString("N")[..8]
        : Path.Combine(Path.GetTempPath(), "sonde-test-" + Guid.NewGuid().ToString("N")[..8] + ".sock");

    public async Task InitializeAsync()
    {
        _serving = Listener.ServeAsync(Endpoint, _lifetime.Token);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!await ProbeClient.IsListeningAsync(Endpoint, TimeSpan.FromMilliseconds(200)) && DateTime.UtcNow < deadline)
            await Task.Delay(50);
    }

    public async Task DisposeAsync()
    {
        await _lifetime.CancelAsync();
        try
        {
            await _serving;
        }
        finally
        {
            _lifetime.Dispose();
            if (!OperatingSystem.IsWindows())
                File.Delete(Endpoint);
        }
    }
}
