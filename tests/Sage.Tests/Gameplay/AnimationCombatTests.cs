#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Combat on animation events (issue #119, docs/design/16 §3.2, 12 "As built (animation events)"): a swing
// sets the fighter's animator trigger, its graph picks the clip, and the blow lands on the clip's `hit`
// event — a sprite goblin's frame event and a skinned NPC's anim_events alike — on the tick it always
// did: one after the Animation phase that crossed the event. A clip with no event lands on the windup.
public class AnimationCombatTests
{
    public AnimationCombatTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    // A goblin's sheet (the Sandbox creature's: the blow on frame 1 of a 4 fps swing, 0.25 s in), a graph
    // over it, the rig with a `hit` 0.25 s into its walk (swung with once), and attacks whose windup is
    // far too long to be what lands them — except `quick`, whose is what does.
    private const string Records = """
    [
      { "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
      { "type": "effect", "id": "damage", "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
      { "type": "damage_type", "id": "physical", "effect": "damage" },
      { "type": "attack", "id": "claws", "damage": 10, "reach": 2.0, "radius": 0.3, "arcDegrees": 120,
        "windupTime": 5, "recoverTime": 0.3, "cooldown": 0.5 },
      { "type": "attack", "id": "quick", "damage": 10, "reach": 2.0, "radius": 0.3, "arcDegrees": 120,
        "windupTime": 0.2, "recoverTime": 0.3, "cooldown": 0.5 },
      { "type": "sprite_sheet", "id": "goblin", "texture": "textures/goblin.png", "directions": 1,
        "frames": [ { "rect": [0, 0, 64, 96] }, { "rect": [64, 0, 64, 96] } ],
        "animations": {
          "idle":   { "fps": 3, "loop": true,  "dirs": [[0, 1]] },
          "attack": { "fps": 4, "loop": false, "dirs": [[0, 1]], "events": [ { "frame": 1, "name": "hit" } ] } } },
      { "type": "anim_graph", "id": "goblin", "initial": "idle", "fade": 0,
        "params": { "attack": { "kind": "Trigger" } },
        "states": { "idle": { "clip": "idle" },
                    "attack": { "clip": "attack", "loop": false,
                                "transitions": [ { "to": "idle", "when": { "anim_finished": "base" } } ] } },
        "transitions": [ { "to": "attack", "on": "attack" } ] },
      { "type": "anim_events", "id": "rig", "model": "models/rig.glb", "clips": { "walk": [ { "time": 0.25, "name": "hit" } ] } },
      { "type": "anim_graph", "id": "npc", "initial": "idle", "fade": 0.1,
        "params": { "attack": { "kind": "Trigger" } },
        "states": { "idle": { "clip": "idle" },
                    "swing": { "clip": "walk", "loop": false, "transitions": [ { "to": "idle", "when": { "anim_finished": "base" } } ] } },
        "transitions": [ { "to": "swing", "on": "attack" } ] },
      { "type": "anim_graph", "id": "shover", "initial": "idle", "fade": 0.1,
        "params": { "attack": { "kind": "Trigger" } },
        "states": { "idle": { "clip": "idle" }, "shove": { "clip": "run", "loop": false } },
        "transitions": [ { "to": "shove", "on": "attack" } ] },
      { "type": "prefab", "id": "goblin", "parts": { "sprite": { "sheet": "goblin", "graph": "goblin" } } },
      { "type": "gameplay_conventions", "id": "default_conventions", "health": "health", "damageType": "physical" }
    ]
    """;

    private static HeadlessApp NewGame()
    {
        var files = new MountFixture();
        SkinnedModelBuilder.Write(Path.Combine(files.Dir("game"), "models", "rig.glb"), withRun: true);
        files.Mount("game", "game");
        var app = HeadlessApp.Gameplay().OnRegistered(a => a.Records.Register<SpriteSheetRecord>())
            .File("data/combat.json", Records).Mount(files).Boot("anim-combat");
        Assert.Equal(0, app.Records.ErrorCount);
        var ground = app.World.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        app.World.Add(ground, Collider.Box(new Vector3(200, 1, 200)));
        return app;
    }

    // A fighter at `x` swinging `attack` at a victim 1.2 m in front of it, and the victim.
    private static (Entity Fighter, Entity Victim) Pair(HeadlessApp app, float x, string attack, Action<Entity> dress)
    {
        var world = app.World;
        var physics = world.Resources.Get<IPhysicsWorld>();
        Entity Body(Vector3 feet, string name, Vector3 lookAt)
        {
            var e = world.Create(Transform.At(feet), name);
            world.AddCharacter(e, physics.Layers.Player);
            world.AddAttributes(e);
            world.Get<PawnIntent>(e).Yaw = SageMath.YawTo(feet, lookAt);
            return e;
        }
        var fighter = Body(new Vector3(x, 0, 0), $"fighter{x}", new Vector3(x, 0, -2));
        world.Add(fighter, Melee.With(new RecordId("sage", attack)));
        dress(fighter);
        var victim = Body(new Vector3(x, 0, -1.2f), $"victim{x}", new Vector3(x, 0, 0));
        return (fighter, victim);
    }

    // Presses Attack on every fighter on one tick (the press tick, 0) and says, per victim, on which tick
    // after it the blow landed (-1: never, within `ticks`).
    private static int[] Swing(HeadlessApp app, Entity[] fighters, Entity[] victims, int ticks = 60)
    {
        var world = app.World;
        var health = new RecordId("sage", "health");
        var attack = app.Engine.Actions.Get("Attack");
        foreach (var f in fighters) world.Get<PawnIntent>(f).Pressed = default(ActionMask).With(attack);
        world.RunFixed(Dt);
        foreach (var f in fighters) world.Get<PawnIntent>(f).Pressed = default;
        var landed = Enumerable.Repeat(-1, victims.Length).ToArray();
        for (int t = 1; t <= ticks; t++)
        {
            world.RunFixed(Dt);
            for (int v = 0; v < victims.Length; v++)
                if (landed[v] < 0 && world.Attribute(victims[v], health) < 100f) landed[v] = t;
        }
        return landed;
    }

    // Acceptance: a sprite goblin whose graph plays its sheet, a sprite goblin from before graphs (the
    // conventions' upgrade gives it one), and a skinned NPC whose swing clip has a `hit` 0.25 s in all land
    // on tick 15 after the press — the tick a sprite's hit frame 0.25 s in always landed on (the clip
    // started on the press tick, crossed its frame in the Animation phase of tick 14, and combat read it
    // on tick 15). The windup (5 s) would be tick 300.
    [Xunit.Fact]
    public void ASpriteGoblinAndASkinnedNpcLandOnHitOnTheSameTickAsBefore()
    {
        using var app = NewGame();
        var world = app.World;
        var sheet = app.Records.Get<SpriteSheetRecord>(new RecordId("sage", "goblin"));
        var (goblin, g) = Pair(app, 0, "claws", e =>
        {
            world.Add(e, new SpriteRenderer { Sheet = new RecordId("sage", "goblin") });
            world.Add(e, new Animator { Graph = new RecordId("sage", "goblin") });
            world.Add(e, new SpriteAnimator());
        });
        var (legacy, l) = Pair(app, 10, "claws", e =>
        {
            world.Add(e, new SpriteRenderer { Sheet = new RecordId("sage", "goblin") });
            world.Add(e, SpriteAnimator.Play(sheet.ClipIndex("idle")));   // a sprite part's `animation`, no graph
        });
        var (npc, n) = Pair(app, 20, "claws", e =>
            world.Add(e, new Animator { Graph = new RecordId("sage", "npc"), Model = AssetPath.Intern("models/rig.glb") }));
        var spawned = world.Spawn(new RecordId("sage", "goblin"), new Vector3(30, 0, 0));
        Assert.True(world.Has<Animator>(spawned) && world.Has<SpriteAnimator>(spawned));   // the sprite part's `graph`
        for (int i = 0; i < 5; i++) world.RunFixed(Dt);
        Assert.False(world.Has<Animator>(legacy));                                     // upgraded when it first swings
        Assert.Equal(sheet.ClipIndex("idle"), world.Get<SpriteAnimator>(goblin).Clip);

        var anim = new EventProbe<AnimationEvent>(world);
        var landed = Swing(app, new[] { goblin, legacy, npc }, new[] { g, l, n });

        Assert.Equal(new[] { 15, 15, 15 }, landed);
        Assert.True(world.Has<Animator>(legacy));
        Assert.Equal(3, anim.All.Count(e => e.Name == "hit"));                         // once each
        // The sprite shows the swing, then stands again when its clip is done (the graph's, not combat's).
        Assert.Equal(sheet.ClipIndex("idle"), world.Get<SpriteAnimator>(goblin).Clip);
        Assert.Equal("idle", Animators.StateOf(world, npc));
    }

    // The graph's sprite clock is the plain sprite clock: a clip played by a trigger raises its frame
    // events on the ticks the same clip played by name (SpriteAnimationSystem) does, at any fps.
    [Xunit.Fact]
    public void AGraphRaisesASpritesFrameEventsOnTheTicksThePlainSpriteDoes()
    {
        const string sheets = """
        [ { "type": "sprite_sheet", "id": "strip", "texture": "textures/strip.png", "directions": 1,
            "frames": [ { "rect": [0, 0, 8, 8] }, { "rect": [8, 0, 8, 8] }, { "rect": [16, 0, 8, 8] }, { "rect": [24, 0, 8, 8] }, { "rect": [32, 0, 8, 8] } ],
            "animations": {
              "idle": { "fps": 3,  "loop": true,  "dirs": [[0, 1]] },
              "a4":   { "fps": 4,  "loop": false, "dirs": [[0, 1]],          "events": [ { "frame": 0, "name": "go" }, { "frame": 1, "name": "hit" } ] },
              "a7":   { "fps": 7,  "loop": false, "dirs": [[0, 1, 2, 3]],    "events": [ { "frame": 2, "name": "hit" }, { "frame": 3, "name": "last" } ] },
              "a10":  { "fps": 10, "loop": true,  "dirs": [[0, 1, 2]],       "events": [ { "frame": 1, "name": "hit" } ] },
              "a12":  { "fps": 12, "loop": false, "dirs": [[0, 1, 2, 3, 4]], "events": [ { "frame": 3, "name": "hit" } ] } } },
          { "type": "anim_graph", "id": "strip", "initial": "idle", "fade": 0,
            "params": { "t4": { "kind": "Trigger" }, "t7": { "kind": "Trigger" }, "t10": { "kind": "Trigger" }, "t12": { "kind": "Trigger" } },
            "states": { "idle": { "clip": "idle" }, "a4": { "clip": "a4", "loop": false }, "a7": { "clip": "a7", "loop": false },
                        "a10": { "clip": "a10" }, "a12": { "clip": "a12", "loop": false } },
            "transitions": [ { "to": "a4", "on": "t4" }, { "to": "a7", "on": "t7" }, { "to": "a10", "on": "t10" }, { "to": "a12", "on": "t12" } ] } ]
        """;
        using var app = HeadlessApp.Gameplay().OnRegistered(a => a.Records.Register<SpriteSheetRecord>())
            .File("data/strip.json", sheets).Boot("strip");
        Assert.Equal(0, app.Records.ErrorCount);
        var world = app.World;
        var sheet = app.Records.Get<SpriteSheetRecord>(new RecordId("sage", "strip"));
        foreach (var clip in new[] { "a4", "a7", "a10", "a12" })
        {
            var plain = world.Create();
            world.Add(plain, new SpriteRenderer { Sheet = new RecordId("sage", "strip") });
            world.Add(plain, SpriteAnimator.Play(sheet.ClipIndex("idle")));
            var graphed = world.Create();
            world.Add(graphed, new SpriteRenderer { Sheet = new RecordId("sage", "strip") });
            world.Add(graphed, new SpriteAnimator());
            world.Add(graphed, new Animator { Graph = new RecordId("sage", "strip") });
            for (int i = 0; i < 3; i++) world.RunFixed(Dt);

            var probe = new EventProbe<AnimationEvent>(world);
            world.PlayClip(plain, clip, app.Records);                   // as combat did, in Gameplay
            Assert.True(Animators.SetTrigger(world, graphed, "t" + clip[1..]));
            var plainTicks = new List<(int, string)>();
            var graphTicks = new List<(int, string)>();
            for (int t = 0; t < 90; t++)
            {
                world.RunFixed(Dt);
                foreach (var e in probe.Since())
                {
                    if (e.Entity == plain) plainTicks.Add((t, e.Name));
                    else if (e.Entity == graphed) graphTicks.Add((t, e.Name));
                }
            }
            Assert.NotEmpty(plainTicks);
            if (clip == "a10")
            {
                // A loop: the plain sprite's clock is a sum of dt that float rounding leaves a hair short
                // of 1 s at tick 60, so its fourth pass comes a tick late; the graph's lands every pass on
                // the tick it is due (0.1 s + 0.3 s a pass: ticks 6, 24, 42, 60, 78, counted from 1).
                Assert.Equal(plainTicks.Take(3), graphTicks.Take(3));
                Assert.Equal(new[] { 5, 23, 41, 59, 77 }, graphTicks.Select(g => g.Item1).ToArray());
            }
            else Assert.Equal(plainTicks, graphTicks);                 // a one-shot, as combat swings
            // And it shows the frame the plain sprite shows (a one-shot's last, held).
            Assert.Equal(world.Get<SpriteAnimator>(plain).Clip, world.Get<SpriteAnimator>(graphed).Clip);
            if (clip != "a10")
                Assert.Equal(SpriteMath.FrameAt(sheet.Clip(world.Get<SpriteAnimator>(plain).Clip)!, 0, world.Get<SpriteAnimator>(plain).Time),
                         SpriteMath.FrameAt(sheet.Clip(world.Get<SpriteAnimator>(graphed).Clip)!, 0, world.Get<SpriteAnimator>(graphed).Time));
            world.Destroy(plain);
            world.Destroy(graphed);
        }
    }

    // Acceptance: a clip with no events lands on the attack's windup, on the tick a fighter with no
    // animation at all does — and a skinned fighter whose graph has no state for the trigger, too.
    [Xunit.Fact]
    public void AClipWithNoEventsLandsOnItsWindup()
    {
        using var app = NewGame();
        var world = app.World;
        var (bare, b) = Pair(app, 0, "quick", _ => { });
        var (shover, s) = Pair(app, 10, "quick", e =>
            world.Add(e, new Animator { Graph = new RecordId("sage", "shover"), Model = AssetPath.Intern("models/rig.glb") }));
        for (int i = 0; i < 5; i++) world.RunFixed(Dt);

        var landed = Swing(app, new[] { bare, shover }, new[] { b, s });

        Assert.True(landed[0] > 0, "the bare fighter never landed");
        Assert.Equal(landed[0], landed[1]);
        Assert.Equal("shove", Animators.StateOf(world, shover));      // it did play its swing
    }

    // The Sandbox's own watcher, from its own content: its sprite part's graph plays the creature sheet,
    // and its claws land on the attack clip's "hit" frame (0.25 s in) on tick 15 after the press, as they
    // did when combat played the clip by name — not on the claws' 0.45 s windup (tick 27).
    [Xunit.Fact]
    public void TheSandboxWatcherLandsOnItsHitFrameOnTheTickItAlwaysDid()
    {
        using var app = HeadlessApp.ForGame(Path.Combine(TestEnv.FolderAbove("Sage.sln"), "games/Sandbox"), new Sandbox.SandboxModule())
            .WithEngineContent().OnRegistered(a => a.Records.Register<SpriteSheetRecord>()).Boot("sandbox");
        Assert.Equal(0, app.Records.ErrorCount);
        var world = app.World;
        var space = world.Resources.Get<IPhysicsWorld>();
        var at = new Vector3(400, 300, 400);                              // on a platform of its own
        var ground = world.Create(Transform.At(at + new Vector3(0, -0.5f, 0)), "platform");
        world.Add(ground, Collider.Box(new Vector3(20, 1, 20)));
        var watcher = world.Spawn(new RecordId("sandbox", "watcher"), at);
        world.Remove<AIState>(watcher);                                   // it swings when told, not when it likes
        Assert.True(world.Has<Animator>(watcher));
        var victim = world.Create(Transform.At(at + new Vector3(0, 0, -1.2f)), "victim");
        world.AddCharacter(victim, space.Layers.Player);
        world.AddAttributes(victim);
        for (int i = 0; i < 5; i++) world.RunFixed(Dt);
        var health = world.Conventions().Health;
        float before = world.Attribute(victim, health);

        var attack = app.Engine.Actions.Get(world.Conventions().Actions.Attack);
        world.Get<PawnIntent>(watcher).Pressed = default(ActionMask).With(attack);
        world.RunFixed(Dt);
        world.Get<PawnIntent>(watcher).Pressed = default;
        int landed = -1;
        for (int t = 1; t <= 40 && landed < 0; t++)
        {
            world.RunFixed(Dt);
            if (world.Attribute(victim, health) < before) landed = t;
        }
        Assert.False(health.IsEmpty);
        Assert.Equal(15, landed);
    }
}
