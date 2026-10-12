using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Sonde.Probe;

/// <summary>
/// What a script sees as <c>g</c>. Constructed fresh per eval; <see cref="Vars"/>, hooks and taps
/// are process-wide and persist across evals. The full surface, with examples, is
/// <c>docs/script-surface.md</c>.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
    Justification = "Scripts call these as g.Member; the instance is the script's whole surface.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple overloads with optional parameters",
    Justification = "Hook(MethodBase, ...) and Hook<T>(string, ...) differ in generic arity and first parameter type; a call can never be ambiguous between them.")]
public sealed class ScriptGlobals
{
    private const BindingFlags AnyMethod = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private readonly List<string> _logs;

    internal ScriptGlobals(List<string> logs, CancellationToken cancellation)
    {
        _logs = logs;
        Cancellation = cancellation;
    }

    /// <summary>The host's root service provider.</summary>
    public IServiceProvider Services => SondeHost.Services;

    /// <summary>Resolves a required service from the root provider.</summary>
    public T Resolve<T>() where T : notnull => Services.GetRequiredService<T>();

    /// <summary>A fresh disposable scope. Dispose it when done.</summary>
    public IServiceScope Scope() => SondeHost.CreateScope();

    /// <summary>Persists for the life of the process, across evals.</summary>
    public ConcurrentDictionary<string, object?> Vars => SondeHost.Vars;

    /// <summary>Trips at the eval timeout. A long-running script should honour it; a runaway one
    /// cannot be stopped from outside.</summary>
    public CancellationToken Cancellation { get; }

    /// <summary>Appends a line returned alongside the result.</summary>
    public void Log(string message)
    {
        lock (_logs)
        {
            _logs.Add(message);
        }
    }

    /// <summary>Hooks a method with a prefix and/or postfix. Lives until unhooked or the process
    /// ends. Logs a warning when the target is small and non-virtual, because calls the JIT already
    /// inlined it into are not intercepted.</summary>
    public HookHandle Hook(MethodBase target, Action<HookContext>? prefix = null, Action<HookContext>? postfix = null) =>
        Hooks.Add(target, prefix, postfix, Log);

    /// <summary>Hooks <typeparamref name="T"/>'s method by name (public or non-public, instance or static).</summary>
    /// <exception cref="MissingMethodException">No method of that name on <typeparamref name="T"/>.</exception>
    public HookHandle Hook<T>(string methodName, Action<HookContext>? prefix = null, Action<HookContext>? postfix = null)
    {
        var target = typeof(T).GetMethod(methodName, AnyMethod)
            ?? throw new MissingMethodException(typeof(T).FullName, methodName);
        return Hook(target, prefix, postfix);
    }

    /// <summary>Removes one hook.</summary>
    public void Unhook(HookHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        Hooks.Remove(handle.Id);
    }

    /// <summary>Removes every hook in the process.</summary>
    public void UnhookAll() => Hooks.RemoveAll();

    /// <summary>Ring-buffers a named <see cref="System.Diagnostics.DiagnosticListener"/>'s events
    /// (e.g. <c>Microsoft.EntityFrameworkCore</c>, <c>HttpHandlerDiagnosticListener</c>). Persists
    /// across evals; read it with <see cref="Tapped"/>.</summary>
    public void Tap(string listenerName, int capacity = 1000) => Taps.Start(listenerName, capacity);

    /// <summary>The captured events for a listener, optionally only those at or after <paramref name="since"/>.</summary>
    public IReadOnlyList<TapEntry> Tapped(string listenerName, DateTimeOffset? since = null) => Taps.Read(listenerName, since);

    /// <summary>Stops tapping a listener and drops its buffer.</summary>
    public void Untap(string listenerName) => Taps.Stop(listenerName);
}
