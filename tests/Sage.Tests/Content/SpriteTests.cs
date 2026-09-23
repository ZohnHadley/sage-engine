#nullable enable
using System;
using System.Linq;
using System.Numerics;
using Friflo.Engine.ECS;
using sage_engine;

namespace sage_engine.Tests;

using Assert = Xunit.Assert;

// Billboard sprites, simulation side (docs/design/06 §3.8, 12 §3): direction selection, animation
// frames and the sheet record. The quads themselves are client-side and are checked by screenshots.
public class SpriteMathTests
{
    private const float Deg = MathF.PI / 180f;

    // The camera stands `degrees` around a sprite at the origin, measured from the sprite's front
    // (yaw 0 faces -Z, SageMath) and growing counter-clockwise seen from above: toward its left.
    private static int FromAngle(float degrees, int directions, out bool flip)
    {
        var camera = SageMath.ForwardFromYaw(degrees * Deg) * 10f;
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
        // The camera stands at +Z, which is *behind* an entity at yaw 0 (it faces -Z). Turning the
        // entity toward the camera walks the group back round to the front view.
        var camera = new Vector3(0, 0, 10);
        Assert.Equal(4, SpriteMath.DirectionIndex(Vector3.Zero, camera, 0f, 8, out _));
        Assert.Equal(2, SpriteMath.DirectionIndex(Vector3.Zero, camera, 90 * Deg, 8, out _));
        Assert.Equal(0, SpriteMath.DirectionIndex(Vector3.Zero, camera, 180 * Deg, 8, out _));
        Assert.Equal(6, SpriteMath.DirectionIndex(Vector3.Zero, camera, -90 * Deg, 8, out _));
    }

    // The ordering is Doom's, so a sheet ripped from Doom or Daggerfall drops straight in
    // (doomwiki.org/wiki/Sprite): rotation 1 is head-on, and 2..8 follow the thing turning 45° at a
    // time *clockwise seen from above* — rotation 2 is it "facing diagonally to the left of the
    // player", 3 side-on facing the player's left, 5 back-on. Our groups are 0-based, so Doom's
    // rotation N is our group N-1.
    [Fact]
    public void DirectionGroupsFollowDoomsRotationOrder()
    {
        var camera = new Vector3(0, 0, 10);            // the player stands at +Z, looking at the thing
        // Head-on: the thing faces +Z, which is yaw 180 (yaw 0 faces -Z, SageMath).
        Assert.Equal(0, SpriteMath.DirectionIndex(Vector3.Zero, camera, 180 * Deg, 8, out _));
        // Turned 45° clockwise from there — yaw counts counter-clockwise, so clockwise is 180 - 45 —
        // and it now faces diagonally to the player's left: Doom's rotation 2.
        Assert.Equal(1, SpriteMath.DirectionIndex(Vector3.Zero, camera, 135 * Deg, 8, out _));
        Assert.Equal(2, SpriteMath.DirectionIndex(Vector3.Zero, camera, 90 * Deg, 8, out _));    // side on, facing the player's left
        Assert.Equal(4, SpriteMath.DirectionIndex(Vector3.Zero, camera, 0f, 8, out _));          // back on
        Assert.Equal(6, SpriteMath.DirectionIndex(Vector3.Zero, camera, 270 * Deg, 8, out _));   // side on, facing the player's right

        // And the mirrored 5-direction sheets Doom and Daggerfall shipped: 6, 7, 8 reuse 4, 3, 2.
        Assert.Equal(2, SpriteMath.DirectionIndex(Vector3.Zero, camera, 270 * Deg, 5, out bool flip));
        Assert.True(flip);
    }

    [Fact]
    public void Yaw_ReadsTheRotationAboutY()
    {
        Assert.Equal(0f, SageMath.YawOf(Quaternion.Identity), 5);
        Assert.Equal(90 * Deg, SageMath.YawOf(Quaternion.CreateFromYawPitchRoll(90 * Deg, 0, 0)), 4);
        // Pitch and roll don't change the yaw of a character standing up.
        Assert.Equal(30 * Deg, SageMath.YawOf(Quaternion.CreateFromYawPitchRoll(30 * Deg, 0.2f, 0)), 3);
    }

    // The bug this convention replaced: a sprite's art was chosen as if its front were +Z while every
    // other subsystem faced -Z, so the two only agreed because the character controller added half a
    // turn on the way out (review #43). A sheet's direction 0 must be the art for a camera standing
    // where the entity is looking, whichever way that is.
    [Theory]
    [InlineData(0f)]
    [InlineData(90f)]
    [InlineData(-135f)]
    [InlineData(200f)]
    public void DirectionZeroIsAlwaysTheViewFromWhereTheEntityLooks(float yawDegrees)
    {
        float yaw = yawDegrees * Deg;
        var rotation = SageMath.RotationFromYaw(yaw);
        var inFront = SageMath.ForwardFromYaw(yaw) * 6f;                 // where it is looking
        var behind = -inFront;

        Assert.Equal(0, SpriteMath.DirectionIndex(Vector3.Zero, inFront, SageMath.YawOf(rotation), 8, out _));
        Assert.Equal(4, SpriteMath.DirectionIndex(Vector3.Zero, behind, SageMath.YawOf(rotation), 8, out _));
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

    // "idle" loops with a "step" event halfway; "swing" plays once with the "hit" that lands a blow.
    private const string Sheet = """
        [{ "type": "sprite_sheet", "id": "goblin", "texture": "textures/goblin.png", "directions": 1,
           "frames": [ { "rect": [0, 0, 64, 96] }, { "rect": [64, 0, 64, 96] } ],
           "animations": {
             "idle":  { "fps": 10, "loop": true,  "dirs": [[0, 1]], "events": [ { "frame": 1, "name": "step" } ] },
             "swing": { "fps": 10, "loop": false, "dirs": [[0, 1]], "events": [ { "frame": 1, "name": "hit" } ] } } }]
        """;

    private static (World World, RecordStore Records) NewWorld()
    {
        var fx = new MountFixture();
        fx.Write("engine", "data/sheets.json", Sheet);
        fx.Mount("engine", "sage");
        var records = new RecordStore();
        records.Register<SpriteSheetRecord>();
        records.Load(fx.Vfs);

        var world = new World("anim");
        world.Resources.Set(new AnimationEvents());
        world.AddSystem(new SpriteAnimationSystem(world, records), Phase.Animation);
        return (world, records);
    }

    private static Entity Sprite(World world, int clip, float speed = 1f, bool playing = true)
    {
        var entity = world.Create();
        world.Add(entity, new SpriteRenderer { Sheet = new RecordId("sage", "goblin") });
        world.Add(entity, playing ? SpriteAnimator.Play(clip, speed) : new SpriteAnimator { Clip = clip });
        return entity;
    }

    [Fact]
    public void AdvancesPlayingAnimatorsByTheTick()   // 12 §3: the simulation owns animation time
    {
        var (world, _) = NewWorld();
        using (world)
        {
            var playing = Sprite(world, 0);
            var stopped = Sprite(world, 0, playing: false);
            var fast = Sprite(world, 1, speed: 2f);

            for (int i = 0; i < 10; i++) world.RunFixed(0.1f);

            Assert.Equal(1f, world.Get<SpriteAnimator>(playing).Time, 4);
            Assert.Equal(0f, world.Get<SpriteAnimator>(stopped).Time);
            Assert.Equal(2f, world.Get<SpriteAnimator>(fast).Time, 4);
        }
    }

    // 12 §3: a clip's frames carry named moments, and combat lands its blow on one (16 §3.2).
    [Fact]
    public void FrameEventsFireOnceAsTheClipPassesThem()
    {
        var (world, records) = NewWorld();
        using (world)
        {
            int swing = records.Get<SpriteSheetRecord>(new RecordId("sage", "goblin")).ClipIndex("swing");
            var fighter = Sprite(world, swing);
            var events = world.Resources.Get<AnimationEvents>();

            int hits = 0;
            for (int i = 0; i < 40; i++)      // 0.4 s: well past the end of a two-frame clip at 10 fps
            {
                world.RunFixed(1f / 60f);
                if (events.Fired(fighter, "hit")) hits++;
            }

            Assert.Equal(1, hits);            // once, on frame 1, and never again: the clip doesn't loop
        }
    }

    [Fact]
    public void ALoopingClipRaisesItsEventEveryTimeRound()
    {
        var (world, records) = NewWorld();
        using (world)
        {
            int idle = records.Get<SpriteSheetRecord>(new RecordId("sage", "goblin")).ClipIndex("idle");
            var walker = Sprite(world, idle);
            var events = world.Resources.Get<AnimationEvents>();

            int steps = 0;
            for (int i = 0; i < 60; i++)      // 1 s of a 0.2 s loop
            {
                world.RunFixed(1f / 60f);
                if (events.Fired(walker, "step")) steps++;
            }

            Assert.Equal(5, steps);
            Assert.False(events.Fired(walker, "hit"), "a clip only raises its own events");
        }
    }
}
