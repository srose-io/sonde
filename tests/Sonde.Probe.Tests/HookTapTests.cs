using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Sonde.Probe.Tests;

[Collection("process-global")]
public class HookTapTests
{
    private static ScriptGlobals NewGlobals() => new([], CancellationToken.None);

    [Fact]
    public void PostfixHookRewritesResult_AndUnhookRestores()
    {
        var g = NewGlobals();
        Assert.Equal(5, HookTarget.Identity(5));

        var handle = g.Hook(
            typeof(HookTarget).GetMethod(nameof(HookTarget.Identity))!,
            postfix: context => context.Result = (int)context.Result! * 2);

        Assert.Equal(10, HookTarget.Identity(5));
        Assert.Contains(Hooks.Describe(), d => d.Contains("HookTarget.Identity", StringComparison.Ordinal));

        g.Unhook(handle);
        Assert.Equal(5, HookTarget.Identity(5));
        Assert.DoesNotContain(Hooks.Describe(), d => d.Contains("HookTarget.Identity", StringComparison.Ordinal));
    }

    [Fact]
    public void PrefixCanSkipTheOriginal()
    {
        var g = NewGlobals();
        g.Hook<HookTarget>(nameof(HookTarget.Throws), prefix: context => { context.Skip = true; context.Result = "skipped"; });
        try
        {
            Assert.Equal("skipped", HookTarget.Throws());
        }
        finally
        {
            g.UnhookAll();
        }
    }

    [Fact]
    public void HookObservesArguments()
    {
        var g = NewGlobals();
        object? seen = null;
        var handle = g.Hook<HookTarget>(nameof(HookTarget.Identity), prefix: context => seen = context.Args[0]);
        try
        {
            HookTarget.Identity(42);
            Assert.Equal(42, seen);
        }
        finally
        {
            g.Unhook(handle);
        }
    }

    [Fact]
    public void HookOnAVoidInstanceMethodSeesTheInstance()
    {
        var g = NewGlobals();
        var target = new HookTarget();
        object? seen = null;
        var handle = g.Hook<HookTarget>(nameof(HookTarget.Touch), postfix: context => seen = context.Instance);
        try
        {
            target.Touch();
            Assert.Same(target, seen);
        }
        finally
        {
            g.Unhook(handle);
        }
    }

    [Fact]
    public void HookNeedsAPrefixOrAPostfix()
    {
        var g = NewGlobals();
        Assert.Throws<ArgumentException>(() => g.Hook<HookTarget>(nameof(HookTarget.Identity)));
    }

    [Fact]
    public void HookWarnsWhenTheTargetIsLikelyInlined()
    {
        var logs = new List<string>();
        var g = new ScriptGlobals(logs, CancellationToken.None);
        var handle = g.Hook<HookTarget>(nameof(HookTarget.Tiny), postfix: _ => { });
        try
        {
            Assert.Contains(logs, line => line.Contains("inlined", StringComparison.Ordinal));
        }
        finally
        {
            g.Unhook(handle);
        }
    }

    [Fact]
    public void TapCapturesAndCapsToCapacity()
    {
        var g = NewGlobals();
        using var listener = new DiagnosticListener("sonde.test.listener." + Guid.NewGuid().ToString("N"));
        g.Tap(listener.Name, capacity: 3);
        try
        {
            for (int i = 1; i <= 5; i++)
                listener.Write("evt", new { n = i });

            var tapped = g.Tapped(listener.Name);
            Assert.Equal(3, tapped.Count);
            Assert.All(tapped, e => Assert.Equal("evt", e.EventName));
            Assert.Equal(5, tapped[^1].Payload!["n"]!.GetValue<int>()); // oldest dropped, newest kept
            Assert.Contains(listener.Name, Taps.Names());
        }
        finally
        {
            g.Untap(listener.Name);
        }
        Assert.DoesNotContain(listener.Name, Taps.Names());
    }

    [Fact]
    public void TappedSinceFiltersByTimestamp()
    {
        var g = NewGlobals();
        using var listener = new DiagnosticListener("sonde.test.since." + Guid.NewGuid().ToString("N"));
        g.Tap(listener.Name, capacity: 100);
        try
        {
            listener.Write("a", new { });
            var cutoff = DateTimeOffset.UtcNow.AddMilliseconds(1);
            while (DateTimeOffset.UtcNow < cutoff)
            {
            }
            listener.Write("b", new { });

            var recent = g.Tapped(listener.Name, since: cutoff);
            Assert.All(recent, e => Assert.True(e.Timestamp >= cutoff));
            Assert.Contains(recent, e => e.EventName == "b");
        }
        finally
        {
            g.Untap(listener.Name);
        }
    }

    public sealed class HookTarget
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Identity(int x) => x;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string Throws() => throw new InvalidOperationException("should have been skipped");

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Touch() => Touched++;

        public static int Tiny() => 1;

        public int Touched { get; private set; }
    }
}
