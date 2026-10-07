#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Sage.Kits.Rpg;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Quest objectives for places, talking, timers and failure (issue #391): `reach` a trigger volume, `talk`
// by asking about a topic, a stage with a time limit, a stage that fails when a condition holds, quest
// items that cannot be dropped or sold, and a journal that keeps what it was told. One quest of each kind,
// in data, and a golden save taken with all of them half done.
public class QuestFailureTests
{
    public QuestFailureTests() { _ = TestEnv.UserRoot; }

    private static RecordId Id(string name) => new("sage", name);

    private static readonly RecordId Gate = Id("gate"), Rumor = Id("rumor"), Courier = Id("courier"), Escort = Id("escort");
    private static readonly RecordId Letter = Id("sealed_letter");

    private const string Records = """
        [{ "type": "gameplay_conventions", "id": "default_conventions", "health": "health", "dead": "state.dead", "damageType": "physical", "playerFaction": "player" },
         { "type": "faction", "id": "player" },
         { "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
         { "type": "tag", "id": "state.dead" },
         { "type": "item", "id": "sealed_letter", "label": "sealed letter", "weight": 0.1, "value": 50, "questItem": true },
         { "type": "item", "id": "coin", "label": "coin", "weight": 0, "maxStack": 1000, "category": "money" },
         { "type": "merchant", "id": "pedlar", "currency": "coin", "gold": 500 },

         { "type": "dialogue_topic", "id": "the_bridge", "keyword": "the bridge", "known": true,
           "infos": [ { "text": "Washed out in the spring." } ] },
         { "type": "dialogue_topic", "id": "the_abbot", "known": true,
           "infos": [ { "requires": { "quest": "courier", "failed": true }, "text": "He left without his letter." } ] },
         { "type": "dialogue_topic", "id": "give_up", "known": true,
           "infos": [ { "text": "Then go home.", "then": [ { "fail_quest": "gate" } ] } ] },

         { "type": "quest", "id": "gate", "label": "The North Gate",
           "stages": [
             { "id": "go", "text": "Get through the north gate.",
               "objectives": [ { "kind": "reach", "volume": "north_gate", "place": "the north gate" } ], "next": "through" },
             { "id": "through", "text": "Through the gate.", "done": true } ] },

         { "type": "quest", "id": "rumor", "label": "Word of the Bridge",
           "stages": [
             { "id": "ask", "text": "Ask the ferryman about the bridge.",
               "objectives": [ { "kind": "talk", "topic": "the_bridge", "prefab": "ferryman" } ], "next": "heard" },
             { "id": "heard", "text": "The bridge is out.", "done": true } ] },

         { "type": "prefab", "id": "ferryman", "name": "ferryman", "parts": {} },

         { "type": "quest", "id": "courier", "label": "The Abbot's Letter",
           "stages": [
             { "id": "run", "text": "Take the letter to the abbey within six hours.", "timeLimit": 6, "fail": "late",
               "objectives": [ { "kind": "reach", "at": [40, 0, 0], "radius": 3, "place": "the abbey" } ], "next": "delivered" },
             { "id": "delivered", "text": "Delivered.", "done": true },
             { "id": "late", "text": "Too late: the abbot has gone.", "failed": true } ] },

         { "type": "quest", "id": "escort", "label": "The Pilgrim",
           "stages": [
             { "id": "guard", "text": "See the pilgrim safely to the shrine.",
               "failWhen": { "not": { "entity_exists": "pilgrim" } }, "fail": "lost",
               "objectives": [ { "kind": "reach", "at": [-40, 0, 0], "radius": 3, "place": "the shrine" } ], "next": "safe" },
             { "id": "safe", "text": "The pilgrim is at the shrine.", "done": true },
             { "id": "lost", "text": "The pilgrim is dead.", "failed": true } ] }]
        """;

    private static HeadlessApp NewApp(string? savesRoot = null)
    {
        var app = HeadlessApp.Gameplay().File("data/quests.json", Records).Build();
        if (savesRoot != null) app.Engine.Saves.Root = savesRoot;
        return app;
    }

    // The level: ground, and the gate's trigger volume. Not saved — a level's own entities come from the level.
    private static World NewWorld(HeadlessApp app)
    {
        var world = app.Engine.CreateWorld("main");
        var ground = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        world.Add(ground, Collider.Box(new Vector3(200, 1, 200)));
        var gate = world.Create(Transform.At(new Vector3(0, 1f, -20f)), "north_gate");
        world.Add(gate, new Collider { Shape = ColliderShape.Box, Size = new Vector3(4, 2, 2), IsTrigger = true });
        return world;
    }

    private static Entity Hero(World world)
    {
        var hero = world.Create(Transform.At(Vector3.Zero), "hero");
        world.AddCharacter(hero, world.Resources.Get<IPhysicsWorld>().Layers.Player);
        world.AddInventory(hero, 50f);
        world.AddAttributes(hero);
        hero.AddTag<PlayerControlled>();
        world.Add(hero, new Persistent { Id = PersistentId.FromName("hero") });
        return hero;
    }

    private static Entity Pilgrim(World world)
    {
        var pilgrim = world.Create(Transform.At(new Vector3(-2, 0, 0)), "pilgrim");
        world.Add(pilgrim, new Persistent { Id = PersistentId.FromName("pilgrim") });
        return pilgrim;
    }

    private static Entity Ferryman(World world)
    {
        var ferryman = world.Spawn(Id("ferryman"), new Vector3(3, 0, 0));
        world.FlushCommands();
        return ferryman;
    }

    private static Entity Pedlar(World world)
    {
        var pedlar = world.Create(Transform.At(new Vector3(0, 0, 3)), "pedlar");
        world.AddInventory(pedlar, 500f);
        world.Add(pedlar, new Merchant { Record = Id("pedlar") });
        return pedlar;
    }

    private static void Tick(World world, int ticks = 2)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    private static void Hours(World world, double hours) => WorldClock.Of(world).Hour += hours;   // the same day: noon plus

    private static void StartAll(World world)
    {
        foreach (var quest in new[] { Gate, Rumor, Courier, Escort }) Assert.True(Quests.Start(world, quest));
    }

    // `reach` with a volume: walking into the trigger of that name, not standing near a point.
    [Fact]
    public void WalkingIntoTheNamedVolumeReachesThePlace()
    {
        using var app = NewApp();
        var world = NewWorld(app);
        var hero = Hero(world);
        Assert.True(Quests.Start(world, Gate));
        Assert.Equal("reach the north gate", Quests.Describe(world, app.Records.Get<QuestRecord>(Gate).Stages[0].Objectives[0], 0));

        world.Teleport(hero, Transform.At(new Vector3(0, 0, -15f)));   // near, not in
        Tick(world, 4);
        Assert.Equal("go", Quests.StageOf(world, Gate));

        world.Teleport(hero, Transform.At(new Vector3(0, 0, -20f)));
        Tick(world, 4);
        Assert.True(Quests.IsFinished(world, Gate));
    }

    // `talk` with a topic: asking the right speaker about it and being answered (TopicAsked); anybody else
    // answering the same question does not count.
    [Fact]
    public void AskingTheRightSpeakerAboutATopicCounts()
    {
        using var app = NewApp();
        var world = NewWorld(app);
        var hero = Hero(world);
        Assert.True(Quests.Start(world, Rumor));
        Assert.Equal("ask about the bridge", Quests.Describe(world, app.Records.Get<QuestRecord>(Rumor).Stages[0].Objectives[0], 0));

        var stranger = world.Create(Transform.At(new Vector3(2, 0, 0)), "stranger");
        Assert.NotNull(DialogueTopics.Ask(world, stranger, hero, Id("the_bridge")));
        Tick(world);
        Assert.Equal("ask", Quests.StageOf(world, Rumor));

        Assert.NotNull(DialogueTopics.Ask(world, Ferryman(world), hero, Id("the_bridge")));
        Tick(world);
        Assert.True(Quests.IsFinished(world, Rumor));
    }

    // A timed stage fails when the world's clock passes its limit — to its `fail` stage, which is marked
    // failed — and the quest condition, the event and the journal all say so.
    [Fact]
    public void ATimedStageFailsWhenTheClockRunsOut()
    {
        using var app = NewApp();
        var world = NewWorld(app);
        var hero = Hero(world);
        var changed = new System.Collections.Generic.List<QuestChanged>();
        var reader = world.Events.Reader<QuestChanged>(this, Schedule.Fixed);
        Assert.True(Quests.Start(world, Courier));
        Assert.Equal(6.0, Quests.HoursLeft(world, Courier)!.Value, 2);

        Hours(world, 5);
        Tick(world);
        Assert.Equal("run", Quests.StageOf(world, Courier));
        Assert.Equal(1.0, Quests.HoursLeft(world, Courier)!.Value, 1);

        Hours(world, 1.5);
        Tick(world);
        Assert.Equal("late", Quests.StageOf(world, Courier));
        Assert.True(Quests.IsFailed(world, Courier));
        Assert.False(Quests.IsFinished(world, Courier));   // over, but not done
        Assert.False(Quests.IsActive(world, Courier));
        Assert.Null(Quests.HoursLeft(world, Courier));
        foreach (ref readonly var e in reader.Read()) changed.Add(e);
        Assert.Contains(changed, c => c.Quest == Courier && c.Failed && c.Finished && c.Stage == "late");

        Assert.NotNull(DialogueTopics.Answer(world, Ferryman(world), hero, Id("the_abbot")));   // `quest` … `failed`

#pragma warning disable SAGE0125   // the kit's screens are Phase 4c's experimental UI
        var journal = new JournalView();
        journal.Refresh(new Sage.UI.UiBindContext(world, hero));
        Assert.True(journal.Lines[0].Failed && journal.Lines[0].Done);
        Assert.Contains(journal.Lines, l => l.Past && l.Text == "Too late: the abbot has gone.");
#pragma warning restore SAGE0125
    }

    // `failWhen`: the pilgrim gone fails the escort the next tick; `fail_quest` fails one by hand, and a
    // quest with no `fail` stage ends failed where it stands.
    [Fact]
    public void AFailWhenConditionAndTheFailQuestActionFailAQuest()
    {
        using var app = NewApp();
        var world = NewWorld(app);
        var hero = Hero(world);
        var pilgrim = Pilgrim(world);
        Assert.True(Quests.Start(world, Escort));
        Assert.True(Quests.Start(world, Gate));
        Tick(world);
        Assert.Equal("guard", Quests.StageOf(world, Escort));

        world.Destroy(pilgrim);
        world.FlushCommands();
        Tick(world);
        Assert.Equal("lost", Quests.StageOf(world, Escort));
        Assert.True(Quests.IsFailed(world, Escort));

        Assert.NotNull(DialogueTopics.Ask(world, Ferryman(world), hero, Id("give_up")));   // its `then`: fail_quest
        Assert.True(Quests.IsFailed(world, Gate));
        Assert.Equal("go", Quests.StageOf(world, Gate));
        Assert.False(Quests.Fail(world, Gate));   // once is enough
    }

    // A quest item stays in the bag: it cannot be dropped, nor sold, and the kit's grid says why.
    [Fact]
    public void AQuestItemCannotBeDroppedOrSold()
    {
        using var app = NewApp();
        var world = NewWorld(app);
        var hero = Hero(world);
        world.Give(hero, Letter);

        Assert.False(world.CanDrop(Letter, out string why));
        Assert.Equal("sealed letter is needed for a quest", why);
        Assert.True(world.Drop(hero, Letter).IsNull);
        Assert.True(world.DropAt(hero, 0, 1).IsNull);
        Assert.Equal(1, world.CountOf(hero, Letter));

        var sale = Merchants.Sell(world, hero, Pedlar(world), 0, 1);
        Assert.Equal(TradeRefusal.NotBought, sale.Refusal);
        Assert.Equal(1, world.CountOf(hero, Letter));

        Assert.True(world.Take(hero, Letter));   // the story may still take it
    }

    // The journal keeps what it was told: a stage's text rewritten later (a mod, a patch) does not rewrite
    // the line the player read.
    [Fact]
    public void TheJournalKeepsTheTextItWasToldAtTheTime()
    {
        using var app = NewApp();
        var world = NewWorld(app);
        var hero = Hero(world);
        Assert.True(Quests.Start(world, Rumor));
        var record = app.Records.Get<QuestRecord>(Rumor);
        string was = record.Stages[0].Text;
        record.Stages[0].Text = "Rewritten later.";
        try
        {
            Assert.Equal(new[] { "Ask the ferryman about the bridge." }, Quests.JournalOf(world)!.Of(Rumor)!.Told);
#pragma warning disable SAGE0125   // the kit's screens are Phase 4c's experimental UI
            var journal = new JournalView();
            journal.Refresh(new Sage.UI.UiBindContext(world, hero));
            Assert.Contains(journal.Lines, l => l.Stage && l.Text == "Ask the ferryman about the bridge.");

            DialogueTopics.Ask(world, Ferryman(world), hero, Id("the_bridge"));
            Tick(world);
            journal.Refresh(new Sage.UI.UiBindContext(world, hero));
            Assert.Contains(journal.Lines, l => l.Past && l.Text == "Ask the ferryman about the bridge.");
            Assert.DoesNotContain(journal.Lines, l => l.Text == "Rewritten later.");
#pragma warning restore SAGE0125
            Assert.Equal(new[] { "Ask the ferryman about the bridge.", "The bridge is out." }, Quests.JournalOf(world)!.Of(Rumor)!.Told);
        }
        finally { record.Stages[0].Text = was; }
    }

    // The issue's acceptance: tests/Sage.Tests/Content/Saves/quests was written (format 4) by
    // WriteTheGoldenQuestSaveWhenAsked — the four quests half done: the gate not yet reached, the ferryman
    // not yet asked, the courier two hours into six (and told a line the record no longer says), the pilgrim
    // alive and guarded, the sealed letter in the bag. It must load with all of it, and each quest must
    // then finish or fail the way it would have without the save.
    [Fact]
    public void AGoldenSaveWithFourQuestsHalfDoneLoadsAndCarriesOn()
    {
        string golden = Path.Combine(TestEnv.FolderAbove("Sage.sln"), "tests", "Sage.Tests", "Content", "Saves", "quests");
        using var app = NewApp(golden);
        var world = NewWorld(app);
        using var log = new CaptureSink();
        int thread = Environment.CurrentManagedThreadId;

        Assert.True(app.Engine.Saves.Load("golden"));
        Assert.DoesNotContain(log.Entries, e => e.ThreadId == thread && e.Level >= LogLevel.Warn && e.Message.Contains("golden/world_main"));

        var hero = world.Resolve(PersistentId.FromName("hero"));
        Assert.False(hero.IsNull);
        Assert.True(hero.Tags.Has<PlayerControlled>());
        Assert.Equal("go", Quests.StageOf(world, Gate));
        Assert.Equal("ask", Quests.StageOf(world, Rumor));
        Assert.Equal("run", Quests.StageOf(world, Courier));
        Assert.Equal("guard", Quests.StageOf(world, Escort));
        Assert.Equal(4.0, Quests.HoursLeft(world, Courier)!.Value, 2);
        Assert.Equal("The abbot wants this letter by dusk.", Quests.JournalOf(world)!.Of(Courier)!.Told.Single());
        Assert.Equal(1, world.CountOf(hero, Letter));
        Assert.True(world.Drop(hero, Letter).IsNull);

        // And on from there.
        world.Teleport(hero, Transform.At(new Vector3(0, 0, -20f)));
        Tick(world, 4);
        Assert.True(Quests.IsFinished(world, Gate));

        DialogueTopics.Ask(world, Ferryman(world), hero, Id("the_bridge"));
        Tick(world);
        Assert.True(Quests.IsFinished(world, Rumor));

        Hours(world, 4.5);
        Tick(world);
        Assert.Equal("late", Quests.StageOf(world, Courier));
        Assert.True(Quests.IsFailed(world, Courier));

        Assert.Equal("guard", Quests.StageOf(world, Escort));
        world.Destroy(world.Resolve(PersistentId.FromName("pilgrim")));
        world.FlushCommands();
        Tick(world);
        Assert.True(Quests.IsFailed(world, Escort));
    }

    // Saved and loaded by this build: a failed quest stays failed, a timer keeps its deadline.
    [Fact]
    public void FailureAndATimerRoundTripThroughASave()
    {
        string root = TestEnv.NewTempDir();
        using var app = NewApp(root);
        var world = NewWorld(app);
        Hero(world);
        Assert.True(Quests.Start(world, Courier));
        Assert.True(Quests.Start(world, Gate));
        Assert.True(Quests.Fail(world, Gate));
        Hours(world, 1);
        Tick(world);
        Assert.True(app.Engine.Saves.Save("trip"));
        Hours(world, 3);
        Assert.True(app.Engine.Saves.Load("trip"));

        Assert.True(Quests.IsFailed(world, Gate));
        Assert.Equal(5.0, Quests.HoursLeft(world, Courier)!.Value, 1);
    }

    // Writes the golden quest save, only when asked: run with SAGE_WRITE_GOLDEN_QUEST_SAVE=<folder> and
    // commit what it writes there as tests/Sage.Tests/Content/Saves/quests.
    [Fact]
    public void WriteTheGoldenQuestSaveWhenAsked()
    {
        string? target = Environment.GetEnvironmentVariable("SAGE_WRITE_GOLDEN_QUEST_SAVE");
        if (string.IsNullOrEmpty(target)) return;
        using var app = NewApp(target);
        var world = NewWorld(app);
        app.CVars.Execute("save_autosave 0");

        var hero = Hero(world);
        Pilgrim(world);
        world.Give(hero, Letter);
        // What the courier's stage said when this save was made; the record has been reworded since.
        var run = app.Records.Get<QuestRecord>(Courier).Stages[0];
        string text = run.Text;
        run.Text = "The abbot wants this letter by dusk.";
        StartAll(world);
        run.Text = text;
        Hours(world, 2);
        Tick(world);

        Assert.True(app.Engine.Saves.Save("golden"));
    }
}
