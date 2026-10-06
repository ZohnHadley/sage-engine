#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Directional attacks and blocks (issue #359, docs/design/12, TODO F10): a fighter's AttackStance says
// which way it swings or guards — chosen by a player's mouse gesture or movement keys, or by an AI — and
// a data-only anim_graph reads it (`from: AttackDirection`, `from: Blocking`) to pick one of four swings
// or one of four guards, as in Warband. Each swing lands on its own clip's `hit` event, and a window
// between two clip events (hit_start .. hit_end) is Animators.InWindow and the `anim_window` condition.
public class DirectionalCombatTests
{
    public DirectionalCombatTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    // Four swings over the rig's clips at their own rates — overhead the walk as authored, from the right
    // the walk twice as fast, from the left at half speed, a thrust the run — so each lands at its own
    // time; four guards; an `edge` layer that is `lit` while the base's clip is inside its hit window.
    // The windup (5 s) is far too long to be what lands any of them.
    private const string Records = """
    [
      { "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
      { "type": "effect", "id": "damage", "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
      { "type": "damage_type", "id": "physical", "effect": "damage" },
      { "type": "attack", "id": "sword", "damage": 10, "reach": 2.0, "radius": 0.3, "arcDegrees": 120,
        "windupTime": 5, "recoverTime": 0.1, "cooldown": 0 },
      { "type": "anim_events", "id": "rig", "model": "models/rig.glb",
        "clips": { "walk": [ { "time": 0.16, "name": "hit_start" }, { "time": 0.26, "name": "hit" }, { "time": 0.41, "name": "hit_end" } ],
                   "run":  [ { "time": 0.05, "name": "hit_start" }, { "time": 0.11, "name": "hit" }, { "time": 0.2, "name": "hit_end" } ] } },
      { "type": "anim_graph", "id": "duelist", "initial": "idle", "fade": 0,
        "params": { "attack": { "kind": "Trigger" }, "dir": { "from": "AttackDirection" }, "block": { "from": "Blocking" } },
        "states": {
          "idle":           { "clip": "idle" },
          "swing_overhead": { "clip": "walk", "loop": false, "transitions": [ { "to": "idle", "when": { "anim_finished": "base" } } ] },
          "swing_right":    { "clip": "walk", "loop": false, "speed": 2, "transitions": [ { "to": "idle", "when": { "anim_finished": "base" } } ] },
          "swing_thrust":   { "clip": "run",  "loop": false, "transitions": [ { "to": "idle", "when": { "anim_finished": "base" } } ] },
          "swing_left":     { "clip": "walk", "loop": false, "speed": 0.5, "transitions": [ { "to": "idle", "when": { "anim_finished": "base" } } ] },
          "block_overhead": { "clip": "idle", "tags": ["guard"], "transitions": [ { "to": "idle", "when": { "anim_param": "block", "eq": 0 } } ] },
          "block_right":    { "clip": "idle", "tags": ["guard"], "transitions": [ { "to": "idle", "when": { "anim_param": "block", "eq": 0 } } ] },
          "block_thrust":   { "clip": "idle", "tags": ["guard"], "transitions": [ { "to": "idle", "when": { "anim_param": "block", "eq": 0 } } ] },
          "block_left":     { "clip": "idle", "tags": ["guard"], "transitions": [ { "to": "idle", "when": { "anim_param": "block", "eq": 0 } } ] } },
        "transitions": [
          { "to": "swing_overhead", "on": "attack", "when": { "anim_param": "dir", "max": 1 } },
          { "to": "swing_right",    "on": "attack", "when": { "anim_param": "dir", "eq": 2 } },
          { "to": "swing_thrust",   "on": "attack", "when": { "anim_param": "dir", "eq": 3 } },
          { "to": "swing_left",     "on": "attack", "when": { "anim_param": "dir", "eq": 4 } },
          { "to": "block_overhead", "when": { "all": [ { "anim_param": "block", "eq": 1 }, { "anim_param": "dir", "max": 1 } ] } },
          { "to": "block_right",    "when": { "all": [ { "anim_param": "block", "eq": 1 }, { "anim_param": "dir", "eq": 2 } ] } },
          { "to": "block_thrust",   "when": { "all": [ { "anim_param": "block", "eq": 1 }, { "anim_param": "dir", "eq": 3 } ] } },
          { "to": "block_left",     "when": { "all": [ { "anim_param": "block", "eq": 1 }, { "anim_param": "dir", "eq": 4 } ] } } ],
        "layers": [
          { "name": "edge", "initial": "dull",
            "states": { "dull": {}, "lit": {} },
            "transitions": [ { "to": "lit",  "when": { "anim_window": "hit_start", "close": "hit_end" } },
                             { "to": "dull", "when": { "not": { "anim_window": "hit_start", "close": "hit_end" } } } ] } ] },
      { "type": "gameplay_conventions", "id": "default_conventions", "health": "health", "damageType": "physical" }
    ]
    """;

    private static HeadlessApp NewGame()
    {
        var files = new MountFixture();
        SkinnedModelBuilder.Write(Path.Combine(files.Dir("game"), "models", "rig.glb"), withRun: true);
        files.Mount("game", "game");
        var app = HeadlessApp.Gameplay().File("data/duel.json", Records).Mount(files).Boot("duel");
        Assert.Equal(0, app.Records.ErrorCount);
        var ground = app.World.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        app.World.Add(ground, Collider.Box(new Vector3(400, 1, 400)));
        return app;
    }

    private static Entity Body(HeadlessApp app, Vector3 feet, string name, Vector3 lookAt)
    {
        var world = app.World;
        var e = world.Create(Transform.At(feet), name);
        world.AddCharacter(e, world.Resources.Get<IPhysicsWorld>().Layers.Player);
        world.AddAttributes(e);
        world.Get<PawnIntent>(e).Yaw = SageMath.YawTo(feet, lookAt);
        return e;
    }

    // A duelist at `x` with a stance, facing a victim 1.2 m in front of it.
    private static (Entity Fighter, Entity Victim) Pair(HeadlessApp app, float x, AttackStance stance)
    {
        var world = app.World;
        var fighter = Body(app, new Vector3(x, 0, 0), $"duelist{x}", new Vector3(x, 0, -2));
        world.Add(fighter, Melee.With(new RecordId("sage", "sword")));
        world.Add(fighter, new Animator { Graph = new RecordId("sage", "duelist"), Model = AssetPath.Intern("models/rig.glb") });
        world.Add(fighter, stance);
        var victim = Body(app, new Vector3(x, 0, -1.2f), $"victim{x}", new Vector3(x, 0, 0));
        return (fighter, victim);
    }

    private static void Tick(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(Dt);
    }

    // The tick after the press on which a clip event `seconds` into a clip played at `rate` lands a blow:
    // the clip starts on the press tick and has played (k + 1) ticks after tick k's Animation phase, and
    // combat reads the event one tick later (AnimationCombatTests).
    private static int LandsOn(float seconds, float rate = 1f) => (int)MathF.Ceiling(seconds / (Dt * rate) - 1e-3f);

    // Acceptance (#359's Done): one data-only graph swings four ways. Fighters poised overhead, from the
    // right, thrusting and from the left — and one with no direction yet, which swings the graph's default
    // (overhead) — press Attack on the same tick; each enters its own swing, keeps the direction it
    // started with, and lands on its own clip's `hit`: overhead 0.26 s into the walk, from the right the
    // walk at twice the speed, from the left at half, a thrust 0.11 s into the run.
    [Xunit.Fact]
    public void ADataOnlyGraphSwingsFourWays_EachLandingOnItsOwnClipsHit()
    {
        using var app = NewGame();
        var world = app.World;
        var directions = new[] { AttackDirection.Overhead, AttackDirection.Right, AttackDirection.Thrust, AttackDirection.Left, AttackDirection.None };
        var pairs = directions.Select((d, i) => Pair(app, i * 10, new AttackStance { Direction = d })).ToArray();
        Tick(world, 3);

        var attack = app.Engine.Actions.Get("Attack");
        foreach (var (f, _) in pairs) world.Get<PawnIntent>(f).Pressed = default(ActionMask).With(attack);
        Tick(world);
        foreach (var (f, _) in pairs) world.Get<PawnIntent>(f).Pressed = default;

        var states = pairs.Select(p => Animators.StateOf(world, p.Fighter)).ToArray();
        Assert.Equal(new[] { "swing_overhead", "swing_right", "swing_thrust", "swing_left", "swing_overhead" }, states);
        for (int i = 0; i < pairs.Length; i++) Assert.Equal(directions[i], world.Get<Melee>(pairs[i].Fighter).Direction);

        // A stance chosen anew mid-swing changes the next swing, not this one.
        AttackStances.Set(world, pairs[0].Fighter, AttackDirection.Left);
        var health = new RecordId("sage", "health");
        var landed = Enumerable.Repeat(-1, pairs.Length).ToArray();
        for (int t = 1; t <= 60; t++)
        {
            Tick(world);
            for (int v = 0; v < pairs.Length; v++)
                if (landed[v] < 0 && world.Attribute(pairs[v].Victim, health) < 100f) landed[v] = t;
        }
        Assert.Equal(new[] { LandsOn(0.26f), LandsOn(0.26f, 2f), LandsOn(0.11f), LandsOn(0.26f, 0.5f), LandsOn(0.26f) }, landed);
        Assert.Equal(new[] { 16, 8, 7, 32, 16 }, landed);   // and none on the 5 s windup
        Assert.Equal(AttackDirection.Overhead, world.Get<Melee>(pairs[0].Fighter).Direction);
    }

    // The hit window comes from the clip's events: the `edge` layer (an `anim_window` condition) is lit
    // from the walk's hit_start to its hit_end and dull outside it, Animators.InWindow agrees every tick,
    // and the blow lands inside the window.
    [Xunit.Fact]
    public void TheHitWindowIsTakenFromTheClipsEvents()
    {
        using var app = NewGame();
        var world = app.World;
        var (fighter, victim) = Pair(app, 0, new AttackStance { Direction = AttackDirection.Overhead });
        Tick(world, 3);
        Assert.False(Animators.InWindow(world, fighter, "hit_start", "hit_end"));
        Assert.Equal("dull", Animators.StateOf(world, fighter, "edge"));

        world.Get<PawnIntent>(fighter).Pressed = default(ActionMask).With(app.Engine.Actions.Get("Attack"));
        Tick(world);
        world.Get<PawnIntent>(fighter).Pressed = default;

        int opened = -1, closed = -1, landed = -1;
        var health = new RecordId("sage", "health");
        for (int t = 1; t <= 70; t++)
        {
            Tick(world);
            bool open = Animators.InWindow(world, fighter, "hit_start", "hit_end");
            Assert.Equal(open ? "lit" : "dull", Animators.StateOf(world, fighter, "edge"));
            if (open && opened < 0) opened = t;
            if (!open && opened >= 0 && closed < 0) closed = t;
            if (landed < 0 && world.Attribute(victim, health) < 100f) landed = t;
        }
        // Open once the walk has played 0.16 s (tick 9: ten ticks of it), shut once it has played 0.41 s.
        Assert.Equal(9, opened);
        Assert.Equal(24, closed);
        Assert.InRange(landed, opened, closed);
        Assert.Equal("idle", Animators.StateOf(world, fighter));
        Assert.False(Animators.InWindow(world, fighter, "hit_start"));   // back to idle: its clip has no hit_start
        Assert.False(Animators.InWindow(world, fighter, ""));
    }

    // A player chooses with the mouse — the look moving up, right, down or left past the threshold — or
    // with the movement keys, as Warband's two options; a Manual stance keeps what it was given.
    [Xunit.Fact]
    public void APlayerChoosesTheDirectionByMouseGestureOrMovementKeys()
    {
        using var app = NewGame();
        var world = app.World;
        var (mouse, _) = Pair(app, 0, new AttackStance { Input = AttackDirectionInput.Mouse });
        var (keys, _) = Pair(app, 10, new AttackStance { Input = AttackDirectionInput.Movement });
        var (manual, _) = Pair(app, 20, new AttackStance { Input = AttackDirectionInput.Manual, Direction = AttackDirection.Thrust });
        Tick(world, 2);
        Assert.Equal(AttackDirection.None, AttackStances.Of(world, mouse));

        float deg = MathF.PI / 180f;
        void Look(float yawDegrees, float pitchDegrees)
        {
            ref var intent = ref world.Get<PawnIntent>(mouse);
            intent.Yaw += yawDegrees * deg;
            intent.Pitch += pitchDegrees * deg;
            Tick(world);
        }
        Look(0, 1f);                                     // under the 2° threshold
        Assert.Equal(AttackDirection.None, AttackStances.Of(world, mouse));
        Look(0, 1.5f);                                   // 2.5° up, less a little fading
        Assert.Equal(AttackDirection.Overhead, AttackStances.Of(world, mouse));
        Tick(world, 60);                                 // the gesture fades; the direction stays
        Assert.Equal(AttackDirection.Overhead, AttackStances.Of(world, mouse));
        Look(-4f, 0);                                    // yaw down: the view turned right
        Assert.Equal(AttackDirection.Right, AttackStances.Of(world, mouse));
        Tick(world, 60);
        Look(0, -4f);
        Assert.Equal(AttackDirection.Thrust, AttackStances.Of(world, mouse));
        Tick(world, 60);
        Look(4f, 1f);                                    // mostly left
        Assert.Equal(AttackDirection.Left, AttackStances.Of(world, mouse));
        // The graph reads it the same tick.
        Assert.Equal((float)AttackDirection.Left, Animators.GetParam(world, mouse, "dir"));

        void Move(Vector2 move, AttackDirection expected)
        {
            world.Get<PawnIntent>(keys).Move = move;
            Tick(world);
            Assert.Equal(expected, AttackStances.Of(world, keys));
        }
        Move(new Vector2(0, 1), AttackDirection.Thrust);
        Move(new Vector2(0, -1), AttackDirection.Overhead);
        Move(new Vector2(1, 0.3f), AttackDirection.Right);
        Move(new Vector2(-1, 0), AttackDirection.Left);
        Move(new Vector2(0.2f, 0.2f), AttackDirection.Left);   // inside the dead zone: kept

        world.Get<PawnIntent>(manual).Pitch += 0.5f;
        world.Get<PawnIntent>(manual).Move = new Vector2(1, 0);
        Tick(world);
        Assert.Equal(AttackDirection.Thrust, AttackStances.Of(world, manual));
    }

    // Holding Block raises the guard the stance faces: the graph goes to that block state, follows the
    // direction while it is held, and drops back to idle when it is let go. A swing under way lowers it.
    [Xunit.Fact]
    public void HoldingBlockRaisesTheGuardInTheChosenDirection()
    {
        using var app = NewGame();
        var world = app.World;
        var (fighter, _) = Pair(app, 0, new AttackStance { Direction = AttackDirection.Right });
        Tick(world, 3);
        var block = app.Engine.Actions.Get("Block");
        Assert.NotEqual(ActionId.None, block);

        world.Get<PawnIntent>(fighter).Held = default(ActionMask).With(block);
        Tick(world);
        Assert.True(AttackStances.IsBlocking(world, fighter));
        Assert.Equal("block_right", Animators.StateOf(world, fighter));
        Assert.True(Animators.HasTag(world, fighter, "guard"));

        AttackStances.Set(world, fighter, AttackDirection.Thrust);
        Tick(world);
        Assert.Equal("block_thrust", Animators.StateOf(world, fighter));

        // A swing from the guard: the guard drops for the swing.
        world.Get<PawnIntent>(fighter).Pressed = default(ActionMask).With(app.Engine.Actions.Get("Attack"));
        Tick(world);
        world.Get<PawnIntent>(fighter).Pressed = default;
        Assert.Equal("swing_thrust", Animators.StateOf(world, fighter));
        Tick(world);
        Assert.False(AttackStances.IsBlocking(world, fighter));
        Assert.Equal("swing_thrust", Animators.StateOf(world, fighter));

        world.Get<PawnIntent>(fighter).Held = default;
        Tick(world, 60);
        Assert.False(AttackStances.IsBlocking(world, fighter));
        Assert.Equal("idle", Animators.StateOf(world, fighter));
    }

    // An AI chooses too: its MeleeAttack varies the direction swing by swing (overhead, right, thrust,
    // left, round again) and avoids the side its target guards; its Block task holds the Block button and
    // turns its guard to the blow coming at it.
    [Xunit.Fact]
    public void AnAiVariesItsSwingsAndGuardsAgainstTheIncomingBlow()
    {
        using var app = NewGame();
        var world = app.World;
        var (ai, target) = Pair(app, 0, new AttackStance());
        world.Add(target, new AttackStance { Direction = AttackDirection.Right });
        Tick(world, 2);

        var chosen = new AttackDirection[5];
        for (int i = 0; i < chosen.Length; i++)
        {
            AITasks.ChooseDirection(world, ai, target);
            chosen[i] = AttackStances.Of(world, ai);
        }
        Assert.Equal(new[] { AttackDirection.Overhead, AttackDirection.Right, AttackDirection.Thrust, AttackDirection.Left, AttackDirection.Overhead }, chosen);

        world.Get<PawnIntent>(target).Held = default(ActionMask).With(app.Engine.Actions.Get("Block"));
        Tick(world);
        Assert.True(AttackStances.IsBlocking(world, target));
        AITasks.ChooseDirection(world, ai, target);                 // overhead -> right, which is guarded
        Assert.Equal(AttackDirection.Thrust, AttackStances.Of(world, ai));

        // A player's stance is the player's: the AI's choosing leaves it alone.
        world.Get<AttackStance>(ai).Input = AttackDirectionInput.Mouse;
        AITasks.ChooseDirection(world, ai, target);
        Assert.Equal(AttackDirection.Thrust, AttackStances.Of(world, ai));
        world.Get<AttackStance>(ai).Input = AttackDirectionInput.Manual;

        // The target swings from the left; the AI's Block task guards left and holds the button.
        world.Get<PawnIntent>(target).Held = default;
        world.Add(target, Melee.With(new RecordId("sage", "sword")));
        AttackStances.Set(world, target, AttackDirection.Left);
        world.Get<PawnIntent>(target).Pressed = default(ActionMask).With(app.Engine.Actions.Get("Attack"));
        Tick(world);
        world.Get<PawnIntent>(target).Pressed = default;
        AttackStances.Set(world, target, AttackDirection.Overhead);   // already choosing the next one
        Assert.Equal(MeleePhase.Windup, world.Get<Melee>(target).Phase);

        var task = new AITaskRegistry().Find("Block")!;
        Assert.Equal("seconds", task.Argument);
        var state = new AIState { Target = target };
        var status = RunTask(app, task, ai, ref state, 1f);
        Assert.Equal(AITaskStatus.Running, status);
        Assert.Equal(AttackDirection.Left, AttackStances.Of(world, ai));
        Tick(world);
        Assert.True(AttackStances.IsBlocking(world, ai));
        Assert.Equal("block_left", Animators.StateOf(world, ai));

        state.TaskTime = 2f;
        Assert.Equal(AITaskStatus.Succeeded, RunTask(app, task, ai, ref state, 1f));
    }

    private static AITaskStatus RunTask(HeadlessApp app, IAITask task, Entity entity, ref AIState state, float param)
    {
        var world = app.World;
        var context = new AITaskContext
        {
            World = world,
            Entity = entity,
            State = ref state,
            Intent = ref world.Get<PawnIntent>(entity),
            Transform = ref world.Get<Transform>(entity),
            Profile = new AIProfileRecord(),
            Movement = MovementProfileRecord.Fallback,
            Space = world.Resources.Get<IPhysicsWorld>(),
            Attack = app.Engine.Actions.Get("Attack"),
            Block = app.Engine.Actions.Get("Block"),
            Dt = Dt,
            Param = param,
        };
        return task.Run(ref context);
    }

    // The choosing itself, without a world.
    [Xunit.Fact]
    public void GesturesAndMovementMapToDirections()
    {
        Assert.Equal(AttackDirection.Overhead, AttackStances.FromGesture(new Vector2(0.5f, 3)));
        Assert.Equal(AttackDirection.Thrust, AttackStances.FromGesture(new Vector2(0.5f, -3)));
        Assert.Equal(AttackDirection.Left, AttackStances.FromGesture(new Vector2(3, 0.5f)));
        Assert.Equal(AttackDirection.Right, AttackStances.FromGesture(new Vector2(-3, 0.5f)));
        Assert.Equal(AttackDirection.Overhead, AttackStances.FromGesture(new Vector2(2, 2)));   // a tie goes vertical
        Assert.Equal(AttackDirection.None, AttackStances.FromGesture(Vector2.Zero));
        Assert.Equal(AttackDirection.None, AttackStances.FromGesture(new Vector2(float.NaN, 1)));
        Assert.Equal(AttackDirection.None, AttackStances.FromMovement(new Vector2(0.3f, -0.3f)));
        Assert.Equal(AttackDirection.Overhead, AttackStances.FromMovement(new Vector2(0.3f, -0.9f)));

        // A turn across ±180° is a small movement, not a whole turn.
        var stance = new AttackStance { Input = AttackDirectionInput.Mouse };
        var intent = new PawnIntent { Yaw = MathF.PI - 0.01f };
        AttackStances.Step(ref stance, in intent, Dt);
        intent.Yaw = -MathF.PI + 0.01f;
        AttackStances.Step(ref stance, in intent, Dt);
        Assert.Equal(AttackDirection.None, stance.Direction);
        intent.Yaw -= 0.1f;                                         // ~5.7° to the right
        AttackStances.Step(ref stance, in intent, Dt);
        Assert.Equal(AttackDirection.Right, stance.Direction);
    }
}
