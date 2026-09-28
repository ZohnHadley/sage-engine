#nullable enable
using System;
using System.Numerics;
using sage_engine;

namespace sage_engine.Tests;

using Assert = Xunit.Assert;

// Choosing which lights a surface is lit by (docs/design/06 §3.9, TODO F2).
//
// The shader can carry four. Which four is a decision, and a wrong one is the kind of bug that looks
// like flickering: a wall changes which lamp it believes in as the camera moves, and nobody can say why.
public class LightTests
{
    public LightTests() { _ = TestEnv.UserRoot; }

    private static LightSample Lamp(float x, float range = 10f, float brightness = 1f) =>
        new(new Vector3(x, 0, 0), new Vector3(brightness, brightness, brightness), range);

    [Fact]
    public void ALightOutOfRangeIsAbsentRatherThanDim()
    {
        var light = Lamp(0, range: 5f);

        Assert.True(light.InfluenceAt(new Vector3(4.9f, 0, 0)) > 0f);
        Assert.Equal(0f, light.InfluenceAt(new Vector3(5f, 0, 0)));
        Assert.Equal(0f, light.InfluenceAt(new Vector3(50f, 0, 0)));
    }

    [Fact]
    public void TheBrightestNearbyLightWinsRatherThanTheClosestOne()
    {
        // A bright lamp four metres away against a candle at two. Distance alone picks the candle, and
        // the room would be lit by the wrong thing.
        Span<LightSample> lights = stackalloc LightSample[]
        {
            new(new Vector3(2, 0, 0), new Vector3(0.1f, 0.1f, 0.1f), 6f),    // candle
            new(new Vector3(4, 0, 0), new Vector3(4f, 4f, 4f), 20f),         // lamp
        };
        Span<LightSample> chosen = stackalloc LightSample[LightRules.PerObject];

        int found = LightRules.Nearest(lights, Vector3.Zero, chosen);

        Assert.Equal(2, found);
        Assert.Equal(new Vector3(4, 0, 0), chosen[0].Position);
    }

    [Fact]
    public void OnlyFourSurviveAndTheyAreTheStrongestFour()
    {
        Span<LightSample> lights = stackalloc LightSample[]
        {
            Lamp(9), Lamp(1), Lamp(7), Lamp(3), Lamp(5), Lamp(2),
        };
        Span<LightSample> chosen = stackalloc LightSample[LightRules.PerObject];

        int found = LightRules.Nearest(lights, Vector3.Zero, chosen);

        Assert.Equal(LightRules.PerObject, found);
        Assert.Equal(1f, chosen[0].Position.X);
        Assert.Equal(2f, chosen[1].Position.X);
        Assert.Equal(3f, chosen[2].Position.X);
        Assert.Equal(5f, chosen[3].Position.X);
    }

    [Fact]
    public void NoLightsMeansNoneRatherThanBlack()
    {
        Span<LightSample> chosen = stackalloc LightSample[LightRules.PerObject];

        // Nothing in range is the normal case outdoors, and the shader adds nothing rather than
        // multiplying by nothing — the sun and the ambient are what light the world there.
        Assert.Equal(0, LightRules.Nearest(ReadOnlySpan<LightSample>.Empty, Vector3.Zero, chosen));
        Assert.Equal(0, LightRules.Nearest(stackalloc LightSample[] { Lamp(100) }, Vector3.Zero, chosen));
    }

    [Fact]
    public void TheOrderIsTheSameWhateverOrderTheLightsArrivedIn()
    {
        // The renderer walks entities in archetype order, which changes as things spawn and die. If the
        // chosen four depended on that, a wall would change its lighting when something unrelated was
        // destroyed across the map.
        Span<LightSample> forwards = stackalloc LightSample[] { Lamp(1), Lamp(2), Lamp(3), Lamp(4), Lamp(5) };
        Span<LightSample> backwards = stackalloc LightSample[] { Lamp(5), Lamp(4), Lamp(3), Lamp(2), Lamp(1) };

        Span<LightSample> first = stackalloc LightSample[LightRules.PerObject];
        Span<LightSample> second = stackalloc LightSample[LightRules.PerObject];
        LightRules.Nearest(forwards, Vector3.Zero, first);
        LightRules.Nearest(backwards, Vector3.Zero, second);

        for (int i = 0; i < LightRules.PerObject; i++)
            Assert.Equal(first[i].Position.X, second[i].Position.X);
    }
}
