// Find the hosted service and read its private counter.
var ticker = g.Resolve<IEnumerable<Microsoft.Extensions.Hosting.IHostedService>>().OfType<Worker.TickerService>().Single();
var field = typeof(Worker.TickerService).GetField("_ticks", BindingFlags.NonPublic | BindingFlags.Instance)!;
return new { ticks = (long)field.GetValue(ticker)!, running = ticker.ExecuteTask is { IsCompleted: false } };
