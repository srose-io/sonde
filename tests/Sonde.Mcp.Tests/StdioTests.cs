using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Sonde.Tests;

namespace Sonde.Mcp.Tests;

/// <summary>Launches the real server over stdio, as a client would, and drives it with the SDK's client.</summary>
public class StdioTests(LoopbackFixture loopback) : IClassFixture<LoopbackFixture>
{
    private static async Task<McpClient> ConnectAsync()
    {
        var serverDll = Path.Combine(AppContext.BaseDirectory, "Sonde.Mcp.dll");
        Assert.True(File.Exists(serverDll), $"{serverDll} should be beside the tests");
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "sonde",
            Command = "dotnet",
            Arguments = [serverDll],
            StandardErrorLines = line => Console.Error.WriteLine("[server] " + line),
        });
        return await McpClient.CreateAsync(transport);
    }

    [Fact]
    public async Task ListsTheFourToolsAndTheResource()
    {
        await using var client = await ConnectAsync();
        var tools = await client.ListToolsAsync();
        Assert.Equal(["sonde_eval", "sonde_hello", "sonde_list_targets", "sonde_status"], tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.All(tools, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));

        var resources = await client.ListResourcesAsync();
        Assert.Contains(resources, r => r.Uri == SondeResources.ScriptSurfaceUri);
        Assert.Contains("sonde_eval", client.ServerInstructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallsHelloAndEvalOverStdio()
    {
        await using var client = await ConnectAsync();
        var hello = await client.CallToolAsync("sonde_hello", new Dictionary<string, object?> { ["target"] = loopback.Endpoint });
        var text = Assert.IsType<TextContentBlock>(Assert.Single(hello.Content)).Text;
        Assert.Contains($"\"processId\": {Environment.ProcessId}", text, StringComparison.Ordinal);

        var eval = await client.CallToolAsync("sonde_eval", new Dictionary<string, object?> { ["target"] = loopback.Endpoint, ["script"] = "return 2 + 2;" });
        text = Assert.IsType<TextContentBlock>(Assert.Single(eval.Content)).Text;
        Assert.Contains("\"outcome\": \"completed\"", text, StringComparison.Ordinal);
        Assert.Contains("\"result\": 4", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadsTheScriptSurface()
    {
        await using var client = await ConnectAsync();
        var read = await client.ReadResourceAsync(SondeResources.ScriptSurfaceUri);
        var contents = Assert.IsType<TextResourceContents>(Assert.Single(read.Contents));
        Assert.Contains("g.Services", contents.Text, StringComparison.Ordinal);
    }
}
