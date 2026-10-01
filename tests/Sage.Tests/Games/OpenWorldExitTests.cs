#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Phase 4g's exit criterion (issue #190, docs/design/14 "As built (the 4g exit game, issue 4g-8)"): walk from
// an exterior into a dungeon and back; an NPC keeps its schedule across a day while you are away.
// tests/games/open-world has no C# and no kit: a streamed valley on the built-in hills; a village in sector
// (0, 0) with a smith (home 20–08, the forge across the sector edge in (1, 0) 08–20); guards at the north
// gate and bandits who raid it by day, in (0, -1); a crypt door two sectors east, in (2, 0), into a `.map`
// interior with a skeleton. The player's input is a PlayerCommand, as the client's is. The clock is stopped
// (`Scale` 0) once the game has started at 07:00, so every hour is the one the test passes.
#pragma warning disable SAGE0129   // the open world (phase 4g)
public class OpenWorldExitTests
{
    public OpenWorldExitTests() { _ = TestEnv.UserRoot; }

    private static readonly RecordId Valley = new("openworld", "valley");
    private static readonly RecordId Crypt = new("openworld", "crypt");
    private static readonly RecordId Sword = new("openworld", "sword");
    private static readonly RecordId Hammer = new("openworld", "hammer");
    private static readonly RecordId Health = new("sage", "health");
    private static readonly RecordId Damage = new("sage", "damage");

    private static readonly Vector2 Forge = new(1100, 520), SmithHome = new(860, 520);

    private static string GameDirectory => Path.Combine(TestEnv.FolderAbove("tests"), "tests", "games", "open-world");

    private static HeadlessApp Boot(string saves)
    {
        using var log = new CaptureSink();
        var app = HeadlessApp.ForGame(GameDirectory).WithEngineContent().Boot();
        Assert.True(app.Records.ErrorCount == 0, string.Join("\n", log.Entries.Where(e => e.Level >= LogLevel.Error).Select(e => e.Message)));
        Assert.Null(app.Engine.Modules.Game);                    // no C#
        app.Engine.Saves.Root = saves;
        app.CVars.Execute("save_autosave 0");
        var clock = WorldClock.Of(app.World);
        clock.Scale = 0;                                         // the test passes the time
        clock.Hour = 7;
        Tick(app.World, 3);                                      // the ring's sectors placed around the player
        return app;
    }

    private static void Tick(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    private static Entity Player(World world) =>
        Assert.Single(world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList());

    private static Entity[] Named(World world, string name) =>
        world.QueryAll().Entities.ToEntityList().Where(e => e.Name == name).ToArray();

    private static Entity One(World world, string name) => Assert.Single(Named(world, name));

    private static RecordId SceneOf(World world) => world.Resources.Get<ActiveScene>().Id;

    private static Vector3 Absolute(World world, Entity entity) => world.Origin().ToAbsolute(world.Get<Transform>(entity).LocalPosition);

    private static float FromXZ(World world, Entity entity, Vector2 point)
    {
        var at = Absolute(world, entity);
        return Vector2.Distance(new Vector2(at.X, at.Z), point);
    }

    private static bool IsDead(World world, Entity entity) => world.HasTag(entity, world.Conventions().Dead);

    private static OffscreenAgents Table(World world) => world.Resources.Get<OffscreenAgents>();

    private static Entity[] Pickups(World world, RecordId item) =>
        world.Query<Pickup>().Entities.ToEntityList().Where(e => world.Get<Pickup>(e).Item == item).ToArray();

    // Nothing doubled: no persistent id twice in the world, none twice in the off-screen table, and none
    // both in the world and in the table.
    private static void AssertNothingDoubled(World world)
    {
        var ids = world.Query<Persistent>().Entities.ToEntityList().Select(e => e.GetComponent<Persistent>().Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        var away = Table(world).Agents.Select(a => a.Id).ToList();
        Assert.Equal(away.Count, away.Distinct().Count());
        Assert.DoesNotContain(away, id => PersistentId.TryParse(id, out var p) && !world.Resolve(p).IsNull);
    }

    // The watch and the bandits as the off-screen table has them: name, id, health, and whether dead.
    private static string Outcome(World world) =>
        string.Join("; ", Table(world).Agents.Where(a => a.Name is "guard" or "bandit")
            .OrderBy(a => a.Id, StringComparer.Ordinal)
            .Select(a => $"{a.Name} {a.Id[..8]} {a.Health:0.###}{(a.Dead ? " dead" : "")} at ({a.X:0.#}, {a.Z:0.#})"));

    private static Vector3 Eye(World world, Entity player)
    {
        var character = world.Get<CharacterController>(player);
        var profile = CharacterConventions.Of(world).ProfileOf(world.Resources.Get<RecordStore>(), character.Profile);
        return CharacterController.EyeOf(world.Get<Transform>(player).LocalPosition, in character, profile);
    }

    // Presses `action` for one tick looking at `at`, then waits `ticks` looking at it still.
    private static void Press(HeadlessApp app, string? action, Vector3 at, int ticks)
    {
        var world = app.World;
        var player = Player(world);
        var input = world.Resources.Get<PlayerInput>();
        var look = at - Eye(world, player);
        float yaw = SageMath.YawOf(look), pitch = SageMath.PitchOf(look);
        input.HasCommand = true;
        input.Command = new PlayerCommand
        {
            Tick = input.Command.Tick + 1, ViewYaw = yaw, ViewPitch = pitch,
            Pressed = action == null ? default : default(ActionMask).With(app.Engine.Actions.Get(action)),
        };
        Tick(world);
        for (int i = 1; i < ticks; i++)
        {
            input.Command = new PlayerCommand { Tick = input.Command.Tick + 1, ViewYaw = yaw, ViewPitch = pitch };
            Tick(world);
        }
        // Let go: a command left as it is is read again by the next tick, the press with it.
        input.Command = input.Command with { Pressed = default };
    }

    // The player stands at `target` + `offset` (on the same floor), and settles.
    private static void StandBy(World world, Vector3 target, Vector3 offset)
    {
        world.Teleport(Player(world), Transform.At(target + offset));
        Tick(world, 10);
    }

    // Use, looking at a door's middle.
    private static void UseDoor(HeadlessApp app, Entity door) =>
        Press(app, "Use", app.World.Get<Transform>(door).LocalPosition, 2);

    [Xunit.Fact]
    public void TheOpenWorldGameValidates()
    {
        var report = ContentValidation.Run(new ValidateOptions
        {
            GameDirectory = GameDirectory,
            EngineContentDirectory = Path.Combine(TestEnv.FolderAbove("engine_content"), "engine_content"),
            AvailablePlugins = BasePlugins.All(),
        });
        Assert.True(report.Ok, string.Join("\n", report.Errors));
        Assert.DoesNotContain(report.Warnings, w => w.Contains("open-world"));
    }

    // Acceptance. At 07:00 the player goes to the crypt door two sectors east of the village and in. The
    // skeleton is killed and a sword dropped; the player rests 26 hours, saves, and loads in a fresh app;
    // and comes out at 09:00 the next day. The smith is at the forge, not at home, the same smith with his
    // wound and his hammer, and at 20:00 he walks home. The watch and the bandits met at the gate while
    // nobody was there, and one side is dead, the same side and the same wounds as in a run that never
    // saved. Back in the crypt the skeleton is still dead and the sword is on the floor. Nothing is doubled.
    [Xunit.Fact]
    public void OpenWorldExit_AwayADayInTheCrypt_TheSmithKeptHisScheduleAndNothingIsDoubled()
    {
        string saved = Play(saveAndLoad: true);
        string straight = Play(saveAndLoad: false);
        Assert.Equal(straight, saved);
    }

    // The whole run; returns how the fight at the gate came out.
    private static string Play(bool saveAndLoad)
    {
        string saves = TestEnv.NewTempDir();
        var app = Boot(saves);
        try
        {
            var world = app.World;
            var clock = WorldClock.Of(world);
            int firstDay = clock.Day;
            Assert.Equal(Valley, SceneOf(world));

            // 07:00 in the village: the smith is at home, the watch at the gate and the bandits in their camp.
            var smith = One(world, "smith");
            var smithId = smith.GetComponent<Persistent>().Id;
            Assert.True(FromXZ(world, smith, SmithHome) < 1f);
            Assert.Equal(3, Named(world, "guard").Length);
            Assert.Equal(3, Named(world, "bandit").Length);
            Assert.Empty(Named(world, "crypt_door"));                      // two sectors east: not placed
            Effects.Apply(world, smith, Damage, default, 20f);              // a burn at the forge yesterday
            world.FlushCommands();
            Tick(world);
            Assert.Equal(80f, world.Attribute(smith, Health));

            // To the crypt door, and in. The door is placed with its sector as the player arrives.
            Assert.True(Travel.To(world, Valley, "crypt_door_out"));
            Tick(world, 3);
            var door = One(world, "crypt_door");
            Assert.True(Vector2.Distance(new Vector2(Absolute(world, Player(world)).X, Absolute(world, Player(world)).Z), new Vector2(2148, 500)) < 0.5f);
            UseDoor(app, door);
            Assert.Equal(Crypt, SceneOf(world));
            Assert.True(Interiors.Active(world));
            Assert.Empty(world.Resources.Get<Terrain>().Sectors);
            Assert.True(Vector3.Distance(new Vector3(6000, 0.06f, 4), Absolute(world, Player(world))) < 0.2f,
                        $"the player is at {Absolute(world, Player(world))}, not at the crypt's entry");
            Assert.Equal(7.0, clock.Hour, 6);

            // The village is asleep, and its people with the `offscreen` part are in the table: the smith at
            // home, the watch at the gate, the bandits in their camp.
            Assert.Empty(Named(world, "smith"));
            Assert.Empty(Named(world, "guard"));
            var away = Table(world).Find(smithId);
            Assert.NotNull(away);
            Assert.Equal(860.0, away!.X, 1);
            Assert.Equal(3, Table(world).Agents.Count(a => a.Name == "guard"));
            Assert.Equal(3, Table(world).Agents.Count(a => a.Name == "bandit"));

            // The skeleton: three blows of the staff.
            var bones = One(world, "bones");
            var bonesId = bones.GetComponent<Persistent>().Id;
            Assert.Equal(30f, world.Attribute(bones, Health));
            var bonesAt = world.Get<Transform>(bones).LocalPosition;
            StandBy(world, bonesAt, new Vector3(0, 0.1f, 1.6f));
            var chest = bonesAt + new Vector3(0, 1.1f, 0);
            for (int blow = 0; blow < 6 && !IsDead(world, bones); blow++)
            {
                Press(app, null, chest, 2);
                Press(app, "Attack", chest, 70);
            }
            Assert.True(IsDead(world, bones));

            // A sword dropped on the crypt's floor.
            var player = Player(world);
            var sword = world.Drop(player, Sword);
            Assert.False(sword.IsNull);
            Tick(world, 30);
            var swordId = sword.GetComponent<Persistent>().Id;
            var swordAt = Absolute(world, sword);
            Assert.Equal(0, world.CountOf(player, Sword));

            // A rest of 26 hours: one skip at the tick boundary; the table catches up a minute at a time.
            Assert.True(Time.Pass(world, 26, "rest"));
            Tick(world, 2);
            Assert.Equal(firstDay + 1, clock.Day);
            Assert.Equal(9.0, clock.Hour, 6);
            away = Table(world).Find(smithId)!;
            Assert.True(Math.Abs(away.X - Forge.X) < 0.01 && Math.Abs(away.Z - Forge.Y) < 0.01, $"the smith is off-screen at {away}");
            string outcome = Outcome(world);

            if (saveAndLoad)
            {
                Assert.True(app.Engine.Saves.Save("rest"));
                app.Dispose();
                app = Boot(saves);
                world = app.World;
                Assert.Equal(Valley, SceneOf(world));
                Assert.True(app.Engine.Saves.Load("rest"));
                clock = WorldClock.Of(world);                            // the save's clock, not the boot's
                Tick(world);
                Assert.Equal(Crypt, SceneOf(world));
                Assert.Equal(firstDay + 1, clock.Day);
                Assert.Equal(9.0, clock.Hour, 6);
                Assert.Equal(outcome, Outcome(world));
                Assert.True(IsDead(world, One(world, "bones")));
                Assert.Single(Pickups(world, Sword));
            }

            // One side of the fight at the gate is dead: decided while nobody was there.
            var squads = Table(world).Agents.Where(a => a.Name is "guard" or "bandit").ToList();
            bool guardsStand = squads.Any(a => a.Name == "guard" && !a.Dead), banditsStand = squads.Any(a => a.Name == "bandit" && !a.Dead);
            Assert.True(guardsStand ^ banditsStand, $"both sides or neither still stand: {outcome}");

            // Out again, at 09:00 the next day, at the crypt door; the forge's sector is in the ring there.
            var wayOut = One(world, "way_out");
            StandBy(world, world.Get<Transform>(wayOut).LocalPosition with { Y = 0.1f }, new Vector3(0, 0, -1.5f));
            UseDoor(app, wayOut);
            Assert.Equal(Valley, SceneOf(world));
            Assert.Equal(9.0, clock.Hour, 6);
            Tick(world, 3);
            Assert.True(Vector2.Distance(new Vector2(Absolute(world, Player(world)).X, Absolute(world, Player(world)).Z), new Vector2(2148, 500)) < 0.5f);

            // The smith is at the forge, not at home: the same smith, still burnt, his hammer on him, and once.
            smith = One(world, "smith");
            Assert.Equal(smithId, smith.GetComponent<Persistent>().Id);
            Assert.True(FromXZ(world, smith, Forge) < 0.5f, $"the smith is {FromXZ(world, smith, Forge):F1} m from the forge");
            Assert.True(FromXZ(world, smith, SmithHome) > 200f);
            Assert.Equal(80f, world.Attribute(smith, Health));
            Assert.Equal(1, world.CountOf(smith, Hammer));
            Assert.False(IsDead(world, smith));
            Assert.Null(Table(world).Find(smithId));
            AssertNothingDoubled(world);

            // To the village square: the gate's sector comes back with what the fight left.
            Assert.True(Travel.To(world, Valley, "square"));
            Tick(world, 4);
            var standing = squads.ToDictionary(a => a.Id, a => (a.Health, a.Dead));
            foreach (var name in new[] { "guard", "bandit" })
                foreach (var one in Named(world, name))
                {
                    var (health, dead) = standing[one.GetComponent<Persistent>().Id.ToString()];
                    Assert.Equal(health, world.Attribute(one, Health));
                    Assert.Equal(dead, IsDead(world, one));
                }
            Assert.Equal(squads.Count, Named(world, "guard").Length + Named(world, "bandit").Length);
            Assert.DoesNotContain(Table(world).Agents, a => a.Name is "guard" or "bandit");
            AssertNothingDoubled(world);

            // 20:00: he leaves the forge and walks home (the hour has just begun, so he is not put there).
            smith = One(world, "smith");
            Assert.True(Time.Pass(world, 11, "wait"));
            Tick(world);
            Assert.Equal(20.0, clock.Hour, 6);
            Assert.True(FromXZ(world, smith, Forge) < 0.5f);
            float before = FromXZ(world, smith, SmithHome);
            Tick(world, 600);
            Assert.True(FromXZ(world, smith, Forge) > 3f, "the smith did not leave the forge at 20:00");
            Assert.True(FromXZ(world, smith, SmithHome) < before - 3f, "the smith is not walking home");
            Assert.Equal(new RecordId("openworld", "keep_to_routine"), world.Get<AIState>(smith).Schedule);
            Assert.True(Time.Pass(world, 1, "wait"));                      // and an hour later he is there
            Tick(world, 2);
            Assert.True(FromXZ(world, smith, SmithHome) < 1f, $"the smith is {FromXZ(world, smith, SmithHome):F1} m from home");

            // Back into the crypt: the skeleton is still dead, and the sword still on the floor where it fell.
            Assert.True(Travel.To(world, Valley, "crypt_door_out"));
            Tick(world, 3);
            UseDoor(app, One(world, "crypt_door"));
            Assert.Equal(Crypt, SceneOf(world));
            bones = One(world, "bones");
            Assert.Equal(bonesId, bones.GetComponent<Persistent>().Id);
            Assert.True(IsDead(world, bones));
            Assert.Equal(0f, world.Attribute(bones, Health));
            var swordBack = Assert.Single(Pickups(world, Sword));
            Assert.Equal(swordId, swordBack.GetComponent<Persistent>().Id);
            Assert.True(Vector3.Distance(swordAt, Absolute(world, swordBack)) < 0.01f, $"the sword moved from {swordAt} to {Absolute(world, swordBack)}");
            Assert.Equal(0, world.CountOf(Player(world), Sword));
            Assert.Empty(Named(world, "smith"));
            Assert.NotNull(Table(world).Find(smithId));
            AssertNothingDoubled(world);
            return outcome;
        }
        finally { app.Dispose(); }
    }
}
#pragma warning restore SAGE0129
