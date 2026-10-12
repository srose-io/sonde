
namespace Sonde.Cli;

internal static class Program
{
    public static Task<int> Main(string[] args) => SondeCommand.Parse(args).InvokeAsync();
}
