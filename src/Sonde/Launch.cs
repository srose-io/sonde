using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sonde;

/// <summary>
/// How a process is launched so the probe is inside it: the environment variables, the endpoint
/// naming, and the runtimeconfig template that reaches an app <c>dotnet watch</c> launches.
/// Pure functions over dictionaries and paths; the caller starts the process.
/// </summary>
public static class Launch
{
    /// <summary>The variable the probe reads its endpoint from.</summary>
    public const string EndpointVariable = "SONDE_ENDPOINT";

    /// <summary>The runtime's list of startup-hook assemblies.</summary>
    public const string StartupHooksVariable = "DOTNET_STARTUP_HOOKS";

    /// <summary>The MSBuild property, read from the environment, naming a runtimeconfig template to merge in.</summary>
    public const string UserRuntimeConfigVariable = "UserRuntimeConfig";

    /// <summary>Overrides where Sonde keeps sockets and templates (default <c>~/.sonde</c>).</summary>
    public const string HomeVariable = "SONDE_HOME";

    /// <summary>The longest Unix domain socket path Linux accepts (<c>sun_path</c> is 108 bytes with its NUL).</summary>
    public const int MaxSocketPathBytes = 107;

    /// <summary>Where sockets and templates live: <c>$SONDE_HOME</c>, else <c>~/.sonde</c>.</summary>
    public static string HomeDirectory
    {
        get
        {
            var home = Environment.GetEnvironmentVariable(HomeVariable);
            if (!string.IsNullOrEmpty(home))
                return home;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sonde");
        }
    }

    /// <summary>
    /// The endpoint for a process called <paramref name="name"/>: on Windows a pipe name
    /// (<c>sonde-{name}</c>); elsewhere <c>{home}/{name}.sock</c> when that fits a socket path,
    /// else a hashed name in the same directory, else a hashed name under the temp directory. A
    /// deep home directory or a long name would otherwise give a path the probe cannot bind.
    /// </summary>
    public static string EndpointFor(string name) => EndpointFor(name, HomeDirectory, Path.GetTempPath());

    /// <summary>The POSIX endpoint rule with its directories supplied, for tests.</summary>
    public static string EndpointFor(string name, string homeDirectory, string tempDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(homeDirectory);
        ArgumentNullException.ThrowIfNull(tempDirectory);

        var safeName = SafeName(name);
        if (OperatingSystem.IsWindows())
            return "sonde-" + safeName;

        var readable = Path.Combine(homeDirectory, safeName + ".sock");
        if (Fits(readable))
            return readable;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(homeDirectory + "\n" + name)))[..16].ToLowerInvariant();
        var hashed = Path.Combine(homeDirectory, hash + ".sock");
        return Fits(hashed) ? hashed : Path.Combine(tempDirectory, "sonde", hash + ".sock");

        static bool Fits(string path) => Encoding.UTF8.GetByteCount(path) <= MaxSocketPathBytes;
    }

    /// <summary>
    /// Adds the probe to a launch environment: <c>DOTNET_STARTUP_HOOKS</c> (appended with the
    /// platform path separator if one is already set), <c>SONDE_ENDPOINT</c>, and, when a
    /// runtimeconfig template was written, <c>UserRuntimeConfig</c> so a build the launch runs
    /// bakes the hook into the app's runtimeconfig too (see <see cref="WriteRuntimeConfigTemplate"/>).
    /// </summary>
    public static void ApplyEnvironment(IDictionary<string, string?> environment, string endpoint, string? probeAssemblyPath = null, string? runtimeConfigTemplate = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        probeAssemblyPath ??= ProbeAssembly.Path;

        environment.TryGetValue(StartupHooksVariable, out var existing);
        environment[StartupHooksVariable] = string.IsNullOrEmpty(existing)
            ? probeAssemblyPath
            : existing.Split(Path.PathSeparator).Contains(probeAssemblyPath, StringComparer.Ordinal)
                ? existing
                : existing + Path.PathSeparator + probeAssemblyPath;
        environment[EndpointVariable] = endpoint;
        if (!string.IsNullOrEmpty(runtimeConfigTemplate))
            environment[UserRuntimeConfigVariable] = runtimeConfigTemplate;
    }

    /// <summary>
    /// Writes <c>{directory}/{name}.runtimeconfig.template.json</c> naming the probe in
    /// <c>configProperties.STARTUP_HOOKS</c>, merged over the project's own
    /// <c>runtimeconfig.template.json</c> when <paramref name="projectDirectory"/> has one, and
    /// returns its path. <c>dotnet watch run</c> rewrites <c>DOTNET_STARTUP_HOOKS</c> for the app it
    /// launches, keeping only its own hooks, so an app under watch never sees the probe through the
    /// environment. The runtime merges this property with the environment's hooks, so this route
    /// reaches the app and leaves hot reload intact. MSBuild reads <c>UserRuntimeConfig</c> from the
    /// environment because the SDK only defaults it when empty. Returns null when the template
    /// cannot be written.
    /// </summary>
    public static string? WriteRuntimeConfigTemplate(string directory, string name, string? probeAssemblyPath = null, string? projectDirectory = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentException.ThrowIfNullOrEmpty(name);
        probeAssemblyPath ??= ProbeAssembly.Path;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, SafeName(name) + ".runtimeconfig.template.json");
            var root = new JsonObject();
            if (!string.IsNullOrEmpty(projectDirectory))
            {
                var own = Path.Combine(projectDirectory, "runtimeconfig.template.json");
                if (File.Exists(own) && JsonNode.Parse(File.ReadAllText(own)) is JsonObject theirs)
                    root = theirs;
            }
            if (root["configProperties"] is not JsonObject properties)
                root["configProperties"] = properties = new JsonObject();
            var hooks = properties["STARTUP_HOOKS"]?.GetValue<string>();
            properties["STARTUP_HOOKS"] = string.IsNullOrEmpty(hooks)
                ? probeAssemblyPath
                : hooks.Split(Path.PathSeparator).Contains(probeAssemblyPath, StringComparer.Ordinal)
                    ? hooks
                    : hooks + Path.PathSeparator + probeAssemblyPath;
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string SafeName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
            builder.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
        return builder.ToString();
    }
}

/// <summary>Where <c>Sonde.Probe.dll</c> is: beside this library, which is where the package puts it.</summary>
public static class ProbeAssembly
{
    /// <summary>The file name the runtime loads.</summary>
    public const string FileName = "Sonde.Probe.dll";

    /// <summary>The probe's full path beside this library, or beside the application when this library is bundled.</summary>
    /// <exception cref="FileNotFoundException">The probe is not beside this library.</exception>
    public static string Path
    {
        get
        {
            var candidate = Candidate();
            return File.Exists(candidate)
                ? candidate
                : throw new FileNotFoundException($"{FileName} is not beside Sonde.dll; the Sonde package ships it there.", candidate);
        }
    }

    private static string Candidate()
    {
        var location = typeof(Probe.SondeHost).Assembly.Location;
        var dir = string.IsNullOrEmpty(location) ? AppContext.BaseDirectory : System.IO.Path.GetDirectoryName(location) ?? AppContext.BaseDirectory;
        return System.IO.Path.Combine(dir, FileName);
    }
}
