#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Physics materials and surface types (issue #270, docs/design/10 "As built (surfaces)"): a
// physics_material record given to a collider, a brush texture or a terrain layer, returned in every
// RayHit and SweepHit, feeding the collider's friction, and picked up by a footstep for its cue.
public class SurfaceTests
{
    public SurfaceTests() { _ = TestEnv.UserRoot; }

    private const byte PlayerLayer = 1;
    private const float Dt = 1f / 60f;

    // A 16 m square floor a metre thick, its top at y = 0: grass on top, brick round the sides and a
    // bottom no record names (the map's fallback, plaster). And a crate standing on it at x 4..5, a solid
    // entity whose faces are all "crate_wood".
    private const string Yard = """
        {
        "classname" "worldspawn"
        {
        ( -256 -256 -32 ) ( -256 -255 -32 ) ( -256 -256 -31 ) brick_wall 0 0 0 1 1
        ( -256 -256 -32 ) ( -256 -256 -31 ) ( -255 -256 -32 ) brick_wall 0 0 0 1 1
        ( -256 -256 -32 ) ( -255 -256 -32 ) ( -256 -255 -32 ) underside 0 0 0 1 1
        ( 256 256 0 ) ( 256 257 0 ) ( 257 256 0 ) GRASS_top 0 0 0 1 1
        ( 256 256 0 ) ( 257 256 0 ) ( 256 256 1 ) brick_wall 0 0 0 1 1
        ( 256 256 0 ) ( 256 256 1 ) ( 256 257 0 ) brick_wall 0 0 0 1 1
        }
        }
        {
        "classname" "crate"
        {
        ( 128 -16 0 ) ( 128 -15 0 ) ( 128 -16 1 ) crate_wood 0 0 0 1 1
        ( 128 -16 0 ) ( 128 -16 1 ) ( 129 -16 0 ) crate_wood 0 0 0 1 1
        ( 128 -16 0 ) ( 129 -16 0 ) ( 128 -15 0 ) crate_wood 0 0 0 1 1
        ( 160 16 32 ) ( 160 17 32 ) ( 161 16 32 ) crate_wood 0 0 0 1 1
        ( 160 16 32 ) ( 161 16 32 ) ( 160 16 33 ) crate_wood 0 0 0 1 1
        ( 160 16 32 ) ( 160 16 33 ) ( 160 17 32 ) crate_wood 0 0 0 1 1
        }
        }
        """;

    private const string Records = """
        [
          { "type": "map", "id": "yard", "file": "maps/yard.map", "surface": "plaster" },
          { "type": "physics_material", "id": "grass", "friction": 0.8, "footstep": "step_grass", "textures": ["grass*"] },
          { "type": "physics_material", "id": "brick", "textures": ["brick_wall"] },
          { "type": "physics_material", "id": "wood", "footstep": "step_wood", "textures": ["*wood"] },
          { "type": "physics_material", "id": "plaster" },
          { "type": "physics_material", "id": "dirt", "footstep": "step_dirt" },
          { "type": "physics_material", "id": "ice", "friction": 0.02 },
          { "type": "physics_material", "id": "rough", "friction": 1.5 },
          { "type": "cue", "id": "step_grass" },
          { "type": "cue", "id": "step_wood" },
          { "type": "cue", "id": "step_dirt" }
        ]
        """;

    private static RecordId Id(string name) => new("game", name);

    private static HeadlessApp NewApp()
    {
        var fixture = new MountFixture();
        fixture.Write("game", "maps/yard.map", Yard);
        fixture.Write("game", "data/surfaces.json", Records);
        fixture.Mount("game", "game");
        return HeadlessApp.Gameplay().With(new MapModule()).Mount(fixture).Build();
    }

    private static Pose At(Vector3 position) => new() { Position = position, Rotation = Quaternion.Identity, Scale = Vector3.One };

    [Fact]
    public void ARayOrASweepThatHitsAColliderReportsItsSurface()
    {
        using var app = NewApp();
        var world = app.CreateWorld("surfaces");
        var crate = world.Create(Transform.At(new Vector3(0, 0.5f, 0)), "crate");
        var collider = Collider.Box(Vector3.One);
        collider.Surface = Id("wood");
        world.Add(crate, collider);
        var bare = world.Create(Transform.At(new Vector3(5, 0.5f, 0)), "bare");
        world.Add(bare, Collider.Box(Vector3.One));
        world.RunFixed(Dt);
        var physics = world.Resources.Get<IPhysicsWorld>();

        var ray = physics.Raycast(new Vector3(0, 5, 0), -Vector3.UnitY, 10f);
        Assert.Equal((crate, Id("wood")), (ray.Entity, ray.Surface));
        var sweep = physics.Sweep(Collider.Sphere(0.2f), At(new Vector3(-3, 0.5f, 0)), Vector3.UnitX, 10f);
        Assert.Equal((crate, Id("wood")), (sweep.Entity, sweep.Surface));
        Assert.Equal(Id("wood"), physics.SurfaceOf(world.Get<PhysicsBody>(crate)));
        var all = new RayHit[4];   // RaycastAll (#269) says what each hit is made of too
        Assert.Equal(1, physics.RaycastAll(new Vector3(0, 5, 0), -Vector3.UnitY, 10f, all));
        Assert.Equal((crate, Id("wood")), (all[0].Entity, all[0].Surface));

        // A collider nobody gave a surface reports none.
        var nothing = physics.Raycast(new Vector3(5, 5, 0), -Vector3.UnitY, 10f);
        Assert.Equal(bare, nothing.Entity);
        Assert.True(nothing.Surface.IsEmpty);

        // SetSurface gives one to a collider that already exists.
        physics.SetSurface(world.Get<PhysicsBody>(bare), Id("brick"));
        Assert.Equal(Id("brick"), physics.Raycast(new Vector3(5, 5, 0), -Vector3.UnitY, 10f).Surface);
    }

    // The surface's friction is the collider's, through Bepu's pair material: on a rough floor, a crate
    // made of ice slides further than a rough one when both are shoved. A body's own friction wins over
    // its surface's.
    [Fact]
    public void ASurfacesFrictionIsTheCollidersFriction()
    {
        using var app = NewApp();
        var world = app.CreateWorld("friction");
        var floor = world.Create(Transform.At(new Vector3(0, -0.5f, 5)), "floor");
        var ground = Collider.Box(new Vector3(60, 1, 20));
        ground.Surface = Id("rough");
        world.Add(floor, ground);
        Entity Crate(float z, string surface, float friction = 0f)
        {
            var crate = world.Create(Transform.At(new Vector3(0, 0.5f, z)), "crate");
            var collider = Collider.Box(Vector3.One);
            collider.Surface = Id(surface);
            world.Add(crate, collider);
            world.Add(crate, new RigidBody { Kind = BodyKind.Dynamic, Mass = 10f, Friction = friction });
            return crate;
        }
        var rough = Crate(0, "rough");
        var ice = Crate(5, "ice");
        var gripping = Crate(10, "ice", friction: 1.5f);   // its own friction, not ice's
        world.RunFixed(Dt);

        var physics = world.Resources.Get<IPhysicsWorld>();
        foreach (var crate in new[] { rough, ice, gripping })
            physics.SetVelocity(world.Get<PhysicsBody>(crate), new Vector3(6, 0, 0));
        for (int i = 0; i < 90; i++) world.RunFixed(Dt);

        float roughX = world.Get<Transform>(rough).LocalPosition.X;
        float iceX = world.Get<Transform>(ice).LocalPosition.X;
        float gripX = world.Get<Transform>(gripping).LocalPosition.X;
        Assert.True(iceX > roughX + 2f, $"ice {iceX:F2} m, rough {roughX:F2} m");
        Assert.True(MathF.Abs(gripX - roughX) < 0.2f, $"rough {roughX:F2} m, ice with its own friction {gripX:F2} m");
    }

    // A brush hull's faces each have their texture's surface: the grass on top, the brick round the
    // sides, the map's fallback underneath; a solid entity's hull the same.
    [Fact]
    public void ABrushFaceIsTheSurfaceItsTextureNames()
    {
        using var app = NewApp();
        var world = app.CreateWorld("yard");
        Assert.NotNull(MapLoader.Load(world, Id("yard")));
        world.RunFixed(Dt);
        var physics = world.Resources.Get<IPhysicsWorld>();

        Assert.Equal(Id("grass"), physics.Raycast(new Vector3(0, 3, 0), -Vector3.UnitY, 10f).Surface);   // "GRASS_top": case is ignored
        Assert.Equal(Id("brick"), physics.Raycast(new Vector3(-12, -0.5f, 0), Vector3.UnitX, 20f).Surface);
        Assert.Equal(Id("brick"), physics.Raycast(new Vector3(0, -0.5f, 12), -Vector3.UnitZ, 20f).Surface);
        Assert.Equal(Id("plaster"), physics.Raycast(new Vector3(0, -5, 0), Vector3.UnitY, 10f).Surface);   // no record names "underside"

        // The crate (x 4..5): its side, shot at, and its top, swept onto.
        Assert.Equal(Id("wood"), physics.Raycast(new Vector3(8, 0.5f, 0), -Vector3.UnitX, 10f).Surface);
        var sweep = physics.Sweep(Collider.Sphere(0.2f), At(new Vector3(4.5f, 3, 0)), -Vector3.UnitY, 5f);
        Assert.Equal(Id("wood"), sweep.Surface);

        // What a floor stands on is its top: the hull's own surface, and its friction.
        var floor = physics.Raycast(new Vector3(0, 3, -3), -Vector3.UnitY, 10f).Entity;
        Assert.Equal(Id("grass"), physics.SurfaceOf(world.Get<PhysicsBody>(floor)));
    }

    // Terrain layers: each cell's layer names a surface (Terrain.SurfaceLayers), per triangle of the
    // collision mesh, for rays and for sweeps alike.
    [Fact]
    public void ATerrainLayerIsTheSurfaceOfTheGroundItCovers()
    {
        using var app = NewApp();
        var world = app.CreateWorld("terrain");
        var terrain = new Terrain { Origin = world.Origin(), Generator = new HalfGrass() };
        terrain.SurfaceLayers.AddRange(new[] { Id("dirt"), Id("grass") });
        world.Resources.Add(terrain);
        terrain.Load(SectorCoord.Zero);
        world.RunFixed(Dt);
        var physics = world.Resources.Get<IPhysicsWorld>();

        Assert.Equal(Id("dirt"), physics.Raycast(new Vector3(100, 5, 300), -Vector3.UnitY, 10f).Surface);
        Assert.Equal(Id("grass"), physics.Raycast(new Vector3(900, 5, 300), -Vector3.UnitY, 10f).Surface);
        Assert.Equal(Id("grass"), physics.Sweep(Collider.Sphere(0.3f), At(new Vector3(900, 5, 300)), -Vector3.UnitY, 10f).Surface);
        Assert.Equal(Id("dirt"), physics.Sweep(Collider.Sphere(0.3f), At(new Vector3(100, 5, 300)), -Vector3.UnitY, 10f).Surface);
    }

    // Done when (issue #270): a step looks at what is underfoot and raises that surface's footstep cue,
    // the cue the client plays. A stride walked is a step, and so is the animation's `footstep` event.
    [Fact]
    public void AFootstepPicksItsCueFromTheSurfaceUnderfoot()
    {
        using var app = NewApp();
        var world = app.CreateWorld("walk");
        MapLoader.Load(world, Id("yard"));
        world.RunFixed(Dt);

        var walker = world.Create(Transform.At(new Vector3(-4, 0.05f, 0)), "walker");
        world.AddCharacter(walker, PlayerLayer);
        world.Add(walker, new Footsteps { Stride = 1f, Reach = 0.4f });
        var cues = world.Events.Reader<CueTriggered>("test");
        var steps = world.Events.Reader<Footstep>("test");
        var heard = new List<CueTriggered>();
        var taken = new List<Footstep>();
        void Tick(int ticks, Vector2 move)
        {
            for (int i = 0; i < ticks; i++)
            {
                world.Get<PawnIntent>(walker).Move = move;
                world.RunFixed(Dt);
                foreach (ref readonly var cue in cues.Read()) heard.Add(cue);
                foreach (ref readonly var step in steps.Read()) taken.Add(step);
            }
        }

        Tick(20, Vector2.Zero);   // settles onto the grass: standing still is no step
        Assert.Empty(taken);

        Tick(45, new Vector2(0, 1));   // a few metres
        Assert.True(taken.Count >= 2, $"{taken.Count} steps");
        Assert.All(taken, step => Assert.Equal((walker, Id("grass")), (step.Entity, step.Surface)));
        Assert.All(taken, step => Assert.InRange(step.Point.Y, -0.01f, 0.01f));   // where the foot met the ground
        Assert.Equal(taken.Count, heard.Count);
        Assert.All(heard, cue => Assert.Equal((Id("step_grass"), walker), (cue.Cue, cue.Source)));
        Assert.Equal(Id("grass"), world.Get<Footsteps>(walker).LastSurface);

        // An animation's footstep event is a step too, standing still.
        taken.Clear();
        heard.Clear();
        Tick(10, Vector2.Zero);
        world.Events.Send(new AnimationEvent(walker, "footstep"));
        world.Events.Send(new AnimationEvent(walker, "hit"));   // another event is not
        Tick(1, Vector2.Zero);
        Assert.Single(taken);
        Assert.Equal(Id("step_grass"), Assert.Single(heard).Cue);
    }

    // The texture table: an exact name wins, then the longest pattern, case ignored.
    [Theory]
    [InlineData("wood*", "wood_plank", true)]
    [InlineData("*wood", "crate_WOOD", true)]
    [InlineData("w*d", "wood", true)]
    [InlineData("wood*", "oak_wood", false)]
    [InlineData("*", "anything", true)]
    [InlineData("metal", "metal2", false)]
    public void ATexturePatternMatchesWithStars(string pattern, string texture, bool matches) =>
        Assert.Equal(matches, SurfaceTextures.Matches(pattern, texture));

    // Flat ground, the east half (x >= 512 m) painted layer 1.
    private sealed class HalfGrass : ITerrainGenerator
    {
        public void Generate(SectorCoord sector, Heightfield heights, int seed)
        {
            for (int z = 0; z < heights.Cells; z++)
                for (int x = heights.Cells / 2; x < heights.Cells; x++)
                    heights.SetLayer(x, z, 1);
        }
    }
}
