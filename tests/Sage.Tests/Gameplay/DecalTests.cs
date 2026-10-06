#nullable enable
using System;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Marks that stay (issue #306): a pooled, capped store of decals, named by impact cues and damage types
// the way particle effects are. The pool, where a mark lands, how it fades, the ceiling and the rebase
// are the simulation's and checked here without a window; the client only draws what the pool holds.
public class DecalTests
{
    public DecalTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;
    private static readonly RecordId Hole = new("sage", "hole");

    private static DecalRecord Record(float lifetime = 10f, float fade = 2f, float size = 0.2f) =>
        new() { Texture = AssetPath.Intern("textures/particle.png"), Size = size, Lifetime = lifetime, Fade = fade, RandomRotation = false };

    // ---- the pool ---------------------------------------------------------------------------------

    // Done: the pool ceiling. A full pool makes room by dropping its oldest mark, so the newest — the
    // one the player is looking at — is always there; lowering the ceiling drops the oldest at once.
    [Xunit.Fact]
    public void AFullPoolDropsItsOldestMarkFirst()
    {
        var decals = new Decals { Ceiling = 3 };
        var record = Record(lifetime: 0f);   // stays until pushed out
        for (int i = 0; i < 5; i++)
            Assert.True(decals.Place(Hole, record, new Vector3(i, 0, 0), Vector3.UnitZ));

        Assert.Equal(3, decals.Count);
        Assert.Equal(2, decals.Evicted);
        Assert.Equal(new[] { 2f, 3f, 4f }, decals.Live.ToArray().Select(d => d.Position.X));   // oldest first

        decals.Ceiling = 1;
        Assert.Equal(4f, Assert.Single(decals.Live.ToArray()).Position.X);
        Assert.Equal(4, decals.Evicted);

        decals.Ceiling = 0;                                    // r_decals 0: none at all
        Assert.Equal(0, decals.Count);
        Assert.False(decals.Place(Hole, record, Vector3.Zero, Vector3.UnitZ));
    }

    // A mark is opaque until the last `fade` seconds of its lifetime, then goes to nothing, and is gone
    // when its lifetime is up; the tint's alpha follows.
    [Xunit.Fact]
    public void AMarkFadesOverTheEndOfItsLifetimeAndThenGoes()
    {
        var decals = new Decals();
        var record = Record(lifetime: 4f, fade: 2f);
        record.Colour = 0xC0102030;   // alpha 0xC0
        decals.Place(Hole, record, Vector3.Zero, Vector3.UnitY);

        decals.Update(null, 1f);
        Assert.Equal(1f, decals.Live[0].Alpha);
        Assert.Equal(0xC0u, decals.Live[0].Colour >> 24);

        decals.Update(null, 2f);                               // 3 s: half way through the fade
        Assert.Equal(0.5f, decals.Live[0].Alpha, 3);
        Assert.Equal(0x60u, decals.Live[0].Colour >> 24);
        Assert.Equal(0x102030u, decals.Live[0].Colour & 0xFFFFFF);

        decals.Update(null, 1.01f);
        Assert.Equal(0, decals.Count);
    }

    // Where it lies: on the point, facing out along the normal, its square in the surface's plane and
    // turned by the rotation it was given.
    [Xunit.Fact]
    public void AMarkLiesInTheSurfacesPlane()
    {
        var decals = new Decals();
        var normal = Vector3.Normalize(new Vector3(1, 0.3f, -0.2f));
        decals.Place(Hole, Record(), new Vector3(1, 2, 3), normal * 5f, rotation: 0f);
        decals.Place(Hole, Record(), new Vector3(1, 2, 3), normal, rotation: MathF.PI / 2f);

        var flat = decals.Live[0];
        Assert.Equal(new Vector3(1, 2, 3), flat.Position);
        Assert.Equal(1f, flat.Normal.Length(), 4);
        Assert.Equal(0f, Vector3.Dot(flat.Right, flat.Normal), 4);
        Assert.Equal(0f, Vector3.Dot(flat.Up, flat.Normal), 4);
        Assert.Equal(0f, Vector3.Dot(flat.Right, flat.Up), 4);
        Assert.Equal(0f, flat.Right.Y, 4);                     // unturned on a wall: "up" is up
        Assert.True(flat.Up.Y > 0f);

        var turned = decals.Live[1];
        Assert.Equal(1f, Vector3.Dot(turned.Right, flat.Up), 3);   // a quarter turn about the normal
    }

    // ---- in a world ---------------------------------------------------------------------------------

    private const string Records = """
        [{ "type": "decal", "id": "hole", "texture": "textures/particle.png", "size": 0.1, "lifetime": 3, "fade": 1,
           "randomRotation": false },
         { "type": "decal", "id": "blood", "texture": "textures/particle.png", "size": 0.4, "lifetime": 3, "fade": 1, "reach": 3 },
         { "type": "cue", "id": "impact_plaster", "decal": "hole" },
         { "type": "physics_material", "id": "plaster", "impact": "impact_plaster" },
         { "type": "damage_type", "id": "bleeding", "effect": "damage", "decal": "blood" },
         { "type": "attack", "id": "rifle", "delivery": "ray", "damage": 25, "range": 50, "damageType": "bleeding",
           "windupTime": 0, "recoverTime": 0.1, "cooldown": 0.3 }]
        """;

    private static HeadlessApp NewGame(bool pool = true, string? patch = null)
    {
        var builder = HeadlessApp.Gameplay()
            .OnRegistered(a => a.Records.Register<DecalRecord>())
            .File("data/hits.json", HitPipelineTests.Records)
            .File("data/decals.json", Records);
        if (patch != null) builder = builder.File("data/patch.json", patch);
        var app = builder.Boot("decals");
        Assert.Equal(0, app.Records.ErrorCount);
        if (pool) app.World.Resources.Add(new Decals());
        var ground = app.World.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        app.World.Add(ground, Collider.Box(new Vector3(400, 1, 400)));
        return app;
    }

    // A plaster wall whose near face is at z = -9.8.
    private static Entity Wall(World world)
    {
        var wall = world.Create(Transform.At(new Vector3(0, 1.5f, -10)), "wall");
        var collider = Collider.Box(new Vector3(6, 3, 0.4f));
        collider.Surface = new RecordId("sage", "plaster");
        world.Add(wall, collider);
        return wall;
    }

    private static void Fire(HeadlessApp app, Entity shooter)
    {
        var world = app.World;
        world.Get<PawnIntent>(shooter).Pressed = default(ActionMask).With(app.Engine.Actions.Get("Attack"));
        world.RunFixed(Dt);
        world.Get<PawnIntent>(shooter).Pressed = default;
        world.RunFixed(Dt);   // a zero-windup blow lands the tick after the press
    }

    // Done: a hit spawns a decal that fades. A round into plaster raises the surface's impact cue (with
    // the wall's normal), the cue names a decal, and the mark lies on the wall's face — then fades over
    // its last second and is gone after three.
    [Xunit.Fact]
    public void AShotIntoAWallLeavesAMarkThatFades()
    {
        using var app = NewGame();
        var world = app.World;
        var wall = Wall(world);
        var shooter = HitPipelineTests.Body(world, Vector3.Zero, "shooter", new Vector3(0, 0, -10), "rifle");
        var cues = new EventProbe<CueTriggered>(world);
        var decals = world.Resources.Get<Decals>();

        Fire(app, shooter);

        var cue = Assert.Single(cues.All, c => c.Cue == new RecordId("sage", "impact_plaster"));
        Assert.Equal(1f, cue.Normal.Z, 3);                      // the impact says which way the wall faces
        var mark = Assert.Single(decals.Live.ToArray());
        Assert.Equal(new RecordId("sage", "hole"), mark.Effect);
        Assert.Equal(-9.8f, mark.Position.Z, 2);
        Assert.Equal(1f, mark.Normal.Z, 3);
        Assert.Equal(wall, mark.Surface);
        Assert.Equal(1f, mark.Alpha);

        for (int i = 0; i < 150; i++) world.RunFixed(Dt);       // 2.5 s: half way through its fade
        Assert.InRange(decals.Live[0].Alpha, 0.4f, 0.6f);
        for (int i = 0; i < 40; i++) world.RunFixed(Dt);
        Assert.Equal(0, decals.Count);
    }

    // A damage type names a decal too: blood lands on the wall behind what was hurt, along the blow —
    // never on the creature itself, which would walk away from it.
    [Xunit.Fact]
    public void AWoundLeavesItsDamageTypesMarkOnTheWallBehind()
    {
        using var app = NewGame();
        var world = app.World;
        Wall(world);
        var shooter = HitPipelineTests.Body(world, Vector3.Zero, "shooter", new Vector3(0, 0, -10), "rifle");
        var target = HitPipelineTests.Body(world, new Vector3(0, 0, -8), "target", Vector3.Zero);
        var decals = world.Resources.Get<Decals>();

        Fire(app, shooter);

        Assert.Equal(75f, world.Attribute(target, HitPipelineTests.Health), 3);
        var mark = Assert.Single(decals.Live.ToArray());
        Assert.Equal(new RecordId("sage", "blood"), mark.Effect);
        Assert.Equal(-9.8f, mark.Position.Z, 2);
        Assert.NotEqual(target, mark.Surface);
    }

    // The ceiling holds in a fight: a pool of four, eight shots, and the four left are the last four.
    [Xunit.Fact]
    public void TheCeilingHoldsInAFirefight()
    {
        // Holes that stay until pushed out, so only the ceiling decides which are left.
        using var app = NewGame(patch: """[{ "type": "decal", "id": "hole", "patch": true, "lifetime": 0, "fade": 0 }]""");
        var world = app.World;
        Wall(world);
        var decals = world.Resources.Get<Decals>();
        decals.Ceiling = 4;
        var shooter = HitPipelineTests.Body(world, Vector3.Zero, "shooter", new Vector3(0, 0, -10), "rifle");

        var heights = new float[8];
        for (int shot = 0; shot < 8; shot++)
        {
            // Each shot a little higher, so which marks survived can be told apart.
            world.Get<PawnIntent>(shooter).Pitch = shot * 0.01f;
            Fire(app, shooter);
            heights[shot] = decals.Live[decals.Count - 1].Position.Y;
            for (int i = 0; i < 40; i++) world.RunFixed(Dt);   // recovery and the cooldown
        }

        Assert.Equal(4, decals.Count);
        Assert.True(decals.Evicted >= 4);
        Assert.Equal(heights.Skip(4), decals.Live.ToArray().Select(d => d.Position.Y));
    }

    // Done: it survives a sector rebase. The world moves under the mark and the mark moves with it: it
    // is still on the wall's face, not a sector away from it.
    [Xunit.Fact]
    public void AMarkMovesWithTheWorldWhenTheOriginDoes()
    {
        using var app = NewGame();
        var world = app.World;
        var wall = Wall(world);
        var shooter = HitPipelineTests.Body(world, Vector3.Zero, "shooter", new Vector3(0, 0, -10), "rifle");
        var decals = world.Resources.Get<Decals>();
        Fire(app, shooter);
        var before = decals.Live[0].Position - world.Get<Transform>(wall).LocalPosition;

        var offset = world.Rebase(new SectorCoord(1, 0));
        Assert.NotEqual(Vector3.Zero, offset);

        var mark = Assert.Single(decals.Live.ToArray());
        Assert.Equal(before.X, (mark.Position - world.Get<Transform>(wall).LocalPosition).X, 2);
        Assert.Equal(before.Z, (mark.Position - world.Get<Transform>(wall).LocalPosition).Z, 2);
        var space = world.Resources.Get<IPhysicsWorld>();
        var under = space.Raycast(mark.Position + mark.Normal * 0.1f, -mark.Normal, 0.2f);
        Assert.True(under.Hit);
        Assert.Equal(wall, under.Entity);                       // still on the wall
        world.RunFixed(Dt);
        Assert.Equal(1, decals.Count);                          // and lives on in the new frame
    }

    // A mark goes with what it is on: a wall knocked down takes its bullet holes with it.
    [Xunit.Fact]
    public void AMarkGoesWithTheThingItIsOn()
    {
        using var app = NewGame();
        var world = app.World;
        var wall = Wall(world);
        var shooter = HitPipelineTests.Body(world, Vector3.Zero, "shooter", new Vector3(0, 0, -10), "rifle");
        var decals = world.Resources.Get<Decals>();
        Fire(app, shooter);
        Assert.Equal(1, decals.Count);

        world.Destroy(wall);
        world.RunFixed(Dt);
        world.RunFixed(Dt);
        Assert.Equal(0, decals.Count);
    }

    // Only things that stay put hold a mark: a crate physics throws about does not.
    [Xunit.Fact]
    public void NothingThatMovesHoldsAMark()
    {
        using var app = NewGame();
        var world = app.World;
        var crate = world.Create(Transform.At(new Vector3(0, 1.5f, -10)), "crate");
        var collider = Collider.Box(new Vector3(6, 3, 0.4f));
        collider.Surface = new RecordId("sage", "plaster");
        world.Add(crate, collider);
        world.Add(crate, RigidBody.Kinematic());
        var shooter = HitPipelineTests.Body(world, Vector3.Zero, "shooter", new Vector3(0, 0, -10), "rifle");
        var cues = new EventProbe<CueTriggered>(world);

        Fire(app, shooter);

        Assert.Single(cues.All, c => c.Cue == new RecordId("sage", "impact_plaster"));   // it was struck
        Assert.Equal(0, world.Resources.Get<Decals>().Count);                            // and keeps no mark
    }

    // A cue with no surface (a burst in the air) lays its mark on what is below it, within reach.
    [Xunit.Fact]
    public void ACueWithNoSurfaceMarksTheGroundBelow()
    {
        using var app = NewGame();
        var world = app.World;
        world.RunFixed(Dt);
        world.Events.Send(new CueTriggered(new RecordId("sage", "impact_plaster"), default, new Vector3(3, 1.5f, 3)));
        world.RunFixed(Dt);
        var mark = Assert.Single(world.Resources.Get<Decals>().Live.ToArray());
        Assert.Equal(0f, mark.Position.Y, 2);
        Assert.Equal(1f, mark.Normal.Y, 3);
    }

    // A world with no pool (a headless server) places nothing, and shooting still works.
    [Xunit.Fact]
    public void AWorldWithNoPoolPlacesNothing()
    {
        using var app = NewGame(pool: false);
        var world = app.World;
        Wall(world);
        var shooter = HitPipelineTests.Body(world, Vector3.Zero, "shooter", new Vector3(0, 0, -10), "rifle");
        var cues = new EventProbe<CueTriggered>(world);
        Fire(app, shooter);
        Assert.Single(cues.All);
        Assert.False(world.Resources.TryGet<Decals>(out _));
    }

    // Bad decal data is a load error at its line, not a mark that silently never shows.
    [Xunit.Theory]
    [Xunit.InlineData("""{ "type": "decal", "id": "bad", "size": 0.2 }""", "needs a texture")]
    [Xunit.InlineData("""{ "type": "decal", "id": "bad", "texture": "textures/particle.png", "size": 0 }""", "size must be above 0")]
    [Xunit.InlineData("""{ "type": "decal", "id": "bad", "texture": "textures/particle.png", "lifetime": -1, "fade": 0 }""", "lifetime must be 0")]
    [Xunit.InlineData("""{ "type": "decal", "id": "bad", "texture": "textures/particle.png", "lifetime": 2, "fade": 5 }""", "longer than the lifetime")]
    [Xunit.InlineData("""{ "type": "decal", "id": "bad", "texture": "textures/particle.png", "reach": -2 }""", "reach must be 0 or more")]
    public void BadDecalDataIsALoadError(string record, string message)
    {
        using var app = HeadlessApp.Gameplay()
            .OnRegistered(a => a.Records.Register<DecalRecord>())
            .File("data/decals.json", "[" + record + "]")
            .Build();
        Assert.True(app.Records.ErrorCount > 0);
        Assert.Contains(app.Records.LoadErrors, e => e.Contains("decals.json") && e.Contains(message));
    }
}
