#nullable enable
using System;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Something to do, and something that notices you did it (docs/design/16 §3.5, TODO F24).
//
// A quest watches; it never moves anybody. So these tests do the *game's* half — kill the things, carry
// the thing, say the line — and ask the journal what it made of that.
public class QuestTests
{
    public QuestTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
        [{ "type": "gameplay_conventions", "id": "default_conventions", "health": "health", "dead": "state.dead", "damageType": "physical", "playerFaction": "player" },
         { "type": "faction", "id": "player" },
         { "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
         { "type": "tag", "id": "state.dead" },
         { "type": "effect", "id": "damage", "duration": "Instant",
           "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
         { "type": "damage_type", "id": "physical", "effect": "damage" },
         { "type": "item", "id": "pelt", "label": "wolf pelt", "weight": 0.5 },
         { "type": "item", "id": "coin", "label": "gold coin", "weight": 0.01 },

         { "type": "faction", "id": "wolves", "label": "wolves", "standing": -50 },

         { "type": "quest", "id": "cull", "label": "Thin the Pack",
           "start": "hunt",
           "stages": [
             { "id": "hunt", "text": "Kill two wolves.",
               "objectives": [ { "kind": "Kill", "faction": "sage:wolves", "count": 2 } ],
               "next": "report" },
             { "id": "report", "text": "Tell the hermit it is done." } ] },

         { "type": "quest", "id": "pelts", "label": "Two Pelts",
           "stages": [
             { "id": "gather", "text": "Bring two pelts.",
               "objectives": [ { "kind": "Have", "item": "sage:pelt", "count": 2 } ],
               "done": true } ] },

         { "type": "dialogue", "id": "hermit",
           "nodes": [
             { "id": "greet", "text": "Well?",
               "options": [
                 { "text": "I will thin the pack.", "goto": "greet",
                   "requires": { "quest": "sage:cull", "notStarted": true },
                   "then": { "startQuest": "sage:cull" } },
                 { "text": "It is done.", "end": true,
                   "requires": { "quest": "sage:cull", "stage": "report" },
                   "refusal": "there is nothing to say about that",
                   "then": { "finishQuest": "sage:cull", "giveItem": "sage:coin", "giveCount": 10 } } ] } ] }]
        """;

    private static RecordId Id(string name) => new("sage", name);

    private static (Engine, World) NewWorld()
    {
        var engine = HeadlessApp.Gameplay().File("data/quests.json", Records).Build().Engine;
        return (engine, engine.CreateWorld("quests"));
    }

    private static Entity Player(World world)
    {
        var entity = world.Create(Transform.At(Vector3.Zero), "player");
        world.AddAttributes(entity);
        world.Add(entity, new Inventory { Capacity = 50f });
        entity.AddTag<PlayerControlled>();
        return entity;
    }

    private static Entity Wolf(World world)
    {
        var entity = world.Create(Transform.At(new Vector3(0, 0, -3)), "wolf");
        world.AddAttributes(entity);
        world.Add(entity, new Faction { Id = Id("wolves") });
        return entity;
    }

    private static void Kill(World world, Entity victim, Entity killer) =>
        Combat.ApplyDamage(world, new DamageInfo(killer, victim, Id("physical"), 500f, Vector3.Zero, Vector3.UnitZ));

    private static void Tick(World world, int ticks)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    // Kills are counted, and the stage moves on by itself the moment the last one lands.
    [Fact]
    public void KillingWhatAQuestAskedForMovesItAlong()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var player = Player(world);
            Assert.True(Quests.Start(world, Id("cull")));
            Assert.Equal("hunt", Quests.StageOf(world, Id("cull")));

            Kill(world, Wolf(world), player);
            Tick(world, 2);
            Assert.Equal(1, Quests.Progress(world, player, Id("cull"), 0));
            Assert.Equal("hunt", Quests.StageOf(world, Id("cull")));

            Kill(world, Wolf(world), player);
            Tick(world, 2);
            Assert.Equal("report", Quests.StageOf(world, Id("cull")));
            Assert.True(Quests.IsActive(world, Id("cull")));   // the last stage still has to be told
        }
    }

    // A kill somebody else made is not yours, and neither is one of something the quest never mentioned.
    [Fact]
    public void OnlyTheKillsTheQuestAskedForCount()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var player = Player(world);
            var wolf = Wolf(world);
            Quests.Start(world, Id("cull"));

            Kill(world, wolf, Wolf(world));                 // a wolf killed by a wolf
            Tick(world, 2);
            Assert.Equal(0, Quests.Progress(world, player, Id("cull"), 0));

            var bystander = world.Create(Transform.At(Vector3.Zero), "bystander");
            world.AddAttributes(bystander);
            Kill(world, bystander, player);                 // and something with no faction at all
            Tick(world, 2);
            Assert.Equal(0, Quests.Progress(world, player, Id("cull"), 0));
        }
    }

    // "Bring me two pelts" is met by carrying them, and unmet again the moment they are handed over — so
    // it is asked rather than counted.
    [Fact]
    public void CarryingWhatWasAskedForFinishesTheStage()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var player = Player(world);
            Quests.Start(world, Id("pelts"));
            Assert.True(Quests.IsActive(world, Id("pelts")));

            world.Give(player, Id("pelt"), 1);
            Quests.Check(world);
            Assert.Equal(1, Quests.Progress(world, player, Id("pelts"), 0));
            Assert.True(Quests.IsActive(world, Id("pelts")));

            world.Give(player, Id("pelt"), 1);
            Quests.Check(world);
            Assert.True(Quests.IsFinished(world, Id("pelts")));
        }
    }

    // A quest whose objectives are already met when it starts does not sit there waiting to be nudged.
    [Fact]
    public void AQuestYouHaveAlreadyDoneFinishesWhenItStarts()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var player = Player(world);
            world.Give(player, Id("pelt"), 2);

            Quests.Start(world, Id("pelts"));
            Assert.True(Quests.IsFinished(world, Id("pelts")));
        }
    }

    // The whole loop, through a conversation: ask for it, do it, say it is done, get paid.
    [Fact]
    public void AConversationGivesAQuestAndTakesTheReport()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var player = Player(world);
            var hermit = world.Create(Transform.At(new Vector3(0, 0, -2)), "hermit");
            world.Add(hermit, new Dialogue { Record = Id("hermit") });

            // Asking for it.
            Assert.True(DialogueRules.Start(world, hermit, player));
            var node = DialogueRules.Current(world)!;
            Assert.True(DialogueRules.CanPick(world, player, node.Options[0], out _));
            Assert.False(DialogueRules.CanPick(world, player, node.Options[1], out string why));
            Assert.Equal("there is nothing to say about that", why);

            DialogueRules.Pick(world, node.Options[0]);
            Assert.True(Quests.IsActive(world, Id("cull")));

            // Reporting before it is done is still refused.
            Assert.False(DialogueRules.CanPick(world, player, DialogueRules.Current(world)!.Options[1], out _));

            Kill(world, Wolf(world), player);
            Kill(world, Wolf(world), player);
            Tick(world, 2);

            // And now it is the only thing left to say.
            var report = DialogueRules.Current(world)!.Options[1];
            Assert.True(DialogueRules.CanPick(world, player, report, out _));
            DialogueRules.Pick(world, report);

            Assert.Equal(10, world.CountOf(player, Id("coin")));
            Assert.False(world.Resources.Get<Conversation>().Running);
        }
    }

    // The journal is a list with the counting shown, because "how many have I killed" is the question a
    // player actually has.
    [Fact]
    public void TheJournalShowsWhereYouAreUpTo()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var player = Player(world);
            Quests.Start(world, Id("cull"));
            Kill(world, Wolf(world), player);
            Tick(world, 2);

            var screen = new JournalScreen();
            screen.Build(world, player);

            Assert.Equal("Journal", screen.Panel.Title);
            Assert.Equal("Thin the Pack", screen.Panel[0].Name);
            Assert.Contains(screen.Panel.Rows, r => r.Name.Contains("Kill two wolves"));
            Assert.Contains(screen.Panel.Rows, r => r.Detail == "1/2");
        }
    }

    // An empty journal says so rather than showing nothing at all.
    [Fact]
    public void AnEmptyJournalSaysSo()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var screen = new JournalScreen();
            screen.Build(world, Player(world));
            Assert.Equal(1, screen.Panel.Count);
            Assert.False(screen.Panel[0].Enabled);
        }
    }

}
