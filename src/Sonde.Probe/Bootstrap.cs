using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;

namespace Sonde.Probe;

/// <summary>What the startup hook decided to do in this process.</summary>
internal enum HookAction
{
    /// <summary>The process is SDK tooling (<c>dotnet.dll</c>, <c>dotnet-watch</c>, MSBuild): leave
    /// the environment alone so the app it launches gets the probe, and bind nothing.</summary>
    PassThrough,

    /// <summary>No endpoint was given: scrub the environment and do nothing else. No thread, no socket.</summary>
    Dormant,

    /// <summary>An endpoint was given: scrub the environment and start the listener.</summary>
    Listen,
}

/// <summary>
/// Wires the probe up inside the host: decides whether this process is a target, scrubs the
/// environment so children never inherit the hook, captures the dependency-injection root, and
/// starts the listener on a background thread. Idempotent: a repeated start is ignored.
/// </summary>
internal static partial class Bootstrap
{
    /// <summary>The environment variable naming the endpoint: a Unix socket path on POSIX, a pipe name on Windows.</summary>
    public const string EndpointVariable = "SONDE_ENDPOINT";

    /// <summary>The runtime's own variable listing startup-hook assemblies.</summary>
    public const string StartupHooksVariable = "DOTNET_STARTUP_HOOKS";

    private const string LogPrefix = "[Sonde] ";

    private static int _started;
    private static readonly CancellationTokenSource Lifetime = new();

    /// <summary>The pure decision: what the hook does given the entry assembly and the endpoint variable.</summary>
    public static HookAction Plan(string? entryAssemblyLocation, string? endpoint)
    {
        if (IsSdkTooling(entryAssemblyLocation))
            return HookAction.PassThrough;
        return string.IsNullOrEmpty(endpoint) ? HookAction.Dormant : HookAction.Listen;
    }

    /// <summary>Carries out <see cref="Plan"/> in this process. Returns what it did.</summary>
    public static HookAction Initialize(string? entryAssemblyLocation, string? endpoint)
    {
        var action = Plan(entryAssemblyLocation, endpoint);
        if (action == HookAction.PassThrough)
            return action;

        // The runtime has read DOTNET_STARTUP_HOOKS by now. Take both variables out of this
        // process's environment before anything else, so no child it ever starts inherits them: a
        // child that did would load the probe too and bind the same endpoint.
        ScrubEnvironment();
        if (action == HookAction.Dormant)
            return action;

        SondeHost.RecordEndpoint(endpoint!);
        Start(endpoint!);
        return action;
    }

    /// <summary>
    /// True when the entry assembly lives under an SDK version directory
    /// (<c>…/sdk/9.0.312/dotnet.dll</c>, <c>…/sdk/10.0.401/DotnetTools/dotnet-watch/…</c>,
    /// <c>…/sdk/…/MSBuild.dll</c>, <c>…/sdk/…/Roslyn/bincore/VBCSCompiler.dll</c>): a tool that
    /// launches the real process as a child, never the process to probe.
    /// </summary>
    public static bool IsSdkTooling(string? entryAssemblyLocation) =>
        !string.IsNullOrEmpty(entryAssemblyLocation) && SdkVersionDirectory().IsMatch(entryAssemblyLocation);

    [GeneratedRegex(@"(^|[/\\])sdk[/\\]\d+\.\d+\.\d+[^/\\]*[/\\]", RegexOptions.IgnoreCase)]
    private static partial Regex SdkVersionDirectory();

    /// <summary>Removes <see cref="StartupHooksVariable"/> and <see cref="EndpointVariable"/> from
    /// this process's environment. Idempotent.</summary>
    public static void ScrubEnvironment()
    {
        Environment.SetEnvironmentVariable(StartupHooksVariable, null);
        Environment.SetEnvironmentVariable(EndpointVariable, null);
    }

    /// <summary>Starts the probe once: Harmony resolution, DI-root capture, the listener thread.</summary>
    public static void Start(string endpoint)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            return;

        RegisterHarmonyResolver();
        SondeHost.StartDiagnosticCapture();
        TryStartHarmonyDiCapture();
        SondeHost.MarkActive();

        var thread = new Thread(() =>
        {
            try
            {
                // Let the host settle before binding; the probe is dormant until something connects.
                Thread.Sleep(250);
                Listener.ServeAsync(endpoint, Lifetime.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log($"listener thread stopped: {ex.Message}");
            }
        })
        {
            IsBackground = true,
            Name = "sonde-listener",
        };
        thread.Start();
    }

    /// <summary>Writes one line to stderr with the <c>[Sonde]</c> prefix. Never throws.</summary>
    public static void Log(string message)
    {
        try
        {
            Console.Error.WriteLine(LogPrefix + message);
        }
        catch (Exception)
        {
            // stderr is closed or redirected somewhere that refuses writes; nothing to be done.
        }
    }

    /// <summary>
    /// <c>0Harmony.dll</c> ships beside <c>Sonde.Probe.dll</c>; the host does not reference it.
    /// Resolve it from the probe's own directory so hooks work without the host carrying Harmony.
    /// </summary>
    private static void RegisterHarmonyResolver()
    {
        AssemblyLoadContext.Default.Resolving += static (context, name) =>
        {
            if (!string.Equals(name.Name, "0Harmony", StringComparison.OrdinalIgnoreCase))
                return null;
            var dir = Path.GetDirectoryName(typeof(Bootstrap).Assembly.Location);
            if (string.IsNullOrEmpty(dir))
                return null;
            var path = Path.Combine(dir, "0Harmony.dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
    }

    /// <summary>
    /// The general DI-root path: a postfix on the <c>ServiceProvider</c> constructor records the
    /// first root built, so a host without ASP.NET is covered too. Best-effort: the ASP.NET request
    /// observer and <see cref="SondeHost.Register"/> stand on their own if this cannot patch.
    /// </summary>
    private static void TryStartHarmonyDiCapture()
    {
        try
        {
            HarmonyBackend.EnsureConfigured();
            var providerType = Type.GetType(
                "Microsoft.Extensions.DependencyInjection.ServiceProvider, Microsoft.Extensions.DependencyInjection");
            if (providerType is null)
                return;

            var harmony = new HarmonyLib.Harmony("sonde.di");
            var postfix = new HarmonyLib.HarmonyMethod(
                typeof(Bootstrap).GetMethod(nameof(CaptureRootPostfix), BindingFlags.NonPublic | BindingFlags.Static)!);
            foreach (var ctor in providerType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                try
                {
                    harmony.Patch(ctor, postfix: postfix);
                }
                catch (Exception)
                {
                    // One constructor shape refused; the others may take.
                }
            }
        }
        catch (Exception)
        {
            // Harmony missing or the DI assembly's shape changed: the fallbacks remain.
        }
    }

    private static void CaptureRootPostfix(object __instance)
    {
        if (__instance is IServiceProvider services)
            SondeHost.Register(services);
    }
}
