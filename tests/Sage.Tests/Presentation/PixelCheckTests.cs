#nullable enable
using System;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Pixel checks of a drawn frame (issue #318, src/Sage.Simulation/Rendering/PixelCheck.cs): the spec file and
// the comparison, on frames made here. The host's `r_pixelcheck` reads the real back buffer and runs exactly
// this; RenderCheckSceneTests proves the test scene's regions see what they say.
public class PixelCheckTests
{
    // A 10x10 frame: the top half one colour, the bottom half another.
    private static PixelFrame Halves(Vector3 top, Vector3 bottom, int width = 10, int height = 10)
    {
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var c = y < height / 2 ? top : bottom;
                int i = (y * width + x) * 4;
                rgba[i] = (byte)MathF.Round(c.X * 255f);
                rgba[i + 1] = (byte)MathF.Round(c.Y * 255f);
                rgba[i + 2] = (byte)MathF.Round(c.Z * 255f);
                rgba[i + 3] = 255;
            }
        return new PixelFrame(width, height, rgba);
    }

    private const string TwoRegions = """
        {
          // comments and trailing commas, like every data file
          "width": 10, "height": 10,
          "regions": [
            { "name": "top", "rect": [0, 0, 1, 0.5], "sees": "sky" },
            { "name": "bottom", "rect": [0, 0.5, 1, 0.5] },
          ],
          "checks": [ CHECKS ]
        }
        """;

    private static PixelCheckSpec Spec(string checks) => PixelCheckSpec.Parse(TwoRegions.Replace("CHECKS", checks));

    private static PixelCheckResult Only(PixelCheckSpec spec, PixelFrame frame, bool shaders = true) =>
        Assert.Single(PixelCheck.Run(spec, frame, shaders));

    [Fact]
    public void ASpecReadsRegionsAndChecks()
    {
        var spec = Spec("""{ "name": "dark below", "region": "bottom", "than": "top", "measure": "luma", "max": -0.2, "when": "shaders" },""" +
                        """{ "region": "top", "color": [0.5, 0.6, 0.9], "tolerance": 0.1, "when": "noShaders" }""");
        Assert.Equal(10, spec.Width);
        Assert.Equal(2, spec.Regions.Count);
        Assert.Equal(new PixelRegion("top", 0, 0, 1, 0.5f, "sky"), spec.Regions[0]);
        Assert.Equal("", spec.Regions[1].Sees);
        var dark = spec.Checks[0];
        Assert.Equal(("dark below", "bottom", "top", PixelMeasure.Luma, -0.2f, PixelCheckWhen.Shaders),
            (dark.Name, dark.Region, dark.Than, dark.Measure, dark.Max!.Value, dark.When));
        Assert.Null(dark.Min);
        var colour = spec.Checks[1];
        Assert.Equal("top", colour.Name);   // a check without a name is called after its region
        Assert.Equal(new Vector3(0.5f, 0.6f, 0.9f), colour.Color);
        Assert.Equal(PixelCheckWhen.NoShaders, colour.When);
    }

    // A mistake in a spec is an error naming it, never a check that quietly checks less.
    [Theory]
    [InlineData("""{ "region": "nowhere", "min": 0 }""", "no region 'nowhere'")]
    [InlineData("""{ "region": "top", "than": "nowhere", "min": 0 }""", "no region 'nowhere'")]
    [InlineData("""{ "region": "top" }""", "give min and/or max, or a color")]
    [InlineData("""{ "region": "top", "min": 0, "color": [1, 1, 1] }""", "give min and/or max, or a color")]
    [InlineData("""{ "region": "top", "color": [1, 1, 1], "than": "bottom" }""", "a color check has no 'than'")]
    [InlineData("""{ "region": "top", "measure": "brightness", "min": 0 }""", "unknown measure 'brightness'")]
    [InlineData("""{ "region": "top", "when": "sometimes", "min": 0 }""", "unknown 'when' 'sometimes'")]
    [InlineData("""{ "region": "top", "minimum": 0 }""", "unknown key 'minimum'")]
    [InlineData("""{ "region": "top", "color": [1, 1], "tolerance": 0.1 }""", "'color' is 3 numbers")]
    public void AMistakeInASpecIsAnError(string check, string message)
    {
        var ex = Assert.Throws<FormatException>(() => Spec(check));
        Assert.Contains(message, ex.Message);
    }

    [Theory]
    [InlineData("""{ "regions": [ { "name": "a", "rect": [0.5, 0, 0.6, 1] } ], "checks": [ { "region": "a", "min": 0 } ] }""", "not inside the frame")]
    [InlineData("""{ "regions": [ { "name": "a", "rect": [0, 0, 1, 1] }, { "name": "a", "rect": [0, 0, 1, 1] } ], "checks": [ { "region": "a", "min": 0 } ] }""", "two regions are called 'a'")]
    [InlineData("""{ "regions": [ { "name": "a", "rect": [0, 0, 1, 1] } ], "checks": [] }""", "no checks")]
    [InlineData("""{ "width": 10, "regions": [ { "name": "a", "rect": [0, 0, 1, 1] } ], "checks": [ { "region": "a", "min": 0 } ] }""", "both width and height")]
    [InlineData("""[ 1 ]""", "is an object")]
    [InlineData("""{ "regions": """, "not JSON")]
    public void AMistakeInTheSpecsShapeIsAnError(string json, string message)
    {
        var ex = Assert.Throws<FormatException>(() => PixelCheckSpec.Parse(json));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void ARegionsMeanIsTheAverageOfItsPixels()
    {
        var frame = Halves(new Vector3(0.2f, 0.4f, 0.8f), new Vector3(1f, 1f, 1f));
        var top = frame.Mean(new PixelRegion("top", 0, 0, 1, 0.5f, ""));
        Assert.Equal(0.2f, top.X, 2);
        Assert.Equal(0.4f, top.Y, 2);
        Assert.Equal(0.8f, top.Z, 2);
        // Straddling the edge: a fifth of its rows are the top's.
        var mixed = frame.Mean(new PixelRegion("mixed", 0, 0.4f, 1, 0.5f, ""));
        Assert.Equal((0.2f + 4f) / 5f, mixed.X, 2);
        // A region smaller than a pixel still covers one.
        Assert.Equal((0, 0, 1, 1), frame.Pixels(new PixelRegion("speck", 0, 0, 0.001f, 0.001f, "")));
    }

    [Fact]
    public void TheMeasuresAreLumaChannelsChromaAndTheBlueRedBalance()
    {
        var c = new Vector3(0.2f, 0.5f, 0.9f);
        Assert.Equal(0.2f * 0.2126f + 0.5f * 0.7152f + 0.9f * 0.0722f, PixelCheck.Measure(PixelMeasure.Luma, c), 5);
        Assert.Equal(0.2f, PixelCheck.Measure(PixelMeasure.R, c));
        Assert.Equal(0.5f, PixelCheck.Measure(PixelMeasure.G, c));
        Assert.Equal(0.9f, PixelCheck.Measure(PixelMeasure.B, c));
        Assert.Equal(0.7f, PixelCheck.Measure(PixelMeasure.Chroma, c), 5);
        Assert.Equal(0f, PixelCheck.Measure(PixelMeasure.Chroma, new Vector3(0.3f)), 5);   // grey
        Assert.Equal(0.7f, PixelCheck.Measure(PixelMeasure.BlueMinusRed, c), 5);
        Assert.Equal(-0.7f, PixelCheck.Measure(PixelMeasure.RedMinusBlue, c), 5);
    }

    // What the render check asks of a frame: a shadow darker than the ground beside it. Drawn, it passes; a
    // frame where the shadow stopped drawing (both halves alike) fails, and says by how much.
    [Fact]
    public void ARegionComparedWithAnotherPassesOrFails()
    {
        var spec = Spec("""{ "name": "shadow", "region": "bottom", "than": "top", "max": -0.35 }""");
        var drawn = Only(spec, Halves(new Vector3(1f), new Vector3(0.2f)));
        Assert.Equal(PixelCheckOutcome.Passed, drawn.Outcome);
        Assert.Contains("minus top -0.800", drawn.Detail);

        var lost = Only(spec, Halves(new Vector3(1f), new Vector3(1f)));
        Assert.Equal(PixelCheckOutcome.Failed, lost.Outcome);
        Assert.Contains("want at most -0.350", lost.Detail);
    }

    [Fact]
    public void AColourCheckAllowsItsToleranceOnEveryChannel()
    {
        var spec = Spec("""{ "name": "fog", "region": "top", "color": [0.85, 0.6, 0.35], "tolerance": 0.05 }""");
        Assert.Equal(PixelCheckOutcome.Passed, Only(spec, Halves(new Vector3(0.88f, 0.57f, 0.35f), Vector3.Zero)).Outcome);
        var white = Only(spec, Halves(new Vector3(1f), Vector3.Zero));
        Assert.Equal(PixelCheckOutcome.Failed, white.Outcome);
        Assert.Contains("off by 0.650", white.Detail);
    }

    [Fact]
    public void MinAndMaxBoundAMeasure()
    {
        var spec = Spec("""{ "region": "top", "measure": "b-r", "min": 0.3, "max": 0.6 }""");
        Assert.Equal(PixelCheckOutcome.Passed, Only(spec, Halves(new Vector3(0.3f, 0.5f, 0.8f), Vector3.Zero)).Outcome);
        Assert.Equal(PixelCheckOutcome.Failed, Only(spec, Halves(new Vector3(0.1f, 0.5f, 0.9f), Vector3.Zero)).Outcome);   // 0.8: too blue
        Assert.Equal(PixelCheckOutcome.Failed, Only(spec, Halves(new Vector3(0.5f), Vector3.Zero)).Outcome);              // grey
    }

    // A host built without shaders draws clear colours only: checks that need the shaders are skipped there,
    // and checks of the clear colour are skipped where the shaders drew.
    [Fact]
    public void ChecksRunOnlyOnTheFramesTheyAreFor()
    {
        var spec = Spec("""{ "name": "needs", "region": "top", "when": "shaders", "min": 0.9 },""" +
                        """{ "name": "without", "region": "top", "when": "noShaders", "max": 0.1 },""" +
                        """{ "name": "any", "region": "top", "min": 0 }""");
        var frame = Halves(Vector3.Zero, Vector3.Zero);

        var withShaders = PixelCheck.Run(spec, frame, shaders: true);
        Assert.Equal(new[] { PixelCheckOutcome.Failed, PixelCheckOutcome.Skipped, PixelCheckOutcome.Passed }, withShaders.Select(r => r.Outcome));
        var without = PixelCheck.Run(spec, frame, shaders: false);
        Assert.Equal(new[] { PixelCheckOutcome.Skipped, PixelCheckOutcome.Passed, PixelCheckOutcome.Passed }, without.Select(r => r.Outcome));
        Assert.Equal("needs the compiled shaders", without[0].Detail);
    }

    // The regions were placed for a frame's shape: another size is one failure up front, naming the cvars.
    [Fact]
    public void AFrameOfAnotherSizeFails()
    {
        var spec = Spec("""{ "region": "top", "min": 0 }""");
        var results = PixelCheck.Run(spec, Halves(Vector3.Zero, Vector3.Zero, 20, 10), shaders: true);
        Assert.Equal(PixelCheckOutcome.Failed, results[0].Outcome);
        Assert.Contains("the frame is 20x10, the spec's regions are for 10x10", results[0].Detail);
        Assert.Contains("vid_width", results[0].Detail);
        Assert.Equal(PixelCheckOutcome.Passed, results[1].Outcome);

        var anySize = PixelCheckSpec.Parse("""{ "regions": [ { "name": "a", "rect": [0, 0, 1, 1] } ], "checks": [ { "region": "a", "min": 0 } ] }""");
        Assert.Equal(PixelCheckOutcome.Passed, Only(anySize, Halves(Vector3.Zero, Vector3.Zero, 20, 10)).Outcome);
    }
}
