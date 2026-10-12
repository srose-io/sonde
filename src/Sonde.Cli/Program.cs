namespace Sonde.Cli;

/// <summary>Entry point of the <c>sonde</c> tool.</summary>
internal static class Program
{
    /// <summary>Runs the tool. Commands arrive in a later slice.</summary>
    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        Console.WriteLine("sonde: under construction");
        return 0;
    }
}
