using MinimalWeb;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<CounterService>();
builder.Services.AddHttpClient();
var app = builder.Build();

app.MapGet("/", () => "hello from MinimalWeb");
app.MapGet("/count", (CounterService counter) => counter.Increment());
app.MapGet("/fetch", async (IHttpClientFactory clients, HttpContext context) =>
{
    // An outbound HTTP call to ourselves, so a script can tap HttpClient.
    var self = $"{context.Request.Scheme}://{context.Request.Host}/";
    return await clients.CreateClient().GetStringAsync(new Uri(self));
});

app.Run();

namespace MinimalWeb
{
    /// <summary>A service with private state and a method worth hooking.</summary>
    public sealed class CounterService
    {
        private int _count;

        // A hook patches the method's entry point, so a caller the JIT already inlined this tiny
        // body into would bypass it. Real service methods are rarely small enough to inline; this
        // one is, so say so. (`g.Hook` warns when a target looks like this.)
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public int Increment() => Interlocked.Increment(ref _count);
    }
}
