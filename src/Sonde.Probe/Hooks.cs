using System.Collections.Concurrent;
using System.Reflection;
using HarmonyLib;

namespace Sonde.Probe;

/// <summary>What a hook's prefix or postfix sees and can change: the call's instance and
/// arguments, and (for a non-void target) its result. A prefix that sets <see cref="Skip"/>
/// short-circuits the original and returns <see cref="Result"/>; a postfix may rewrite
/// <see cref="Result"/>.</summary>
public sealed class HookContext
{
    internal HookContext(object? instance, object?[] args)
    {
        Instance = instance;
        Args = args;
    }

    /// <summary>The target's <c>this</c>, or null for a static method.</summary>
    public object? Instance { get; }

    /// <summary>The call's arguments, in declaration order.</summary>
    public IReadOnlyList<object?> Args { get; }

    /// <summary>The return value: what the original produced (in a postfix), or what to return instead.</summary>
    public object? Result { get; set; }

    /// <summary>Set by a prefix to skip the original and return <see cref="Result"/>.</summary>
    public bool Skip { get; set; }
}

/// <summary>A live hook, returned by <c>g.Hook(...)</c> and passed to <c>g.Unhook(...)</c>.</summary>
public sealed class HookHandle
{
    internal HookHandle(MethodBase target, Action<HookContext>? prefix, Action<HookContext>? postfix)
    {
        Target = target;
        Prefix = prefix;
        Postfix = postfix;
        Description = $"{target.DeclaringType?.FullName}.{target.Name}({string.Join(", ", target.GetParameters().Select(p => p.ParameterType.Name))})";
    }

    /// <summary>Identifies the hook for <c>g.Unhook</c>.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>The target method, as reported in <c>Status</c>.</summary>
    public string Description { get; }

    internal MethodBase Target { get; }
    internal Action<HookContext>? Prefix { get; }
    internal Action<HookContext>? Postfix { get; }
}

/// <summary>
/// Harmony-backed method hooks, living for the life of the process until unhooked. One Harmony
/// patch per target dispatches to every handle registered for it.
/// </summary>
internal static class Hooks
{
    private const string HarmonyId = "sonde.hooks";
    private const int LikelyInlinedIlBytes = 32;

    // Created on the first hook, so a Status call (which reads Describe) never loads Harmony.
    private static readonly Lazy<Harmony> HarmonyInstance = new(CreateHarmony);
    private static readonly ConcurrentDictionary<MethodBase, List<HookHandle>> ByMethod = new();
    private static readonly ConcurrentDictionary<Guid, HookHandle> ById = new();
    private static readonly object Gate = new();

    private static Harmony CreateHarmony()
    {
        HarmonyBackend.EnsureConfigured();
        return new Harmony(HarmonyId);
    }

    public static HookHandle Add(MethodBase target, Action<HookContext>? prefix, Action<HookContext>? postfix, Action<string>? warn)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (prefix is null && postfix is null)
            throw new ArgumentException("A hook needs a prefix or a postfix.");

        WarnIfLikelyInlined(target, warn);

        var handle = new HookHandle(target, prefix, postfix);
        lock (Gate)
        {
            var list = ByMethod.GetOrAdd(target, _ => []);
            var firstForMethod = list.Count == 0;
            list.Add(handle);
            ById[handle.Id] = handle;
            if (firstForMethod)
                Patch(target);
        }
        return handle;
    }

    public static void Remove(Guid id)
    {
        lock (Gate)
        {
            if (!ById.TryRemove(id, out var handle))
                return;
            if (ByMethod.TryGetValue(handle.Target, out var list))
            {
                list.RemoveAll(h => h.Id == id);
                if (list.Count == 0)
                {
                    ByMethod.TryRemove(handle.Target, out _);
                    HarmonyInstance.Value.Unpatch(handle.Target, HarmonyPatchType.All, HarmonyId);
                }
            }
        }
    }

    public static void RemoveAll()
    {
        lock (Gate)
        {
            if (HarmonyInstance.IsValueCreated)
                HarmonyInstance.Value.UnpatchAll(HarmonyId);
            ByMethod.Clear();
            ById.Clear();
        }
    }

    public static IReadOnlyList<string> Describe() => ById.Values.Select(h => h.Description).ToArray();

    internal static void RunPrefix(MethodBase target, HookContext context)
    {
        if (!ByMethod.TryGetValue(target, out var list))
            return;
        foreach (var handle in list.ToArray())
            handle.Prefix?.Invoke(context);
    }

    internal static void RunPostfix(MethodBase target, HookContext context)
    {
        if (!ByMethod.TryGetValue(target, out var list))
            return;
        foreach (var handle in list.ToArray())
            handle.Postfix?.Invoke(context);
    }

    private static void Patch(MethodBase target)
    {
        var nonVoid = target is MethodInfo { ReturnType: var returnType } && returnType != typeof(void);
        var prefix = new HarmonyMethod(typeof(HookPatches).GetMethod(
            nonVoid ? nameof(HookPatches.PrefixWithResult) : nameof(HookPatches.PrefixNoResult),
            BindingFlags.Public | BindingFlags.Static)!);
        var postfix = new HarmonyMethod(typeof(HookPatches).GetMethod(
            nonVoid ? nameof(HookPatches.PostfixWithResult) : nameof(HookPatches.PostfixNoResult),
            BindingFlags.Public | BindingFlags.Static)!);
        HarmonyInstance.Value.Patch(target, prefix: prefix, postfix: postfix);
    }

    private static void WarnIfLikelyInlined(MethodBase target, Action<string>? warn)
    {
        if (warn is null || target.IsVirtual || target.IsAbstract)
            return;
        try
        {
            var il = target.GetMethodBody()?.GetILAsByteArray();
            if (il is not null && il.Length <= LikelyInlinedIlBytes)
                warn($"Hook on {target.DeclaringType?.Name}.{target.Name}: small and non-virtual, so callers the JIT already inlined it into are not intercepted.");
        }
        catch (Exception)
        {
            // GetMethodBody throws for an abstract or runtime-provided method; nothing to warn about.
        }
    }
}

/// <summary>The shared Harmony patch methods. Public because Harmony's generated code calls them;
/// scripts never do. Chosen per target by whether it returns a value, so a void method is never
/// given a <c>__result</c> (which Harmony rejects at patch time).</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores",
    Justification = "Harmony binds its injected parameters (__originalMethod, __args, __instance, __result) by exactly these names.")]
public static class HookPatches
{
    /// <summary>Prefix for a void target.</summary>
    public static bool PrefixNoResult(MethodBase __originalMethod, object?[] __args, object? __instance)
    {
        var context = new HookContext(__instance, __args);
        Hooks.RunPrefix(__originalMethod, context);
        return !context.Skip;
    }

    /// <summary>Prefix for a non-void target.</summary>
    public static bool PrefixWithResult(MethodBase __originalMethod, object?[] __args, object? __instance, ref object? __result)
    {
        var context = new HookContext(__instance, __args);
        Hooks.RunPrefix(__originalMethod, context);
        if (context.Skip)
        {
            __result = context.Result;
            return false;
        }
        return true;
    }

    /// <summary>Postfix for a void target.</summary>
    public static void PostfixNoResult(MethodBase __originalMethod, object?[] __args, object? __instance)
    {
        var context = new HookContext(__instance, __args);
        Hooks.RunPostfix(__originalMethod, context);
    }

    /// <summary>Postfix for a non-void target.</summary>
    public static void PostfixWithResult(MethodBase __originalMethod, object?[] __args, object? __instance, ref object? __result)
    {
        var context = new HookContext(__instance, __args) { Result = __result };
        Hooks.RunPostfix(__originalMethod, context);
        __result = context.Result;
    }
}
