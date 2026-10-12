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
    public void ANativeDllBesideAnAssemblyIsSkipped()
    {
        // Windows frameworks keep native libraries as .dll files beside the managed ones. One
        // such sibling must be skipped, not referenced and then rejected with CS0009.
        var dir = Path.Combine(Path.GetTempPath(), "sonde-native-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var managed = Path.Combine(dir, "Managed.dll");
        File.Copy(ProbeAssembly.Path, managed);
        File.WriteAllBytes(Path.Combine(dir, "Something.Native.dll"), NativePe());
        File.WriteAllBytes(Path.Combine(dir, "Garbage.dll"), [1, 2, 3, 4]);

        Assert.False(ScriptCompiler.IsManagedAssembly(Path.Combine(dir, "Something.Native.dll")));
        Assert.False(ScriptCompiler.IsManagedAssembly(Path.Combine(dir, "Garbage.dll")));
        Assert.True(ScriptCompiler.IsManagedAssembly(managed));

        var result = ScriptCompiler.Compile("return 1;", [typeof(object).Assembly.Location, managed], [], Probe);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
    }

    /// <summary>A minimal PE32+ image with no sections and no CLI header: what a native DLL looks like to a PE reader.</summary>
    private static byte[] NativePe()
    {
        var image = new byte[0x40 + 4 + 20 + 240];
        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        BitConverter.GetBytes(0x40).CopyTo(image, 0x3C);          // e_lfanew
        image[0x40] = (byte)'P';
        image[0x41] = (byte)'E';                                  // "PE\0\0"
        BitConverter.GetBytes((ushort)0x8664).CopyTo(image, 0x44); // Machine: x64
        BitConverter.GetBytes((ushort)0).CopyTo(image, 0x46);      // NumberOfSections
        BitConverter.GetBytes((ushort)240).CopyTo(image, 0x54);    // SizeOfOptionalHeader (PE32+)
        BitConverter.GetBytes((ushort)0x2022).CopyTo(image, 0x56); // Characteristics: DLL, executable, large-address-aware
        BitConverter.GetBytes((ushort)0x20B).CopyTo(image, 0x58);  // Magic: PE32+
        BitConverter.GetBytes(16).CopyTo(image, 0x58 + 108);       // NumberOfRvaAndSizes; the 16 directories stay zero
        return image;
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
