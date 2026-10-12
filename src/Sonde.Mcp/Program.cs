namespace Sonde.Mcp;

/// <summary>Entry point of the <c>sonde-mcp</c> server.</summary>
internal static class Program
{
    /// <summary>Runs the server. Tools arrive in a later slice.</summary>
    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        Console.Error.WriteLine("sonde-mcp: under construction");
        return 0;
    }
}
