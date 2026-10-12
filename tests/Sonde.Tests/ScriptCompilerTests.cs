namespace Sonde.Tests;

public class ScriptCompilerTests
{
    private static readonly string Probe = ProbeAssembly.Path;
    private static readonly string[] CoreReferences = [typeof(object).Assembly.Location];

    [Fact]
    public void CompilesAScriptBodyIntoTheEntryType()
    {
        var result = ScriptCompiler.Compile("return 1 + 2;", CoreReferences, [], Probe);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.False(result.Assembly.IsEmpty);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void DiagnosticsMapToTheScriptsOwnLines()
    {
        var result = ScriptCompiler.Compile("var a = 1;\nvar b = 2;\nreturn c;", CoreReferences, [], Probe);
        Assert.False(result.Success);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("CS0103", error.Id);
        Assert.Equal(3, error.Line);
        Assert.Equal(8, error.Column);
        Assert.Contains("'c'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SiblingsOfALoadedAssemblyAreReferenced()
    {
        // Only System.Private.CoreLib is named, but the script uses a type from a sibling in the
        // same runtime directory the target has not loaded: it must still compile.
        var result = ScriptCompiler.Compile(
            "return System.Collections.Immutable.ImmutableArray.Create(1, 2).Length;", CoreReferences, [], Probe);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public void NamesWithoutPathsResolveFromTheRuntimeDirectory()
    {
        var result = ScriptCompiler.Compile(
            "return System.Net.Http.HttpMethod.Get.Method;", [], ["System.Private.CoreLib", "System.Runtime", "System.Net.Http", "System.Net.Primitives"], Probe);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public void TheScriptSeesTheGlobalsAndTheToken()
    {
        var result = ScriptCompiler.Compile("g.Log(\"x\"); ct.ThrowIfCancellationRequested(); return g.Vars.Count;", CoreReferences, [], Probe);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public void ReferencesAreCachedBetweenCompiles()
    {
        ScriptCompiler.Compile("return 0;", CoreReferences, [], Probe);
        var after = ScriptCompiler.CachedReferenceCount;
        ScriptCompiler.Compile("return 1;", CoreReferences, [], Probe);
        Assert.Equal(after, ScriptCompiler.CachedReferenceCount);
        Assert.True(after > 10, "the runtime directory's siblings should be cached");
    }
}
