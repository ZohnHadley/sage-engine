#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Footsteps from the surface underfoot (issue #327, docs/design/10 "As built (surfaces)" and 11 §12): a
// step, a landing and a jump each raise the cue of the physics_material under the foot — its `footstep`,
// `land` or `jump` — falling back to the engine's `sage:default` for ground with no surface or a surface
// with no cue; a skeletal walker steps on its clips' `foot_left`/`foot_right` events under the foot that foot
// IK placed, and a character steps by the distance it walks. The Sandbox wires both with data only.
public class FootstepTests
{
    public FootstepTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    private const string Records = """
        [
          { "type": "physics_material", "id": "sage:default", "patch": true, "footstep": "step_default" },
          { "type": "physics_material", "id": "grass", "footstep": "step_grass" },
          { "type": "physics_material", "id": "wood", "footstep": "step_wood", "land": "land_wood", "jump": "jump_wood" },
          { "type": "physics_material", "id": "stone" },
          { "type": "cue", "id": "step_default" },
          { "type": "cue", "id": "step_grass" },
          { "type": "cue", "id": "step_wood" },
          { "type": "cue", "id": "land_wood" },
          { "type": "cue", "id": "jump_wood" }
        ]
        """;

    private static RecordId Id(string name) => new("game", name);

    private static HeadlessApp NewApp()
    {
        var fixture = new MountFixture();
        fixture.Write("game", "data/footsteps.json", Records);
        fixture.Mount("game", "game");
        var app = HeadlessApp.Gameplay().WithEngineContent().Mount(fixture).Build();
        Assert.Equal(0, app.Records.ErrorCount);
        return app;
    }

    // A 4 m square slab, its top at y = 0, made of `surface` (none when empty).
    private static void Slab(World world, Vector3 centre, string surface)
    {
        var slab = world.Create(Transform.At(centre + new Vector3(0, -0.5f, 0)), "slab " + surface);
        var collider = Collider.Box(new Vector3(4, 1, 4));
        if (surface.Length > 0) collider.Surface = Id(surface);
        world.Add(slab, collider);
    }

    private sealed class Heard
    {
        private readonly EventReader<CueTriggered> _cues;
        private readonly EventReader<Footstep> _steps;
        public readonly List<CueTriggered> Cues = new();
        public readonly List<Footstep> Steps = new();

        public Heard(World world)
        {
            _cues = world.Events.Reader<CueTriggered>(this);
            _steps = world.Events.Reader<Footstep>(this);
        }

        public void Read()
        {
            foreach (ref readonly var cue in _cues.Read()) Cues.Add(cue);
            foreach (ref readonly var step in _steps.Read()) Steps.Add(step);
        }

        public void Clear() { Cues.Clear(); Steps.Clear(); }
    }

    // Done-when: the cue per surface. A footstep event over grass raises step_grass, over wood step_wood;
    // over a slab no one gave a surface, and over stone (a surface with no footstep cue), it raises the
    // patched sage:default's step_default; and the Footstep event says what was really there.
    [Fact]
    public void EachSurfaceRaisesItsOwnCue_AndAnythingElseTheDefaults()
    {
        using var app = NewApp();
        var world = app.CreateWorld("surfaces");
        Slab(world, new Vector3(0, 0, 0), "grass");
        Slab(world, new Vector3(10, 0, 0), "wood");
        Slab(world, new Vector3(20, 0, 0), "");
        Slab(world, new Vector3(30, 0, 0), "stone");
        var walker = world.Create(Transform.At(Vector3.Zero), "walker");
        world.Add(walker, new Footsteps { Reach = 0.4f, Event = "footstep" });
        world.RunFixed(Dt);
        var heard = new Heard(world);

        foreach (var (x, surface, cue) in new[] { (0f, "grass", "step_grass"), (10f, "wood", "step_wood"), (20f, "", "step_default"), (30f, "stone", "step_default") })
        {
            heard.Clear();
            world.Get<Transform>(walker).LocalPosition = new Vector3(x, 0, 0);
            world.Events.Send(new AnimationEvent(walker, "footstep"));
            world.RunFixed(Dt);
            heard.Read();
            var step = Assert.Single(heard.Steps);
            Assert.Equal((walker, surface.Length == 0 ? default : Id(surface), FootstepKind.Step), (step.Entity, step.Surface, step.Kind));
            Assert.Equal(0f, step.Point.Y, 3);
            var raised = Assert.Single(heard.Cues);
            Assert.Equal((cue == "step_default" ? Id("step_default") : Id(cue), walker), (raised.Cue, raised.Source));
        }

        // Nothing underfoot at all: the step is still taken, with the default's noise, where the feet are.
        heard.Clear();
        world.Get<Transform>(walker).LocalPosition = new Vector3(50, 0, 0);
        world.Events.Send(new AnimationEvent(walker, "footstep"));
        world.RunFixed(Dt);
        heard.Read();
        Assert.True(Assert.Single(heard.Steps).Surface.IsEmpty);
        Assert.Equal(Id("step_default"), Assert.Single(heard.Cues).Cue);
    }

    // A character steps by the ground it covers: a stride of 1 m walked over grass is a step a metre, none
    // standing still; jumping off wood raises its jump cue, and coming down its land cue — and on grass,
    // which names neither, its footstep.
    [Fact]
    public void ACharacterStepsByDistance_AndJumpsAndLandsOnItsSurface()
    {
        using var app = NewApp();
        var world = app.CreateWorld("walk");
        var floor = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "grass");
        var grass = Collider.Box(new Vector3(40, 1, 40));
        grass.Surface = Id("grass");
        world.Add(floor, grass);
        Slab(world, new Vector3(0, 0, 30), "wood");
        var physics = world.Resources.Get<IPhysicsWorld>();
        world.RunFixed(Dt);

        var walker = world.Create(Transform.At(new Vector3(0, 0.05f, 0)), "walker");
        world.AddCharacter(walker, physics.Layers.Player);
        world.Add(walker, new Footsteps { Stride = 1f, Reach = 0.4f, LandSpeed = 2.5f });
        var heard = new Heard(world);
        var jump = app.Engine.Actions.Get(CharacterConventions.Of(world).JumpAction);
        void Tick(int ticks, Vector2 move, bool jumping = false)
        {
            for (int i = 0; i < ticks; i++)
            {
                ref var intent = ref world.Get<PawnIntent>(walker);
                intent.Move = move;
                intent.Pressed = jumping && i == 0 ? default(ActionMask).With(jump) : default;
                world.RunFixed(Dt);
                heard.Read();
            }
        }

        Tick(30, Vector2.Zero);   // settles: standing still is no step
        Assert.Empty(heard.Steps);

        var start = world.Get<Transform>(walker).LocalPosition;
        Tick(120, new Vector2(0, 1));
        var walked = world.Get<Transform>(walker).LocalPosition - start;
        float metres = MathF.Sqrt(walked.X * walked.X + walked.Z * walked.Z);
        Assert.True(metres > 3f, $"walked {metres} m");
        Assert.InRange(heard.Steps.Count, (int)metres - 1, (int)metres);
        Assert.All(heard.Steps, s => Assert.Equal((Id("grass"), FootstepKind.Step), (s.Surface, s.Kind)));
        Assert.All(heard.Cues, c => Assert.Equal(Id("step_grass"), c.Cue));
        Assert.Equal(heard.Steps.Count, heard.Cues.Count);

        // A jump on grass: the push off and the landing are grass's footstep (it names no land or jump).
        Tick(20, Vector2.Zero);
        heard.Clear();
        Tick(90, Vector2.Zero, jumping: true);
        Assert.Equal(new[] { FootstepKind.Jump, FootstepKind.Land }, heard.Steps.Select(s => s.Kind));
        Assert.All(heard.Cues, c => Assert.Equal(Id("step_grass"), c.Cue));

        // And on wood: its own jump and land cues.
        world.Get<Transform>(walker).LocalPosition = new Vector3(0, 0.05f, 30);
        Tick(30, Vector2.Zero);
        heard.Clear();
        Tick(90, Vector2.Zero, jumping: true);
        Assert.Equal(new[] { (FootstepKind.Jump, Id("wood")), (FootstepKind.Land, Id("wood")) }, heard.Steps.Select(s => (s.Kind, s.Surface)));
        Assert.Equal(new[] { Id("jump_wood"), Id("land_wood") }, heard.Cues.Select(c => c.Cue));
        Assert.Equal(Id("wood"), world.Get<Footsteps>(walker).LastSurface);
    }

    // The Sandbox (issue #327's done-when): the hut's skinned walker paces the planks and the player walks
    // the hills, and both make the right noise with no C# — the walker on its walk clip's foot events,
    // under each foot in turn, the player by the metres it walks; and the player's jump and landing too.
    [Fact]
    public void TheSandboxsWalkerAndPlayerStepOnTheirOwnSurfaces()
    {
        using var app = SandboxScreensTests.Boot();
        Assert.Equal(0, app.Records.ErrorCount);
        var world = app.World;
        for (int i = 0; i < 3; i++) world.RunFixed(Dt);   // the hut's map is in
        var walker = world.FindByName("hut_walker");
        Assert.False(walker.IsNull, "no walker in the hut");
        Assert.True(world.Has<Animator>(walker) && world.Has<Footsteps>(walker));
        var player = SandboxScreensTests.Player(world);
        var heard = new Heard(world);
        var planks = new RecordId("sandbox", "planks");

        for (int tick = 0; tick < 60 * 6; tick++)
        {
            world.RunFixed(Dt);
            world.RunFrame(Dt, 1f);
            heard.Read();
        }
        var walked = heard.Steps.Where(s => s.Entity == walker).ToList();
        // Six seconds: a 6 m leg at a walk (a step every 0.75 m, each half second), a half turn, and most of
        // the way back.
        Assert.True(walked.Count is >= 8 and <= 14, $"{walked.Count} steps");
        Assert.All(walked, s => Assert.Equal((planks, FootstepKind.Step), (s.Surface, s.Kind)));
        Assert.All(heard.Cues.Where(c => c.Source == walker), c => Assert.Equal(new RecordId("sandbox", "step_wood"), c.Cue));
        Assert.Equal(walked.Count, heard.Cues.Count(c => c.Source == walker));
        // Each under its own foot: the lane runs along X, and the ankles are 0.2 m apart across it (less as
        // the legs swing in).
        var lane = world.Get<Transform>(walker).LocalPosition.Z;
        Assert.Contains(walked, s => s.Point.Z < lane - 0.05f);
        Assert.Contains(walked, s => s.Point.Z > lane + 0.05f);

        // The player, on the hills (no surface: sage:default, which the Sandbox says is grass).
        heard.Clear();
        var jump = app.Engine.Actions.Get(CharacterConventions.Of(world).JumpAction);
        var input = world.Resources.Get<PlayerInput>();
        for (int tick = 0; tick < 150; tick++)
        {
            // West along the clearing (a view yaw of a quarter turn faces -X), then a stand and a jump.
            input.Command = new PlayerCommand
            {
                Tick = world.Tick + 1,
                Move = tick < 90 ? new Vector2(0, 1) : Vector2.Zero,
                ViewYaw = MathF.PI / 2,
                Pressed = tick == 110 ? default(ActionMask).With(jump) : default,
            };
            input.HasCommand = true;
            world.RunFixed(Dt);
            heard.Read();
        }
        var stepped = heard.Steps.Where(s => s.Entity == player).ToList();
        Assert.True(stepped.Count(s => s.Kind == FootstepKind.Step) >= 2, $"{stepped.Count} steps");
        Assert.Contains(stepped, s => s.Kind == FootstepKind.Jump);
        Assert.Contains(stepped, s => s.Kind == FootstepKind.Land);
        var cues = heard.Cues.Where(c => c.Source == player).Select(c => c.Cue.Name).ToList();
        Assert.Contains("step_grass", cues);
        Assert.Contains("jump", cues);
        Assert.Contains("land_grass", cues);
    }
}
