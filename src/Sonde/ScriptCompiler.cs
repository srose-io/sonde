using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Sonde;

/// <summary>
/// Compiles a script against a target's loaded assemblies (and their siblings on disk) plus
/// <c>Sonde.Probe.dll</c>, into an in-memory assembly whose entry type the probe invokes. The
/// script is the body of <c>Run(ScriptGlobals g, CancellationToken ct)</c>: statements ending in
/// <c>return x;</c>. A <c>#line</c> directive makes diagnostics report the script's own line numbers.
/// </summary>
public static class ScriptCompiler
{
    /// <summary>The type the script body is wrapped in; the probe looks it up by this name.</summary>
    public const string EntryTypeName = "SondeScript";

    /// <summary>The method on <see cref="EntryTypeName"/> the probe invokes.</summary>
    public const string EntryMethodName = "Run";

    /// <summary>A directory with more managed files than this is not a framework or an app's bin, and is not scanned.</summary>
    public const int MaxSiblingsPerDirectory = 2000;

    // The usings a script gets for free. Reflection into target internals needs System.Reflection.
    private const string Preamble = """
        using System;
        using System.Collections;
        using System.Collections.Generic;
        using System.Linq;
        using System.Reflection;
        using System.Text;
        using System.Threading;
        using System.Threading.Tasks;
        using Sonde.Probe;
        public static class SondeScript
        {
            public static async Task<object?> Run(ScriptGlobals g, CancellationToken ct)
            {
        #line 1 "script.cs"
        """;

    private const string Postamble = """

        #line default
            }
        }
        """;

    // A MetadataReference reads the whole file; a framework directory has ~170 of them, so each is
    // kept for as long as the file keeps its size and mtime. A native file is remembered as null
    // so it is not reparsed on every eval.
    private static readonly ConcurrentDictionary<string, (DateTime Mtime, long Length, MetadataReference? Reference)> ReferenceCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many files the reference cache currently holds. Diagnostic.</summary>
    public static int CachedReferenceCount => ReferenceCache.Count;

    /// <summary>
    /// Compiles <paramref name="script"/> against <paramref name="referencePaths"/> (the target's
    /// loaded assembly files, plus every managed sibling in their directories),
    /// <paramref name="referenceNames"/> (assemblies the target reported without a path, resolved
    /// from the runtime directory) and the probe assembly.
    /// </summary>
    public static ScriptCompileResult Compile(
        string script,
        IEnumerable<string> referencePaths,
        IEnumerable<string> referenceNames,
        string probeAssemblyPath)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(referencePaths);
        ArgumentNullException.ThrowIfNull(referenceNames);
        ArgumentException.ThrowIfNullOrEmpty(probeAssemblyPath);

        var source = Preamble + "\n" + script + Postamble;
        var tree = CSharpSyntaxTree.ParseText(SourceText.From(source), new CSharpParseOptions(LanguageVersion.Latest));
        var references = ResolveReferences(referencePaths, referenceNames, probeAssemblyPath);

        var compilation = CSharpCompilation.Create(
            "SondeScript_" + Guid.NewGuid().ToString("N"),
            [tree],
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: false,
                nullableContextOptions: NullableContextOptions.Annotations));

        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        if (emit.Success)
            return ScriptCompileResult.Ok(stream.ToArray());

        var diagnostics = emit.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(ToDiagnostic)
            .ToList();
        if (diagnostics.Count == 0)
            diagnostics = emit.Diagnostics.Select(ToDiagnostic).ToList();
        return ScriptCompileResult.Failed(diagnostics);
    }

    private static ScriptDiagnostic ToDiagnostic(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetMappedLineSpan();
        return new ScriptDiagnostic(
            span.IsValid ? span.StartLinePosition.Line + 1 : 0,
            span.IsValid ? span.StartLinePosition.Character + 1 : 0,
            diagnostic.Id,
            diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static MetadataReference? CachedReference(string path)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        if (ReferenceCache.TryGetValue(path, out var cached) && cached.Mtime == info.LastWriteTimeUtc && cached.Length == info.Length)
            return cached.Reference;

        MetadataReference? reference;
        try
        {
            reference = MetadataReference.CreateFromFile(path);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            reference = null; // not a managed assembly
        }
        ReferenceCache[path] = (info.LastWriteTimeUtc, info.Length, reference);
        return reference;
    }

    private static List<MetadataReference> ResolveReferences(
        IEnumerable<string> referencePaths,
        IEnumerable<string> referenceNames,
        string probeAssemblyPath)
    {
        var byPath = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);
        var paths = referencePaths.ToList();

        void AddFile(string? path)
        {
            if (string.IsNullOrEmpty(path) || byPath.ContainsKey(path) || !File.Exists(path))
                return;
            var reference = CachedReference(path);
            if (reference is not null)
                byPath[path] = reference;
        }

        foreach (var path in paths)
            AddFile(path);

        // A framework assembly the target has not touched yet (System.Net.Http before the first
        // request, say) is not in its loaded set, and a script naming it would fail with CS1069
        // "forwarded to an assembly that is not referenced". Every managed file beside a loaded
        // one, the shared frameworks and the app's own bin, is referenced too, so a script can
        // name anything the app could.
        foreach (var dir in paths.Select(Path.GetDirectoryName).Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.OrdinalIgnoreCase))
            AddSiblings(dir!);

        void AddSiblings(string dir)
        {
            string[] siblings;
            try
            {
                siblings = Directory.GetFiles(dir, "*.dll");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
            if (siblings.Length > MaxSiblingsPerDirectory)
                return;
            foreach (var sibling in siblings)
                AddFile(sibling);
        }

        // Single-file hosts hand back names with no path: resolve them from the runtime directory
        // and the probe's own folder, where the files do exist on disk. The runtime directory then
        // counts as a loaded directory too, so its siblings (the usings every script gets for free
        // need System.Linq and friends) are referenced by the same rule as above.
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        var probeDir = Path.GetDirectoryName(probeAssemblyPath);
        var scanRuntimeDir = false;
        foreach (var name in referenceNames)
        {
            if (string.IsNullOrEmpty(name))
                continue;
            var inRuntime = Path.Combine(runtimeDir, name + ".dll");
            if (File.Exists(inRuntime))
            {
                AddFile(inRuntime);
                scanRuntimeDir = true;
            }
            if (probeDir is not null)
                AddFile(Path.Combine(probeDir, name + ".dll"));
        }
        if (scanRuntimeDir)
            AddSiblings(runtimeDir);

        AddFile(probeAssemblyPath);
        return [.. byPath.Values];
    }
}

/// <summary>One compiler error, positioned in the script's own lines.</summary>
/// <param name="Line">1-based line in the script; 0 when the error has no position.</param>
/// <param name="Column">1-based column; 0 when the error has no position.</param>
/// <param name="Id">The Roslyn diagnostic id, e.g. <c>CS0103</c>.</param>
/// <param name="Message">The compiler's message.</param>
public sealed record ScriptDiagnostic(int Line, int Column, string Id, string Message);

/// <summary>What <see cref="ScriptCompiler.Compile"/> produced: an assembly, or the errors.</summary>
public sealed record ScriptCompileResult
{
    private ScriptCompileResult()
    {
    }

    /// <summary>True when <see cref="Assembly"/> is set.</summary>
    public bool Success { get; private init; }

    /// <summary>The compiled assembly's bytes, when successful.</summary>
    public ReadOnlyMemory<byte> Assembly { get; private init; }

    /// <summary>The errors, when not successful.</summary>
    public IReadOnlyList<ScriptDiagnostic> Diagnostics { get; private init; } = [];

    internal static ScriptCompileResult Ok(byte[] assembly) => new() { Success = true, Assembly = assembly };

    internal static ScriptCompileResult Failed(IReadOnlyList<ScriptDiagnostic> diagnostics) => new() { Success = false, Diagnostics = diagnostics };
}
