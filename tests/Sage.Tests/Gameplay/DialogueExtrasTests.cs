#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Sage.Kits.Rpg;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Dialogue's extras (issue #392): barks (a line said without a window), greetings chosen by condition,
// topics a speaker brings up, topics learnt by being said (Morrowind's hyperlinks), and TopicAsked.
public class DialogueExtrasTests
{
    public DialogueExtrasTests() { _ = TestEnv.UserRoot; }

    // A guard that notices the player: the engine's AI records, cut to what seeing and chasing need.
    private const string AiRecords = """
        [{ "type": "gameplay_conventions", "id": "default_conventions", "health": "health", "dead": "state.dead", "damageType": "physical",
           "aiProfile": "default_ai", "schedules": { "idle": "idle", "chase": "chase", "meleeAttack": "melee_attack" } },
         { "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
         { "type": "tag", "id": "state.dead" },
         { "type": "effect", "id": "damage", "duration": "Instant", "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
         { "type": "damage_type", "id": "physical", "effect": "damage" },
         { "type": "attack", "id": "claws", "damage": 12, "damageType": "physical", "reach": 2.2, "radius": 0.45, "windupTime": 0.4, "recoverTime": 0.2, "cooldown": 1.0 },
         { "type": "ai_profile", "id": "default_ai", "sightRange": 25, "meleeRange": 1.8, "thinkRate": 20 },
         { "type": "ai_schedule", "id": "idle", "tasks": [{ "task": "Wait", "seconds": 1.5 }], "interrupts": ["SeeEnemy"] },
         { "type": "ai_schedule", "id": "chase", "tasks": [{ "task": "MoveToTarget", "distance": 1.6 }], "interrupts": ["EnemyInMeleeRange", "LostEnemy", "NoEnemy"] },
         { "type": "ai_schedule", "id": "melee_attack", "tasks": ["FaceTarget", { "task": "MeleeAttack", "giveUpAfter": 0.2 }, { "task": "Wait", "seconds": 0.4 }], "interrupts": ["LostEnemy", "NoEnemy"] },

         { "type": "faction", "id": "watch", "standing": 0 },
         { "type": "barks", "id": "guard", "speaker": "Guard", "cooldown": 0,
           "lines": [ { "on": "alert", "requires": { "standing": "watch", "max": -20 }, "text": "You again!" },
                      { "on": "alert", "text": "Halt!" },
                      { "on": "lost", "text": "Must have been the wind." },
                      { "on": "hurt", "text": "Ow!" } ] }]
        """;

    private const string TalkRecords = """
        [{ "type": "faction", "id": "town", "standing": 0 },
         { "type": "barks", "id": "crier", "speaker": "Crier", "cooldown": 5, "idleSeconds": 2, "range": 0,
           "lines": [ { "on": "idle", "text": "Hear ye!" },
                      { "on": "idle", "text": "News from the coast!" },
                      { "on": "farewell", "requires": { "standing": "town", "min": 10 }, "text": "Safe roads, friend." },
                      { "on": "farewell", "text": "Off with you." } ] },
         { "type": "prefab", "id": "crier", "name": "crier", "parts": { "barks": "crier", "dialogue": "innkeeper" } },
         { "type": "dialogue", "id": "innkeeper", "start": "greet",
           "greetings": [ { "requires": { "standing": "town", "min": 30 }, "text": "My favourite customer!", "goto": "regular" },
                          { "requires": { "standing": "town", "max": -10 }, "text": "We don't serve your kind." },
                          { "text": "Welcome, stranger. Ask about the ward if you like.",
                            "then": [ { "add_var": "greeted" } ] } ],
           "topics": [ "rooms" ],
           "nodes": [ { "id": "greet", "text": "What'll it be?", "options": [
                          { "text": "Something else.", "goto": "else" },
                          { "text": "Goodbye.", "end": true, "actions": [ { "bark": "farewell" } ] } ] },
                      { "id": "else", "text": "The warden drinks here.", "options": [ { "text": "Bye.", "end": true } ] },
                      { "id": "regular", "text": "The usual?", "options": [ { "text": "Bye.", "end": true } ] } ] },
         { "type": "dialogue", "id": "stranger", "nodes": [ { "id": "hi", "text": "Hm?", "options": [ { "text": "Bye.", "end": true } ] } ] },
         { "type": "dialogue_topic", "id": "rooms", "infos": [ { "text": "Two coins a night. The old ward is cheaper." } ] },
         { "type": "dialogue_topic", "id": "ward", "keyword": "the old ward", "linked": true,
           "infos": [ { "text": "Where the plague was." } ] },
         { "type": "dialogue_topic", "id": "warden", "keyword": "warden", "linked": true, "infos": [ { "text": "He keeps the keys." } ] },
         { "type": "dialogue_topic", "id": "plague", "keyword": "plague", "infos": [ { "text": "Nobody talks of it." } ] }]
        """;

    private static RecordId Id(string name) => new("sage", name);

    private static Entity Player(World world, Vector3 at)
    {
        var entity = world.Create(Transform.At(at), "player");
        world.Add(entity, Collider.Standing(0.35f, 1.8f, 1));
        world.Add(entity, RigidBody.Kinematic());
        world.AddAttributes(entity);
        entity.AddTag<PlayerControlled>();
        return entity;
    }

    // Ticks, keeping what was barked (a queue keeps its events only a few ticks).
    private static void Run(World world, int ticks, EventReader<Barked> reader, List<Barked> into)
    {
        for (int i = 0; i < ticks; i++)
        {
            world.RunFixed(1f / 60f);
            foreach (ref readonly var barked in reader.Read()) into.Add(barked);
        }
    }

    private static List<Barked> Drain(List<Barked> said)
    {
        var all = new List<Barked>(said);
        said.Clear();
        return all;
    }

    // Acceptance: a guard (a prefab with a `barks` part) barks on alert when its AI takes the player as its
    // target, says it in the subtitles, picks its line by reputation, and barks again when it loses them.
    [Xunit.Fact]
    public void AGuardBarksOnAlertAndWhenItLosesYou()
    {
        using var app = HeadlessApp.Gameplay().With(new UiModule()).File("data/ai.json", AiRecords).Boot("barks");
        var world = app.World;
        Assert.Equal(0, app.Records.ErrorCount);
        var ground = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        world.Add(ground, Collider.Box(new Vector3(400, 1, 400)));
        var heard = world.Events.Reader<Barked>("test");
        var said = new List<Barked>();

        var guard = world.Create(Transform.At(new Vector3(0, 0.1f, 0)), "guard");
        world.AddCharacter(guard, 2);
        world.Add(guard, new AIState { Schedule = Conventional.Idle });
        world.Add(guard, Melee.With(Id("claws")));
        world.AddAttributes(guard);
        world.Add(guard, new Barks { Record = Id("guard") });
        Run(world, 30, heard, said);
        Assert.Empty(Drain(said));                                    // nobody to see: nothing said

        var player = Player(world, new Vector3(0, 0.1f, -10));         // in front of it, in sight
        Run(world, 60, heard, said);
        Assert.Equal(player, world.Get<AIState>(guard).Target);
        var alert = Assert.Single(Drain(said));
        Assert.Equal(new Barked(guard, player, "alert", "Halt!"), alert);
        var line = Assert.Single(world.Resources.Get<Subtitles>().Lines);
        Assert.Equal("Guard: Halt!", Subtitles.Format(line));

        // It loses its target (here: the player is gone) and says so.
        world.Destroy(player);
        Run(world, 10, heard, said);
        var lost = Assert.Single(Drain(said));
        Assert.Equal("lost", lost.Occasion);
        Assert.Equal("Must have been the wind.", lost.Text);

        // A player the watch hates is greeted differently: the line's `requires` asks their standing.
        Factions.Change(world, Id("watch"), -50f);
        var again = Player(world, new Vector3(0, 0.1f, -10));
        Run(world, 60, heard, said);
        Assert.Equal(new Barked(guard, again, "alert", "You again!"), Assert.Single(Drain(said)));

        // Hurt, it says so to whoever hit it.
        world.Events.Send(new Damaged(new DamageInfo(again, guard, Id("physical"), 5f, Vector3.Zero, Vector3.Zero), 5f));
        Run(world, 1, heard, said);
        Assert.Contains(new Barked(guard, again, "hurt", "Ow!"), Drain(said));
    }

    // Lines of an occasion take turns, a barker waits out its cooldown, idle lines come every idleSeconds,
    // the `bark` action says a game's own occasion with a requires, and the subtitle names the speaker as
    // the record says (`range` 0: heard anywhere).
    [Xunit.Fact]
    public void BarksTakeTurnsWaitTheirCooldownAndAnswerTheBarkAction()
    {
        using var app = HeadlessApp.Gameplay().With(new UiModule()).File("data/talk.json", TalkRecords).Boot("barks");
        var world = app.World;
        Assert.Equal(0, app.Records.ErrorCount);
        var heard = world.Events.Reader<Barked>("test");
        var said = new List<Barked>();
        var crier = world.Spawn(Id("crier"), new Vector3(0, 0, -3));   // the `barks` part
        Assert.Equal(Id("crier"), world.Get<Barks>(crier).Record);
        var player = Player(world, Vector3.Zero);

        Run(world, 60 * 15, heard, said);   // idle every 2 s, but a line every 5 s at most
        var idle = Drain(said);
        Assert.Equal(new[] { "Hear ye!", "News from the coast!", "Hear ye!" }, idle.Select(b => b.Text));
        Assert.All(idle, b => Assert.Equal("idle", b.Occasion));
        Assert.Contains("Crier: Hear ye!", world.Resources.Get<Subtitles>().Lines.Select(l => Subtitles.Format(l)));

        Assert.False(DialogueBarks.Bark(world, crier, "farewell", player));   // still cooling down
        Run(world, 60 * 5, heard, said);
        Drain(said);
        Assert.False(DialogueBarks.Bark(world, crier, "nothing_written", player));

        // The `bark` action (a dialogue option's): the speaker barks at the listener.
        Assert.True(DialogueRules.Start(world, crier, player));
        Assert.True(DialogueRules.Pick(world, DialogueRules.Current(world)!.Options[1]));
        foreach (ref readonly var barked in heard.Read()) said.Add(barked);
        Assert.Equal(new Barked(crier, player, "farewell", "Off with you."), Assert.Single(Drain(said)));
    }

    // Acceptance: the greeting is the first whose requires holds — by reputation here — said in place of
    // the start node's line (and shown as the kit's dialogue screen's title), doing its `then`; one may
    // start the conversation at another node. Moving on shows the node's line again.
    [Xunit.Fact]
    public void AGreetingVariesWithReputation()
    {
        using var app = HeadlessApp.Gameplay().File("data/talk.json", TalkRecords).Boot("greetings");
        var world = app.World;
        var innkeeper = world.Create(Transform.At(new Vector3(0, 0, -2)), "innkeeper");
        world.Add(innkeeper, new Dialogue { Record = Id("innkeeper") });
        var player = Player(world, Vector3.Zero);

        Assert.True(DialogueRules.Start(world, innkeeper, player));
        Assert.Equal("Welcome, stranger. Ask about the ward if you like.", DialogueRules.Line(world));
        Assert.Equal("greet", world.Resources.Get<Conversation>().Node);
        Assert.Equal(1, Vars.Of(world).Get("greeted"));
#pragma warning disable SAGE0125   // the kit's screens are Phase 4c's experimental UI
        var screen = new DialogueView();
        var context = new UiBindContext(world, player);
        screen.Refresh(in context);
        Assert.Equal("Welcome, stranger. Ask about the ward if you like.", screen.Title);
        Assert.True(DialogueRules.Pick(world, DialogueRules.Current(world)!.Options[0]));
        Assert.Equal("The warden drinks here.", DialogueRules.Line(world));
        screen.Refresh(in context);
        Assert.Equal("The warden drinks here.", screen.Title);
#pragma warning restore SAGE0125
        world.Resources.Get<Conversation>().Stop();

        Factions.Change(world, Id("town"), -20f);
        Assert.True(DialogueRules.Start(world, innkeeper, player));
        Assert.Equal("We don't serve your kind.", DialogueRules.Line(world));
        world.Resources.Get<Conversation>().Stop();

        Factions.Change(world, Id("town"), 60f);
        Assert.True(DialogueRules.Start(world, innkeeper, player));
        Assert.Equal("My favourite customer!", DialogueRules.Line(world));
        Assert.Equal("regular", world.Resources.Get<Conversation>().Node);   // its goto

        // A dialogue with no greetings says its start node's line, as before.
        var stranger = world.Create(Transform.At(new Vector3(2, 0, -2)), "stranger");
        world.Add(stranger, new Dialogue { Record = Id("stranger") });
        Assert.True(DialogueRules.Start(world, stranger, player));
        Assert.Equal("Hm?", DialogueRules.Line(world));
        Assert.Equal("", world.Resources.Get<Conversation>().Greeting);
    }

    // A speaker's own topics are on their list though the listener never learnt them, and asking one
    // teaches it — so it is asked of somebody else afterwards; every answer sends TopicAsked; and a
    // `linked` topic is learnt when its keyword is said as a whole phrase in an answer, a greeting or a
    // node's line (not "ward" inside "warden"; not a topic that is not `linked`).
    [Xunit.Fact]
    public void ASpeakersTopicsAndTopicsSaidInTheirWordsAreLearnt()
    {
        using var app = HeadlessApp.Gameplay().File("data/talk.json", TalkRecords).Boot("topics");
        var world = app.World;
        var asked = world.Events.Reader<TopicAsked>("test");
        var innkeeper = world.Create(Transform.At(new Vector3(0, 0, -2)), "innkeeper");
        world.Add(innkeeper, new Dialogue { Record = Id("innkeeper") });
        var stranger = world.Create(Transform.At(new Vector3(2, 0, -2)), "stranger");
        world.Add(stranger, new Dialogue { Record = Id("stranger") });
        var player = Player(world, Vector3.Zero);

        var listed = new List<AvailableTopic>();
        DialogueTopics.Available(world, innkeeper, player, listed);
        Assert.Equal(new[] { Id("rooms") }, listed.Select(t => t.Topic));
        DialogueTopics.Available(world, stranger, player, listed);
        Assert.Empty(listed);
        Assert.False(DialogueTopics.Knows(world, player, Id("rooms")));
        Assert.Null(DialogueTopics.Ask(world, stranger, player, Id("rooms")));

        Assert.Equal("Two coins a night. The old ward is cheaper.", DialogueTopics.Ask(world, innkeeper, player, Id("rooms"))?.Text);
        Assert.True(DialogueTopics.Knows(world, player, Id("rooms")));         // asked: the listener's now
        Assert.True(DialogueTopics.Knows(world, player, Id("ward")));          // said in the answer
        Assert.False(DialogueTopics.Knows(world, player, Id("warden")));
        var one = Assert.Single(Read(asked));
        Assert.Equal(new TopicAsked(innkeeper, player, Id("rooms")), one);

        Assert.Equal("Where the plague was.", DialogueTopics.Ask(world, stranger, player, Id("ward"))?.Text);
        Assert.False(DialogueTopics.Knows(world, player, Id("plague")));       // not linked
        Assert.Equal(new TopicAsked(stranger, player, Id("ward")), Assert.Single(Read(asked)));
        Assert.Null(DialogueTopics.Ask(world, stranger, player, Id("plague")));
        Assert.Empty(Read(asked));                                              // no answer, no event

        // A node's line links too ("The warden drinks here."), as a greeting's does.
        Assert.True(DialogueRules.Start(world, innkeeper, player));
        Assert.True(DialogueRules.Pick(world, DialogueRules.Current(world)!.Options[0]));
        Assert.True(DialogueTopics.Knows(world, player, Id("warden")));

        Assert.True(DialogueTopics.Says("Ask about the Old Ward, then.", "the old ward"));
        Assert.False(DialogueTopics.Says("The warden.", "ward"));
    }

    // In the Sandbox's data: a watcher shouts when it sees the player, and the hermit's greeting follows
    // what the hermits think of you.
    [Xunit.Fact]
    public void TheSandboxsWatchersBarkAndItsHermitGreetsByStanding()
    {
        string game = System.IO.Path.Combine(TestEnv.FolderAbove("Sage.sln"), "games", "Sandbox");
        using var app = HeadlessApp.ForGame(game, new global::Sandbox.SandboxModule()).WithEngineContent().Boot();
        var world = app.World;
        var player = Scenes.Player(world);
        var heard = world.Events.Reader<Barked>("test");
        var said = new List<Barked>();

        var watcher = world.FindByName("watcher");
        Assert.False(watcher.IsNull);
        Assert.Equal(new RecordId("sandbox", "watcher_barks"), world.Get<Barks>(watcher).Record);
        var where = Transform.At(world.Get<Transform>(player).LocalPosition + new Vector3(0, 0, -6f));
        where.LocalRotation = SageMath.RotationFromYaw(System.MathF.PI);   // facing the player
        world.Teleport(watcher, where);
        Run(world, 90, heard, said);
        Assert.Contains(said, b => b.Speaker == watcher && b.Occasion == "alert" && b.Target == player);

        var hermit = world.FindByName("hermit");
        Assert.False(hermit.IsNull);
        Factions.Change(world, new RecordId("sandbox", "hermits"), 100f);
        Assert.True(DialogueRules.Start(world, hermit, player));
        Assert.Equal("Ah, my friend. Sit, if you like.", DialogueRules.Line(world));
        world.Resources.Get<Conversation>().Stop();
        Factions.Change(world, new RecordId("sandbox", "hermits"), -200f);
        Assert.True(DialogueRules.Start(world, hermit, player));
        Assert.Equal("You again. Say what you came to say.", DialogueRules.Line(world));
    }

    private static List<TopicAsked> Read(EventReader<TopicAsked> reader)
    {
        var all = new List<TopicAsked>();
        foreach (ref readonly var e in reader.Read()) all.Add(e);
        return all;
    }
}
