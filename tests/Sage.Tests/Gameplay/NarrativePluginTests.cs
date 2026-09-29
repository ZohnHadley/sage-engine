#nullable enable
using System.IO;
using System.Linq;
using System.Numerics;
using Friflo.Engine.ECS;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Deaths as an event, and factions, quests and dialogue as plugins nothing calls into (issue #26).
//
// The effect system used to call Factions.OnKilled and Quests.OnKilled itself, and FactionsModule
// carried dialogue and quests with it, so switching factions off left combat calling into a plugin
// that was not there. Now a death is a `Died` event that each of them reads, and each is its own
// plugin a game can switch off in game.json.
public class NarrativePluginTests
{
    public NarrativePluginTests() { _ = TestEnv.UserRoot; }

    private const string Content = """
    [
      { "type": "faction", "id": "wolves", "standing": -50, "killCost": 10 },
      {
        "type": "quest", "id": "cull",
        "stages": [ { "id": "hunt", "objectives": [ { "kind": "Kill", "faction": "wolves", "count": 1 } ] } ]
      },
      {
        "type": "dialogue", "id": "elder",
        "nodes": [ { "id": "hello", "text": "The wolves again.", "options": [
          { "text": "I will deal with them.", "then": { "startQuest": "cull", "faction": "wolves", "standing": 5 }, "end": true }
        ] } ]
      }
    ]
    """;

    private sealed class RecordingRules : GameRules
    {
        public int Deaths;
        public float StandingWhenTold = float.NaN;
        public bool QuestDoneWhenTold;

        public override void OnEntityDied(World world, Entity victim, Entity killer)
        {
            Deaths++;
            // The rules run after the plugins that read `Died`: the kill is already counted.
            StandingWhenTold = Factions.StandingWith(world, new RecordId("narr", "wolves"));
            QuestDoneWhenTold = Quests.IsFinished(world, new RecordId("narr", "cull"));
        }
    }

    private sealed class RulesModule : IModule
    {
        public readonly RecordingRules Rules = new();
        public void Init(ModuleContext ctx) { }
        public void OnWorldCreated(World world) => world.Resources.Replace<GameRules>(Rules);
    }

    // A game on the engine's content with `disable` in its game.json, the way a designer switches a
    // plugin off; null switches nothing off.
    private static (HeadlessApp App, RecordingRules Rules) NewGame(string? disable)
    {
        string dir = TestEnv.NewTempDir();
        string disabled = disable == null ? "" : $"\"{disable}\"";
        File.WriteAllText(Path.Combine(dir, "game.json"),
            $$"""{ "name": "Narrative", "id": "narr", "mounts": ["content"], "modules": { "disable": [{{disabled}}], "add": [] } }""");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(dir, "content", "data")).FullName, "narr.json"), Content);

        var rules = new RulesModule();
        var app = HeadlessApp.ForGame(dir).WithEngineContent().With(rules).Boot("narr");
        return (app, rules.Rules);
    }

    private static RecordId Id(string name) => new("narr", name);

    [Xunit.Theory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("sage.gameplay.factions")]
    [Xunit.InlineData("sage.gameplay.quests")]
    [Xunit.InlineData("sage.gameplay.dialogue")]
    public void EachNarrativePluginCanBeSwitchedOffAlone(string? disabled)
    {
        var (app, rules) = NewGame(disabled);
        using (app)
        {
            bool factions = disabled != "sage.gameplay.factions";
            bool quests = disabled != "sage.gameplay.quests";
            bool dialogue = disabled != "sage.gameplay.dialogue";
            Assert.Equal(factions, app.PluginIds.Contains("sage.gameplay.factions"));
            Assert.Equal(quests, app.PluginIds.Contains("sage.gameplay.quests"));
            Assert.Equal(dialogue, app.PluginIds.Contains("sage.gameplay.dialogue"));
            // Records of a switched-off plugin's types are skipped with a warning, not errors.
            Assert.Equal(0, app.Records.ErrorCount);

            var world = app.World;
            var died = new EventProbe<Died>(world);
            var space = world.Resources.Get<PhysicsSpace>();

            var ground = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
            world.Add(ground, Collider.Box(new Vector3(100, 1, 100)));
            var player = world.Create(Transform.At(Vector3.Zero), "player");
            world.AddCharacter(player, space.Layers.Player);
            world.AddAttributes(player);
            player.AddTag<PlayerControlled>();

            var elder = world.Create(Transform.At(new Vector3(3, 0, 0)), "elder");
            world.Add(elder, new Dialogue { Record = Id("elder") });

            // A creature that thinks: it runs the engine's schedules and sizes the player up, which asks
            // factions who is an enemy whether or not the plugin is there.
            var wolf = world.Create(Transform.At(new Vector3(0, 0, -4)), "wolf");
            world.AddCharacter(wolf, space.Layers.Enemy);
            world.AddAttributes(wolf);
            world.Add(wolf, new Faction { Id = Id("wolves") });
            world.Add(wolf, new AIState());
            for (int i = 0; i < 30; i++) world.RunFixed(1f / 60f);

            // Talking: only with dialogue; what it does to a quest or a name, only with those.
            Assert.Equal(dialogue, DialogueRules.Start(world, elder, player));
            if (dialogue)
            {
                Assert.True(DialogueRules.Pick(world, DialogueRules.Current(world)!.Options[0]));
                Assert.Equal(quests, Quests.IsActive(world, Id("cull")));
            }
            else if (quests)
            {
                Assert.True(Quests.Start(world, Id("cull")));
            }
            Assert.Equal(quests, Quests.JournalOf(world) != null);
            Assert.Equal(factions && dialogue ? -45f : factions ? -50f : 0f, Factions.StandingWith(world, Id("wolves")));

            // Killing: the effect system says so, and whoever is installed hears it.
            Combat.ApplyDamage(world, new DamageInfo(player, wolf, default, 500f, Vector3.Zero, Vector3.UnitZ));
            world.RunFixed(1f / 60f);

            Assert.True(world.HasTag(wolf, world.Conventions().Dead));
            var death = Assert.Single(died.All);
            Assert.Equal(wolf, death.Victim);
            Assert.Equal(player, death.Killer);
            Assert.Equal(1, rules.Deaths);

            float standing = factions ? (dialogue ? -45f : -50f) - 10f : 0f;
            Assert.Equal(standing, Factions.StandingWith(world, Id("wolves")), 3);
            Assert.Equal(standing, rules.StandingWhenTold, 3);
            Assert.Equal(quests, Quests.IsFinished(world, Id("cull")));
            Assert.Equal(quests, rules.QuestDoneWhenTold);

            for (int i = 0; i < 30; i++) world.RunFixed(1f / 60f);   // and the world carries on
        }
    }

    // The effect system names none of the plugins that react to a death: they read the event.
    [Fact]
    public void TheEffectSystemCallsNobodyWhenSomethingDies()
    {
        string effects = File.ReadAllText(Path.Combine(TestEnv.FolderAbove("engine_content"), "src", "Sage.Gameplay", "Attributes", "Effects.cs"));
        Assert.DoesNotContain("Factions.", effects);
        Assert.DoesNotContain("Quests.", effects);
        Assert.DoesNotContain("OnEntityDied(", effects[..effects.IndexOf("class DeathRulesSystem", System.StringComparison.Ordinal)]);
    }
}
