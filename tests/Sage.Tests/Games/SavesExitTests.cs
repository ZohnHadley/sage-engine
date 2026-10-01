#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Phase 4i's exit criterion (issue #167, docs/design/09 "As built (the 4i exit game, issue 4i-7)"): save
// anywhere, change a prefab and a record, load — nothing lost and nothing duplicated. tests/games/saves has
// no C#: placed goblins, a `.map` with a ring and a tally, a player with a sword, a bow and bread. The
// player's input is a PlayerCommand, as the client's is, and the save and the load are F5 and F9 (the
// QuickSave and QuickLoad actions). The load is in a fresh app with `rebalance/` mounted over the game: the
// goblin prefab at 60 health instead of 30, and a fourth goblin placed in the scene.
#pragma warning disable SAGE0127   // projectiles are phase 4e's
#pragma warning disable SAGE0131   // saves you can trust (phase 4i)
public class SavesExitTests
{
    public SavesExitTests() { _ = TestEnv.UserRoot; }

    private static readonly RecordId Sword = new("saves", "sword");
    private static readonly RecordId Bow = new("saves", "bow");
    private static readonly RecordId Arrow = new("saves", "arrow");
    private static readonly RecordId Bread = new("saves", "bread");
    private static readonly RecordId Ring = new("saves", "ring");
    private static readonly RecordId Health = new("sage", "health");

    private static string GameDirectory => Path.Combine(TestEnv.FolderAbove("tests"), "tests", "games", "saves");
    private static string RebalanceDirectory => Path.Combine(GameDirectory, "rebalance");

    // The game, with the rebalance patch mounted after it (in its own namespace, as a mod is) when asked.
    private static HeadlessApp Boot(string saves, bool rebalanced)
    {
        var builder = HeadlessApp.ForGame(GameDirectory).WithEngineContent();
        if (rebalanced)
        {
            var patch = new MountFixture();
            foreach (var file in Directory.GetFiles(RebalanceDirectory, "*", SearchOption.AllDirectories))
                patch.Write("rebalance", Path.GetRelativePath(RebalanceDirectory, file).Replace('\\', '/'), File.ReadAllText(file));
            patch.Mount("rebalance", "rebalance");
            builder.Mount(patch);
        }
        var app = builder.Boot();
        Assert.Equal(0, app.Records.ErrorCount);
        Assert.Null(app.Engine.Modules.Game);                    // no C#
        app.Engine.Saves.Root = saves;
        Step(app.World, 3);                                      // settled, the level's entities in
        return app;
    }

    private static void Step(World world, int ticks = 1) => NpcLocomotionTests.Step(world, ticks);

    private static Entity Player(World world) =>
        Assert.Single(world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList());

    private static Entity[] Named(World world, string name) =>
        world.QueryAll().Entities.ToEntityList().Where(e => e.Name == name).ToArray();

    private static Entity One(World world, string name) => Assert.Single(Named(world, name));

    private static float HealthOf(World world, string name) => world.Attribute(One(world, name), Health);

    private static bool IsDead(World world, Entity entity) => world.HasTag(entity, world.Conventions().Dead);

    private static List<PersistentId> Ids(World world) =>
        world.Query<Persistent>().Entities.ToEntityList().Select(e => e.GetComponent<Persistent>().Id).ToList();

    private static Entity[] Pickups(World world, RecordId item) =>
        world.Query<Pickup>().Entities.ToEntityList().Where(e => world.Get<Pickup>(e).Item == item).ToArray();

    private static Entity[] Arrows(World world) => world.Query<Projectile>().Entities.ToEntityList().ToArray();

    private static Vector3 Eye(World world, Entity player)
    {
        var character = world.Get<CharacterController>(player);
        var profile = CharacterConventions.Of(world).ProfileOf(world.Resources.Get<RecordStore>(), character.Profile);
        return CharacterController.EyeOf(world.Get<Transform>(player).LocalPosition, in character, profile);
    }

    // Presses `action` for one tick looking at `at`, then waits `ticks` looking at it still.
    private static void Press(HeadlessApp app, Entity player, string? action, Vector3 at, int ticks)
    {
        var world = app.World;
        var input = world.Resources.Get<PlayerInput>();
        var look = at - Eye(world, player);
        float yaw = SageMath.YawOf(look), pitch = SageMath.PitchOf(look);
        input.HasCommand = true;
        input.Command = new PlayerCommand
        {
            Tick = input.Command.Tick + 1, ViewYaw = yaw, ViewPitch = pitch,
            Pressed = action == null ? default : default(ActionMask).With(app.Engine.Actions.Get(action)),
        };
        Step(world);
        for (int i = 1; i < ticks; i++)
        {
            input.Command = new PlayerCommand { Tick = input.Command.Tick + 1, ViewYaw = yaw, ViewPitch = pitch };
            Step(world);
        }
        // Let go: a command left as it is is read again by the next tick, the press with it.
        input.Command = input.Command with { Pressed = default };
    }

    // The player stands `distance` metres south of `target`, facing it.
    private static void WalkUpTo(World world, Entity player, Vector3 target, float distance)
    {
        world.Teleport(player, Transform.At(new Vector3(target.X, 0.1f, target.Z + distance)));
        Step(world, 10);                                         // on its feet again
    }

    private static void Wield(World world, Entity player, RecordId item)
    {
        Assert.True(world.Equip(player, item));
        Step(world, 2);
    }

    // One swing at a goblin, at its chest, once the player has turned to face it.
    private static void Swing(HeadlessApp app, Entity player, Entity goblin)
    {
        var chest = app.World.Get<Transform>(goblin).LocalPosition with { Y = 1.1f };
        Press(app, player, null, chest, 2);
        Press(app, player, "Attack", chest, 70);   // windup, recovery and cooldown: 0.9 s
    }

    [Xunit.Fact]
    public void TheSavesGameValidates()
    {
        foreach (var mounts in new[] { Array.Empty<(string, string)>(), new[] { (RebalanceDirectory, "rebalance") } })
        {
            var report = ContentValidation.Run(new ValidateOptions
            {
                GameDirectory = GameDirectory,
                EngineContentDirectory = Path.Combine(TestEnv.FolderAbove("engine_content"), "engine_content"),
                AvailablePlugins = BasePlugins.All(),
                Mounts = mounts,
            });
            Assert.True(report.Ok, string.Join("\n", report.Errors));
        }
    }

    // Acceptance: a scripted run — a goblin killed, one hurt, one left alone, the map's ring taken (which
    // bumps the tally, as the kill does), bread dropped and an arrow loosed into the sky — then F5. A fresh
    // app with the rebalance patch, then F9. Nothing is doubled and nothing is lost: the dead goblin is still
    // dead and the hurt one keeps its 18, the untouched one has the patch's 60, the new goblin is there, the
    // ring is not back on the floor, the bread and the arrow (still in flight) are, and the tally is at 2.
    [Xunit.Fact]
    public void SavesExit_AQuickSaveSurvivesARebalanceWithNothingLostOrDoubled()
    {
        string saves = TestEnv.NewTempDir();
        List<PersistentId> before;
        Vector3 breadAt, arrowAt;
        PersistentId arrowId;
        float spentArrows;
        using (var first = Boot(saves, rebalanced: false))
        {
            var world = first.World;
            var player = Player(world);
            var doomed = One(world, "doomed goblin");
            var wounded = One(world, "wounded goblin");
            Assert.Equal(30f, world.Attribute(doomed, Health));
            Assert.Equal(30f, HealthOf(world, "spared goblin"));
            Assert.Empty(Named(world, "newcomer goblin"));
            var tally = One(world, "tally");
            Assert.Equal(0f, world.Get<LogicCounter>(tally).Value);

            // A goblin killed: three swings of 12 against its 30. Its death bumps the tally.
            var damaged = new EventProbe<Damaged>(world);
            Wield(world, player, Sword);
            WalkUpTo(world, player, world.Get<Transform>(doomed).LocalPosition, 1.6f);
            for (int swing = 0; swing < 6 && !IsDead(world, doomed); swing++) Swing(first, player, doomed);
            Assert.True(IsDead(world, doomed));
            Assert.Equal(0f, world.Attribute(doomed, Health));
            Assert.Equal(new[] { 12f, 12f, 6f }, damaged.All.Where(d => d.Hit.Target == doomed).Select(d => d.Applied));

            // One hurt: a single swing, 18 left.
            WalkUpTo(world, player, world.Get<Transform>(wounded).LocalPosition, 1.6f);
            Swing(first, player, wounded);
            Assert.Equal(18f, world.Attribute(wounded, Health));
            Assert.Equal(12f, Assert.Single(damaged.All, d => d.Hit.Target == wounded).Applied);
            Assert.False(IsDead(world, wounded));

            // The map's ring, taken with Use: in the pack, off the floor, and the tally bumped by its wire.
            var ring = One(world, "ring");
            var ringAt = world.Get<Transform>(ring).LocalPosition;
            Assert.Equal(new Vector3(6, 0, 4), ringAt);
            WalkUpTo(world, player, ringAt, 1.2f);
            Press(first, player, "Use", ringAt, 5);
            Assert.Equal(1, world.CountOf(player, Ring));
            Assert.Empty(Named(world, "ring"));
            Step(world, 2);
            Assert.Equal(2f, world.Get<LogicCounter>(One(world, "tally")).Value);

            // Bread dropped: a pickup the game made, at the player's feet.
            var bread = world.Drop(player, Bread);
            Assert.False(bread.IsNull);
            Step(world, 2);
            breadAt = world.Get<Transform>(bread).LocalPosition;
            Assert.Equal(1, world.CountOf(player, Bread));

            // An arrow loosed south into the open sky, nothing in its way for twenty seconds.
            Wield(world, player, Bow);
            var sky = world.Get<Transform>(player).LocalPosition + new Vector3(0, 20, 40);
            Press(first, player, "Attack", sky, 20);
            var arrow = Assert.Single(Arrows(world));
            Assert.Equal(player, world.Get<Projectile>(arrow).Caster);
            spentArrows = 10 - world.CountOf(player, Arrow);
            Assert.Equal(1f, spentArrows);

            // F5: a quick-save, at the end of the tick that asked for it.
            Press(first, player, SaveSystem.QuickSaveAction, sky, 1);
            Assert.Equal(SaveKind.Quick, Assert.Single(first.Engine.Saves.Slots).Kind);
            arrowAt = world.Get<Transform>(arrow).LocalPosition;
            arrowId = arrow.GetComponent<Persistent>().Id;
            Assert.True(arrowAt.Y > 2f && arrowAt.Z > world.Get<Transform>(player).LocalPosition.Z + 2f, $"the arrow is in the air: {arrowAt}");

            before = Ids(world);
            Assert.Equal(before.Count, before.Distinct().Count());

            // What the save holds of the goblins: the untouched one as "nothing changed" (no attributes),
            // the hurt one's health; the ring as a tombstone of the map it came from.
            var file = JsonNode.Parse(File.ReadAllText(Path.Combine(saves, SaveSystem.QuickSlot, "world_main.json")))!;
            JsonObject Saved(string name) => file["entities"]!.AsArray().Select(e => e!.AsObject())
                .Single(e => (string?)e["id"] == One(world, name).GetComponent<Persistent>().Id.ToString());
            Assert.False(Saved("spared goblin")["components"]!.AsObject().ContainsKey("sage:attributes"));
            Assert.True(Saved("wounded goblin")["components"]!.AsObject().ContainsKey("sage:attributes"));
            Assert.Single(file["tombstones"]!["map:saves:camp"]!.AsArray());
        }

        using var second = Boot(saves, rebalanced: true);
        var w = second.World;
        // The patch is in: every goblin at 60, the newcomer placed, the ring on the floor again — until F9.
        Assert.Equal(60f, HealthOf(w, "spared goblin"));
        Assert.Equal(60f, HealthOf(w, "newcomer goblin"));
        Assert.Single(Named(w, "ring"));
        var newcomer = One(w, "newcomer goblin").GetComponent<Persistent>().Id;

        Press(second, Player(w), SaveSystem.QuickLoadAction, Vector3.Zero, 1);
        w = second.World;
        var player2 = Player(w);

        // Nothing doubled, nothing lost: every id the save had, once each, and the newcomer.
        var after = Ids(w);
        Assert.Equal(after.Count, after.Distinct().Count());
        Assert.Equal(before.Append(newcomer).OrderBy(i => i.Value), after.OrderBy(i => i.Value));
        Assert.Equal(4, Named(w, "doomed goblin").Length + Named(w, "wounded goblin").Length
                        + Named(w, "spared goblin").Length + Named(w, "newcomer goblin").Length);

        // The dead stay dead; the hurt keep their hurt; the untouched and the new take the patch.
        var doomed2 = One(w, "doomed goblin");
        Assert.True(IsDead(w, doomed2));
        Assert.Equal(0f, w.Attribute(doomed2, Health));
        Assert.Equal(18f, HealthOf(w, "wounded goblin"));
        Assert.Equal(60f, HealthOf(w, "spared goblin"));
        Assert.Equal(60f, HealthOf(w, "newcomer goblin"));
        Assert.False(IsDead(w, One(w, "spared goblin")));

        // The ring is in the pack and not on the floor; the bread is on the floor and once in the pack.
        Assert.Empty(Named(w, "ring"));
        Assert.Empty(Pickups(w, Ring));
        Assert.Equal(1, w.CountOf(player2, Ring));
        var bread2 = Assert.Single(Pickups(w, Bread));
        Assert.Equal(breadAt, w.Get<Transform>(bread2).LocalPosition);
        Assert.Equal(1, w.CountOf(player2, Bread));
        Assert.Equal(10 - (int)spentArrows, w.CountOf(player2, Arrow));

        // The arrow is where it was, still the player's, and still flying.
        var arrow2 = Assert.Single(Arrows(w));
        Assert.Equal(arrowId, arrow2.GetComponent<Persistent>().Id);
        Assert.Equal(arrowAt, w.Get<Transform>(arrow2).LocalPosition);
        Assert.Equal(player2, w.Get<Projectile>(arrow2).Caster);
        Step(w, 10);
        Assert.Equal(arrow2, Assert.Single(Arrows(w)));
        Assert.True(w.Get<Transform>(arrow2).LocalPosition.Z > arrowAt.Z + 1f);

        // The tally keeps its 2, and counts on from there.
        var tally2 = One(w, "tally");
        Assert.Equal(2f, w.Get<LogicCounter>(tally2).Value);
        w.IO().FireInput(tally2, "Add");
        Step(w, 2);
        Assert.Equal(3f, w.Get<LogicCounter>(tally2).Value);
    }
}
#pragma warning restore SAGE0131
#pragma warning restore SAGE0127
