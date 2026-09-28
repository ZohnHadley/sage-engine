#nullable enable
using System;
using System.Numerics;
using Friflo.Engine.ECS;
using sage_engine;

namespace sage_engine.Tests;

using Assert = Xunit.Assert;

// Talking to somebody (docs/design/16 §3.5, TODO F24).
//
// A conversation is data, and everything it *does* is machinery that already existed: giving, taking,
// tagging, reputation. So these tests are about the flow and the gating — and the one thing a screen
// must never get wrong, which is offering a line the rules would then refuse (R17).
public class DialogueTests
{
    public DialogueTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
        [{ "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
         { "type": "tag", "id": "state.dead" },
         { "type": "tag", "id": "quest.errand" },
         { "type": "effect", "id": "blessing", "duration": "Infinite", "grantTags": ["quest.errand"], "modifiers": [] },
         { "type": "item", "id": "coin", "label": "gold coin", "weight": 0.01 },
         { "type": "item", "id": "relic", "label": "old relic", "weight": 1 },

         { "type": "faction", "id": "townsfolk", "label": "townsfolk", "standing": 0 },

         { "type": "dialogue", "id": "innkeeper", "label": "the innkeeper",
           "start": "greet",
           "nodes": [
             { "id": "greet", "text": "What'll it be?",
               "options": [
                 { "text": "Tell me about the relic.", "goto": "relic" },
                 { "text": "Here, take this relic.", "goto": "thanks",
                   "requires": { "item": "sage:relic" },
                   "refusal": "you have no relic",
                   "then": { "takeItem": "sage:relic", "giveItem": "sage:coin", "giveCount": 5,
                             "faction": "sage:townsfolk", "standing": 30 } },
                 { "text": "Nothing.", "end": true } ] },
             { "id": "relic", "text": "Old thing. Cursed, they say.",
               "options": [ { "text": "I see.", "goto": "greet" } ] },
             { "id": "thanks", "text": "Much obliged.",
               "options": [ { "text": "Goodbye.", "end": true } ] } ] },

         { "type": "dialogue", "id": "guard", "label": "a guard",
           "nodes": [
             { "id": "halt", "text": "Move along.",
               "options": [
                 { "text": "Let me through.", "goto": "pass",
                   "requires": { "faction": "sage:townsfolk", "minStanding": 25 },
                   "refusal": "they do not trust you" },
                 { "text": "As you say.", "end": true } ] },
             { "id": "pass", "text": "Go on, then.", "options": [] } ] }]
        """;

    private static RecordId Id(string name) => new("sage", name);

    private static (Engine, World) NewWorld()
    {
        var engine = HeadlessApp.Gameplay().File("data/dialogue.json", Records).Build().Engine;
        return (engine, engine.CreateWorld("dialogue"));
    }

    private static Entity Speaker(World world, string dialogue, string name = "innkeeper")
    {
        var entity = world.Create(Transform.At(new Vector3(0, 0, -2)), name);
        world.Add(entity, new Dialogue { Record = Id(dialogue) });
        return entity;
    }

    private static Entity Player(World world)
    {
        var entity = world.Create(Transform.At(Vector3.Zero), "player");
        world.AddAttributes(entity);
        world.Add(entity, new Inventory { Capacity = 50f });
        entity.AddTag<PlayerControlled>();
        return entity;
    }

    // The flow: a line, the things you may say, and where each one leads.
    [Fact]
    public void AConversationWalksItsNodes()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var innkeeper = Speaker(world, "innkeeper");
            var player = Player(world);

            Assert.True(DialogueRules.Start(world, innkeeper, player));
            var node = DialogueRules.Current(world)!;
            Assert.Equal("What'll it be?", node.Text);
            Assert.Equal(3, node.Options.Count);

            DialogueRules.Pick(world, node.Options[0]);            // "tell me about the relic"
            Assert.Equal("Old thing. Cursed, they say.", DialogueRules.Current(world)!.Text);

            DialogueRules.Pick(world, DialogueRules.Current(world)!.Options[0]);   // back to the start
            Assert.Equal("What'll it be?", DialogueRules.Current(world)!.Text);

            DialogueRules.Pick(world, DialogueRules.Current(world)!.Options[2]);   // "nothing"
            Assert.Null(DialogueRules.Current(world));
            Assert.False(world.Resources.Get<Conversation>().Running);
        }
    }

    // A line you cannot say is offered greyed out with the reason, and saying it anyway changes nothing.
    // The screen and the rules ask the same question, which is the point of `CanPick` (R17).
    [Fact]
    public void ALineYouCannotSayIsRefusedWithAReason()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var innkeeper = Speaker(world, "innkeeper");
            var player = Player(world);
            DialogueRules.Start(world, innkeeper, player);

            var give = DialogueRules.Current(world)!.Options[1];
            Assert.False(DialogueRules.CanPick(world, player, give, out string why));
            Assert.Equal("you have no relic", why);

            Assert.False(DialogueRules.Pick(world, give));
            Assert.Equal("What'll it be?", DialogueRules.Current(world)!.Text);   // nothing moved

            // With the relic in hand it is sayable, and saying it does what it says.
            world.Give(player, Id("relic"), 1);
            Assert.True(DialogueRules.CanPick(world, player, give, out _));
            Assert.True(DialogueRules.Pick(world, give));

            Assert.Equal("Much obliged.", DialogueRules.Current(world)!.Text);
            Assert.Equal(0, world.CountOf(player, Id("relic")));
            Assert.Equal(5, world.CountOf(player, Id("coin")));
            Assert.Equal(30f, Factions.StandingWith(world, Id("townsfolk")), 1);
        }
    }

    // Reputation gates a line, which is the whole reason factions came first: what you may *say* depends
    // on what they think of you.
    [Fact]
    public void WhatYouMaySayDependsOnWhatTheyThinkOfYou()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var guard = Speaker(world, "guard", "guard");
            var player = Player(world);
            DialogueRules.Start(world, guard, player);

            var through = DialogueRules.Current(world)!.Options[0];
            Assert.False(DialogueRules.CanPick(world, player, through, out string why));
            Assert.Equal("they do not trust you", why);

            Factions.Change(world, Id("townsfolk"), 40f);
            Assert.True(DialogueRules.CanPick(world, player, through, out _));
            Assert.True(DialogueRules.Pick(world, through));
            Assert.Equal("Go on, then.", DialogueRules.Current(world)!.Text);
        }
    }

    // A node with nothing to say is a dead end, not a trap: the screen offers a way out and the
    // conversation ends.
    [Fact]
    public void ANodeWithNoOptionsCanStillBeLeft()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var guard = Speaker(world, "guard", "guard");
            var player = Player(world);
            Factions.Change(world, Id("townsfolk"), 40f);
            DialogueRules.Start(world, guard, player);
            DialogueRules.Pick(world, DialogueRules.Current(world)!.Options[0]);

            var screen = new DialogueScreen();
            screen.Build(world, player);
            Assert.Equal("Go on, then.", screen.Panel.Title);
            Assert.Equal(1, screen.Panel.Count);          // the way out

            screen.Activate(world, player, screen.Panel[0]);
            Assert.False(world.Resources.Get<Conversation>().Running);
        }
    }

    // The screen shows exactly what the rules allow: every row that is greyed is one `Pick` would
    // refuse, and every row that is not is one it would take.
    [Fact]
    public void TheScreenAndTheRulesAgreeAboutEveryRow()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var innkeeper = Speaker(world, "innkeeper");
            var player = Player(world);
            DialogueRules.Start(world, innkeeper, player);

            var screen = new DialogueScreen();
            screen.Build(world, player);

            var node = DialogueRules.Current(world)!;
            Assert.Equal(node.Options.Count, screen.Panel.Count);
            for (int i = 0; i < node.Options.Count; i++)
            {
                bool allowed = DialogueRules.CanPick(world, player, node.Options[i], out string why);
                Assert.Equal(allowed, screen.Panel[i].Enabled);
                Assert.Equal(node.Options[i].Text, screen.Panel[i].Name);
                if (!allowed) Assert.Equal(why, screen.Panel[i].Reason);
            }
        }
    }

    // Somebody with nothing to say is not a conversation, and asking is not a crash.
    [Fact]
    public void TalkingToSomethingWithNothingToSayDoesNothing()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var rock = world.Create(Transform.At(Vector3.Zero), "rock");
            var player = Player(world);

            Assert.False(DialogueRules.Start(world, rock, player));
            Assert.False(world.Resources.Get<Conversation>().Running);
            Assert.Null(DialogueRules.Current(world));
        }
    }
}
