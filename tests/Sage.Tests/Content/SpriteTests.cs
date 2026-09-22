#nullable enable
using System;
using System.Linq;
using System.Numerics;
using sage_engine;

namespace sage_engine.Tests;

using Assert = Xunit.Assert;

// Billboard sprites, simulation side (docs/design/06 §3.8, 12 §3): direction selection, animation
// frames and the sheet record. The quads themselves are client-side and are checked by screenshots.
public class SpriteMathTests
{
    private const float Deg = MathF.PI / 180f;

    // The camera stands around a sprite at the origin that faces +Z (yaw 0).
    private static int FromAngle(float degrees, int directions, out bool flip)
    {
        float a = degrees * Deg;
        var camera = new Vector3(MathF.Sin(a) * 10f, 0, MathF.Cos(a) * 10f);
        return SpriteMath.DirectionIndex(Vector3.Zero, camera, 0f, directions, out flip);
    }

    [Theory]
    [InlineData(0, 0)]      // the camera is in front of it: the front view
    [InlineData(44, 1)]
    [InlineData(90, 2)]     // from its left: the side view
    [InlineData(180, 4)]    // from behind
    [InlineData(270, 6)]    // from its right
    [InlineData(359, 0)]    // wraps
    public void EightDirections_PickTheGroupFacingTheCamera(float degrees, int expected)
    {
        Assert.Equal(expected, FromAngle(degrees, 8, out bool flip));
        Assert.False(flip);
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(90, 2, false)]
    [InlineData(180, 4, false)]
    [InlineData(225, 3, true)]    // 5 mirrors 3
    [InlineData(270, 2, true)]    // 6 mirrors 2
    [InlineData(315, 1, true)]    // 7 mirrors 1
    public void FiveDirections_MirrorTheOtherThree(float degrees, int expected, bool expectFlip)
    {
        Assert.Equal(expected, FromAngle(degrees, 5, out bool flip));
        Assert.Equal(expectFlip, flip);
    }

    [Fact]
    public void OneDirection_AlwaysUsesGroupZero()
    {
        Assert.Equal(0, FromAngle(123, 1, out bool flip));
        Assert.False(flip);
    }

    [Fact]
    public void TheEntitysOwnYawTurnsTheSelection()
    {
        // The camera is in front (+Z). Turning the entity 90° left shows the camera its other side.
        var camera = new Vector3(0, 0, 10);
        Assert.Equal(0, SpriteMath.DirectionIndex(Vector3.Zero, camera, 0f, 8, out _));
        Assert.Equal(6, SpriteMath.DirectionIndex(Vector3.Zero, camera, 90 * Deg, 8, out _));
        Assert.Equal(4, SpriteMath.DirectionIndex(Vector3.Zero, camera, 180 * Deg, 8, out _));
        Assert.Equal(2, SpriteMath.DirectionIndex(Vector3.Zero, camera, -90 * Deg, 8, out _));
    }

    [Fact]
    public void Yaw_ReadsTheRotationAboutY()
    {
        Assert.Equal(0f, SpriteMath.Yaw(Quaternion.Identity), 5);
        Assert.Equal(90 * Deg, SpriteMath.Yaw(Quaternion.CreateFromYawPitchRoll(90 * Deg, 0, 0)), 4);
        // Pitch and roll don't change the yaw of a character standing up.
        Assert.Equal(30 * Deg, SpriteMath.Yaw(Quaternion.CreateFromYawPitchRoll(30 * Deg, 0.2f, 0)), 3);
    }

    [Fact]
    public void FrameAt_PlaysAtFpsAndLoops()
    {
        var clip = new SpriteAnimation { Fps = 4, Loop = true, Dirs = { new() { 10, 11, 12 } } };
        Assert.Equal(10, SpriteMath.FrameAt(clip, 0, 0f));
        Assert.Equal(11, SpriteMath.FrameAt(clip, 0, 0.26f));
        Assert.Equal(12, SpriteMath.FrameAt(clip, 0, 0.51f));
        Assert.Equal(10, SpriteMath.FrameAt(clip, 0, 0.76f));     // wraps
        Assert.Equal(11, SpriteMath.FrameAt(clip, 0, 1.01f));
    }

    [Fact]
    public void FrameAt_HoldsTheLastFrameWhenNotLooping_AndFallsBackToDirectionZero()
    {
        var clip = new SpriteAnimation { Fps = 10, Loop = false, Dirs = { new() { 3, 4 } } };
        Assert.Equal(3, SpriteMath.FrameAt(clip, 0, 0f));
        Assert.Equal(4, SpriteMath.FrameAt(clip, 0, 5f));
        Assert.Equal(4, SpriteMath.FrameAt(clip, 7, 5f));          // the sheet has one direction group
        Assert.Equal(-1, SpriteMath.FrameAt(new SpriteAnimation(), 0, 0f));
    }
}

public class SpriteSheetRecordTests
{
    public SpriteSheetRecordTests() { _ = TestEnv.UserRoot; }

    private const string Sheet = """
        [{ "type": "sprite_sheet", "id": "goblin", "texture": "textures/goblin.png", "directions": 5,
           "size": [1.1, 1.7], "mode": "Cylindrical", "material": "sage:sprite_lit",
           "frames": [ { "rect": [0, 0, 64, 96], "pivot": [32, 94] }, { "rect": [64, 0, 64, 96] } ],
           "animations": {
             "walk":   { "fps": 8, "loop": true, "dirs": [[0, 1], [1, 0]] },
             "attack": { "fps": 12, "loop": false, "dirs": [[1]] } } }]
        """;

    private static SpriteSheetRecord Load(out RecordStore store)
    {
        var fx = new MountFixture();
        // The sheet names a material, and record references are validated across types (05 §3.5).
        fx.Write("engine", "data/materials.json", """[{ "type": "material", "id": "sprite_lit", "effect": "shaders/sprite.mgfxo" }]""");
        fx.Write("game", "data/sheets.json", Sheet);
        fx.Mount("engine", "sage");
        fx.Mount("game", "sandbox");
        store = new RecordStore();
        store.Register<MaterialRecord>();
        store.Register<SpriteSheetRecord>();
        store.Load(fx.Vfs);
        return store.Get<SpriteSheetRecord>(new RecordId("sandbox", "goblin"));
    }

    [Fact]
    public void Parses()
    {
        var sheet = Load(out var store);
        Assert.Equal(0, store.ErrorCount);
        Assert.Equal(AssetPath.Intern("textures/goblin.png"), sheet.Texture);
        Assert.Equal(5, sheet.Directions);
        Assert.Equal(new Vector2(1.1f, 1.7f), sheet.Size);
        Assert.Equal(BillboardMode.Cylindrical, sheet.Mode);
        Assert.Equal(new RecordId("sage", "sprite_lit"), sheet.Material);
        Assert.Equal(new[] { 0, 0, 64, 96 }, sheet.Frames[0].Rect);
        Assert.Equal(new[] { 32, 94 }, sheet.Frames[0].Pivot);
        Assert.Empty(sheet.Frames[1].Pivot);                        // the renderer defaults it to bottom centre
    }

    [Fact]
    public void ClipsAreIndexedByName_InAStableOrder()
    {
        var sheet = Load(out _);
        Assert.Equal(new[] { "attack", "walk" }, sheet.ClipNames);   // sorted, so indices don't depend on JSON order
        Assert.Equal(0, sheet.ClipIndex("Attack"));
        Assert.Equal(1, sheet.ClipIndex("walk"));
        Assert.Equal(-1, sheet.ClipIndex("die"));

        var walk = sheet.Clip(sheet.ClipIndex("walk"))!;
        Assert.Equal(8f, walk.Fps);
        Assert.True(walk.Loop);
        Assert.Equal(new[] { 0, 1 }, walk.Dirs[0]);
        Assert.Null(sheet.Clip(9));
    }
}

public class SpriteAnimationSystemTests
{
    public SpriteAnimationSystemTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void AdvancesPlayingAnimatorsByTheTick()   // 12 §3: the simulation owns animation time
    {
        using var world = new World("anim");
        var playing = world.Create();
        world.Add(playing, SpriteAnimator.Play(0));
        var stopped = world.Create();
        world.Add(stopped, new SpriteAnimator { Clip = 0, Playing = false });
        var fast = world.Create();
        world.Add(fast, SpriteAnimator.Play(1, speed: 2f));
        world.AddSystem(new SpriteAnimationSystem(world), Phase.Animation);

        for (int i = 0; i < 10; i++) world.RunFixed(0.1f);

        Assert.Equal(1f, world.Get<SpriteAnimator>(playing).Time, 4);
        Assert.Equal(0f, world.Get<SpriteAnimator>(stopped).Time);
        Assert.Equal(2f, world.Get<SpriteAnimator>(fast).Time, 4);
    }
}
