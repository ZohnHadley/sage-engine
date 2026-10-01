#nullable enable
using System;
using System.Numerics;

namespace Sage.Tests;

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

    // Sprites batch while the lamps lighting them are the same four (Renderer.DrawSprites): the same
    // lights in another order still batch, one more or one different does not.
    [Fact]
    public void TwoChoicesAreTheSameSetWhateverTheirOrder()
    {
        Span<LightSample> a = stackalloc LightSample[] { Lamp(1), Lamp(2) };
        Span<LightSample> b = stackalloc LightSample[] { Lamp(2), Lamp(1) };
        Span<LightSample> c = stackalloc LightSample[] { Lamp(1), Lamp(3) };

        Assert.True(LightRules.SameSet(a, b));
        Assert.False(LightRules.SameSet(a, c));
        Assert.False(LightRules.SameSet(a, a[..1]));
        Assert.True(LightRules.SameSet(ReadOnlySpan<LightSample>.Empty, ReadOnlySpan<LightSample>.Empty));
    }

    // A sprite sits in the world's light by default: the engine's sprite material is the sprite shader's
    // Lit technique (sun, shadows, ambient and lamps), and full-bright is `sage:sprite_unlit`, by name.
    [Fact]
    public void SpritesAreLitByDefault_AndFullBrightIsOptIn()
    {
        string game = System.IO.Path.Combine(TestEnv.FolderAbove("Sage.sln"), "tests", "games", "skeletal");
        using var app = HeadlessApp.ForGame(game).WithEngineContent()
            .OnRegistered(a => a.Records.Register<MaterialRecord>())   // the client's to register; headless asks for it
            .Boot();
        Assert.Equal("Lit", app.Records.Get<MaterialRecord>(SpriteSheetRecord.DefaultMaterial).Technique);
        Assert.Equal("Lit", app.Records.Get<MaterialRecord>(new RecordId("sage", "sprite_lit")).Technique);
        Assert.Equal("Unlit", app.Records.Get<MaterialRecord>(new RecordId("sage", "sprite_unlit")).Technique);
    }

    // The switch (issue 4h-7): a lamp is lit or not by entity I/O, so data can light it at dusk. TurnOn,
    // TurnOff and Toggle are routed to lights, beside a branch's or a door's Toggle; a light that is off
    // gives nothing (`Lit`, what the client's light extract asks), and the part can start it dark.
    [Fact]
    public void ALightIsSwitchedByEntityIO()
    {
        using var app = HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule(), new LightsModule())
            .File("data/lamps.json", """
                [{ "type": "prefab", "id": "dark_lamp", "parts": { "light": { "range": 6, "off": true } } },
                 { "type": "prefab", "id": "lamp", "parts": { "light": { "range": 6 } } }]
                """)
            .Boot("lights");
        Assert.Equal(0, app.Records.ErrorCount);
        var world = app.World;
        var dark = world.Spawn(new RecordId("sage", "dark_lamp"), Vector3.Zero);
        var lit = world.Spawn(new RecordId("sage", "lamp"), Vector3.Zero);
        Assert.False(world.Get<PointLight>(dark).Lit);
        Assert.True(world.Get<PointLight>(lit).Lit);                       // on by default, as every light was

        var io = world.IO();
        io.FireInput(dark, "TurnOn");
        io.FireInput(lit, "TurnOff");
        world.RunFixed(1f / 60f);
        Assert.True(world.Get<PointLight>(dark).Lit);
        Assert.False(world.Get<PointLight>(lit).Lit);

        io.FireInput(dark, "Toggle");
        io.FireInput(lit, "Toggle");
        world.RunFixed(1f / 60f);
        Assert.False(world.Get<PointLight>(dark).Lit);
        Assert.True(world.Get<PointLight>(lit).Lit);

        // A light with no range or intensity is never lit, whatever the switch says.
        Assert.False(new PointLight { Colour = Vector3.One, Range = 0f, Intensity = 1f }.Lit);
        Assert.Equal("sage.gameplay.lights", app.Engine.Registrations.OwnerOf("entity input", "TurnOn@sage:point_light"));
    }
}
