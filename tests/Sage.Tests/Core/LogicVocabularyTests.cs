#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The base's words for a data-only game (issue #275): chance, existence, distance, scene, spawn, destroy,
// teleport, sound, time, scene load, save, log, message and wait. Each is read from JSON, in a game with
// no plugins but the test's own (GateRule, ConditionLanguageTests), and asked or done the way a relay or
// a state machine would.
public class LogicVocabularyTests
{
    public LogicVocabularyTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    private static RecordId Id(string name) => new("sage", name);

    private const string Content = """
        [{ "type": "prefab", "id": "ghost" },
         { "type": "prefab", "id": "marker" },
         { "type": "scene", "id": "hall", "place": [ { "prefab": "marker", "at": [1, 0, 0], "name": "hall_marker" } ] },
         { "type": "scene", "id": "crypt", "player": { "prefab": "marker", "at": [0, 0, 0] },
           "place": [ { "prefab": "marker", "at": [20, 0, 0], "name": "crypt_in", "yaw": 90 } ] },

         { "type": "gate_rule", "id": "coin", "requires": { "random": 0.5 } },
         { "type": "gate_rule", "id": "never", "requires": { "random": 0 } },
         { "type": "gate_rule", "id": "always", "requires": { "random": 1 } },
         { "type": "gate_rule", "id": "boss_here", "requires": { "entity_exists": "boss" } },
         { "type": "gate_rule", "id": "other_here", "requires": { "entity_exists": "!other" } },
         { "type": "gate_rule", "id": "near_altar", "requires": { "distance_to": "altar", "max": 3 } },
         { "type": "gate_rule", "id": "ring", "requires": { "distance_to": "altar", "from": "boss", "min": 2, "max": 5 } },
         { "type": "gate_rule", "id": "in_crypt", "requires": { "in_scene": "crypt" } },

         { "type": "gate_rule", "id": "summon", "then": [ { "spawn_prefab": "ghost", "at": "altar", "offset": [0, 1, 0], "yaw": 90, "name": "ghost_1" },
                                                          { "spawn_prefab": "ghost", "position": [7, 0, 7], "name": "ghost_2" } ] },
         { "type": "gate_rule", "id": "banish", "then": [ { "destroy": "ghost_1" }, { "destroy": "!other" } ] },
         { "type": "gate_rule", "id": "beam", "then": [ { "teleport": "!subject", "to": "altar", "offset": [0, 0, 2] } ] },
         { "type": "gate_rule", "id": "beam_to", "then": [ { "action": "teleport", "target": "boss", "position": [3, 4, 5], "yaw": 180 } ] },
         { "type": "gate_rule", "id": "bell", "then": [ { "play_sound": "bell", "at": "altar", "volume": 0.5 }, { "play_sound": "music" } ] },
         { "type": "gate_rule", "id": "rest", "then": [ { "pass_time": 8, "reason": "rest" } ] },
         { "type": "gate_rule", "id": "to_crypt", "then": [ { "load_scene": "crypt" } ] },
         { "type": "gate_rule", "id": "to_crypt_door", "then": [ { "load_scene": "crypt", "entry": "crypt_in" } ] },
         { "type": "gate_rule", "id": "checkpoint", "then": [ { "save_game": "checkpoint" } ] },
         { "type": "gate_rule", "id": "talk", "then": [ { "log": "the bell rang", "level": "warn" }, { "message": "Something stirs.", "kind": "bad", "seconds": 4 } ] },
         { "type": "gate_rule", "id": "slowly", "then": [ { "set_var": "a", "value": 1 }, { "wait": 0.5 }, { "set_var": "b", "value": 1 },
                                                          { "wait": 0.25 }, { "set_var": "c", "value": 1 }, { "destroy": "!subject" } ] }]
        """;

    private static HeadlessApp App(string world = "words")
    {
        using var log = new CaptureSink();
        var app = HeadlessApp.Bare().With(new LogicTestPlugin()).File("data/words.json", Content).Boot(world);
        // Only this content's: the sink hears every test running beside this one.
        var errors = log.Entries.Where(e => e.Level >= LogLevel.Error && e.Message.Contains("words.json")).Select(e => e.Message).ToList();
        Assert.True(app.Records.ErrorCount == 0 && errors.Count == 0, string.Join("\n", errors));
        return app;
    }

    private static bool Holds(HeadlessApp app, string rule, Entity subject = default, Entity other = default) =>
        Conditions.Evaluate(app.World, subject, app.Records.Get<GateRule>(Id(rule)).Requires, other);

    private static void Do(HeadlessApp app, string rule, Entity subject = default, Entity other = default) =>
        Conditions.Run(app.World, subject, app.Records.Get<GateRule>(Id(rule)).Then, other);

    private static void Tick(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(Dt);
    }

    // Every word is the base's, so a game with no gameplay plugins has them.
    [Fact]
    public void TheWordsAreTheBases()
    {
        using var app = App();
        var owners = app.Engine.Vocabularies.All.SelectMany(v => v.Entries.Select(e => (v.Name, e.Id, e.Owner))).ToList();
        foreach (string id in new[] { "random", "entity_exists", "distance_to", "in_scene" })
            Assert.Contains(("condition", id, "sage.core"), owners);
        foreach (string id in new[] { "spawn_prefab", "destroy", "teleport", "play_sound", "pass_time", "load_scene", "save_game", "log", "message", "wait" })
            Assert.Contains(("action", id, "sage.core"), owners);
    }

    // `random` draws from the world's own stream: the same numbers on every run of a world of that name,
    // and, across a save, the numbers the game would have drawn.
    [Fact]
    public void RandomIsDeterministic_AcrossRunsAndASave()
    {
        bool[] Draw(HeadlessApp app, int n) => Enumerable.Range(0, n).Select(_ => Holds(app, "coin")).ToArray();

        using var first = App("dice");
        using var second = App("dice");
        var a = Draw(first, 40);
        Assert.Equal(a, Draw(second, 40));
        Assert.Contains(true, a);
        Assert.Contains(false, a);

        // Never and always.
        Assert.DoesNotContain(true, Enumerable.Range(0, 20).Select(_ => Holds(first, "never")));
        Assert.DoesNotContain(false, Enumerable.Range(0, 20).Select(_ => Holds(first, "always")));

        // Saved: the draws after a load are the draws after the save.
        first.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        Assert.True(first.Engine.Saves.Save("dice"));
        var after = Draw(first, 30);
        Assert.True(first.Engine.Saves.Load("dice"));
        Assert.Equal(after, Draw(first, 30));
        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(first.Engine.Saves.Root, "dice", "world_dice.json")))!;
        Assert.NotNull(saved["resources"]!["random"]);
    }

    // `entity_exists` and `distance_to`, by name and by `!other`.
    [Fact]
    public void EntityExistsAndDistanceToFindEntitiesByName()
    {
        using var app = App();
        var world = app.World;
        var player = world.Create(Transform.At(Vector3.Zero), "player");
        var altar = world.Create(Transform.At(new Vector3(0, 0, 2.5f)), "altar");

        Assert.False(Holds(app, "boss_here"));
        var boss = world.Create(Transform.At(new Vector3(0, 0, 6f)), "boss");
        Assert.True(Holds(app, "boss_here"));
        Assert.True(Holds(app, "other_here", player, altar));
        Assert.False(Holds(app, "other_here", player, default));

        Assert.True(Holds(app, "near_altar", player));
        Assert.True(Holds(app, "ring", player));                    // boss to altar: 3.5 m
        world.Teleport(player, Transform.At(new Vector3(0, 0, -1f)));
        Assert.False(Holds(app, "near_altar", player));             // 3.5 m: too far
        world.Teleport(boss, Transform.At(new Vector3(0, 0, 3f)));
        Assert.False(Holds(app, "ring", player));                   // 0.5 m: too close

        world.Destroy(boss);
        Assert.False(Holds(app, "boss_here"));
        Assert.False(Holds(app, "ring", player));                   // nobody to measure from
    }

    // `spawn_prefab` at an entity (turned as it is) or a place, `destroy` by name or `!other`, `teleport`
    // to an entity or a place.
    [Fact]
    public void SpawnDestroyAndTeleportChangeTheWorld()
    {
        using var app = App();
        var world = app.World;
        var player = world.Create(Transform.At(Vector3.Zero), "player");
        var altarAt = Transform.At(new Vector3(4, 0, 0));
        altarAt.LocalRotation = SageMath.RotationFromYaw(MathF.PI / 2f);
        var altar = world.Create(altarAt, "altar");

        Do(app, "summon", player, altar);
        var ghost = world.FindByName("ghost_1");
        Assert.False(ghost.IsNull);
        Assert.True(Vector3.Distance(new Vector3(4, 1, 0), world.Get<Transform>(ghost).LocalPosition) < 1e-4f);
        Assert.Equal(180f, MathF.Abs(SageMath.YawOf(world.Get<Transform>(ghost).LocalRotation) * 180f / MathF.PI), 2);
        Assert.True(world.Has<Persistent>(ghost));                  // saved like anything a game spawns
        var other = world.FindByName("ghost_2");
        Assert.True(Vector3.Distance(new Vector3(7, 0, 7), world.Get<Transform>(other).LocalPosition) < 1e-4f);

        Do(app, "banish", player, other);
        Assert.True(world.FindByName("ghost_1").IsNull);
        Assert.False(world.IsAlive(other));

        Do(app, "beam", player, default);
        Assert.True(Vector3.Distance(new Vector3(4, 0, 2), world.Get<Transform>(player).LocalPosition) < 1e-4f);
        Assert.Equal(90f, SageMath.YawOf(world.Get<Transform>(player).LocalRotation) * 180f / MathF.PI, 2);
        Assert.Equal(world.Get<Transform>(player).LocalPosition, world.Get<GlobalTransform>(player).Previous.Position);   // no slide

        var boss = world.Create(Transform.At(Vector3.Zero), "boss");
        Do(app, "beam_to", player, default);
        Assert.True(Vector3.Distance(new Vector3(3, 4, 5), world.Get<Transform>(boss).LocalPosition) < 1e-4f);
        Assert.Equal(180f, MathF.Abs(SageMath.YawOf(world.Get<Transform>(boss).LocalRotation) * 180f / MathF.PI), 2);
    }

    // `play_sound` asks presentation for a sound (SoundRequested), at an entity or everywhere; `message`
    // is a line for the player; `log` a line in the log.
    [Fact]
    public void PlaySoundMessageAndLogSayWhatTheySay()
    {
        using var app = App();
        var world = app.World;
        world.Create(Transform.At(new Vector3(1, 2, 3)), "altar");
        var reader = world.Events.Reader<SoundRequested>(this, Schedule.Fixed);
        using var log = new CaptureSink();

        Do(app, "bell");
        Do(app, "talk");
        Tick(world);

        var sounds = new List<SoundRequested>();
        foreach (ref readonly var sound in reader.Read()) sounds.Add(sound);
        Assert.Equal(2, sounds.Count);
        Assert.Equal((Id("bell"), new Vector3(1, 2, 3), true, 0.5f), (sounds[0].Sound, sounds[0].Point, sounds[0].Positional, sounds[0].Volume));
        Assert.Equal((Id("music"), false), (sounds[1].Sound, sounds[1].Positional));

        var said = world.Messages().Messages.ToArray();
        Assert.Contains(said, m => m.Text == "Something stirs." && m.Kind == MessageKind.Bad && m.Duration == 4f);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains("the bell rang"));
    }

    // `pass_time` moves the clock at the tick's end; `save_game` saves into a slot at the tick's end.
    [Fact]
    public void PassTimeAndSaveGameRunAtTheTickBoundary()
    {
        using var app = App();
        var world = app.World;
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        var clock = WorldClock.Of(world);
        clock.Scale = 0;
        double before = clock.Elapsed;

        Do(app, "rest");
        Assert.Equal(before + 8, clock.Elapsed, 6);

        Do(app, "checkpoint");
        Assert.Contains(app.Engine.Saves.Slots, s => s.Name == "checkpoint");
    }

    // `in_scene` and `load_scene`: to a scene's start, or to an entry as a door goes.
    [Fact]
    public void LoadSceneGoesThereAndInSceneSaysSo()
    {
        using var app = App();
        var world = app.World;
        Assert.True(app.Engine.Scenes.Load(world, Id("hall")));
        var player = world.Create(Transform.At(new Vector3(9, 9, 9)), "player");
        player.AddTag<PlayerControlled>();
        Assert.False(Holds(app, "in_crypt"));

        Do(app, "to_crypt");
        Assert.True(Holds(app, "in_crypt"));
        Assert.True(world.FindByName("hall_marker").IsNull);
        Assert.True(Vector3.Distance(Vector3.Zero, world.Get<Transform>(player).LocalPosition) < 1e-4f);   // the scene's start

        Assert.True(app.Engine.Scenes.Load(world, Id("hall")));
        Do(app, "to_crypt_door");
        Assert.True(Holds(app, "in_crypt"));
        Assert.True(Vector3.Distance(new Vector3(20, 0, 0), world.Get<Transform>(player).LocalPosition) < 1e-4f);   // the entry
    }

    // `wait` puts the rest of the list off: a wait of N begun on tick D runs the rest on tick D + N/dt,
    // as a delayed wire would arrive, and a later wait waits again.
    [Fact]
    public void AWaitPutsOffTheRestOfTheList()
    {
        using var app = App();
        var world = app.World;
        var vars = Vars.Of(world);
        var subject = world.Spawn(Id("ghost"));

        Do(app, "slowly", subject);
        Assert.Equal((1d, 0d, 0d), (vars.Get("a"), vars.Get("b"), vars.Get("c")));
        Tick(world, 29);
        Assert.Equal(0, vars.Get("b"));
        Tick(world);
        Assert.Equal(1, vars.Get("b"));                              // tick 30: half a second
        Tick(world, 14);
        Assert.Equal(0, vars.Get("c"));
        Assert.True(world.IsAlive(subject));
        Tick(world);
        Assert.Equal(1, vars.Get("c"));                              // tick 45: a quarter more
        Assert.False(world.IsAlive(subject));                        // with the subject it began with
        Assert.Equal(0, LogicSequences.Of(world).Count);

        // A paused world's waits stand still.
        vars.Set("b", 0);
        Do(app, "slowly");
        world.Paused = true;
        Tick(world, 60);
        Assert.Equal(0, vars.Get("b"));
        world.Paused = false;
        Tick(world, 30);
        Assert.Equal(1, vars.Get("b"));
    }

    // A sequence saved half-way finishes after a load, on the tick it would have, with its subject found
    // again by persistent id.
    [Fact]
    public void AWaitSavedHalfWayFinishesAfterALoad()
    {
        using var app = App("waits");
        var world = app.World;
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        var vars = Vars.Of(world);
        var subject = world.Spawn(Id("ghost"));
        var id = world.Get<Persistent>(subject).Id;

        Do(app, "slowly", subject);
        Tick(world, 10);
        Assert.True(app.Engine.Saves.Save("half"));
        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(app.Engine.Saves.Root, "half", "world_waits.json")))!;
        var pending = saved["resources"]!["sequences"]!["data"]!["Pending"]!.AsArray();
        Assert.Single(pending);
        Assert.Equal(4, pending[0]!["Then"]!.AsArray().Count);        // set b, wait, set c, destroy

        Tick(world, 40);                                             // the game goes on and finishes it
        Assert.Equal(1, vars.Get("c"));
        Assert.True(app.Engine.Saves.Load("half"));
        vars = Vars.Of(world);                                       // a load replaces the resource
        Assert.Equal(0, vars.Get("b"));
        var again = world.Resolve(id);
        Assert.True(world.IsAlive(again));                           // put back by the load

        Tick(world, 19);
        Assert.Equal(0, vars.Get("b"));
        Tick(world);
        Assert.Equal(1, vars.Get("b"));                              // 10 + 20 = tick 30, as before the save
        Tick(world, 15);
        Assert.Equal(1, vars.Get("c"));
        Assert.False(world.IsAlive(again));
    }

    // The schemas `sage schema` writes know every word, so an editor checks them. (Ids are the loaded
    // games': the committed schemas list the ids games load.)
    [Fact]
    public void TheCommittedSchemasKnowTheWords()
    {
        var validator = new SchemaValidator(Path.Combine(TestEnv.FolderAbove("Sage.sln"), "schemas"));
        var good = JsonNode.Parse("""
            { "type": "prefab", "id": "p", "parts": { "logic_relay": {
                "requires": { "all": [ { "random": 0.5 }, { "entity_exists": "boss" }, { "distance_to": "altar", "max": 3 },
                                       { "in_scene": "crypt" }, { "has_tag": "state.burning", "entity": "guard" }, { "is_alive": "boss" } ] },
                "then": [ { "spawn_prefab": "campfire", "at": "altar", "offset": [0, 1, 0] }, { "destroy": "!other" },
                          { "teleport": "!subject", "to": "exit" }, { "play_sound": "pickup" }, { "pass_time": 8 },
                          { "load_scene": "crypt", "entry": "in" }, { "save_game": "checkpoint" }, { "log": "hi" },
                          { "message": "Hi.", "kind": "Good" }, { "wait": 2 }, { "set_tag": "state.burning", "target": "guard", "on": false },
                          { "cue": "weapon_swing" } ] } } }
            """);
        Assert.Empty(validator.Validate(good));
        foreach (string bad in new[] { """{ "wiat": 2 }""", """{ "teleport": "!subject", "too": "exit" }""" })
        {
            var record = JsonNode.Parse("""{ "type": "prefab", "id": "p", "parts": { "logic_relay": { "then": [] } } }""")!;
            record["parts"]!["logic_relay"]!["then"]!.AsArray().Add(JsonNode.Parse(bad));
            Assert.NotEmpty(validator.Validate(record));
        }
    }
}

// The new conditions ask without allocating, once a name has been found (02 §4.6). Measured alone.
[Xunit.Collection(MeasurementsCollection.Name)]
public class LogicVocabularyAllocationTests
{
    public LogicVocabularyAllocationTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void TheNewConditionsAllocateNothing()
    {
        const string rules = """
            [{ "type": "gate_rule", "id": "gate",
               "requires": { "all": [ { "random": 1 }, { "entity_exists": "altar" }, { "distance_to": "altar", "max": 3 }, { "not": { "in_scene": "crypt" } } ] } }]
            """;
        using var app = HeadlessApp.Bare().With(new LogicTestPlugin()).File("data/gate.json", rules).Boot("measure");
        var world = app.World;
        var player = world.Create(Transform.At(Vector3.Zero), "player");
        world.Create(Transform.At(Vector3.UnitX), "altar");
        var requires = app.Records.Get<GateRule>(new RecordId("sage", "gate")).Requires;

        int held = 0;
        for (int i = 0; i < 10; i++) held += Conditions.Evaluate(world, player, requires) ? 1 : 0;   // warm up
        AllocationProbe.AssertNone(10_000, () =>
        {
            if (Conditions.Evaluate(world, player, requires)) held++;
        });
        Assert.Equal(10_010, held);
    }
}
