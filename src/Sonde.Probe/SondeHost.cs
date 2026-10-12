using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Sonde.Probe;

/// <summary>
/// Process-wide probe state: the captured dependency-injection root, the cross-eval
/// <see cref="Vars"/> bag, the live collectible-context count, and the endpoint. The startup hook
/// populates it. A host that builds its own provider may hand it over with <see cref="Register"/>;
/// that is the one line a host needs when automatic capture cannot find the root.
/// </summary>
public static class SondeHost
{
    private const string NoRootMessage =
        "No DI root captured yet. Send one HTTP request to the host first, or have the host call SondeHost.Register(app.Services).";

    private static volatile bool _active;
    private static IServiceProvider? _explicitRoot;
    private static IServiceScopeFactory? _capturedScopeFactory;
    private static IServiceScope? _ambientScope;
    private static readonly object Gate = new();
    private static int _liveContexts;

    /// <summary>True once the startup hook has initialised and its listener is starting. A host
    /// that merely references this assembly without the hook leaves this false, so
    /// <see cref="Register"/> is a safe no-op there.</summary>
    public static bool IsActive => _active;

    /// <summary>Survives across evals for the life of the process: assign in one script, read in the next.</summary>
    public static ConcurrentDictionary<string, object?> Vars { get; } = new(StringComparer.Ordinal);

    /// <summary>Collectible load contexts not yet unloaded. A timed-out script holds one open until it ends.</summary>
    public static int LiveContexts => Volatile.Read(ref _liveContexts);

    /// <summary>
    /// The endpoint this process's probe listens on, recorded by the startup hook before it scrubs
    /// the variable from the environment. Null when the hook did not run. A host reads this, not
    /// the environment, to reach its own probe.
    /// </summary>
    public static string? Endpoint { get; private set; }

    /// <summary>True once a root provider has been registered or captured.</summary>
    public static bool DiRootCaptured => _explicitRoot is not null || _capturedScopeFactory is not null;

    /// <summary>
    /// Hands the probe the host's root provider. First writer wins; later calls, and calls when
    /// the probe is not active, are harmless no-ops. Call it right after the host is built:
    /// <c>SondeHost.Register(app.Services)</c>.
    /// </summary>
    public static void Register(IServiceProvider services)
    {
        if (services is null)
            return;
        lock (Gate)
        {
            _explicitRoot ??= services;
        }
    }

    /// <summary>The root provider for scripts. An explicit root wins; otherwise a long-lived scope
    /// off the scope factory captured from the first HTTP request.</summary>
    /// <exception cref="InvalidOperationException">No root has been registered or captured yet.</exception>
    public static IServiceProvider Services
    {
        get
        {
            if (_explicitRoot is not null)
                return _explicitRoot;
            lock (Gate)
            {
                if (_explicitRoot is not null)
                    return _explicitRoot;
                if (_capturedScopeFactory is not null)
                {
                    _ambientScope ??= _capturedScopeFactory.CreateScope();
                    return _ambientScope.ServiceProvider;
                }
            }
            throw new InvalidOperationException(NoRootMessage);
        }
    }

    /// <summary>A fresh disposable scope for a script that wants scoped services in isolation.</summary>
    /// <exception cref="InvalidOperationException">No root has been registered or captured yet.</exception>
    public static IServiceScope CreateScope()
    {
        if (_explicitRoot is not null)
            return _explicitRoot.GetRequiredService<IServiceScopeFactory>().CreateScope();
        lock (Gate)
        {
            if (_capturedScopeFactory is not null)
                return _capturedScopeFactory.CreateScope();
        }
        throw new InvalidOperationException(NoRootMessage);
    }

    internal static void MarkActive() => _active = true;
    internal static void RecordEndpoint(string endpoint) => Endpoint = endpoint;
    internal static void IncrementLiveContexts() => Interlocked.Increment(ref _liveContexts);
    internal static void DecrementLiveContexts() => Interlocked.Decrement(ref _liveContexts);

    /// <summary>
    /// Subscribes to <see cref="DiagnosticListener.AllListeners"/> and, on the first ASP.NET
    /// request, captures the request's <see cref="IServiceScopeFactory"/> (a root singleton). The
    /// reflection-only fallback for when the Harmony constructor patch could not be applied.
    /// </summary>
    internal static void StartDiagnosticCapture() =>
        DiagnosticListener.AllListeners.Subscribe(new AllListenersObserver());

    private sealed class AllListenersObserver : IObserver<DiagnosticListener>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }

        public void OnNext(DiagnosticListener value)
        {
            if (DiRootCaptured)
                return;
            if (value.Name == "Microsoft.AspNetCore")
                value.Subscribe(new RequestObserver());
        }
    }

    private sealed class RequestObserver : IObserver<KeyValuePair<string, object?>>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (DiRootCaptured || value.Key != "Microsoft.AspNetCore.Hosting.HttpRequestIn.Start")
                return;
            try
            {
                // The event payload carries the HttpContext as its "HttpContext" property.
                var httpContext = value.Value?.GetType().GetProperty("HttpContext")?.GetValue(value.Value);
                var requestServices = httpContext?.GetType().GetProperty("RequestServices")?.GetValue(httpContext) as IServiceProvider;
                if (requestServices?.GetService(typeof(IServiceScopeFactory)) is IServiceScopeFactory scopeFactory)
                {
                    lock (Gate)
                    {
                        _capturedScopeFactory ??= scopeFactory;
                    }
                }
            }
            catch (Exception)
            {
                // Capture is best-effort; the host must never be disturbed by the probe.
            }
        }
    }
}
