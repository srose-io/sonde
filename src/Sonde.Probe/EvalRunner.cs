using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Sonde.Probe.Protocol;

namespace Sonde.Probe;

/// <summary>
/// Loads a compiled script assembly into a collectible <see cref="AssemblyLoadContext"/>, resolves
/// its references to the host's already-loaded assemblies, invokes its entry method against a
/// <see cref="ScriptGlobals"/>, serialises the result, and unloads. One eval at a time per probe.
/// </summary>
internal static class EvalRunner
{
    private static int _running;
    private static DateTimeOffset _runningSince;
    private static WeakReference<AssemblyLoadContext>? _lastContext;

    /// <summary>When an eval is in progress, the time it started; otherwise null.</summary>
    public static DateTimeOffset? RunningSince => Volatile.Read(ref _running) == 1 ? _runningSince : null;

    /// <summary>The most recent eval's load context, weakly held so a test can prove it was collected.</summary>
    internal static WeakReference<AssemblyLoadContext>? LastContext => Volatile.Read(ref _lastContext);

    public static async Task<RunResponse> RunAsync(ProbeRequest request)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return new RunResponse { BusySinceUtc = _runningSince };

        _runningSince = DateTimeOffset.UtcNow;
        bool slotHeldByRunaway = false;
        try
        {
            if (string.IsNullOrEmpty(request.AssemblyBase64) || string.IsNullOrEmpty(request.EntryTypeName))
                return new RunResponse { ProtocolError = "Run requires assemblyBase64 and entryTypeName." };

            byte[] assemblyBytes;
            try
            {
                assemblyBytes = Convert.FromBase64String(request.AssemblyBase64);
            }
            catch (FormatException)
            {
                return new RunResponse { ProtocolError = "assemblyBase64 is not valid base64." };
            }

            var context = new ScriptLoadContext();
            Volatile.Write(ref _lastContext, new WeakReference<AssemblyLoadContext>(context));
            SondeHost.IncrementLiveContexts();

            try
            {
                var (method, error) = LoadEntry(context, assemblyBytes, request);
                if (method is null)
                    return new RunResponse { ProtocolError = error };

                var logs = new List<string>();
                var timeout = new CancellationTokenSource(request.TimeoutMs);
                var globals = new ScriptGlobals(logs, timeout.Token);
                var stopwatch = Stopwatch.StartNew();

                var evalTask = Task.Run(() => InvokeAsync(method, globals, timeout.Token));

                object? resultValue;
                try
                {
                    resultValue = await evalTask.WaitAsync(TimeSpan.FromMilliseconds(request.TimeoutMs)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    stopwatch.Stop();
                    // The script is still running on its thread and cannot be killed. Leave the
                    // context live (it is counted) and keep the single-eval slot held until the
                    // runaway ends, so a second eval does not race it.
                    slotHeldByRunaway = true;
                    _ = evalTask.ContinueWith(_ =>
                    {
                        timeout.Dispose();
                        TryUnload(context);
                        SondeHost.DecrementLiveContexts();
                        Interlocked.Exchange(ref _running, 0);
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    return new RunResponse
                    {
                        TimedOut = true,
                        ElapsedMs = stopwatch.ElapsedMilliseconds,
                        Logs = SnapshotLogs(logs),
                        LiveContexts = SondeHost.LiveContexts,
                    };
                }

                stopwatch.Stop();
                timeout.Dispose();
                var node = ResultJson.Serialize(resultValue, request.MaxDepth);
                return new RunResponse
                {
                    Result = node,
                    Logs = SnapshotLogs(logs),
                    ElapsedMs = stopwatch.ElapsedMilliseconds,
                    LiveContexts = SondeHost.LiveContexts,
                };
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException { InnerException: { } cause } ? cause : ex;
                return new RunResponse
                {
                    Exception = RenderException(inner),
                    LiveContexts = SondeHost.LiveContexts,
                };
            }
            finally
            {
                if (!slotHeldByRunaway)
                {
                    TryUnload(context);
                    SondeHost.DecrementLiveContexts();
                }
            }
        }
        finally
        {
            if (!slotHeldByRunaway)
                Interlocked.Exchange(ref _running, 0);
        }
    }

    private static (MethodInfo? Method, string? Error) LoadEntry(ScriptLoadContext context, byte[] assemblyBytes, ProbeRequest request)
    {
        Assembly assembly;
        using (var stream = new MemoryStream(assemblyBytes))
        {
            assembly = context.LoadFromStream(stream);
        }

        var entryType = assembly.GetType(request.EntryTypeName!);
        if (entryType is null)
            return (null, $"Entry type '{request.EntryTypeName}' not found in the compiled script.");

        var method = entryType.GetMethod(request.EntryMethodName, BindingFlags.Public | BindingFlags.Static);
        if (method is null)
            return (null, $"Entry method '{request.EntryMethodName}' not found on '{request.EntryTypeName}'.");

        return (method, null);
    }

    private static async Task<object?> InvokeAsync(MethodInfo method, ScriptGlobals globals, CancellationToken cancellationToken)
    {
        object? raw;
        try
        {
            raw = method.Invoke(null, BuildArgs(method, globals, cancellationToken));
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw tie.InnerException;
        }

        if (raw is Task task)
        {
            await task.ConfigureAwait(false);
            var type = task.GetType();
            return type.IsGenericType ? type.GetProperty("Result")?.GetValue(task) : null;
        }
        return raw;
    }

    private static object?[] BuildArgs(MethodInfo method, ScriptGlobals globals, CancellationToken cancellationToken) =>
        method.GetParameters().Length switch
        {
            0 => [],
            1 => [globals],
            _ => [globals, cancellationToken],
        };

    private static string[] SnapshotLogs(List<string> logs)
    {
        lock (logs)
        {
            return [.. logs];
        }
    }

    private static string RenderException(Exception ex) =>
        $"{ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}";

    private static void TryUnload(AssemblyLoadContext context)
    {
        try
        {
            context.Unload();
        }
        catch (Exception)
        {
            // Still referenced; the GC collects it later.
        }
    }

    private sealed class ScriptLoadContext : AssemblyLoadContext
    {
        public ScriptLoadContext() : base(isCollectible: true)
        {
            // Resolve the script's references to the host's already-loaded instances, so a target
            // type is the same Type the host uses rather than a second copy ("type X is not type X").
            Resolving += static (_, name) => AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => !a.IsDynamic && a.GetName().Name == name.Name);
        }

        protected override Assembly? Load(AssemblyName assemblyName) => null; // defer to Resolving, then Default
    }
}
