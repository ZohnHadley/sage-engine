#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Public API discipline (issue #31, docs/RELEASING.md): every assembly a game or kit compiles against
// declares its public API in PublicAPI.Shipped.txt and PublicAPI.Unshipped.txt, and the build fails when
// the code and the files disagree (Microsoft.CodeAnalysis.PublicApiAnalyzers, warnings as errors). These
// tests show the files are there and in step, and run the same analyzer on a small library to show what
// a change without them does.
public class DeclaredApiTests
{
    // The assemblies with declared API: src/Directory.Build.props sets SagePublicApi for these.
    private static readonly string[] Declared =
    {
        "Sage.Core", "Sage.Simulation", "Sage.Physics3D", "Sage.Gameplay", "Sage.Client", "Sage.Kits.Rpg", "Sage.Kits.Rpg.Client",
    };

    private static string Src => Path.Combine(TestEnv.FolderAbove("Sage.sln"), "src");

    [Fact]
    public void EveryAssemblyGamesCompileAgainstDeclaresItsPublicApi()
    {
        foreach (string name in Declared)
            foreach (string file in new[] { "PublicAPI.Shipped.txt", "PublicAPI.Unshipped.txt" })
            {
                string path = Path.Combine(Src, name, file);
                Assert.True(File.Exists(path), $"{path} is missing");
                Assert.Equal("#nullable enable", File.ReadLines(path).First());
            }

        // And the build is what holds them to it: the same list turns the analyzer on.
        string props = File.ReadAllText(Path.Combine(Src, "Directory.Build.props"));
        foreach (string name in Declared)
            Assert.Contains($"'$(MSBuildProjectName)' == '{name}'", props);
        Assert.Contains("Microsoft.CodeAnalysis.PublicApiAnalyzers", props);
    }

    // Every type the base assemblies export is declared, in one file or the other: the files describe
    // the assemblies this test runs against, not some older build.
    [Fact]
    public void EveryExportedTypeOfTheBaseIsDeclared()
    {
        foreach (var assembly in EngineAssemblies.Base)
        {
            string name = assembly.GetName().Name!;
            var declared = Lines(name).ToHashSet(StringComparer.Ordinal);
            var missing = assembly.GetExportedTypes()
                .Select(DeclaredName)
                .Where(type => !declared.Any(line => line.StartsWith(type, StringComparison.Ordinal) || line.Contains(" " + type, StringComparison.Ordinal)))
                .ToList();
            Assert.True(missing.Count == 0, $"{name} exports types its PublicAPI files do not declare: {string.Join(", ", missing)}");
        }
    }

    // Until the first release is tagged (0.1.0), nothing has shipped: every declaration is Unshipped, and
    // moving them to Shipped is a step of tagging a release (docs/RELEASING.md).
    [Fact]
    public void BeforeTheFirstReleaseNothingHasShipped()
    {
        if (BuildInfo.EngineSemVersion > new SemVersion(0, 1, 0) || !BuildInfo.EngineVersion.Contains('-'))
            return;   // 0.1.0 or later is tagged: the Shipped files are that release's
        foreach (string name in Declared)
            Assert.Equal(new[] { "#nullable enable" }, File.ReadAllLines(Path.Combine(Src, name, "PublicAPI.Shipped.txt")).Where(l => l.Length > 0));
    }

    // ---- The analyzer, on a small library ----------------------------------------------------------------

    private const string Library = """
        namespace Lib;
        public sealed class Door
        {
            public void Open() { }
        }
        """;

    private const string Declaration = """
        #nullable enable
        Lib.Door
        Lib.Door.Door() -> void
        Lib.Door.Open() -> void
        """;

    [Fact]
    public void AnAssemblyThatMatchesItsFilesBuilds() =>
        Assert.Empty(Analyze(Library, shipped: "#nullable enable", unshipped: Declaration));

    [Fact]
    public void ANewPublicMemberWithoutTheFilesIsABuildError()
    {
        var d = Assert.Single(Analyze(Library.Replace("public void Open() { }", "public void Open() { }\n    public void Lock() { }"),
                                      shipped: "#nullable enable", unshipped: Declaration));
        Assert.Equal("RS0016", d.Id);
        Assert.Contains("Lock", d.GetMessage());
    }

    [Fact]
    public void ARemovedOrChangedPublicMemberWithoutTheFilesIsABuildError()
    {
        // Open() became Open(bool): the old one is declared and gone (RS0017), the new one is not declared (RS0016).
        var diagnostics = Analyze(Library.Replace("public void Open() { }", "public void Open(bool quietly) { }"),
                                  shipped: Declaration, unshipped: "#nullable enable");
        Assert.Contains(diagnostics, d => d.Id == "RS0016" && d.GetMessage().Contains("Open"));
        Assert.Contains(diagnostics, d => d.Id == "RS0017" && d.GetMessage().Contains("Lib.Door.Open() -> void"));
    }

    [Fact]
    public void MakingATypeInternalIsAnApiChangeToo()
    {
        var diagnostics = Analyze(Library.Replace("public sealed class Door", "internal sealed class Door"),
                                  shipped: Declaration, unshipped: "#nullable enable");
        Assert.Contains(diagnostics, d => d.Id == "RS0017" && d.GetMessage().Contains("Lib.Door"));
    }

    private static ImmutableArray<Diagnostic> Analyze(string source, string shipped, string unshipped)
    {
        var compilation = CSharpCompilation.Create("Lib",
            new[] { CSharpSyntaxTree.ParseText(source, path: "Lib.cs") },
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(p => Path.GetFileName(p) is "System.Runtime.dll" or "System.Private.CoreLib.dll")
                .Select(p => MetadataReference.CreateFromFile(p)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        var files = ImmutableArray.Create<AdditionalText>(new Text("PublicAPI.Shipped.txt", shipped), new Text("PublicAPI.Unshipped.txt", unshipped));
        return compilation.WithAnalyzers(PublicApiAnalyzers(), new AnalyzerOptions(files))
            .GetAnalyzerDiagnosticsAsync(CancellationToken.None).GetAwaiter().GetResult()
            .Where(d => d.Id is not ("RS0026" or "RS0027"))   // advice the engine turns off too
            .ToImmutableArray();
    }

    // The analyzer as the engine's build runs it: the package's dll, from the NuGet packages folder the
    // engine's restore put it in, at the version Directory.Packages.props names. (Not a package reference
    // of the tests: that would run it on them.)
    private static ImmutableArray<DiagnosticAnalyzer> PublicApiAnalyzers()
    {
        const string id = "Microsoft.CodeAnalysis.PublicApiAnalyzers";
        string props = File.ReadAllText(Path.Combine(TestEnv.FolderAbove("Sage.sln"), "Directory.Packages.props"));
        var version = System.Text.RegularExpressions.Regex.Match(props, $"<PackageVersion Include=\"{id}\" Version=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(version), $"Directory.Packages.props names no {id}");
        string packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } set ? set
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        string path = Path.Combine(packages, id.ToLowerInvariant(), version, "analyzers", "dotnet", id + ".dll");
        Assert.True(File.Exists(path), $"{path}: build the engine first (its restore brings the analyzer)");
        var analyzers = Assembly.LoadFrom(path).GetTypes()
            .Where(t => typeof(DiagnosticAnalyzer).IsAssignableFrom(t) && !t.IsAbstract
                        && t.GetCustomAttributes<DiagnosticAnalyzerAttribute>().Any(a => a.Languages.Contains(LanguageNames.CSharp)))
            .Select(t => (DiagnosticAnalyzer)Activator.CreateInstance(t)!)
            .ToImmutableArray();
        Assert.NotEmpty(analyzers);
        return analyzers;
    }

    private sealed class Text : AdditionalText
    {
        private readonly string _text;
        public Text(string path, string text) { Path = path; _text = text; }
        public override string Path { get; }
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(_text);
    }

    // ---- The files --------------------------------------------------------------------------------------

    private static IEnumerable<string> Lines(string assembly) =>
        new[] { "PublicAPI.Shipped.txt", "PublicAPI.Unshipped.txt" }
            .SelectMany(file => File.ReadLines(Path.Combine(Src, assembly, file)))
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.StartsWith("*REMOVED*", StringComparison.Ordinal) ? "" : line);

    // How the files write a type: namespace-qualified, nested with '.', generics as Name<T>.
    private static string DeclaredName(Type type)
    {
        string name = type.IsNested ? DeclaredName(type.DeclaringType!) + "." + type.Name : type.FullName!;
        int tick = name.IndexOf('`');
        if (tick < 0) return name;
        string parameters = string.Join(", ", type.GetGenericArguments().Skip(type.DeclaringType?.GetGenericArguments().Length ?? 0).Select(a => a.Name));
        return name[..tick] + (parameters.Length > 0 ? "<" + parameters + ">" : "");
    }
}
