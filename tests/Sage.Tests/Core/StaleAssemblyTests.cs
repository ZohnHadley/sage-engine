#nullable enable
using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sage.Simulation;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// A game dll built before the assembly split still references Sage.Engine. The host builds only itself,
// so that dll is never rebuilt, and the loader's own message ("Could not load file or assembly") names
// neither the dll nor the fix. The real thing is compiled here: an "Old" dll whose base class lives in
// a "Gone" assembly that is never written to disk.
public class StaleAssemblyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sage-stale-" + Guid.NewGuid().ToString("N"));

    public StaleAssemblyTests() => Directory.CreateDirectory(_dir);
    // On Windows a loaded dll can't be deleted until the process ends, so what's left stays in temp.
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }

    private static readonly MetadataReference[] Platform =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p) is "System.Runtime.dll" or "System.Private.CoreLib.dll")
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToArray();

    private static byte[] Compile(string name, string source, params MetadataReference[] refs)
    {
        var compilation = CSharpCompilation.Create(name, new[] { CSharpSyntaxTree.ParseText(source) },
            Platform.Concat(refs), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return ms.ToArray();
    }

    // Old<id>.dll: a game module deriving from a class in Gone.dll, which is not in the folder.
    private string WriteStaleGame(string config)
    {
        string gone = "Gone" + Guid.NewGuid().ToString("N")[..8];
        var goneRef = MetadataReference.CreateFromImage(Compile(gone, "namespace G { public class Base { } }"));
        string folder = Path.Combine(_dir, "games", "Old", "bin", config, "net8.0");
        Directory.CreateDirectory(folder);
        // A name of its own per test: the default load context holds one assembly of each name.
        string old = "Old" + Guid.NewGuid().ToString("N")[..8];
        string path = Path.Combine(folder, old + ".dll");
        File.WriteAllBytes(path, Compile(old, "namespace O { public class Game : G.Base { } }", goneRef));
        File.WriteAllText(Path.Combine(_dir, "gone.txt"), gone);
        return path;
    }

    [Fact]
    public void ALoadedGameWithAMissingReferenceSaysWhichDllAndHowToFixIt()
    {
        string path = WriteStaleGame("Debug");
        string gone = File.ReadAllText(Path.Combine(_dir, "gone.txt"));

        var e = Assert.Throws<InvalidOperationException>(() => ModuleManager.LoadGame(path));

        Assert.Contains(path, e.Message);
        Assert.Contains(gone, e.Message);
        Assert.Contains("stale", e.Message);
        Assert.Contains("dotnet build Sage.sln -c Debug", e.Message);
        Assert.NotNull(e.InnerException);
    }

    [Fact]
    public void ModuleAssembliesGetTheSameError()
    {
        string path = WriteStaleGame("Development");
        var e = Assert.Throws<InvalidOperationException>(() => ModuleManager.LoadModules(path).ToList());
        Assert.Contains(path, e.Message);
        Assert.Contains("dotnet build Sage.sln -c Development", e.Message);
    }

    [Fact]
    public void TheMessageNamesEveryMissingAssemblyAHalfLoadedTypeListReports()
    {
        var e = new System.Reflection.ReflectionTypeLoadException(new Type?[] { null, null }, new Exception?[]
        {
            new FileNotFoundException("x", "Sage.Engine, Version=1.0.0.0"),
            new FileNotFoundException("x", "Sage.Engine, Version=1.0.0.0"),
            new FileLoadException("x", "Other.Thing"),
        });
        string message = ModuleManager.StaleAssemblyMessage("/repo/games/Sandbox/Sandbox.dll", e);
        Assert.Contains("'Sage.Engine, Version=1.0.0.0', 'Other.Thing'", message);
        Assert.Contains("-c <config>", message);
    }
}
