using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sonde.Probe.Protocol;

namespace Sonde.Probe.Tests;

/// <summary>Compiles a script body the way the client does, so the runner can be tested without the client package.</summary>
internal static class TestScripts
{
    public const string EntryType = "SondeScript";

    public static ProbeRequest RunRequest(string body, int timeoutMs = 10_000, int maxDepth = ResultJson.DefaultMaxDepth) => new()
    {
        Kind = ProbeRequestKind.Run,
        AssemblyBase64 = Convert.ToBase64String(Compile(body)),
        EntryTypeName = EntryType,
        EntryMethodName = "Run",
        TimeoutMs = timeoutMs,
        MaxDepth = maxDepth,
    };

    public static byte[] Compile(string body)
    {
        var source = $$"""
            using System;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;
            using Sonde.Probe;

            public static class {{EntryType}}
            {
                public static async Task<object?> Run(ScriptGlobals g, CancellationToken ct)
                {
                    {{body}}
                }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();

        var compilation = CSharpCompilation.Create(
            "Script_" + Guid.NewGuid().ToString("N"),
            [tree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        if (!result.Success)
            throw new InvalidOperationException("Compile failed: " + string.Join("; ", result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.GetMessage())));
        return ms.ToArray();
    }
}
