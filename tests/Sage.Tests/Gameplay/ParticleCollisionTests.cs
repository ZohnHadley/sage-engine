#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Particles that meet the world, run through a sprite sheet over their life, and drop out past the fog
// (issue 4n-6, docs/design/06 §3.12). The ray, the bounce and the frame are arithmetic over the world's
// physics, so all of it is checked headless; the client only copies the frame's UVs into the snapshot and
// asks `Particles.FogHides` per particle.
public class ParticleCollisionTests
{
    public ParticleCollisionTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    private static HeadlessApp Sandbox() =>
        HeadlessApp.ForGame(Path.Combine(TestEnv.FolderAbove("Sage.sln"), "games", "Sandbox"), new global::Sandbox.SandboxModule())
            .WithEngineContent()
            .OnRegistered(app => app.Records.Register<ParticleRecord>())   // the client's record type, as `sage validate` does
            .Boot();

    private static void Step(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++)
        {
            world.RunFixed(Dt);
            world.RunFrame(Dt, 1f);
        }
    }

    // Done (4n-6): the Sandbox's own sparks record, thrown straight down at its floor, bounces off it —
    // it is never below the floor and is going up again after the hit — where a spark that does not
    // collide falls straight through.
    [Fact]
    public void ASparkBouncesOffTheSandboxFloor()
    {
        using var app = Sandbox();
        var world = app.World;
        Step(world, 2);
        var physics = world.Resources.Get<IPhysicsWorld>();
        var sparks = app.Records.Get<ParticleRecord>(new RecordId("sandbox", "sparks"));
        Assert.Equal(ParticleCollision.Bounce, sparks.Collision);

        // Where the floor is under the player: the same ray a spark will cast.
        var player = Assert.Single(world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList());
        var above = world.Get<Transform>(player).LocalPosition + new Vector3(1.5f, 1.5f, 0f);
        var floor = physics.Raycast(above, -Vector3.UnitY, 10f);
        Assert.True(floor.Hit, "no floor under the player");

        // One spark, straight down, long-lived so it has time to land and come back up.
        var record = Copy(sparks);
        record.Shape = EmitShape.Point;
        record.LifeMin = record.LifeMax = 2f;
        record.SpeedMin = record.SpeedMax = 5f;
        var particles = new Particles();
        Assert.Equal(1, particles.Emit(new RecordId("sandbox", "sparks"), record, above, -Vector3.UnitY, 1));
        var group = particles.Groups[0];

        bool wentUp = false;
        float lowest = float.MaxValue;
        for (int i = 0; i < 40 && group.Count > 0; i++)
        {
            particles.Update(world, Dt);
            lowest = MathF.Min(lowest, group.Position[0].Y);
            if (particles.Hits > 0 && group.Velocity[0].Y > 0f) wentUp = true;
        }
        Assert.True(particles.Hits > 0, "the spark never met the floor");
        Assert.True(wentUp, "the spark met the floor but did not bounce");
        Assert.True(lowest >= floor.Position.Y, $"the spark went to {lowest:F3}, below the floor at {floor.Position.Y:F3}");
        Assert.True(particles.Rays > 0);

        // The same throw without collision passes through.
        record.Collision = ParticleCollision.None;
        var ghost = new Particles();
        ghost.Emit(new RecordId("sandbox", "ghost"), record, above, -Vector3.UnitY, 1);
        for (int i = 0; i < 40; i++) ghost.Update(world, Dt);
        Assert.True(ghost.Groups[0].Position[0].Y < floor.Position.Y - 0.5f, "a spark that does not collide stopped anyway");
        Assert.Equal(0, ghost.Rays);
    }

    // Die ends a particle where it meets the world; the ray budget is a ceiling, and what it turned away
    // is counted rather than quietly let through.
    [Fact]
    public void DieEndsItOnTheFloorAndTheBudgetIsCounted()
    {
        using var app = Sandbox();
        var world = app.World;
        Step(world, 2);
        var player = Assert.Single(world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList());
        var above = world.Get<Transform>(player).LocalPosition + new Vector3(1.5f, 1f, 0f);

        var record = new ParticleRecord
        {
            Shape = EmitShape.Point, LifeMin = 3f, LifeMax = 3f, SpeedMin = 6f, SpeedMax = 6f,
            Gravity = -9.81f, Collision = ParticleCollision.Die,
        };
        var particles = new Particles { CollisionBudget = 4 };
        Assert.Equal(10, particles.Emit(new RecordId("game", "rain"), record, above, -Vector3.UnitY, 10));
        particles.Update(world, Dt);
        Assert.Equal(4, particles.Rays);
        Assert.Equal(6, particles.RaysSkipped);

        particles.CollisionBudget = 512;
        for (int i = 0; i < 30; i++) particles.Update(world, Dt);
        // Those checked every frame end on the floor; the six unchecked in the first frame were still a
        // metre up, so they were caught too.
        Assert.Equal(0, particles.Live);
        Assert.Equal(10, particles.Hits);
        particles.ResetStats();
        Assert.Equal(0, particles.Rays + particles.RaysSkipped + particles.Hits);
    }

    // The bounce itself: into the surface reflected and scaled by restitution, along it scaled by
    // 1 - friction, and a velocity already leaving the surface left alone.
    [Fact]
    public void ABounceKeepsWhatRestitutionAndFrictionSay()
    {
        var bounced = Particles.Bounce(new Vector3(2f, -4f, 0f), Vector3.UnitY, 0.5f, 0.25f);
        Assert.Equal(1.5f, bounced.X, 4);
        Assert.Equal(2f, bounced.Y, 4);
        Assert.Equal(new Vector3(1f, 3f, 0f), Particles.Bounce(new Vector3(1f, 3f, 0f), Vector3.UnitY, 0.5f, 0.25f));
        Assert.Equal(0f, Particles.Bounce(new Vector3(0f, -4f, 0f), Vector3.UnitY, 0f, 0f).Y);
    }

    // A sheet over a life: the frame follows the share of life lived, ends on the last frame rather than
    // wrapping to the first, cycles when asked, and starts on a frame of its own with sheetRandomStart.
    [Fact]
    public void ASheetRunsThroughItsFramesOverALife()
    {
        var record = new ParticleRecord { SheetColumns = 4, SheetRows = 2 };
        Assert.Equal(8, record.FrameCount);
        Assert.Equal(0, Particles.Frame(record, 0f));
        Assert.Equal(4, Particles.Frame(record, 0.5f));
        Assert.Equal(7, Particles.Frame(record, 0.99f));
        Assert.Equal(7, Particles.Frame(record, 1f));                 // dead on its last frame, not back to the first

        record.SheetFrames = 6;                                        // the last two cells are unused
        Assert.Equal(5, Particles.Frame(record, 1f));
        record.SheetCycles = 2f;
        Assert.Equal(0, Particles.Frame(record, 0.5f));                // round once, starting again
        Assert.Equal(3, Particles.Frame(record, 0.75f));
        Assert.Equal(2, Particles.Frame(record, 0f, first: 2));        // a random start moves it on
        record.SheetCycles = 0f;
        Assert.Equal(3, Particles.Frame(record, 0.9f, first: 3));      // 0 cycles holds its first frame

        // UVs: left to right, then top to bottom; a 1 x 1 sheet is the whole texture.
        var uv = Particles.FrameUv(record, 5);                         // column 1, row 1
        Assert.Equal(new Vector4(0.25f, 0.5f, 0.5f, 1f), uv);
        Assert.Equal(new Vector4(0f, 0f, 1f, 1f), Particles.FrameUv(new ParticleRecord(), 0));

        // On live particles: the frame moves with age.
        var world = HeadlessApp.Bare().Boot("sheet").World;
        var particles = new Particles();
        var smoke = new ParticleRecord { SheetColumns = 4, SheetRows = 1, LifeMin = 1f, LifeMax = 1f, Gravity = 0f };
        particles.Emit(new RecordId("game", "smoke"), smoke, Vector3.Zero, Vector3.UnitY, 1);
        var group = particles.Groups[0];
        Assert.Equal(0, group.FrameOf(0));
        for (int i = 0; i < 30; i++) particles.Update(world, Dt);
        Assert.Equal(2, group.FrameOf(0));
        Assert.Equal(new Vector4(0.5f, 0f, 0.75f, 1f), group.UvOf(0));

        // A random start spreads a cloud over the sheet.
        var cloud = new Particles();
        var random = new ParticleRecord { SheetColumns = 4, SheetRows = 4, SheetRandomStart = true };
        cloud.Emit(new RecordId("game", "cloud"), random, Vector3.Zero, Vector3.UnitY, 64);
        Assert.True(Enumerable.Range(0, 64).Select(i => cloud.Groups[0].FrameOf(i)).Distinct().Count() > 4);
    }

    // Fog culling: a particle whose quad is wholly past the fog's cull distance is hidden; one whose
    // corner reaches back inside it is not; no fog (+inf) hides nothing.
    [Fact]
    public void FogHidesAParticleWhollyPastIt()
    {
        Assert.True(Particles.FogHides(100f, new Vector3(0f, 0f, -101f), 0.5f));
        Assert.False(Particles.FogHides(100f, new Vector3(0f, 0f, -100.2f), 0.5f));
        Assert.False(Particles.FogHides(float.PositiveInfinity, new Vector3(0f, 0f, -1e6f), 0.5f));
    }

    // A particle record's mistakes are load errors at their lines: a grid out of range, more frames than
    // the grid holds, negative cycles, fractions that are not fractions; collision on a carried effect is a
    // warning.
    [Fact]
    public void ABadParticleRecordIsALoadError()
    {
        var files = new MountFixture();
        files.Write("game", "data/fx.json", """
        [
          { "type": "particle", "id": "no_columns", "sheetColumns": 0 },
          { "type": "particle", "id": "too_many", "sheetColumns": 2, "sheetRows": 2, "sheetFrames": 5 },
          { "type": "particle", "id": "backwards", "sheetCycles": -1 },
          { "type": "particle", "id": "springy", "collision": "Bounce", "restitution": 1.5 },
          { "type": "particle", "id": "sticky", "collision": "Bounce", "friction": -0.1 },
          { "type": "particle", "id": "carried", "local": true, "collision": "Die" },
          { "type": "particle", "id": "good", "sheetColumns": 4, "sheetRows": 4, "sheetFrames": 12, "collision": "Die" }
        ]
        """);
        files.Mount("game", "game");
        using var log = new CaptureSink();
        using var app = HeadlessApp.Simulation().Mount(files)
            .OnRegistered(a => a.Records.Register<ParticleRecord>()).Boot();
        Assert.Equal(5, app.Records.ErrorCount);
        Assert.Contains(log.Entries, e => e.Message.Contains("no_columns") && e.Message.Contains("sheetColumns"));
        Assert.Contains(log.Entries, e => e.Message.Contains("too_many") && e.Message.Contains("more than the 2 x 2 grid"));
        Assert.Contains(log.Entries, e => e.Message.Contains("backwards") && e.Message.Contains("sheetCycles"));
        Assert.Contains(log.Entries, e => e.Message.Contains("springy") && e.Message.Contains("restitution"));
        Assert.Contains(log.Entries, e => e.Message.Contains("sticky") && e.Message.Contains("friction"));
        // A carried effect never collides: said, but not an error.
        Assert.Contains(log.Entries, e => e.Message.Contains("carried") && e.Message.Contains("not read on a local"));
        Assert.True(app.Records.TryGet(new RecordId("game", "good"), out ParticleRecord good));
        Assert.Equal(ParticleCollision.Die, good.Collision);
    }

    private static ParticleRecord Copy(ParticleRecord r) => new()
    {
        Texture = r.Texture, Burst = r.Burst, MaxParticles = r.MaxParticles, Gravity = r.Gravity, Drag = r.Drag,
        SizeStart = r.SizeStart, SizeEnd = r.SizeEnd, ColourStart = r.ColourStart, ColourEnd = r.ColourEnd,
        Collision = r.Collision, Restitution = r.Restitution, Friction = r.Friction,
    };
}
