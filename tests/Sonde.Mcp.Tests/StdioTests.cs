using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Sonde.Tests;

namespace Sonde.Mcp.Tests;

/// <summary>Launches the real server over stdio, as a client would, and drives it with the SDK's client.</summary>
public class StdioTests(LoopbackFixture loopback) : IClassFixture<LoopbackFixture>
{
    /// <summary>
    /// The server runs from its own build output (a consistent deps.json and runtimeconfig for its
    /// own target framework), the way a tool install lays it out, not from this suite's output,
    /// whose dependencies were resolved for the suite's framework.
    /// </summary>
    private static string ServerDll
    {
        get
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Sonde.sln")))
                root = root.Parent;
            Assert.NotNull(root);
            var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}release", StringComparison.OrdinalIgnoreCase) ? "release" : "debug";
            var candidates = new[] { $"{configuration}_net{Environment.Version.Major}.0", $"{configuration}_net9.0", $"{configuration}_net8.0" };
            foreach (var candidate in candidates)
            {
                var dll = Path.Combine(root.FullName, "artifacts", "bin", "Sonde.Mcp", candidate, "Sonde.Mcp.dll");
                if (File.Exists(dll))
                    return dll;
            }
            throw new FileNotFoundException("Sonde.Mcp.dll not found under artifacts/bin/Sonde.Mcp; build the solution first.");
        }
    }

    private static async Task<McpClient> ConnectAsync()
    {
        var stderr = new System.Text.StringBuilder();
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "sonde",
            Command = "dotnet",
            Arguments = [ServerDll],
            StandardErrorLines = line =>
            {
                lock (stderr)
                {
                    stderr.AppendLine(line);
                }
            },
        });
        try
        {
            return await McpClient.CreateAsync(transport);
        }
        catch (Exception ex)
        {
            string captured;
            lock (stderr)
            {
                captured = stderr.ToString();
            }
            throw new InvalidOperationException($"the server at {ServerDll} did not come up: {ex.Message}\n{captured}", ex);
        }
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
