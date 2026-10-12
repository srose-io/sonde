using System.Runtime.InteropServices;
using System.Text.Json;

namespace Sonde.EndToEnd;

/// <summary>Measures what an idle probe costs and writes it to artifacts/idle-cost.json for CI's summary and the README.</summary>
[Trait("Category", "EndToEnd")]
public sealed class IdleCostTests
{
    private const double CeilingMb = 40;
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    [Fact]
    public async Task IdleCost_IsReported()
    {
        var (baselineRss, baselineThreads) = await MeasureAsync(probe: false);
        var (probedRss, probedThreads) = await MeasureAsync(probe: true);

        var report = new
        {
            os = RuntimeInformation.OSDescription,
            runtime = RuntimeInformation.FrameworkDescription,
            sample = "Worker",
            baselineRssMb = Math.Round(baselineRss / 1048576.0, 1),
            probedRssMb = Math.Round(probedRss / 1048576.0, 1),
            deltaRssMb = Math.Round((probedRss - baselineRss) / 1048576.0, 1),
            baselineThreads,
            probedThreads,
            deltaThreads = probedThreads - baselineThreads,
        };
        Directory.CreateDirectory(Path.Combine(Repo.Root, "artifacts"));
        await File.WriteAllTextAsync(Path.Combine(Repo.Root, "artifacts", "idle-cost.json"),
            JsonSerializer.Serialize(report, Pretty));

        Assert.True(report.deltaRssMb < CeilingMb, $"an idle probe cost {report.deltaRssMb} MB, over the {CeilingMb} MB ceiling");
    }

    private static async Task<(long Rss, int Threads)> MeasureAsync(bool probe)
    {
        await using var app = await SampleProcess.LaunchAsync("Worker", probe);
        if (probe)
            await app.Client.HelloAsync(); // the listener is up and has answered once
        await Task.Delay(TimeSpan.FromSeconds(3));
        app.Process.Refresh();
        return (app.Process.WorkingSet64, app.Process.Threads.Count);
    }
}
