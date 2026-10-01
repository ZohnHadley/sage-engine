#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// NPC routines (issue 4g-4, REDESIGN §5 phase 4g): a `routine` record picks a schedule and a place by the
// hour, the tasks walk there through the navigation grid, and a skip of time catches up by arithmetic.
//
// The smith of the plan, in a scene: home at (-10, 5), the forge 30 m east at (20, 0), the forge marker
// turned to face +X. The clock is stopped (`Scale` 0) and set by hand, so the hour is the test's.
public class RoutineTests
{
    public RoutineTests() { _ = TestEnv.UserRoot; }

    private const string Content = """
        [{ "type": "gameplay_conventions", "id": "default_conventions", "health": "health", "dead": "state.dead", "invulnerable": "state.invulnerable",
           "aiProfile": "default_ai", "schedules": { "idle": "idle", "chase": "chase", "meleeAttack": "chase", "castSpell": "idle", "holdGround": "idle" } },
         { "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
         { "type": "tag", "id": "state.dead" },
         { "type": "tag", "id": "state.invulnerable" },
         { "type": "ai_profile", "id": "default_ai", "sightRange": 25, "meleeRange": 1.8, "thinkRate": 20 },
         { "type": "ai_schedule", "id": "idle", "tasks": [{ "task": "Wait", "seconds": 1.5 }], "interrupts": ["SeeEnemy", "in_routine"] },
         { "type": "ai_schedule", "id": "chase", "tasks": [{ "task": "MoveToTarget", "distance": 1.6 }], "interrupts": ["LostEnemy", "NoEnemy"] },
         { "type": "ai_schedule", "id": "keep_routine", "tasks": ["MoveToAnchor", "FaceAnchor", "StayAt"], "interrupts": ["SeeEnemy"] },

         { "type": "routine", "id": "smith",
           "entries": [ { "from": 8, "to": 20, "schedule": "keep_routine", "at": "forge" },
                        { "from": 20, "to": 8, "schedule": "keep_routine", "at": "smith_home" } ] },
         { "type": "routine", "id": "cellar_keeper",
           "entries": [ { "from": 0, "to": 24, "schedule": "keep_routine", "at": "barrel", "scene": "cellar" } ] },

         { "type": "prefab", "id": "ground", "name": "ground", "parts": { "body": { "size": [400, 1, 400] } } },
         { "type": "prefab", "id": "marker", "name": "marker" },
         { "type": "prefab", "id": "smith", "name": "smith", "components": { "ai_state": {} },
           "parts": { "character": { "layer": "enemy" }, "attributes": {}, "routine": "smith" } },
         { "type": "prefab", "id": "keeper", "name": "keeper", "components": { "ai_state": {} },
           "parts": { "character": { "layer": "enemy" }, "routine": "cellar_keeper" } },

         { "type": "scene", "id": "town",
           "place": [ { "prefab": "ground", "at": [0, -0.5, 0], "name": "ground" },
                      { "prefab": "marker", "at": [20, 0, 0], "yaw": -90, "name": "forge" },
                      { "prefab": "marker", "at": [-10, 0, 5], "name": "smith_home" },
                      { "prefab": "smith", "at": [-10, 0.1, 5], "name": "smith", "id": "smith" },
                      { "prefab": "keeper", "at": [5, 0.1, 20], "name": "keeper", "id": "keeper" } ] },
         { "type": "scene", "id": "cellar", "place": [ { "prefab": "marker", "at": [0, 0, 0], "name": "barrel" } ] }]
        """;

    private static readonly Vector3 Forge = new(20, 0, 0), Home = new(-10, 0, 5);

    private static HeadlessApp Boot()
    {
        using var log = new CaptureSink();
        var app = HeadlessApp.Gameplay().File("data/routines.json", Content).StartScene("sage:town").Boot("town");
        Assert.True(app.Records.ErrorCount == 0, string.Join("\n", log.Entries.Where(e => e.Level >= LogLevel.Error).Select(e => e.Message)));
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        var clock = WorldClock.Of(app.World);
        clock.Scale = 0;                     // the test sets the hour
        clock.Hour = 7;
        return app;
    }

    private static void Tick(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    private static Entity Smith(World world) => world.FindByName("smith");

    private static Vector3 At(World world, Entity entity) => world.Get<Transform>(entity).LocalPosition;

    private static float FromXZ(World world, Entity entity, Vector3 point) => SageMath.DistanceXZ(At(world, entity), point);

    private static RecordId Id(string name) => new("sage", name);

    [Fact]
    public void TheSmithWalksToTheForgeAtEightAndHomeAtTwenty()
    {
        using var app = Boot();
        var world = app.World;
        var smith = Smith(world);

        Tick(world, 60);                                                   // 07:00: home, where he is
        Assert.True(FromXZ(world, smith, Home) < 1f);
        Assert.True(((AICondition)world.Get<AIState>(smith).Conditions).HasFlag(AICondition.InRoutine));
        Assert.Equal(Id("keep_routine"), world.Get<AIState>(smith).Schedule);

        WorldClock.Of(world).Hour = 8;                                     // 08:00: to work
        Tick(world, 60);
        Assert.True(FromXZ(world, smith, Home) > 2f, "he did not set off for the forge at eight");
        Tick(world, 60 * 12);
        Assert.True(FromXZ(world, smith, Forge) < 1.5f, $"he is {FromXZ(world, smith, Forge):F1} m from the forge");
        // And faces the way the forge marker does (+X), once there.
        float yaw = world.Get<PawnIntent>(smith).Yaw;
        Assert.True(MathF.Abs(SageMath.WrapPi(yaw - (-MathF.PI / 2))) < 0.2f, $"facing {yaw:F2}, not the forge's -π/2");

        WorldClock.Of(world).Hour = 20;                                    // 20:00: the same schedule, home
        Tick(world, 60 * 12);
        Assert.True(FromXZ(world, smith, Home) < 1.5f, $"he is {FromXZ(world, smith, Home):F1} m from home");
        Assert.Equal(Id("keep_routine"), world.Get<AIState>(smith).Schedule);
    }

    [Fact]
    public void PassingTimeSnapsTheSmithToWhereHisRoutineHasHim()
    {
        using var app = Boot();
        var world = app.World;
        var smith = Smith(world);
        Tick(world, 10);

        app.CVars.Execute("sv_cheats 1");
        app.CVars.Execute("time_pass 12");                                 // 07:00 + 12 h: 19:00, at work since 08:00
        Tick(world);
        Assert.True(FromXZ(world, smith, Forge) < 0.01f, $"he is {FromXZ(world, smith, Forge):F2} m from the forge");
        Assert.Equal(Id("keep_routine"), world.Get<AIState>(smith).Schedule);
        Tick(world, 60);
        Assert.True(FromXZ(world, smith, Forge) < 1f, "he wandered off the forge after the skip");

        app.CVars.Execute("time_pass 2");                                  // 21:00: home since 20:00
        Tick(world);
        Assert.True(FromXZ(world, smith, Home) < 0.01f);

        // A skip too short to have walked it: from 07:59:56 to 08:00:04 he has had four seconds of work, 17 m
        // of walking, and the forge is 30 m away. He is not moved; he walks when the game resumes.
        WorldClock.Of(world).Hour = 8 - 4.0 / 3600;
        Tick(world, 30);
        Assert.True(Time.Pass(world, 8.0 / 3600, "wait"));
        Tick(world);
        Assert.True(FromXZ(world, smith, Home) < 1f, "he was snapped to a forge he could not have reached");
        Tick(world, 60 * 12);
        Assert.True(FromXZ(world, smith, Forge) < 1.5f);
    }

    // Combat outranks the routine (decision 6): somebody hostile turns up on his way to work, he goes for
    // them, and once they are gone he walks on to the forge.
    [Fact]
    public void AFightInterruptsTheRoutineAndHeGoesBackToIt()
    {
        using var app = Boot();
        var world = app.World;
        var smith = Smith(world);
        WorldClock.Of(world).Hour = 8;
        Tick(world, 120);                                                  // on his way, facing east
        Assert.Equal(Id("keep_routine"), world.Get<AIState>(smith).Schedule);

        var intruder = world.Create(Transform.At(At(world, smith) + new Vector3(6, 0, 0)), "intruder");
        world.Add(intruder, Collider.Standing(0.35f, 1.8f, 1));
        world.Add(intruder, RigidBody.Kinematic());
        world.AddAttributes(intruder);
        intruder.AddTag<PlayerControlled>();
        Tick(world, 30);
        var fighting = world.Get<AIState>(smith);
        Assert.Equal(Id("chase"), fighting.Schedule);
        Assert.Equal(intruder, fighting.Target);

        world.Destroy(intruder);
        Tick(world, 60 * 12);
        Assert.Equal(Id("keep_routine"), world.Get<AIState>(smith).Schedule);
        Assert.True(FromXZ(world, smith, Forge) < 1.5f, $"he is {FromXZ(world, smith, Forge):F1} m from the forge");
    }

    [Fact]
    public void SavedMidWalkHeArrivesAfterALoad()
    {
        using var app = Boot();
        var world = app.World;
        WorldClock.Of(world).Hour = 8;
        Tick(world, 240);                                                  // four seconds: about half-way
        var halfway = At(world, Smith(world));
        Assert.True(SageMath.DistanceXZ(halfway, Home) > 5f && SageMath.DistanceXZ(halfway, Forge) > 5f);
        Assert.True(app.Engine.Saves.Save("walk"));

        WorldClock.Of(world).Hour = 21;                                    // somewhere else entirely
        Tick(world, 120);
        Assert.True(app.Engine.Saves.Load("walk"));

        var smith = Smith(world);
        Assert.True(SageMath.DistanceXZ(At(world, smith), halfway) < 0.5f, "the load did not put him back on his way");
        Assert.Equal(8.0, WorldClock.Of(world).Hour, 3);
        Tick(world, 60 * 10);
        Assert.True(FromXZ(world, smith, Forge) < 1.5f, $"he is {FromXZ(world, smith, Forge):F1} m from the forge");
        Assert.Single(world.Query<Transform>().Entities.ToEntityList(), e => e.Name == "smith");
    }

    // The anchor is an entry in a scene that is not loaded: he is not sent anywhere, and the routine says
    // where he ought to be, for off-screen simulation (4g-6) to act on.
    [Fact]
    public void AnAnchorInASceneThatIsNotLoadedIsRecordedNotWalkedTo()
    {
        using var app = Boot();
        var world = app.World;
        var keeper = world.FindByName("keeper");
        var start = At(world, keeper);
        Tick(world, 120);

        var state = world.Get<AIState>(keeper);
        Assert.False(((AICondition)state.Conditions).HasFlag(AICondition.InRoutine));
        Assert.Equal(Id("idle"), state.Schedule);
        Assert.True(SageMath.DistanceXZ(At(world, keeper), start) < 0.5f);

        var target = Routines.Target(world, keeper);
        Assert.True(target.InForce);
        Assert.True(target.Elsewhere);
        Assert.Equal(Id("cellar"), target.Scene);
        Assert.Equal("barrel", target.At);
        Assert.Equal(Id("keep_routine"), target.Schedule);
    }

    // Days, as the calendar names them: a night that begins on a listed day holds until its morning, and a
    // name no calendar has is a load error.
    [Fact]
    public void AnEntryOnADayHoldsUntilItsMorningAndAnUnknownDayIsALoadError()
    {
        var routine = new RoutineRecord();
        routine.Entries.Add(new RoutineEntry { From = 22, To = 6, Days = { "Friday" }, At = "tavern" });
        routine.Entries.Add(new RoutineEntry { From = 0, To = 24, At = "home" });
        var calendar = CalendarRecord.Default;                             // day 0 is a Monday: day 4 a Friday

        Assert.Equal(0, Routines.EntryAt(routine, calendar, 4 * 24 + 23));  // Friday 23:00
        Assert.Equal(0, Routines.EntryAt(routine, calendar, 5 * 24 + 3));   // Saturday 03:00, Friday's night
        Assert.Equal(1, Routines.EntryAt(routine, calendar, 5 * 24 + 23));  // Saturday 23:00: home
        Assert.Equal(1, Routines.EntryAt(routine, calendar, 4 * 24 + 3));   // Friday 03:00: Thursday's night
        Assert.Equal(4 * 24 + 22, Routines.StartOf(routine.Entries[0], 5 * 24 + 3));

        using var log = new CaptureSink();
        using var app = HeadlessApp.Gameplay().File("data/bad.json", """
            [{ "type": "ai_schedule", "id": "s", "tasks": ["StayAt"] },
             { "type": "routine", "id": "bad", "entries": [ { "from": 8, "to": 30, "days": ["Fryday"], "schedule": "s" } ] }]
            """).Build();
        Assert.Equal(3, app.Records.ErrorCount);
        Assert.Contains(log.Entries, e => e.Message.Contains("no calendar has a weekday 'Fryday'"));
        Assert.Contains(log.Entries, e => e.Message.Contains("is not an hour"));
        Assert.Contains(log.Entries, e => e.Message.Contains("\"at\""));
    }
}
