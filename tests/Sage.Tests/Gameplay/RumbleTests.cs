#nullable enable
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Haptics from data (issue #331): a `rumble` record is a shape of vibration, a cue and a damage type name
// one, and the player whose entity the thing happened to feels it. The mixer sums what is playing per
// player over time; the pad itself is the client's and not touched here.
public class RumbleTests
{
    public RumbleTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    private const string Data = """
        [{ "type": "rumble", "id": "thump", "low": 0.8, "high": 0.2, "duration": 0.3, "attack": 0, "release": 0 },
         { "type": "rumble", "id": "sting", "low": 0.1, "high": 0.6, "duration": 0.2, "release": 0 },
         { "type": "cue", "id": "bell", "rumble": "thump" },
         { "type": "cue", "id": "silent" },
         { "type": "damage_type", "id": "cut", "effect": "damage", "rumble": "sting" }]
        """;

    private static HeadlessApp NewGame(out RumbleMixer mixer)
    {
        var app = HeadlessApp.Gameplay()
            .File("data/hits.json", HitPipelineTests.Records)
            .File("data/rumble.json", Data)
            .Boot("rumble");
        Assert.Equal(0, app.Records.ErrorCount);
        mixer = new RumbleMixer();
        app.World.Resources.Add(mixer);
        var ground = app.World.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        app.World.Add(ground, Collider.Box(new Vector3(400, 1, 400)));
        return app;
    }

    private static Entity Player(World world, int slot, string name)
    {
        var e = HitPipelineTests.Body(world, new Vector3(slot * 5, 0, 0), name, new Vector3(slot * 5, 0, -5));
        e.AddTag<PlayerControlled>();
        if (slot > 0) world.Add(e, new PlayerSlot { Index = slot });
        return e;
    }

    private static DamageInfo Hit(Entity target, string type = "cut") =>
        new(default(Entity), target, new RecordId("sage", type), 10f, Vector3.Zero, Vector3.UnitZ);

    // Done: rumble is triggered from CueTriggered through data. The cue's record names a rumble, and it
    // plays on the pad of the player who is the cue's source — not for a creature, not for a cue with none.
    [Xunit.Fact]
    public void ACuesRumbleIsFeltByThePlayerWhoseEntityRaisedIt()
    {
        using var app = NewGame(out var mixer);
        var world = app.World;
        var player = Player(world, 0, "hero");
        var creature = HitPipelineTests.Body(world, new Vector3(9, 0, 0), "creature", new Vector3(9, 0, -5));

        world.Events.Send(new CueTriggered(new RecordId("sage", "bell"), creature, Vector3.Zero));
        world.Events.Send(new CueTriggered(new RecordId("sage", "silent"), player, Vector3.Zero));
        world.RunFixed(Dt);
        Assert.Equal(0f, mixer.Low(0));                  // a creature's bell and a cue with no rumble: nothing

        world.Events.Send(new CueTriggered(new RecordId("sage", "bell"), player, Vector3.Zero));
        world.RunFixed(Dt);
        Assert.Equal(0.8f, mixer.Low(0), 3);
        Assert.Equal(0.2f, mixer.High(0), 3);

        for (int i = 0; i < 25; i++) world.RunFixed(Dt);   // 0.3 s lasts, and then it is over
        Assert.Equal(0f, mixer.Low(0));
        Assert.Equal(0, mixer.Count);
    }

    // Done: ... and from Damaged. A hit that cost the second player health rumbles their pad and only theirs;
    // a blow that did nothing (god mode) is not felt.
    [Xunit.Fact]
    public void AHitRumblesTheVictimsPadAndOnlyThatPlayers()
    {
        using var app = NewGame(out var mixer);
        var world = app.World;
        var one = Player(world, 0, "one");
        var two = Player(world, 1, "two");

        Assert.True(Combat.ApplyDamage(world, Hit(two)) > 0f);
        world.RunFixed(Dt);
        Assert.Equal(0f, mixer.Low(0));
        Assert.Equal(0.6f, mixer.High(1), 3);
        Assert.Equal(0.1f, mixer.Low(1), 3);

        world.RunFixed(Dt * 30);
        Assert.Equal(0f, mixer.High(1));
        Combat.ApplyDamage(world, Hit(one));
        world.RunFixed(Dt);
        Assert.Equal(0.6f, mixer.High(0), 3);
    }

    // No mixer in the world (a headless server): the events are read into nothing, and nothing throws.
    [Xunit.Fact]
    public void AWorldWithoutAMixerIgnoresRumble()
    {
        using var app = HeadlessApp.Gameplay().File("data/hits.json", HitPipelineTests.Records).File("data/rumble.json", Data).Boot("rumble2");
        var player = Player(app.World, 0, "hero");
        app.World.Events.Send(new CueTriggered(new RecordId("sage", "bell"), player, Vector3.Zero));
        app.World.RunFixed(Dt);
        Assert.False(app.World.Resources.TryGet<RumbleMixer>(out _));
    }

    // ---- the mixer ---------------------------------------------------------------------------------

    // Effects add per player and the motor is clamped; each player has their own; ageing removes them.
    [Xunit.Fact]
    public void TheMixerSumsActiveEffectsPerPlayerAndClamps()
    {
        var mixer = new RumbleMixer();
        mixer.Start(0, 0.4f, 0f, 1f);
        mixer.Start(0, 0.4f, 0.3f, 0.5f);
        mixer.Start(1, 0.9f, 0.9f, 1f);
        Assert.Equal(0.8f, mixer.Low(0), 3);
        Assert.Equal(0.3f, mixer.High(0), 3);

        mixer.Start(0, 0.4f, 0f, 1f);                       // 1.2 clamps to 1
        Assert.Equal(1f, mixer.Low(0));
        Assert.Equal(0.9f, mixer.Low(1), 3);

        mixer.Update(0.6f);                                 // the short one is over
        Assert.Equal(0.8f, mixer.Low(0), 3);
        Assert.Equal(0f, mixer.High(0));
        mixer.Update(0.6f);
        Assert.Equal(0, mixer.Count);
        Assert.Equal(0f, mixer.Low(1));
    }

    // Done: an effect has an envelope (attack up, release down) and the global gain scales all of it.
    [Xunit.Fact]
    public void AnEffectRisesAndFallsAndGainScalesIt()
    {
        var mixer = new RumbleMixer();
        mixer.Start(0, 1f, 0f, duration: 1f, attack: 0.25f, release: 0.5f);
        Assert.Equal(0f, mixer.Low(0), 3);
        mixer.Update(0.125f);
        Assert.Equal(0.5f, mixer.Low(0), 3);                // half way up
        mixer.Update(0.375f);                               // 0.5 s: full strength, release begins
        Assert.Equal(1f, mixer.Low(0), 3);
        mixer.Update(0.25f);                                // 0.75 s: half way down
        Assert.Equal(0.5f, mixer.Low(0), 3);

        mixer.Gain = 0.5f;
        mixer.Update(0f);
        Assert.Equal(0.25f, mixer.Low(0), 3);
        mixer.Gain = 0f;                                    // joy_rumble 0
        mixer.Update(0f);
        Assert.Equal(0f, mixer.Low(0));
    }

    // A flood of cues is bounded: the oldest effect makes room.
    [Xunit.Fact]
    public void TheMixerIsBounded()
    {
        var mixer = new RumbleMixer();
        for (int i = 0; i < 100; i++) mixer.Start(0, 0.01f, 0f, 5f);
        Assert.Equal(RumbleMixer.MaxEffects, mixer.Count);
        mixer.Stop(0);
        Assert.Equal(0, mixer.Count);
    }

    // ---- a second player's command -------------------------------------------------------------------

    // Done: a pawn with a `PlayerSlot` is driven by that player's command, the unslotted pawn by the first's.
    [Xunit.Fact]
    public void ASecondPlayersPawnIsDrivenByTheirOwnCommand()
    {
        using var app = NewGame(out _);
        var world = app.World;
        var one = Player(world, 0, "one");
        var two = Player(world, 1, "two");
        var attack = app.Engine.Actions.Get("Attack");
        var input = world.Resources.Get<PlayerInput>();

        input.SetCommand(0, new PlayerCommand { Tick = 1, Move = new Vector2(0, 1), ViewYaw = 0.5f });
        input.SetCommand(1, new PlayerCommand { Tick = 1, Move = new Vector2(1, 0), ViewYaw = -1f, Held = default(ActionMask).With(attack) });
        world.RunFixed(Dt);

        Assert.Equal(new Vector2(0, 1), world.Get<PawnIntent>(one).Move);
        Assert.Equal(0.5f, world.Get<PawnIntent>(one).Yaw, 3);
        Assert.False(world.Get<PawnIntent>(one).Held.Has(attack));
        Assert.Equal(new Vector2(1, 0), world.Get<PawnIntent>(two).Move);
        Assert.Equal(-1f, world.Get<PawnIntent>(two).Yaw, 3);
        Assert.True(world.Get<PawnIntent>(two).Held.Has(attack));
        Assert.Equal(1, world.PlayerIndexOf(two));
        Assert.Equal(-1, world.PlayerIndexOf(default(Entity)));
    }
}
