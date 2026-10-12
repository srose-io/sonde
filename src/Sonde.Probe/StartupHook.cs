using Sonde.Probe;

// The .NET runtime looks for a top-level type named StartupHook with a public static Initialize()
// in each assembly listed in DOTNET_STARTUP_HOOKS, and runs it before the host's Main. Everything
// here is wrapped: a fault in the probe must never take the host down.
internal sealed class StartupHook
{
    public static void Initialize()
    {
        try
        {
            Bootstrap.Initialize(
                System.Reflection.Assembly.GetEntryAssembly()?.Location,
                Environment.GetEnvironmentVariable(Bootstrap.EndpointVariable));
        }
        catch (Exception ex)
        {
            Bootstrap.Log($"initialize failed: {ex.Message}");
        }
    }
}
