using System.Runtime.CompilerServices;

namespace Sonde.Probe;

/// <summary>
/// Lib.Harmony 2.4.2 or later patches on .NET 8, 9 and 10 (2.3.x's IL generator throws on 9
/// because <c>LocalBuilder</c> became abstract; 2.4.1 refuses CoreCLR 10 with
/// <c>PlatformNotSupportedException</c>). As a belt against a MonoMod regression, prefer its
/// <c>DynamicMethod</c> backend unless the operator chose one. MonoMod reads the variable as it
/// initialises, so this runs from a module initializer, before any Harmony type loads.
/// </summary>
internal static class HarmonyBackend
{
    private const string BackendVariable = "MONOMOD_DMD_TYPE";
    private static int _done;

    /// <summary>Sets the MonoMod backend once, if the operator has not. Idempotent.</summary>
    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "MonoMod reads MONOMOD_DMD_TYPE as it initialises; the module initializer is the only point that runs before any Harmony type loads.")]
    public static void EnsureConfigured()
    {
        if (Interlocked.Exchange(ref _done, 1) == 1)
            return;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(BackendVariable)))
            Environment.SetEnvironmentVariable(BackendVariable, "DynamicMethod");
    }
}
