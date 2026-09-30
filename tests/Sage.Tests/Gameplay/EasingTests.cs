#nullable enable
using System;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Easing (issue #90): the curves tweens, camera blends and 4c's UI transitions are driven by.
public class EasingTests
{
    private static Ease[] All => Enum.GetValues<Ease>();

    [Fact]
    public void EveryCurveStartsAtZeroAndEndsAtOne()
    {
        foreach (var ease in All)
        {
            Assert.Equal(0f, Easing.Apply(ease, 0f));
            Assert.Equal(1f, Easing.Apply(ease, 1f));
            // Outside [0, 1] is clamped, and NaN stays at the start.
            Assert.Equal(0f, Easing.Apply(ease, -3f));
            Assert.Equal(1f, Easing.Apply(ease, 7f));
            Assert.Equal(0f, Easing.Apply(ease, float.NaN));
            // And close to the ends is close to the ends: no curve jumps at either.
            Assert.InRange(Easing.Apply(ease, 1e-4f), -0.01f, 0.01f);
            Assert.InRange(Easing.Apply(ease, 1f - 1e-4f), 0.99f, 1.01f);
        }
    }

    [Fact]
    public void TheMonotonicCurvesNeverGoBack()
    {
        foreach (var ease in All)
        {
            if (!Easing.IsMonotonic(ease)) continue;
            float previous = 0f;
            for (int i = 1; i <= 1000; i++)
            {
                float v = Easing.Apply(ease, i / 1000f);
                Assert.True(v >= previous - 1e-6f, $"{ease} goes back at t={i / 1000f}: {previous} -> {v}");
                Assert.InRange(v, 0f, 1f);
                previous = v;
            }
        }
    }

    [Fact]
    public void TheOthersOvershootWobbleOrBounceOnPurpose()
    {
        // Back overshoots past 1 on the way out and dips below 0 on the way in.
        Assert.True(Easing.Apply(Ease.BackOut, 0.6f) > 1f);
        Assert.True(Easing.Apply(Ease.BackIn, 0.3f) < 0f);
        Assert.True(Easing.Apply(Ease.ElasticOut, 0.1f) > 1f);
        Assert.False(Easing.IsMonotonic(Ease.BounceOut));
        float peak = Easing.Apply(Ease.BounceOut, 1f / 2.75f);
        Assert.True(Easing.Apply(Ease.BounceOut, 0.5f) < peak);   // it came back down
    }

    [Fact]
    public void KnownValuesAndSymmetry()
    {
        Assert.Equal(0.25f, Easing.Apply(Ease.QuadIn, 0.5f), 5);
        Assert.Equal(0.75f, Easing.Apply(Ease.QuadOut, 0.5f), 5);
        Assert.Equal(0.125f, Easing.Apply(Ease.CubicIn, 0.5f), 5);
        Assert.Equal(0.5f, Easing.Apply(Ease.SmoothStep, 0.5f), 5);
        Assert.Equal(0.5f, Easing.Apply(Ease.SmootherStep, 0.5f), 5);
        Assert.Equal(15f, Easing.Lerp(Ease.Linear, 10f, 20f, 0.5f), 5);

        // Every InOut passes through the middle, and In(t) mirrors Out(1 - t).
        foreach (var ease in All)
        {
            string name = ease.ToString();
            if (name.EndsWith("InOut")) Assert.Equal(0.5f, Easing.Apply(ease, 0.5f), 4);
            if (name.EndsWith("In") && Enum.TryParse<Ease>(name[..^2] + "Out", out var outward))
                for (float t = 0.05f; t < 1f; t += 0.1f)
                    Assert.Equal(1f - Easing.Apply(ease, 1f - t), Easing.Apply(outward, t), 4);
        }
    }

    [Theory]
    [InlineData("linear", Ease.Linear)]
    [InlineData("QuadInOut", Ease.QuadInOut)]
    [InlineData("quad_in_out", Ease.QuadInOut)]
    [InlineData("easeInOutQuad", Ease.QuadInOut)]
    [InlineData("in-out-quad", Ease.QuadInOut)]
    [InlineData("easeOutBounce", Ease.BounceOut)]
    [InlineData("sine_in", Ease.SineIn)]
    [InlineData("InSine", Ease.SineIn)]
    [InlineData("cubicout", Ease.CubicOut)]
    [InlineData("smoothstep", Ease.SmoothStep)]
    [InlineData("SmootherStep", Ease.SmootherStep)]
    [InlineData("ElasticInOut", Ease.ElasticInOut)]
    public void CurvesParseTheWayContentWritesThem(string text, Ease expected)
    {
        Assert.True(Easing.TryParse(text, out var ease), text);
        Assert.Equal(expected, ease);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ease")]
    [InlineData("quad")]
    [InlineData("wobble_in")]
    [InlineData("in")]
    [InlineData("a_name_far_too_long_to_be_any_curve_at_all")]
    public void NonsenseDoesNotParse(string text) => Assert.False(Easing.TryParse(text, out _));

    [Fact]
    public void EveryEnumNameParsesBackToItself()
    {
        foreach (var ease in All)
        {
            Assert.True(Easing.TryParse(ease.ToString(), out var parsed), ease.ToString());
            Assert.Equal(ease, parsed);
        }
    }
}

// Measured apart from the rest, with the other measurements (TestSupport.cs): allocation is per thread,
// but a parallel test's GC would still be noise.
[Collection(MeasurementsCollection.Name)]
public class EasingAllocationTests
{
    [Fact]
    public void ApplyingAndParsingAllocateNothing()
    {
        var all = Enum.GetValues<Ease>();
        float sink = 0f;
        int step = 0;
        AllocationProbe.AssertNone(100, () =>
        {
            foreach (var ease in all)
            {
                sink += Easing.Apply(ease, step / 100f);
                if (Easing.TryParse("easeInOutCubic", out var parsed)) sink += (int)parsed;
            }
            step++;
        });
        Assert.True(sink > 0f);
    }
}
