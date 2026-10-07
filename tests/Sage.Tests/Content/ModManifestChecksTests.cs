#nullable enable
using System.IO;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Small dependency checks on mod.json (issue #401): a mod that names itself, or needs a mod it also says it
// is incompatible with, is refused with what is wrong rather than with a confusing load-order reason.
public class ModManifestChecksTests
{
    public ModManifestChecksTests() { _ = TestEnv.UserRoot; }

    [Theory]
    [InlineData("""{ "id": "x", "dependencies": { "x": "" } }""", "names its own id 'x'")]
    [InlineData("""{ "id": "x", "loadAfter": ["x"] }""", "names its own id 'x'")]
    [InlineData("""{ "id": "x", "incompatible": ["x"] }""", "names its own id 'x'")]
    [InlineData("""{ "id": "x", "dependencies": { "y": "^1.0" }, "incompatible": ["y"] }""", "'y' is both a dependency and incompatible")]
    public void AModJsonThatContradictsItselfIsAnError(string json, string expected)
    {
        string folder = TestEnv.NewTempDir();
        File.WriteAllText(Path.Combine(folder, "mod.json"), json);
        Assert.Contains(expected, Assert.Throws<InvalidDataException>(() => ModManifest.Load(folder)).Message);
    }
}
