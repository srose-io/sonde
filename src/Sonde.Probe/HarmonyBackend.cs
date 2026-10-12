using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace Sonde.Probe;

/// <summary>
/// Everything Harmony needs before its first type loads: the MonoMod backend choice and the
/// resolver that finds the probe's own per-runtime Harmony build. Runs from a module initializer,
/// so it holds on every path into the probe (the startup hook, a host that calls
/// <see cref="SondeHost.Register"/>, a test that starts the listener directly). Idempotent.
/// </summary>
/// <remarks>
/// Lib.Harmony 2.4.2 or later patches on .NET 8, 9 and 10 (2.3.x's IL generator throws on 9
/// because <c>LocalBuilder</c> became abstract; 2.4.1 refuses CoreCLR 10 with
/// <c>PlatformNotSupportedException</c>). As a belt against a MonoMod regression, prefer its
/// <c>DynamicMethod</c> backend unless the operator chose one. MonoMod reads the variable as it
/// initialises, which is why this must run before any Harmony type loads.
/// </remarks>
internal static class HarmonyBackend
{
    private const string BackendVariable = "MONOMOD_DMD_TYPE";
    private static int _done;

    /// <summary>Sets the MonoMod backend (if the operator has not) and registers the Harmony resolver. Idempotent.</summary>
    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "MonoMod reads MONOMOD_DMD_TYPE as it initialises; the module initializer is the only point that runs before any Harmony type loads.")]
    public static void EnsureConfigured()
    {
        if (Interlocked.Exchange(ref _done, 1) == 1)
            return;
        try
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(BackendVariable)))
                Environment.SetEnvironmentVariable(BackendVariable, "DynamicMethod");
            // Harmony ships with the probe, not with the host: the host never references it.
            // Answer only for 0Harmony, and only when nothing else resolved it.
            AssemblyLoadContext.Default.Resolving += static (context, name) =>
            {
                if (!string.Equals(name.Name, "0Harmony", StringComparison.OrdinalIgnoreCase))
                    return null;
                var path = HarmonyLocator.PickForThisHost();
                return path is null ? null : context.LoadFromAssemblyPath(path);
            };
        }
        catch (Exception ex)
        {
            // A module initializer that throws fails the assembly load, which would fail the host.
            Bootstrap.Log($"harmony setup failed: {ex.Message}");
        }
    }
}
