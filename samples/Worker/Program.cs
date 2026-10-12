using Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<TickerService>();
builder.Build().Run();

namespace Worker
{
    /// <summary>Ticks in the background; the count is private so a script has to reach for it.</summary>
    public sealed class TickerService : BackgroundService
    {
        private long _ticks;

        public long Ticks => Interlocked.Read(ref _ticks);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _ticks);
                await Task.Delay(100, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
