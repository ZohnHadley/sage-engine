#nullable enable
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Phase 5's exit rules (issue #579, REDESIGN §5 Stage C): every sample in games/Samples is small, is a
// `dotnet new` template that `sage new` knows, and the base engine knows nothing about any of them. Gaps a sample
// finds are fixed in the base; a sample never gets its own branch in it. (A base assembly referencing a kit is
// already a build error, SAGE0025.)
public class SampleRulesTests
{
    private static string Repo => TestEnv.FolderAbove("engine_content");

    private static string[] Samples => Directory.GetDirectories(Path.Combine(Repo, "games", "Samples"));

    private static readonly string[] BaseAssemblies =
        { "Sage.Core", "Sage.Simulation", "Sage.Physics3D", "Sage.Gameplay", "Sage.UI", "Sage.Editing" };

    private static string[] Sources(string folder, string pattern) =>
        Directory.GetFiles(folder, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToArray();

    private static string GameId(string sample) =>
        Regex.Match(File.ReadAllText(Path.Combine(sample, "game.json")), "\"id\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;

    [Xunit.Fact]
    public void ThereAreThreeSamples() => Assert.Equal(3, Samples.Length);

    [Xunit.Fact]
    public void EachSamplesCodeStaysUnderAboutFifteenHundredLines()
    {
        foreach (var sample in Samples)
        {
            int lines = Sources(sample, "*.cs").Sum(f => File.ReadAllLines(f).Length);
            Assert.True(lines <= 1500, $"{Path.GetFileName(sample)} has {lines} lines of C#");
        }
    }

    // `sage new` knowing each one is SageCliVerbTests.New_KnowsEveryTemplateThePackageHas.
    [Xunit.Fact]
    public void EachSampleIsATemplate()
    {
        foreach (var sample in Samples)
        {
            string json = File.ReadAllText(Path.Combine(sample, ".template.config", "template.json"));
            Assert.Matches("\"shortName\"\\s*:\\s*\"sage-sample-", json);
            Assert.Contains("\"sourceName\": \"MyGame\"", json);   // renames the shared sage-game-data project
        }
    }

    [Xunit.Fact]
    public void NoBaseAssemblyNamesASample()
    {
        var ids = Samples.Select(GameId).ToArray();
        Assert.All(ids, id => Assert.False(string.IsNullOrEmpty(id)));
        foreach (var assembly in BaseAssemblies)
            foreach (var file in Sources(Path.Combine(Repo, "src", assembly), "*.cs"))
            {
                string text = File.ReadAllText(file);
                foreach (var id in ids)
                    Assert.False(text.Contains(id, System.StringComparison.OrdinalIgnoreCase), $"{file} names the sample '{id}'");
            }
    }
}
