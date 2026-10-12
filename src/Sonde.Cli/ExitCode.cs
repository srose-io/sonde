namespace Sonde.Cli;

/// <summary>What <c>sonde</c> exits with. Scripts and agents branch on these.</summary>
internal static class ExitCode
{
    /// <summary>The command did what was asked; for <c>eval</c>, the script returned.</summary>
    public const int Ok = 0;

    /// <summary>The script ran but threw or timed out. Its output is still printed.</summary>
    public const int ScriptFailed = 1;

    /// <summary>The script never ran: it did not compile, no probe answered, the probe was busy, or the request was refused.</summary>
    public const int NotRun = 2;

    /// <summary>Bad arguments.</summary>
    public const int Usage = 64;
}
