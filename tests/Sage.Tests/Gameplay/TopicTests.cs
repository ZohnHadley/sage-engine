#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Topics, Morrowind's way of talking (docs/design/16 "As built (dialogue topics)", issue #93): a keyword
// whose answer is the first info whose `requires` holds, asked of whoever you are talking to.
public class TopicTests
{
    public TopicTests() { _ = TestEnv.UserRoot; }

    internal const string Content = """
        [{ "type": "faction", "id": "guard", "standing": 0 },
         { "type": "quest", "id": "toll", "stages": [ { "id": "asked", "next": "paid" }, { "id": "paid" } ] },
         { "type": "dialogue", "id": "ferryman", "nodes": [ { "id": "hello", "text": "Well?", "options": [
             { "text": "Tell me about...", "topics": true, "actions": [ { "add_var": "asked_about" } ] },
             { "text": "Goodbye.", "end": true } ] } ] },
         { "type": "dialogue", "id": "guard", "nodes": [ { "id": "halt", "text": "Halt.", "options": [ { "text": "Bye.", "end": true } ] } ] },
         { "type": "dialogue_topic", "id": "the_bridge", "keyword": "the bridge", "known": true,
           "infos": [ { "requires": { "standing": "guard", "min": 20 }, "text": "Open to you, friend." },
                      { "requires": { "quest": "toll", "atLeast": "paid" }, "text": "You paid. Go on." },
                      { "requires": { "var": "flood", "eq": 1 }, "text": "Under water." },
                      { "requires": { "speaker": "ferryman" }, "text": "Ask the guard about the toll.", "then": [ { "add_topic": "toll" } ] },
                      { "text": "Closed." } ] },
         { "type": "dialogue_topic", "id": "toll", "keyword": "@topic.toll",
           "infos": [ { "requires": { "all": [ { "speaker": "guard" }, { "quest": "toll", "notStarted": true } ] },
                        "text": "Ten coins.", "then": [ { "start_quest": "toll" } ] },
                      { "requires": { "condition": "speaker", "name": "guard_by_name" }, "text": "Paid or not?" } ] },
         { "type": "dialogue_topic", "id": "secret", "infos": [ { "text": "Shh." } ] }]
        """;

    private static RecordId Id(string name) => new("sage", name);

    private static HeadlessApp NewApp()
    {
        using var log = new CaptureSink();
        var app = HeadlessApp.Gameplay().File("data/topics.json", Content).Boot("topics");
        var errors = log.Entries.Where(e => e.Level >= LogLevel.Error && e.Message.Contains("topics.json")).Select(e => e.Message).ToList();
        Assert.True(app.Records.ErrorCount == 0 && errors.Count == 0, string.Join("\n", errors));
        return app;
    }

    private static Entity Npc(World world, string dialogue, string? name = null)
    {
        var npc = world.Create(Transform.At(new Vector3(0, 0, -2)), name ?? dialogue);
        world.Add(npc, new Dialogue { Record = Id(dialogue) });
        return npc;
    }

    private static string? Said(World world, Entity speaker, Entity listener, string topic) =>
        DialogueTopics.Ask(world, speaker, listener, Id(topic))?.Text;

    private static List<string> Listed(World world, Entity speaker, Entity listener)
    {
        var into = new List<AvailableTopic>();
        int count = DialogueTopics.Available(world, speaker, listener, into);
        Assert.Equal(count, into.Count);
        return into.Select(t => t.Keyword).ToList();
    }

    // Acceptance: the same topic answers differently by faction standing, by quest stage and by a var.
    [Xunit.Fact]
    public void ATopicAnswersByStandingQuestStageAndAVar()
    {
        using var app = NewApp();
        var world = app.World;
        var player = world.Create(Transform.At(Vector3.Zero), "player");
        var guard = Npc(world, "guard");

        Assert.Equal("Closed.", Said(world, guard, player, "the_bridge"));
        Vars.Of(world).Set("flood", 1);
        Assert.Equal("Under water.", Said(world, guard, player, "the_bridge"));
        Assert.True(Quests.Start(world, Id("toll")));
        Assert.Equal("Under water.", Said(world, guard, player, "the_bridge"));   // at `asked`: not yet
        Assert.True(Quests.SetStage(world, Id("toll"), "paid"));
        Assert.Equal("You paid. Go on.", Said(world, guard, player, "the_bridge"));
        Factions.Change(world, Id("guard"), 25f);
        Assert.Equal("Open to you, friend.", Said(world, guard, player, "the_bridge"));

        // Answer only looks; the text is stored as written.
        Assert.Same(DialogueTopics.Answer(world, guard, player, Id("the_bridge")), app.Records.Get<TopicRecord>(Id("the_bridge")).Infos[0]);
    }

    // Acceptance: a topic learnt from one NPC (`add_topic` in its answer's `then`) appears when talking
    // to another; and only where somebody has an answer for it.
    [Xunit.Fact]
    public void ATopicLearntFromOneNpcIsAskedOfAnother()
    {
        using var app = NewApp();
        var world = app.World;
        var player = world.Create(Transform.At(Vector3.Zero), "player");
        var ferryman = Npc(world, "ferryman");
        var guard = Npc(world, "guard");

        Assert.Equal(new[] { "the bridge" }, Listed(world, ferryman, player));
        Assert.Equal(new[] { "the bridge" }, Listed(world, guard, player));
        Assert.False(DialogueTopics.Knows(world, player, Id("toll")));
        Assert.Null(DialogueTopics.Ask(world, guard, player, Id("toll")));        // not known: refused, nothing done
        Assert.True(Quests.IsNotStarted(world, Id("toll")));

        Assert.Equal("Ask the guard about the toll.", Said(world, ferryman, player, "the_bridge"));
        Assert.True(DialogueTopics.Knows(world, player, Id("toll")));
        Assert.Equal(new[] { Id("toll") }, world.Get<KnownTopics>(player).Topics);

        // The guard answers it (sorted by the keyword as stored: "@topic.toll" before "the bridge");
        // the ferryman has nothing to say about it, so it is not on his list.
        Assert.Equal(new[] { "@topic.toll", "the bridge" }, Listed(world, guard, player));
        Assert.Equal(new[] { "the bridge" }, Listed(world, ferryman, player));
        Assert.Null(DialogueTopics.Ask(world, ferryman, player, Id("toll")));

        Assert.Equal("Ten coins.", Said(world, guard, player, "toll"));
        Assert.True(Quests.IsActive(world, Id("toll")));
        Assert.Equal(new[] { "the bridge" }, Listed(world, guard, player));      // asked: that answer's `requires` is spent
        var byName = Npc(world, "guard", "guard_by_name");
        Assert.Equal("Paid or not?", Said(world, byName, player, "toll"));       // `speaker` by entity name

        // Learning twice is once; a topic everyone knows is not written down; one nobody wrote is not learnt.
        Assert.False(DialogueTopics.Learn(world, player, Id("toll")));
        Assert.False(DialogueTopics.Learn(world, player, Id("the_bridge")));
        Assert.False(DialogueTopics.Learn(world, player, Id("nowhere")));
        Assert.True(DialogueTopics.Learn(world, player, Id("secret")));
        Assert.Equal(new[] { "@topic.toll", "secret", "the bridge" }, Listed(world, byName, player));
        Assert.Equal("secret", app.Records.Get<TopicRecord>(Id("secret")).KeywordOf(Id("secret")));   // no keyword: the id's name
    }

    // A node option with `topics: true` opens the topics: it does what it does, the conversation stays
    // on its node, and `Conversation.Topics` says a screen should show them. The node tree still works.
    [Xunit.Fact]
    public void ANodeOptionOpensTheTopics()
    {
        using var app = NewApp();
        var world = app.World;
        var player = world.Create(Transform.At(Vector3.Zero), "player");
        var ferryman = Npc(world, "ferryman");

        Assert.True(DialogueRules.Start(world, ferryman, player));
        var conversation = world.Resources.Get<Conversation>();
        var node = DialogueRules.Current(world)!;
        Assert.False(conversation.Topics);

        Assert.True(DialogueRules.Pick(world, node.Options[0]));
        Assert.True(conversation.Running);
        Assert.True(conversation.Topics);
        Assert.Equal("hello", conversation.Node);
        Assert.Equal(1, Vars.ValueOf(world, "asked_about"));
        Assert.Equal(new[] { "the bridge" }, Listed(world, conversation.Speaker, conversation.Listener));

        Assert.True(DialogueRules.Pick(world, node.Options[1]));   // goodbye
        Assert.False(conversation.Running);
        Assert.False(conversation.Topics);
    }

    // Acceptance: known topics survive save and load, as `sage:known_topics` on the listener.
    [Xunit.Fact]
    public void KnownTopicsSurviveSaveAndLoad()
    {
        using var app = NewApp();
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        var world = app.World;
        var player = world.Create(Transform.At(Vector3.Zero), "player");
        world.Add(player, new Persistent { Id = PersistentId.FromName("player") });
        Assert.True(DialogueTopics.Learn(world, player, Id("toll")));
        Assert.True(DialogueTopics.Learn(world, player, Id("secret")));
        world.FlushCommands();

        Assert.True(app.Engine.Saves.Save("topics"));
        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(app.Engine.Saves.Root, "topics", "world_topics.json")))!;
        var entity = saved["entities"]!.AsArray().Single(e => e!["components"]?["sage:known_topics"] != null)!;
        Assert.Equal(new[] { "sage:toll", "sage:secret" },
                     entity["components"]!["sage:known_topics"]!["data"]!["Topics"]!.AsArray().Select(t => (string?)t));

        world.Get<KnownTopics>(player).Topics!.Clear();
        Assert.True(app.Engine.Saves.Load("topics"));
        var loaded = world.Resolve(PersistentId.FromName("player"));
        Assert.True(DialogueTopics.Knows(world, loaded, Id("toll")));
        Assert.True(DialogueTopics.Knows(world, loaded, Id("secret")));
        Assert.Equal(new[] { "@topic.toll", "secret", "the bridge" }, Listed(world, Npc(world, "guard", "guard_by_name"), loaded));
    }

    // With the dialogue plugin off, topics degrade: the records are skipped with a warning, nothing is
    // an error, and the API answers "nothing to say" rather than throwing.
    [Xunit.Fact]
    public void WithoutTheDialoguePluginTopicsSayNothing()
    {
        string dir = TestEnv.NewTempDir();
        File.WriteAllText(Path.Combine(dir, "game.json"),
            """{ "name": "Topics", "id": "topicgame", "mounts": ["content"], "modules": { "disable": ["sage.gameplay.dialogue"], "add": [] } }""");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(dir, "content", "data")).FullName, "topics.json"), Content);

        using var log = new CaptureSink();
        using var app = HeadlessApp.ForGame(dir).WithEngineContent().Boot("topics");
        Assert.DoesNotContain("sage.gameplay.dialogue", app.PluginIds);
        Assert.Equal(0, app.Records.ErrorCount);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains("dialogue_topic"));

        var world = app.World;
        var player = world.Create(Transform.At(Vector3.Zero), "player");
        var npc = world.Create(Transform.At(Vector3.One), "guard");
        var into = new List<AvailableTopic> { new(Id("stale"), "stale") };
        Assert.Equal(0, DialogueTopics.Available(world, npc, player, into));
        Assert.Empty(into);
        Assert.Null(DialogueTopics.Ask(world, npc, player, Id("the_bridge")));
        Assert.False(DialogueTopics.Learn(world, player, Id("toll")));
        Assert.False(DialogueTopics.Knows(world, player, Id("the_bridge")));
        Assert.DoesNotContain(app.Engine.Vocabularies.All.SelectMany(v => v.Entries), e => e.Id is "add_topic" or "speaker");
    }
}

// Listing topics allocates nothing once the list has room (a screen asks every frame it is open).
[Xunit.Collection(MeasurementsCollection.Name)]
public class TopicAllocationTests
{
    public TopicAllocationTests() { _ = TestEnv.UserRoot; }

    [Xunit.Fact]
    public void ListingTopicsAllocatesNothing()
    {
        using var app = HeadlessApp.Gameplay().File("data/topics.json", TopicTests.Content).Boot("measure");
        var world = app.World;
        var player = world.Create(Transform.At(Vector3.Zero), "player");
        var guard = world.Create(Transform.At(Vector3.One), "guard");
        world.Add(guard, new Dialogue { Record = new RecordId("sage", "guard") });
        DialogueTopics.Learn(world, player, new RecordId("sage", "toll"));
        DialogueTopics.Learn(world, player, new RecordId("sage", "secret"));
        var into = new List<AvailableTopic>(8);

        int listed = 0;
        for (int i = 0; i < 10; i++) listed += DialogueTopics.Available(world, guard, player, into);   // warm up
        AllocationProbe.AssertNone(10_000, () =>
        {
            listed += DialogueTopics.Available(world, guard, player, into);
            if (DialogueTopics.Answer(world, guard, player, new RecordId("sage", "the_bridge")) != null) listed++;
        });
        Assert.Equal(3 * 10_010 + 10_000, listed);
    }
}
