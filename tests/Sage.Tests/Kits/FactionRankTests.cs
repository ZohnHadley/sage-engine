#nullable enable
using System.IO;
using System.Linq;
using System.Numerics;
using Sage.Kits.Rpg;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Faction ranks with promotion requirements (issue #389): the RPG kit's faction_ranks ladder, climbed one rung at a
// time when the rung's conditions hold (standing, a skill, no bounty with the town); the `rank` condition rank-gated
// content asks; `join_faction` and `promote`; the saved rpg:memberships; and the rpg:factions screen.
public class FactionRankTests
{
    public FactionRankTests() { _ = TestEnv.UserRoot; }

    private static RecordId Id(string name) => new("sage", name);

    private const string Kit = """
    [
      { "type": "attribute", "id": "blade", "start": 0, "min": 0, "max": 100 },
      { "type": "attribute", "id": "blade_xp", "start": 0, "min": 0 },
      { "type": "skill", "id": "blade", "rank": "blade", "experience": "blade_xp", "rate": 1, "growth": 0 },
      { "type": "faction", "id": "town", "label": "the town" },
      { "type": "faction", "id": "fighters", "label": "Fighters Guild" },
      { "type": "faction_ranks", "id": "fighters_ranks", "faction": "fighters",
        "ranks": [ { "name": "Associate", "requires": [ { "bounty": "town", "below": 1 } ] },
                   { "name": "Swordsman", "requires": [ { "standing": "fighters", "min": 10 }, { "skill": "blade", "atLeast": 2 } ] },
                   { "name": "Champion", "requires": [ { "standing": "fighters", "min": 50 } ] } ] },
      { "type": "dialogue", "id": "guildmaster",
        "nodes": [ { "id": "greet", "text": "Well?",
          "options": [
            { "text": "I want to join.", "end": true, "actions": [ { "join_faction": "fighters" } ] },
            { "text": "Promote me.", "end": true, "actions": [ { "promote": "fighters" } ] },
            { "text": "[Swordsman] The back room, please.", "end": true, "conditions": [ { "rank": "fighters", "atLeast": 1 } ] } ] } ] }
    ]
    """;

    private static HeadlessApp BootKit()
    {
        var app = HeadlessApp.Gameplay().With(new UiModule(), new RpgKitModule()).WithEngineContent()
            .File("data/rpg_screens.json", RpgScreenTests.Records).File("data/kit.json", Kit).Boot("ranks");
        Assert.True(app.Records.ErrorCount == 0, string.Join("\n", app.Records.LoadErrors));
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        return app;
    }

    private static Entity Hero(World world)
    {
        var hero = world.Create(Transform.At(Vector3.Zero), "hero");
        world.AddAttributes(hero);
        world.AddInventory(hero);
        world.Add(hero, new Persistent { Id = PersistentId.FromName("hero") });
        hero.AddTag<PlayerControlled>();
        world.FlushCommands();
        return hero;
    }

    // Joining is the first rung, asked like any other (no bounty with the town); each promotion asks its own
    // rung's conditions and says which failed; the `rank` condition opens the Swordsman's option.
    [Xunit.Fact]
    public void RanksAreClimbedOneRungAtATimeWhenTheirRequirementsHold()
    {
        using var app = BootKit();
        var world = app.World;
        var hero = Hero(world);
        var changed = new EventProbe<RankChanged>(world);
        var fighters = Id("fighters");

        Assert.Equal(-1, FactionRanks.RankOf(world, hero, fighters));
        Crime.SetBounty(world, Id("town"), 20f);
        Assert.False(FactionRanks.CanRise(world, hero, fighters, out string why));
        Assert.Equal("you owe them too much", why);
        Crime.SetBounty(world, Id("town"), 0f);
        Assert.True(FactionRanks.Rise(world, hero, fighters, out _));
        Assert.Equal(0, FactionRanks.RankOf(world, hero, fighters));
        Assert.Equal("Associate", FactionRanks.RankName(world, hero, fighters));

        Assert.False(FactionRanks.CanRise(world, hero, fighters, out why));
        Assert.Equal("they do not trust you", why);
        Factions.Change(world, fighters, 15f);
        Assert.False(FactionRanks.CanRise(world, hero, fighters, out why));
        Assert.Equal("your skill is too low", why);
        Skills.Practise(world, hero, Id("blade"), 2f);
        Assert.True(FactionRanks.Rise(world, hero, fighters, out _));
        Assert.Equal("Swordsman", FactionRanks.RankName(world, hero, fighters));
        Assert.Equal(new[] { 0, 1 }, changed.All.Select(c => c.Rank).ToArray());

        // Rank-gated content: the Swordsman's option.
        var guildmaster = world.Create(Transform.At(new Vector3(0, 0, -2)), "guildmaster");
        world.Add(guildmaster, new Dialogue { Record = Id("guildmaster") });
        Assert.True(DialogueRules.Start(world, guildmaster, hero));
        Assert.True(DialogueRules.CanPick(world, hero, DialogueRules.Current(world)!.Options[2], out _));
        FactionRanks.SetRank(world, hero, fighters, 0);
        Assert.False(DialogueRules.CanPick(world, hero, DialogueRules.Current(world)!.Options[2], out why));
        Assert.Equal("your rank is too low", why);
    }

    // A guildmaster's conversation joins and promotes; the ranks held are saved with the entity.
    [Xunit.Fact]
    public void JoiningAndPromotionComeFromDataAndAreSaved()
    {
        using var app = BootKit();
        var world = app.World;
        var hero = Hero(world);
        var fighters = Id("fighters");
        var guildmaster = world.Create(Transform.At(new Vector3(0, 0, -2)), "guildmaster");
        world.Add(guildmaster, new Dialogue { Record = Id("guildmaster") });

        Assert.True(DialogueRules.Start(world, guildmaster, hero));
        Assert.True(DialogueRules.Pick(world, DialogueRules.Current(world)!.Options[0]));
        Assert.Equal(0, FactionRanks.RankOf(world, hero, fighters));

        Factions.Change(world, fighters, 60f);
        Skills.Practise(world, hero, Id("blade"), 3f);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(DialogueRules.Start(world, guildmaster, hero));
            DialogueRules.Pick(world, DialogueRules.Current(world)!.Options[1]);
        }
        Assert.Equal("Champion", FactionRanks.RankName(world, hero, fighters));

        Assert.True(app.Engine.Saves.Save("ranks"));
        FactionRanks.SetRank(world, hero, fighters, -1);
        Assert.False(FactionRanks.IsMember(world, hero, fighters));
        Assert.True(app.Engine.Saves.Load("ranks"));
        var loaded = world.Resolve(PersistentId.FromName("hero"));
        Assert.Equal(2, FactionRanks.RankOf(world, loaded, fighters));
    }

    // The rpg:factions screen: a row per faction with ranks — rank, standing, bounty — greyed with what the next
    // rank asks; confirming on it joins.
    [Xunit.Fact]
    public void TheFactionsScreenListsRanksAndJoins()
    {
        using var app = BootKit();
        var world = app.World;
        var hero = Hero(world);
        var stack = world.Resources.Get<UiScreenStack>();
        stack.SetViewport(new Vector2(1280f, 720f));
        stack.OpenSeconds = stack.CloseSeconds = 0f;
        Crime.SetBounty(world, Id("fighters"), 3f);
        var layer = stack.Open(RpgKitModule.FactionsScreen, new UiBindContext(world, hero));
        stack.Update(UiInput.Wait(0f));
        var view = Assert.IsType<FactionsView>(layer.Screen!.ViewModel);

        var row = Assert.Single(view.Rows);
        Assert.Equal(Id("fighters"), row.Id);
        Assert.Equal("Fighters Guild", row.Text);
        Assert.Equal("not a member, standing 0, bounty 3", row.Row.Detail);
        Assert.True(row.Row.Enabled);

        var widget = All(layer.Content).First(w => ReferenceEquals(UiScreen.RowOf(w), row));
        Assert.True(view.Activate(widget, new UiBindContext(world, hero)));
        Assert.Equal(0, FactionRanks.RankOf(world, hero, Id("fighters")));
        row = Assert.Single(view.Rows);
        Assert.True(row.Row.Selected);
        Assert.False(row.Row.Enabled);
        Assert.Equal("Swordsman: they do not trust you", row.Row.Reason);
    }

    private static System.Collections.Generic.IEnumerable<Widget> All(Widget root)
    {
        yield return root;
        for (int i = 0; i < root.ChildCount; i++)
            foreach (var w in All(root.Child(i))) yield return w;
    }
}
