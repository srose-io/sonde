namespace Sonde.EndToEnd;

/// <summary>Where things are: the repo root, a sample's build output, its scripts.</summary>
internal static class Repo
{
    public static string Root { get; } = FindRoot();

    /// <summary>The configuration this suite was built in (the samples were built in the same one).</summary>
    public static string Configuration { get; } =
        AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}release", StringComparison.OrdinalIgnoreCase) ? "release" : "debug";

    public static string SampleDll(string sample) =>
        Path.Combine(Root, "artifacts", "bin", sample, Configuration, sample + ".dll");

    public static string SampleProject(string sample) => Path.Combine(Root, "samples", sample);

    public static string Script(string sample, string name) =>
        File.ReadAllText(Path.Combine(SampleProject(sample), "scripts", name + ".cs"));

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Sonde.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Sonde.sln not found above " + AppContext.BaseDirectory);
    }
}
