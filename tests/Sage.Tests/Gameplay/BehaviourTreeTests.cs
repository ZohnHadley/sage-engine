#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Behaviour trees and utility scoring beside schedules (docs/design/16 §3.4, issue #387): a `behaviour_tree`
// record a creature runs in place of schedules, ticked by the same staggered think; guards that interrupt;
// one task a tick; decorators; a blackboard that survives a save; and the `utility` selector and node.
public class BehaviourTreeTests
{
    public BehaviourTreeTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    private const string Records = """
        [{ "type": "gameplay_conventions", "id": "default_conventions", "health": "health", "dead": "state.dead", "damageType": "physical",
           "aiProfile": "default_ai",
           "schedules": { "idle": "idle", "chase": "chase", "meleeAttack": "melee_attack", "castSpell": "idle", "holdGround": "idle" } },
         { "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
         { "type": "tag", "id": "state.dead" },
         { "type": "effect", "id": "damage", "duration": "Instant", "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
         { "type": "damage_type", "id": "physical", "effect": "damage" },
         { "type": "attack", "id": "claws", "damage": 12, "reach": 2.2, "windupTime": 0.3, "recoverTime": 0.2, "cooldown": 1.0 },

         { "type": "ai_profile", "id": "default_ai", "sightRange": 25, "meleeRange": 1.8, "thinkRate": 20 },
         { "type": "ai_schedule", "id": "idle", "tasks": [{ "task": "Wait", "seconds": 1.5 }], "interrupts": ["SeeEnemy"] },
         { "type": "ai_schedule", "id": "chase", "tasks": [{ "task": "MoveToTarget", "distance": 1.6 }], "interrupts": ["EnemyInMeleeRange", "LostEnemy", "NoEnemy"] },
         { "type": "ai_schedule", "id": "melee_attack", "tasks": ["FaceTarget", "MeleeAttack"], "interrupts": ["LostEnemy", "NoEnemy"] },
         { "type": "ai_schedule", "id": "flee", "tasks": [{ "task": "Wait", "seconds": 1 }] },

         // The sentry, in data alone: it fights what it sees (swinging in reach, closing otherwise) and rests
         // when it sees nothing, writing down that it did each.
         { "type": "behaviour_tree", "id": "sentry",
           "root": { "selector": [
             { "name": "fight", "when": ["SeeEnemy"], "selector": [
                 { "when": ["EnemyInMeleeRange", "CanMelee"], "sequence": ["FaceTarget", { "task": "MeleeAttack", "giveUpAfter": 1 }, { "set": "swung" }] },
                 { "task": "MoveToTarget", "distance": 1.6 } ] },
             { "name": "rest", "sequence": [ { "task": "Wait", "seconds": 0.5 }, { "set": "rested" } ] } ] } },
         { "type": "prefab", "id": "sentry", "name": "sentry", "components": { "ai_state": { "tree": "sentry" } },
           "parts": { "character": { "layer": "enemy" }, "attributes": {}, "melee": { "attack": "claws" } } },

         // Waits while it sees nobody; seeing somebody aborts the wait (its guard stops holding).
         { "type": "behaviour_tree", "id": "watch",
           "root": { "selector": [
             { "unless": ["SeeEnemy"], "sequence": [ { "task": "Wait", "seconds": 3 }, { "set": "waited" } ] },
             { "set": "interrupted" } ] } },

         // Tally three times, a task a tick, then never again.
         { "type": "behaviour_tree", "id": "three",
           "root": { "selector": [ { "check": "done" }, { "sequence": [ { "repeat": "Tally", "times": 3 }, { "set": "done" } ] } ] } },
         // Tally, then not again for half a second.
         { "type": "behaviour_tree", "id": "cooled", "root": { "cooldown": "Tally", "seconds": 0.5 } },
         // `invert`, `succeed` and `check` with bounds: tallies only while `level` is outside 2..4.
         { "type": "behaviour_tree", "id": "bounded",
           "root": { "sequence": [ { "succeed": { "set": "seen" } }, { "invert": { "check": "level", "min": 2, "max": 4 } }, "Tally" ] } },

         // A utility node: loiter scores 1; once it sees somebody, the alarm scores 5 and takes over at a think.
         { "type": "behaviour_tree", "id": "pick",
           "root": { "utility": [
             { "name": "loiter", "score": 1, "task": "Wait", "seconds": 10 },
             { "name": "alarm", "considerations": [ { "when": ["SeeEnemy"], "add": 5 } ], "task": "Wait", "seconds": 10 } ] } },

         // The utility selector: idle scores 1; flee scores up to 3 as health falls from half to a fifth.
         { "type": "ai_profile", "id": "careful", "sightRange": 25, "thinkRate": 20,
           "utility": [
             { "schedule": "idle", "score": 1 },
             { "schedule": "flee", "considerations": [ { "measure": "health", "from": 0.5, "to": 0.2, "add": 3 } ] } ] }]
        """;

    private const byte EnemyLayer = 2, PlayerLayer = 1;

    // A game's own task, as a game adds one: adds 1 to the blackboard's `tally` and succeeds.
    private sealed class TallyGame : IGameModule
    {
        public IReadOnlyList<Type> Dependencies => new[] { typeof(AIModule) };
        public void Init(ModuleContext ctx) { }
        public void Start(ModuleContext ctx) => ctx.Get<AITaskRegistry>().Register("Tally", new TallyTask());
    }

    private sealed class TallyTask : IAITask
    {
        public AITaskStatus Run(ref AITaskContext c)
        {
            AIBlackboard.Set(ref c.State, "tally", AIBlackboard.Get(in c.State, "tally") + 1);
            return AITaskStatus.Succeeded;
        }
    }

    private static HeadlessApp NewGame()
    {
        using var log = new CaptureSink();
        var app = HeadlessApp.Gameplay().With(new TallyGame()).File("data/trees.json", Records).Boot("trees");
        Assert.True(app.Records.ErrorCount == 0, string.Join("\n", log.Entries.Where(e => e.Level >= LogLevel.Error).Select(e => e.Message)));
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        var ground = app.World.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        app.World.Add(ground, Collider.Box(new Vector3(400, 1, 400)));
        return app;
    }

    private static RecordId Id(string name) => new("sage", name);

    // A creature made in code, running `tree` (placed facing -Z).
    private static Entity Creature(World world, Vector3 position, string tree, string name = "creature")
    {
        var entity = world.Create(Transform.At(position), name);
        world.AddCharacter(entity, EnemyLayer);
        world.Add(entity, new AIState { Tree = Id(tree) });
        world.Add(entity, Melee.With(Id("claws")));
        world.AddAttributes(entity);
        world.Add(entity, new Persistent { Id = PersistentId.FromName(name) });
        return entity;
    }

    private static Entity Player(World world, Vector3 position)
    {
        var entity = world.Create(Transform.At(position), "player");
        world.Add(entity, Collider.Standing(0.35f, 1.8f, PlayerLayer));
        world.Add(entity, RigidBody.Kinematic());
        world.AddAttributes(entity);
        entity.AddTag<PlayerControlled>();
        return entity;
    }

    private static void Tick(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(Dt);
    }

    private static float Board(World world, Entity entity, string key) => AIBlackboard.Get(world, entity, key);

    private static int Node(World world, Entity entity) => world.Get<AIState>(entity).TaskIndex;

    // The issue's "done": a creature spawned from a prefab names a tree in its `ai_state`, and the tree —
    // data alone — rests while it sees nobody, drops that the think it sees the player (the `fight`
    // branch's guard becomes true and preempts), closes, and swings.
    [Fact]
    public void ACreatureRunsABehaviourTreeFromDataAlone()
    {
        using var app = NewGame();
        var world = app.World;
        var sentry = world.Spawn(Id("sentry"));
        Assert.False(sentry.IsNull);
        Assert.Equal(Id("sentry"), world.Get<AIState>(sentry).Tree);
        Tick(world, 5);
        var tree = app.Engine.Records.Get<BehaviourTreeRecord>(Id("sentry"));
        Assert.Equal(10, tree.NodeCount);                       // 0 selector, 1 fight, 2 sequence, 3-5, 6 MoveToTarget, 7 rest, 8 Wait, 9 set

        Tick(world, 60);
        Assert.Equal(1f, Board(world, sentry, "rested"));
        Assert.Equal(8, Node(world, sentry));                     // resting again: the Wait
        Assert.True(world.Get<AIState>(sentry).Schedule.IsEmpty, "a creature with a tree runs no schedule");

        var start = world.Get<Transform>(sentry).LocalPosition;
        var player = Player(world, start + new Vector3(0, 0, -8));
        Tick(world, 10);
        Assert.True(((AICondition)world.Get<AIState>(sentry).Conditions).HasFlag(AICondition.SeeEnemy));
        Assert.Equal(6, Node(world, sentry));                     // the wait was dropped for MoveToTarget

        bool swung = false;
        for (int i = 0; i < 60 * 6 && !swung; i++)
        {
            Tick(world);
            swung = Board(world, sentry, "swung") == 1f;
        }
        Assert.True(swung, $"it closed and swung (it is {SageMath.DistanceXZ(world.Get<Transform>(sentry).LocalPosition, world.Get<Transform>(player).LocalPosition):F1} m off)");
        Assert.True(world.Attribute(player, Conventional.Health) < 100f, "the swing landed");
    }

    // An interrupt is a guard that stops holding: the running Wait (under `unless SeeEnemy`) is aborted the
    // think the player is seen, its node fails, and the selector moves on; out of sight, it waits again.
    [Fact]
    public void ARunningBranchWhoseGuardStopsHoldingIsAborted()
    {
        using var app = NewGame();
        var world = app.World;
        var watcher = Creature(world, new Vector3(0, 0.1f, 0), "watch");
        Tick(world, 30);
        Assert.Equal(2, Node(world, watcher));
        Assert.True(world.Get<AIState>(watcher).TaskStarted);

        var player = Player(world, new Vector3(0, 0.1f, -6));
        Tick(world, 10);
        Assert.Equal(1f, Board(world, watcher, "interrupted"));
        Assert.False(world.Get<AIState>(watcher).TaskStarted, "the wait was aborted");
        Tick(world, 60 * 4);
        Assert.Equal(0f, Board(world, watcher, "waited"));      // never finished while it was watched

        world.Destroy(player);
        Tick(world, 60 * 4);
        Assert.Equal(1f, Board(world, watcher, "waited"));      // unwatched, the wait ran to its end
    }

    // One task a tick, as a schedule moves to its next task on the next tick: `repeat 3` of a task that
    // succeeds at once tallies 1, 2, 3 on three ticks, and the `check` before it stops it after.
    [Fact]
    public void ATreeRunsOneTaskATickAndRepeatCountsItsTimes()
    {
        using var app = NewGame();
        var world = app.World;
        var counter = Creature(world, new Vector3(0, 0.1f, 0), "three");
        for (int i = 1; i <= 3; i++)
        {
            Tick(world);
            Assert.Equal(i, Board(world, counter, "tally"));
        }
        Assert.Equal(1f, Board(world, counter, "done"));
        Tick(world, 30);
        Assert.Equal(3f, Board(world, counter, "tally"));
    }

    // A cooldown fails for its `seconds` after its node finishes; `invert`, `succeed` and a bounded `check`
    // do what they say.
    [Fact]
    public void CooldownInvertSucceedAndCheckDecorateTheirNode()
    {
        using var app = NewGame();
        var world = app.World;
        var cooled = Creature(world, new Vector3(0, 0.1f, 0), "cooled", "cooled");
        Tick(world, 20);                                          // a third of a second
        Assert.Equal(1f, Board(world, cooled, "tally"));
        Tick(world, 20);                                          // past half a second: once more
        Assert.Equal(2f, Board(world, cooled, "tally"));

        var bounded = Creature(world, new Vector3(10, 0.1f, 0), "bounded", "bounded");
        Tick(world, 3);
        Assert.Equal(1f, Board(world, bounded, "seen"));
        Assert.Equal(3f, Board(world, bounded, "tally"));         // level 0 is outside 2..4: it tallies
        AIBlackboard.Set(world, bounded, "level", 3);
        Tick(world, 3);
        Assert.Equal(3f, Board(world, bounded, "tally"));         // inside: the inverted check fails the sequence
        AIBlackboard.Set(world, bounded, "level", 5);
        Tick(world, 1);
        Assert.Equal(4f, Board(world, bounded, "tally"));
    }

    // A utility node changes its mind at a think when another child scores higher than the one running.
    [Fact]
    public void AUtilityNodeSwitchesToTheChildThatComesToScoreHigher()
    {
        using var app = NewGame();
        var world = app.World;
        var picker = Creature(world, new Vector3(0, 0.1f, 0), "pick");
        Tick(world, 10);
        Assert.Equal(1, Node(world, picker));                     // loiter
        Player(world, new Vector3(0, 0.1f, -6));
        Tick(world, 10);
        Assert.Equal(2, Node(world, picker));                     // alarm
        Assert.True(world.Get<AIState>(picker).TaskStarted);
    }

    // The `utility` schedule selector, named by nothing but the profile's `utility` options: idle at full
    // health; flee once health has fallen far enough that flee's consideration outscores idle.
    [Fact]
    public void TheUtilitySelectorRunsTheBestScoringSchedule()
    {
        using var app = NewGame();
        var world = app.World;
        var creature = world.Create(Transform.At(new Vector3(0, 0.1f, 0)), "careful");
        world.AddCharacter(creature, EnemyLayer);
        world.Add(creature, new AIState { Profile = Id("careful") });
        world.AddAttributes(creature);
        Assert.Equal(AIScheduleSelectors.Utility, AIScheduleSelectors.NameOf(app.Engine.Records.Get<AIProfileRecord>(Id("careful"))));
        Tick(world, 10);
        Assert.Equal(Conventional.Idle, world.Get<AIState>(creature).Schedule);

        int health = world.Resources.Get<GameplayRegistries>().Attribute(Conventional.Health);
        world.Get<Attributes>(creature).Values.SetBase(health, 45f);   // flee scores a half: idle's 1 wins
        Tick(world, 10);
        Assert.Equal(Conventional.Idle, world.Get<AIState>(creature).Schedule);
        world.Get<Attributes>(creature).Values.SetBase(health, 30f);   // flee scores 2
        Tick(world, 10);
        Assert.Equal(Id("flee"), world.Get<AIState>(creature).Schedule);
    }

    // What a tree wrote on its blackboard is saved with the creature; its place in the tree is not, and a
    // loaded creature starts its tree from the root (where `check done` now holds, so it tallies no more).
    [Fact]
    public void ABlackboardSurvivesASaveAndTheTreeStartsAgainFromTheRoot()
    {
        using var app = NewGame();
        var world = app.World;
        var counter = Creature(world, new Vector3(0, 0.1f, 0), "three", "counter");
        Tick(world, 5);
        Assert.Equal(3f, Board(world, counter, "tally"));
        Assert.True(app.Engine.Saves.Save("tree"));

        AIBlackboard.Set(world, counter, "tally", 0);
        AIBlackboard.Set(world, counter, "done", 0);
        Assert.True(app.Engine.Saves.Load("tree"));

        var loaded = world.Resolve(PersistentId.FromName("counter"));
        Assert.False(loaded.IsNull);
        Assert.Equal(Id("three"), world.Get<AIState>(loaded).Tree);
        Assert.Equal(3f, Board(world, loaded, "tally"));
        Assert.Equal(1f, Board(world, loaded, "done"));
        Tick(world, 10);
        Assert.Equal(3f, Board(world, loaded, "tally"));
    }

    // A tree's mistakes are load errors where they are written: an unknown guard condition (with the one
    // meant), an unknown measure, a node that is two things, an empty composite; a task nobody registered
    // is reported when the first world is created.
    [Fact]
    public void ABehaviourTreeNamesItsMistakesAtLoad()
    {
        using var capture = new CaptureSink();
        using var app = HeadlessApp.Gameplay().File("data/bt.json", """
            [
              { "type": "behaviour_tree", "id": "guard",
                "root": { "selector": [
                  { "when": ["SeeEnemi"], "task": "Wait", "seconds": 1 },
                  { "utility": [ { "considerations": [ { "measure": "helth" } ], "task": "Wiat" } ] } ] } },
              { "type": "behaviour_tree", "id": "both", "root": { "sequence": ["Wait"], "task": "Wait" } },
              { "type": "behaviour_tree", "id": "empty", "root": { "selector": [] } }
            ]
            """, ns: "strictbt").Boot("bt");

        var messages = capture.Entries.Select(e => e.Message).Where(m => m.StartsWith("strictbt:")).ToList();
        Assert.Contains(messages, m => m.StartsWith("strictbt:data/bt.json:") && m.Contains("behaviour_tree strictbt:guard: no AI condition 'SeeEnemi'; did you mean 'SeeEnemy'?"));
        Assert.Contains(messages, m => m.Contains("behaviour_tree strictbt:guard") && m.Contains("'helth'"));
        Assert.Contains(messages, m => m.Contains("behaviour_tree strictbt:guard: no AI task named 'Wiat'; did you mean 'Wait'?"));
        Assert.Contains(messages, m => m.StartsWith("strictbt:data/bt.json:") && m.Contains("is both 'sequence' and 'task'"));
        Assert.Contains(messages, m => m.StartsWith("strictbt:data/bt.json:") && m.Contains("a 'selector' needs at least one node"));
    }
}
