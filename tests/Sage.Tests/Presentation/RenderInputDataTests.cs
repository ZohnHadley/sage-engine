#nullable enable
using System;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The simulation-side data of rendering and input (docs/design/05 §3.2, 07 §3.3, 08 §3.2–3.4):
// everything headless. The client halves (renderer, bindings) need a GPU/devices and are smoke-tested.
public class AssetPathTests
{
    [Fact]
    public void Intern_IsStableAndNormalized()
    {
        var a = AssetPath.Intern("Textures/Goblin.PNG");
        var b = AssetPath.Intern(VirtualPath.Parse("textures/goblin.png"));
        Assert.Equal(a, b);
        Assert.Equal(a.Id, b.Id);
        Assert.Equal("textures/goblin.png", a.ToString());
        Assert.NotEqual(a, AssetPath.Intern("textures/other.png"));
        Assert.True(AssetPath.None.IsEmpty);
        Assert.Equal("", AssetPath.None.ToString());
    }
}

public class MaterialAndInputRecordTests
{
    public MaterialAndInputRecordTests() { _ = TestEnv.UserRoot; }

    private static RecordStore Load(MountFixture fx)
    {
        var store = new RecordStore();
        store.Register<MaterialRecord>();
        store.Register<InputMapRecord>();
        store.Load(fx.Vfs);
        return store;
    }

    [Fact]
    public void MaterialRecord_ParsesParamsAndState_AndInheritsThroughBase()
    {
        var fx = new MountFixture();
        fx.Write("engine", "data/materials.json", """
            [{ "type": "material", "id": "lit_default", "effect": "shaders/lit.mgfxo", "technique": "Default",
               "pass": "Opaque", "cull": "Back", "sampler": { "filter": "Point", "address": "Clamp" },
               "params": { "Albedo": "textures/white.png", "AlbedoColor": [1, 1, 1, 1], "AlphaCutoff": 0.5 } }]
            """);
        fx.Write("game", "data/materials.json", """
            [{ "type": "material", "id": "glass", "base": "sage:lit_default", "pass": "Transparent", "blend": "AlphaBlend",
               "depthWrite": false, "fog": false, "params": { "AlbedoColor": [0.2, 0.4, 0.6, 0.5] } }]
            """);
        fx.Mount("engine", "sage");
        fx.Mount("game", "sandbox");
        var store = Load(fx);

        Assert.Equal(0, store.ErrorCount);
        var glass = store.Get<MaterialRecord>(new RecordId("sandbox", "glass"));
        Assert.Equal(AssetPath.Intern("shaders/lit.mgfxo"), glass.Effect);          // inherited
        Assert.Equal("Default", glass.Technique);
        Assert.Equal(RenderPass.Transparent, glass.Pass);
        Assert.Equal(MaterialBlend.AlphaBlend, glass.Blend);
        Assert.False(glass.DepthWrite);
        Assert.False(glass.Fog);
        Assert.Equal(SamplerFilter.Point, glass.Sampler.Filter);                     // nested object inherited
        Assert.Equal(new[] { 0.2f, 0.4f, 0.6f, 0.5f }, glass.Params["AlbedoColor"].Values);   // params merge per key
        Assert.True(glass.Params["Albedo"].IsTexture);
        Assert.Equal("textures/white.png", glass.Params["Albedo"].Texture.ToString());
        Assert.Equal(new[] { 0.5f }, glass.Params["AlphaCutoff"].Values);
    }

    [Fact]
    public void MaterialParam_WithAnObject_IsAnError()
    {
        var fx = new MountFixture();
        fx.Write("game", "data/m.json", """[{ "type": "material", "id": "bad", "effect": "x.mgfxo", "params": { "Tint": { "r": 1 } } }]""");
        fx.Mount("game", "sandbox");
        var store = Load(fx);

        Assert.Equal(1, store.ErrorCount);
        Assert.False(store.TryGet(new RecordId("sandbox", "bad"), out MaterialRecord _));
    }

    [Fact]
    public void InputMap_Parses_AndPatchesReplaceAnActionsBindings()
    {
        var fx = new MountFixture();
        fx.Write("engine", "data/input.json", """
            [{ "type": "input_map", "id": "gameplay", "context": "Gameplay",
               "actions": { "Jump": [ { "key": "Space" }, { "gamepad": "A" } ],
                            "Look": [ { "mouse": "Delta", "scale": 0.0025 }, { "gamepad": "RightStick", "scale": 3, "rate": true } ] } }]
            """);
        fx.Write("user", "data/input.json", """
            [{ "type": "input_map", "id": "sage:gameplay", "patch": true, "actions": { "Jump": [ { "key": "J" } ] } }]
            """);
        fx.Mount("engine", "sage");
        fx.Mount("user", "user");
        var store = Load(fx);

        var map = store.Get<InputMapRecord>(new RecordId("sage", "gameplay"));
        Assert.Equal(InputContext.Gameplay, map.Context);
        Assert.Equal("J", Assert.Single(map.Actions["Jump"]).Key);                   // rebinding = a record patch (08 §3.2)
        Assert.Equal(2, map.Actions["Look"].Count);
        Assert.True(map.Actions["Look"][1].Rate);
        Assert.Equal(0.0025f, map.Actions["Look"][0].Scale);
    }
}

public class InputActionTests
{
    [Fact]
    public void Registry_AssignsBitsToButtonsOnly_AndIsIdempotent()
    {
        var r = new ActionRegistry();
        var move = r.Register("Move", ActionKind.Axis2D);
        var jump = r.Register("Jump", ActionKind.Button);
        var use = r.Register("Use", ActionKind.Button);

        Assert.Equal(-1, move.Bit);
        Assert.Equal(0, jump.Bit);
        Assert.Equal(1, use.Bit);
        Assert.Equal(jump, r.Register("jump", ActionKind.Button));                   // names are case-insensitive
        Assert.Throws<InvalidOperationException>(() => r.Register("Jump", ActionKind.Axis1D));
        Assert.Equal(use, r.Get("USE"));
        Assert.False(r.Get("Nope").IsValid);
    }

    [Fact]
    public void Registry_LimitsButtonsTo128()   // two words of an ActionMask (issue #28; it was one)
    {
        Assert.Equal(128, ActionRegistry.MaxButtons);
        var r = new ActionRegistry();
        for (int i = 0; i < ActionRegistry.MaxButtons; i++) r.Register($"b{i}", ActionKind.Button);
        r.Register("axis", ActionKind.Axis1D);                                        // axes don't count
        Assert.Throws<InvalidOperationException>(() => r.Register("one_too_many", ActionKind.Button));
    }

    [Fact]
    public void ActionMask_HasAndWith()
    {
        var r = new ActionRegistry();
        var a = r.Register("A", ActionKind.Button);
        var b = r.Register("B", ActionKind.Button);
        var axis = r.Register("Axis", ActionKind.Axis2D);
        var mask = default(ActionMask).With(b).With(axis);
        Assert.True(mask.Has(b));
        Assert.False(mask.Has(a));
        Assert.False(mask.Has(axis));
        Assert.Equal(1UL << b.Bit, mask.Bits);
    }

    // A button past the 64th lives in the mask's second word, and the latch carries it like any other.
    [Fact]
    public void ActionMask_HoldsButtonsPast64()
    {
        var r = new ActionRegistry();
        for (int i = 0; i < 100; i++) r.Register($"b{i}", ActionKind.Button);
        var low = r.Get("b3");
        var high = r.Get("b99");
        Assert.Equal(99, high.Bit);

        var mask = default(ActionMask).With(high);
        Assert.True(mask.Has(high));
        Assert.False(mask.Has(low));
        Assert.Equal(0UL, mask.Bits);
        Assert.Equal(1UL << 35, mask.High);
        Assert.True(mask.With(low).Has(low) && mask.With(low).Has(high));

        var latch = new CommandLatch();
        latch.AddFrame(held: mask, pressed: mask, released: default, Vector2.Zero);
        latch.AddFrame(held: default, pressed: default(ActionMask).With(low), released: mask, Vector2.Zero);
        var command = latch.Sample(1);
        Assert.True(command.Pressed.Has(high) && command.Pressed.Has(low) && command.Released.Has(high));
        Assert.False(command.Held.Has(high));
    }

    [Fact]
    public void Latch_KeepsATapShorterThanATick()   // 08 §3.4
    {
        var r = new ActionRegistry();
        var jump = r.Register("Jump", ActionKind.Button);
        var latch = new CommandLatch();
        var bit = default(ActionMask).With(jump);

        // Frame 1: pressed (no tick runs). Frame 2: released (no tick). Then a tick samples.
        latch.AddFrame(held: bit, pressed: bit, released: default, Vector2.Zero);
        latch.AddFrame(held: default, pressed: default, released: bit, Vector2.Zero);
        var command = latch.Sample(7);

        Assert.Equal(7, command.Tick);
        Assert.True(command.Pressed.Has(jump));
        Assert.True(command.Released.Has(jump));
        Assert.False(command.Held.Has(jump));

        var next = latch.Sample(8);                                                   // carried once, then cleared
        Assert.False(next.Pressed.Has(jump));
        Assert.False(next.Released.Has(jump));
    }

    [Fact]
    public void Latch_HeldAndMoveAreTheLatestFrame_MoveIsClampedToLengthOne()
    {
        var r = new ActionRegistry();
        var use = r.Register("Use", ActionKind.Button);
        var latch = new CommandLatch();
        latch.AddFrame(default(ActionMask).With(use), default(ActionMask).With(use), default, new Vector2(0, 1));
        latch.AddFrame(default(ActionMask).With(use), default, default, new Vector2(1, 1));

        var c = latch.Sample(1);
        Assert.True(c.Held.Has(use));
        Assert.True(c.Pressed.Has(use));
        Assert.Equal(1f, c.Move.Length(), 4);
        Assert.Equal(c.Move.X, c.Move.Y, 4);
    }

    [Fact]
    public void Latch_LookTurnsRightAndUp_ClampsPitch_WrapsYaw()
    {
        var latch = new CommandLatch();
        latch.AddLook(new Vector2(0.5f, 0.25f));
        Assert.Equal(-0.5f, latch.ViewYaw, 5);                                        // right = yaw decreases
        Assert.Equal(0.25f, latch.ViewPitch, 5);                                      // up = pitch increases

        latch.AddLook(new Vector2(0, 10f));
        Assert.Equal(CommandLatch.PitchLimit, latch.ViewPitch, 5);

        latch.SetView(0, 0);
        latch.AddLook(new Vector2(-4f, 0));                                           // 4 rad left
        Assert.InRange(latch.ViewYaw, -MathF.PI, MathF.PI);
        Assert.Equal(4f - MathF.Tau, latch.ViewYaw, 4);

        var c = latch.Sample(1);
        Assert.Equal(latch.ViewYaw, c.ViewYaw);
        Assert.Equal(latch.ViewPitch, c.ViewPitch);
    }

    [Fact]
    public void TheCharacterPluginBringsACameraAndPlayerInput_ABareWorldHasNeither()
    {
        _ = TestEnv.UserRoot;
        using var bare = new World("bare");
        Assert.NotNull(bare.Resources.Get<RenderEnvironment>());
        Assert.False(bare.Resources.TryGet<ActiveCamera>(out _));   // issue #13: the plugins' now
        Assert.False(bare.Resources.TryGet<PlayerInput>(out _));

        using var app = HeadlessApp.Gameplay().Boot("test");
        Assert.False(app.World.Resources.Get<PlayerInput>().HasCommand);   // headless: nothing samples input
        Assert.Equal(0.1f, app.World.Resources.Get<ActiveCamera>().Near);
    }

    // Turning the player is not something the simulation can simply do (08 §3.4): the view angles are
    // accumulated by the host between ticks, and `PawnIntent.Yaw` is rewritten from them every tick, so
    // a rotation written onto the pawn is gone a tick later. `map_goto` did exactly that and landed the
    // player in the right room facing the wrong way, with nothing to show anything had been ignored.
    [Fact]
    public void AskingThePlayerToFaceSomewhereIsAnsweredOnceAndThenLetGo()
    {
        var input = new PlayerInput();

        Assert.False(input.TryTakeView(out _, out _));    // nobody asked

        input.RequestView(1.5f, 0.25f);

        Assert.True(input.TryTakeView(out float yaw, out float pitch));
        Assert.Equal(1.5f, yaw);
        Assert.Equal(0.25f, pitch);

        // Once, and then the player is free to look away again: a request that kept being answered
        // would pin the view and feel like a broken mouse.
        Assert.False(input.TryTakeView(out _, out _));
    }
}
