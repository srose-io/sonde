using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Sonde.Mcp;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        // stdout is the protocol channel; everything else goes to stderr.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new() { Name = "sonde", Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" };
                options.ServerInstructions = SondeTools.Instructions;
            })
            .WithStdioServerTransport()
            .WithToolsFromAssembly(typeof(Program).Assembly)
            .WithResourcesFromAssembly(typeof(Program).Assembly);
        await builder.Build().RunAsync().ConfigureAwait(false);
    }
}
