#nullable enable
using System.Collections.Generic;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Which effects a changed shader file recompiles (issue 4h-3): the include graph, from text alone.
public class ShaderIncludesTests
{
    [Xunit.Fact]
    public void IncludesAreReadFromTheLinesThatAreNotComments()
    {
        const string source = """
            #include "common.fxh"
            // #include "gone.fxh"
            /* #include "also_gone.fxh"
               #include "still_gone.fxh" */
              #  include <lib/noise.fxh>
            float4 x; // #include "trailing.fxh"
            """;

        Assert.Equal(new[] { "common.fxh", "lib/noise.fxh" }, ShaderIncludes.Includes(source));
    }

    [Xunit.Fact]
    public void AChangedHeaderRecompilesEveryEffectThatReachesIt()
    {
        var sources = new Dictionary<string, string>
        {
            ["lit.fx"] = "#include \"common.fxh\"",
            ["sprite.fx"] = "#include \"common.fxh\"",
            ["debug.fx"] = "float4 x;",
            ["common.fxh"] = "#include \"lib/noise.fxh\"",
            ["lib/noise.fxh"] = "#include \"../consts.fxh\"",
            ["consts.fxh"] = "",
            ["lib/unused.fxh"] = "",
        };

        Assert.Equal(new[] { "lit.fx", "sprite.fx" }, ShaderIncludes.Dependents(sources, "common.fxh"));
        Assert.Equal(new[] { "lit.fx", "sprite.fx" }, ShaderIncludes.Dependents(sources, "consts.fxh"));   // two hops and a `..`
        Assert.Equal(new[] { "debug.fx" }, ShaderIncludes.Dependents(sources, "debug.fx"));              // an effect is its own dependent
        Assert.Empty(ShaderIncludes.Dependents(sources, "lib/unused.fxh"));
        Assert.Empty(ShaderIncludes.Dependents(sources, "missing.fxh"));
    }

    [Xunit.Fact]
    public void AnIncludeCycleEnds()
    {
        var sources = new Dictionary<string, string>
        {
            ["a.fx"] = "#include \"b.fxh\"",
            ["b.fxh"] = "#include \"c.fxh\"",
            ["c.fxh"] = "#include \"b.fxh\"",
        };

        Assert.Equal(new[] { "a.fx" }, ShaderIncludes.Dependents(sources, "c.fxh"));
    }
}
