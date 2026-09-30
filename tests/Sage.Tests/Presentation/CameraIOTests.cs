#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Fixed and cinematic cameras driven by entity I/O (issue #80): `CameraOn [hold]` / `CameraOff`,
// `OnCameraOn` / `OnCameraOff`, the `sage:scripted_camera` prefab, the input lock, the frame the cut
// lands on, and a data-only game (tests/games/camera-cut) that wires a trigger to a camera in its scene.
public class CameraIOTests
{
    public CameraIOTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    private static string CameraCutGame => Path.Combine(TestEnv.FolderAbove("tests"), "tests", "games", "camera-cut");

    // Every gameplay plugin (entity I/O among them), and a counter input a test can wire outputs to.
    private static int _ons, _offs;

    private static HeadlessApp App()
    {
        _ons = _offs = 0;
        return HeadlessApp.Gameplay().OnRegistered(app =>
        {
            app.Engine.Inputs.Register("TestCameraOn", static (World w, in IOContext io) => _ons++);
            app.Engine.Inputs.Register("TestCameraOff", static (World w, in IOContext io) => _offs++);
        }).Boot("io");
    }

    // A disabled, high-priority camera wired to the counters, the way the prefab makes one.
    private static Entity ScriptedCameraAt(World world, string name, Vector3 at, ScriptedCamera? scripted = null)
    {
        var e = world.Create(Transform.At(at), name);
        e.Name = name;
        var camera = Camera.Perspective(priority: 100);
        camera.Enabled = false;
        world.Add(e, camera);
        if (scripted is { } s) world.Add(e, s);
        world.Add(e, new IOConnections
        {
            Wires = new[]
            {
                new Connection { Output = "OnCameraOn", Target = "!self", Input = "TestCameraOn" },
                new Connection { Output = "OnCameraOff", Target = "!self", Input = "TestCameraOff" },
            },
        });
        return e;
    }

    private static void Tick(World world, int times = 1)
    {
        for (int i = 0; i < times; i++) world.RunFixed(Dt);
    }

    private static void Step(World world, int times = 1)
    {
        for (int i = 0; i < times; i++)
        {
            world.RunFixed(Dt);
            world.RunFrame(Dt, 1f);
        }
    }

    [Fact]
    public void TheInputsOutputsAndSystemsAreTheEngines_InAGameWithNoPlugins()
    {
        using var bare = HeadlessApp.Bare().Boot("bare");
        var engine = bare.Engine;
        Assert.True(engine.Inputs.Has("CameraOn"));
        Assert.True(engine.Inputs.Has("cameraoff"));               // a mapper types these: case-insensitive
        Assert.True(engine.Outputs.Has("OnCameraOn"));
        Assert.True(engine.Outputs.Has("OnCameraOff"));
        Assert.Equal("sage.core", engine.Registrations.OwnerOf("entity input", "CameraOn"));
        Assert.Equal("sage.core", engine.Registrations.OwnerOf("entity output", "OnCameraOff"));
        Assert.True(engine.Prefabs.TryGet("scripted_camera", out _));

        var countdown = bare.World.Systems.Find(ScriptedCameraSystem.Id)!;
        Assert.Equal(Phase.EntityIO, countdown.Phase);
        Assert.Equal("sage.core", countdown.Owner);
        var locking = bare.World.Systems.Find(CameraInputLockSystem.Id)!;
        Assert.Equal(Phase.Commands, locking.Phase);
        Assert.Equal("sage.core", locking.Owner);
    }

    [Fact]
    public void CameraOnTakesTheScreen_CameraOffGivesItBack_AndTheOutputsFireOnlyOnAChange()
    {
        using var app = App();
        var world = app.World;
        var views = world.Resources.Get<CameraViews>();
        var cam = ScriptedCameraAt(world, "cam", new Vector3(5, 6, 7));
        var io = world.IO();

        Step(world);
        Assert.True(views.Main.FromActiveCamera);

        io.FireInput(cam, "CameraOn");
        Step(world);
        Assert.True(world.Get<Camera>(cam).Enabled);
        Assert.Equal(cam, views.Main.Entity);
        Assert.Equal(new Vector3(5, 6, 7), views.Main.Position);

        io.FireInput(cam, "CameraOn");                             // already on: nothing changes
        Step(world, 2);                                            // outputs arrive a tick after their input
        Assert.Equal(1, _ons);
        Assert.Equal(0, _offs);

        io.FireInput(cam, "CameraOff");
        Step(world);
        Assert.False(world.Get<Camera>(cam).Enabled);
        Assert.True(views.Main.FromActiveCamera);                  // back to ActiveCamera: no camera entity left

        io.FireInput(cam, "CameraOff");
        Step(world, 2);
        Assert.Equal(1, _ons);
        Assert.Equal(1, _offs);
    }

    // The precise frame (decision): entity I/O is delivered in the EntityIO phase of a fixed tick and the
    // director runs in FrameUpdate, so a CameraOn delivered in tick N is on screen in the first frame
    // after tick N — the host frame that ran tick N, since a frame runs its ticks first. Nothing is on
    // screen before the tick delivers it.
    [Fact]
    public void TheCutLandsOnTheFrameAfterTheTickThatDeliveredIt()
    {
        using var app = App();
        var world = app.World;
        var views = world.Resources.Get<CameraViews>();
        var cam = ScriptedCameraAt(world, "cam", new Vector3(1, 2, 3));

        world.IO().FireInput(cam, "CameraOn");
        world.RunFrame(Dt, 1f);                                    // queued, not delivered: no cut yet
        Assert.True(views.Main.FromActiveCamera);

        world.RunFixed(Dt);                                        // delivered in this tick's EntityIO phase
        Assert.True(world.Get<Camera>(cam).Enabled);
        world.RunFrame(Dt, 0f);                                    // and on screen in the next frame
        Assert.Equal(cam, views.Main.Entity);
    }

    [Fact]
    public void AHoldTurnsTheCameraOffOnTheTickADelayedCameraOffWouldArrive()
    {
        using var app = App();
        var world = app.World;
        var held = ScriptedCameraAt(world, "held", Vector3.Zero);
        var wired = ScriptedCameraAt(world, "wired", Vector3.One);

        // Both delivered in the same tick: one with a hold of a second, one with a CameraOff a second
        // behind it (queued from inside that tick's dispatch, as an input's own wiring would be).
        world.IO().FireInput(held, "CameraOn", "1");
        world.IO().FireInput(wired, "CameraOn");
        Tick(world);
        world.IO().FireInput(wired, "CameraOff", delay: 1f);       // after the dispatch: measured from this tick
        Assert.True(world.Get<Camera>(held).Enabled && world.Get<Camera>(wired).Enabled);

        int ticks = 0;
        while (world.Get<Camera>(held).Enabled && ticks < 200) { Tick(world); ticks++; }
        Assert.Equal(60, ticks);                                   // a second at 60 Hz, to the tick
        Assert.False(world.Get<Camera>(wired).Enabled);            // and the wired one went on the same tick
        Assert.Equal(0f, world.Get<ScriptedCamera>(held).Remaining);
    }

    [Fact]
    public void TheHoldComesFromTheWire_ElseTheEntity_AndCameraOffOrARestartCancelsIt()
    {
        using var app = App();
        var world = app.World;
        var io = world.IO();
        var cam = ScriptedCameraAt(world, "cam", Vector3.Zero, new ScriptedCamera { HoldTime = 0.5f });

        io.FireInput(cam, "CameraOn");                             // no parameter: the entity's half second
        Tick(world);
        Assert.Equal(0.5f, world.Get<ScriptedCamera>(cam).Remaining);
        Tick(world, 30);
        Assert.False(world.Get<Camera>(cam).Enabled);

        io.FireInput(cam, "CameraOn", "0");                        // 0: until CameraOff, whatever the entity says
        Tick(world, 120);
        Assert.True(world.Get<Camera>(cam).Enabled);

        io.FireInput(cam, "CameraOn", "1");                        // on already: the hold (re)starts
        Tick(world, 30);
        io.FireInput(cam, "CameraOn", "1");                        // and restarts again
        Tick(world, 45);
        Assert.True(world.Get<Camera>(cam).Enabled);               // 75 ticks since the first, 45 since the last

        io.FireInput(cam, "CameraOff");                            // an early CameraOff ends the hold …
        Tick(world);
        io.FireInput(cam, "CameraOn", "");                         // … so a new cut is not cut short by it
        Tick(world);                                               // delivered; counted from the next tick
        Tick(world, 29);
        Assert.True(world.Get<Camera>(cam).Enabled);
        Tick(world);
        Assert.False(world.Get<Camera>(cam).Enabled);              // the entity's own 0.5 s again
    }

    [Fact]
    public void AHoldOnACameraWithoutTheCompanionGivesItOne_AndABadParameterFallsBack()
    {
        using var app = App();
        var world = app.World;
        var plain = world.Create(Transform.At(Vector3.Zero), "plain");
        var camera = Camera.Perspective();
        camera.Enabled = false;
        world.Add(plain, camera);

        world.IO().FireInput(plain, "CameraOn", "0.25");
        Tick(world);
        Assert.True(world.Has<ScriptedCamera>(plain));
        Tick(world, 15);
        Assert.False(world.Get<Camera>(plain).Enabled);

        world.IO().FireInput(plain, "CameraOn", "soon");           // not a number: warned, the entity's hold (0)
        Tick(world, 120);
        Assert.True(world.Get<Camera>(plain).Enabled);

        var nothing = world.Create(Transform.At(Vector3.Zero), "no camera");
        world.IO().FireInput(nothing, "CameraOn");                 // warned, and nothing breaks
        Tick(world);
        Assert.False(world.Has<Camera>(nothing));
    }

    // The input lock: while a LockInput camera is on and draws to the screen, the player's intent is
    // held — no movement, no buttons, the view where it was — and the host is asked to hold the view too.
    [Fact]
    public void ALockingCameraHoldsThePlayersIntent_AndLetsGoWhenItIsOff()
    {
        using var app = App();
        var world = app.World;
        var input = world.Resources.Get<PlayerInput>();
        var player = world.Create(Transform.At(Vector3.Zero), "player");
        world.Add(player, new PawnIntent());
        player.AddTag<PlayerControlled>();
        var jump = app.Engine.Actions.Get("Jump");

        void Command(float yaw)
        {
            input.Command = new PlayerCommand
            {
                Move = new Vector2(0, 1),
                ViewYaw = yaw,
                Held = new ActionMask().With(jump),
                Pressed = new ActionMask().With(jump),
            };
            input.HasCommand = true;
        }

        Command(0.3f);
        Tick(world);
        Assert.Equal(new Vector2(0, 1), world.Get<PawnIntent>(player).Move);
        Assert.False(input.TryTakeView(out _, out _));

        var locking = ScriptedCameraAt(world, "locking", Vector3.Zero, new ScriptedCamera { LockInput = true });
        world.IO().FireInput(locking, "CameraOn");
        Tick(world);                                               // delivered at the end of this tick
        Command(1.2f);                                             // the view as the lock begins …
        Tick(world);
        Command(2.0f);                                             // … and the mouse moving during the cut
        Tick(world);

        var intent = world.Get<PawnIntent>(player);
        Assert.Equal(Vector2.Zero, intent.Move);
        Assert.True(intent.Held.IsEmpty && intent.Pressed.IsEmpty);
        Assert.Equal(1.2f, intent.Yaw);                            // held where the lock began
        Assert.True(input.TryTakeView(out float yaw, out _));      // and the host asked to hold it too
        Assert.Equal(1.2f, yaw);

        world.IO().FireInput(locking, "CameraOff");
        Tick(world);                                               // delivered after this tick's Commands
        Tick(world);
        Assert.Equal(new Vector2(0, 1), world.Get<PawnIntent>(player).Move);
        Assert.Equal(2.0f, world.Get<PawnIntent>(player).Yaw);

        // Not locking: a camera without the flag, and a locking one that draws somewhere else.
        var free = ScriptedCameraAt(world, "free", Vector3.Zero, new ScriptedCamera { LockInput = false });
        var monitor = ScriptedCameraAt(world, "monitor", Vector3.Zero, new ScriptedCamera { LockInput = true });
        world.Get<Camera>(monitor).Target = "security_monitor";
        world.IO().FireInput(free, "CameraOn");
        world.IO().FireInput(monitor, "CameraOn");
        Tick(world, 2);
        Assert.Equal(new Vector2(0, 1), world.Get<PawnIntent>(player).Move);
    }

    [Fact]
    public void TheEnginePrefabIsAnOffHighPriorityCameraThatLocksInput()
    {
        using var app = HeadlessApp.Bare().WithEngineContent().Boot("prefab");
        var world = app.World;
        var cam = world.Spawn(new RecordId("sage", "scripted_camera"), new Vector3(0, 3, 0), 90f);
        Assert.False(cam.IsNull);
        var camera = world.Get<Camera>(cam);
        Assert.False(camera.Enabled);
        Assert.Equal(100, camera.Priority);
        Assert.True(camera.IsScreen);
        Assert.True(world.Get<ScriptedCamera>(cam).LockInput);
        Assert.Equal(0f, world.Get<ScriptedCamera>(cam).HoldTime);
    }

    // A scene placement's `outputs` wire it like a map's `On…` keys, and are checked when content loads.
    [Fact]
    public void AScenePlacementsOutputsAreWiredAndChecked()
    {
        var files = new MountFixture();
        files.Write("game", "data/scene.json", """
        [
          { "type": "prefab", "id": "box", "components": { "transform": {} } },
          { "type": "scene", "id": "main",
            "place": [
              { "prefab": "box", "name": "a", "outputs": [
                  { "output": "OnUse", "target": "b", "input": "CameraOn", "parameter": "2", "delay": 0.5, "times": 1 },
                  { "output": "OnSomethingNobodyFires", "target": "b", "input": "Kill" } ] },
              { "prefab": "box", "name": "b" },
              { "prefab": "box", "name": "c", "outputs": [ { "output": "OnUse", "target": "b", "input": "Explode" } ] }
            ] }
        ]
        """);
        files.Mount("game", "game");
        using var app = HeadlessApp.Gameplay().Mount(files).StartScene("game:main").Boot();
        Assert.Equal(1, app.Records.ErrorCount);                   // 'Explode' is not an input
        Assert.True(app.Records.WarningCount >= 1);                // nobody declares OnSomethingNobodyFires

        var world = app.World;
        var a = world.FindByName("a");
        var wires = a.GetComponent<IOConnections>().Wires;
        Assert.Equal(2, wires.Length);
        Assert.Equal(("OnUse", "b", "CameraOn", "2", 0.5f, 1),
                     (wires[0].Output, wires[0].Target, wires[0].Input, wires[0].Parameter, wires[0].Delay, wires[0].Times));
        Assert.True(a.HasOutput("OnUse"));

        // Each entity has its own copies: a record is shared, what a wire counts is not.
        var scene = app.Records.Get<SceneRecord>(new RecordId("game", "main"));
        Assert.DoesNotContain(scene.Place[0].Outputs, w => ReferenceEquals(w, wires[0]));
    }

    // A `.map` places the engine's prefab by its full id, and wires it with the same inputs.
    [Fact]
    public void AMapPlacesTheEnginesScriptedCameraByItsQualifiedClassname()
    {
        var fixture = new MountFixture();
        fixture.Write("game", "maps/cut.map", """
            {
            "classname" "worldspawn"
            }
            {
            "classname" "sage:scripted_camera"
            "origin" "0 0 64"
            "targetname" "map_cam"
            }
            {
            "classname" "relay"
            "origin" "0 0 0"
            "targetname" "relay"
            "OnUser1" "map_cam,CameraOn,1"
            }
            """);
        fixture.Write("game", "data/level.json", """
            [
              { "type": "map", "id": "cut", "file": "maps/cut.map" },
              { "type": "prefab", "id": "relay", "components": { "transform": {} } }
            ]
            """);
        fixture.Mount("game", "sandbox");
        using var app = HeadlessApp.Bare()
            .With(new PhysicsModule(), new MapModule(), new EntityIOModule())
            .WithEngineContent()
            .Mount(fixture)
            .Boot("map");
        var world = app.World;
        Assert.NotNull(MapLoader.Load(world, new RecordId("sandbox", "cut")));

        Tick(world);                                               // a level places its entities once it is placed
        var cam = world.FindByName("map_cam");
        Assert.False(cam.IsNull);
        Assert.False(world.Get<Camera>(cam).Enabled);
        world.FireOutput(world.FindByName("relay"), "OnUser1");
        Tick(world);
        Assert.True(world.Get<Camera>(cam).Enabled);
        Tick(world, 60);
        Assert.False(world.Get<Camera>(cam).Enabled);
    }

    // ---- The data-only game (the acceptance test) ----------------------------------------------------

    [Fact]
    public void TheCameraCutGameValidates()
    {
        var report = ContentValidation.Run(new ValidateOptions
        {
            GameDirectory = CameraCutGame,
            EngineContentDirectory = Path.Combine(TestEnv.FolderAbove("engine_content"), "engine_content"),
            AvailablePlugins = BasePlugins.All(),
        });
        Assert.True(report.Ok, string.Join("\n", report.Errors));
        Assert.DoesNotContain(report.Warnings, w => w.Contains("scene.json") || w.Contains("cameras.json"));
    }

    // A game with no C#: a trigger's OnStartTouch fires CameraOn at a named camera in its scene, the main
    // view switches on the frame after the tick the trigger was touched in, and a delayed CameraOff (the
    // yard) or a hold time (the gate) gives the screen back to the player's view.
    [Fact]
    public void ATriggerCutsToANamedCameraAndBack_InAGameWithNoCode()
    {
        using var app = HeadlessApp.ForGame(CameraCutGame).WithEngineContent().Boot();
        var world = app.World;
        Assert.Null(app.Engine.Modules.Game);
        var views = world.Resources.Get<CameraViews>();
        var space = world.Resources.Get<IPhysicsWorld>();
        var yardCam = world.FindByName("yard_cam");
        var introCam = world.FindByName("intro_cam");
        var yard = world.FindByName("yard");
        var gate = world.FindByName("gate");
        var player = Scenes.Player(world);
        Assert.False(yardCam.IsNull || introCam.IsNull || yard.IsNull || gate.IsNull || player.IsNull);

        world.RunFrame(Dt, 1f);
        Assert.Equal(CameraRigKind.FirstPerson, world.MainViewRig());   // the player's camera to begin with (#78)

        // The yard: a crate falls through its trigger. The tick whose physics saw it is the tick the
        // camera comes on, and the frame after that tick shows it.
        int tick = 0;
        bool touched = false;
        while (!touched && tick < 120)
        {
            world.RunFixed(Dt);
            tick++;
            foreach (var overlap in space.TriggerEnter)
                if (overlap.Trigger == yard) touched = true;
            if (!touched) Assert.False(world.Get<Camera>(yardCam).Enabled);
            world.RunFrame(Dt, 1f);
        }
        Assert.True(touched, "the crate never fell through the yard trigger");
        Assert.True(world.Get<Camera>(yardCam).Enabled);
        Assert.Equal(yardCam, views.Main.Entity);
        Assert.Equal(world.Get<GlobalTransform>(yardCam).Interpolated(1f).Position, views.Main.Position);

        // Two seconds later the wired CameraOff gives the screen back — to the player's camera (#78),
        // which the director mirrors into ActiveCamera, in the same frame.
        int held = 0;
        while (world.Get<Camera>(yardCam).Enabled && held < 300) { Step(world); held++; }
        // Exactly 2 s: the trigger's wires count their delay from the tick it fired in (#90's clock; it was
        // 119 ticks when the clock moved only in the dispatch).
        Assert.Equal(120, held);
        Assert.Equal(CameraRigKind.FirstPerson, world.MainViewRig());
        var eye = world.Resources.Get<ActiveCamera>().Position;
        var feet = world.Get<GlobalTransform>(player).Interpolated(1f).Position;
        Assert.True(Vector3.Distance(feet, eye) < 2.5f, $"the view is at {eye}, the player at {feet}");

        // The gate: the player walks in (teleported), and the intro camera holds for its three seconds.
        world.Teleport(player, Transform.At(new Vector3(0, 0.1f, 0)));
        int waited = 0;
        while (!world.Get<Camera>(introCam).Enabled && waited < 30) { Step(world); waited++; }
        Assert.True(world.Get<Camera>(introCam).Enabled, "walking into the gate did not cut to the intro camera");
        Assert.Equal(introCam, views.Main.Entity);

        Step(world);                                               // the camera's own OnCameraOn said so
        Assert.Contains(Said(world), m => m == "The gate creaks open.");
        Assert.True(world.Resources.Get<PlayerInput>().TryTakeView(out _, out _));   // and the player is held

        int hold = 1;
        while (world.Get<Camera>(introCam).Enabled && hold < 400) { Step(world); hold++; }
        Assert.Equal(180, hold);                                   // three seconds, to the tick
        Assert.Equal(CameraRigKind.FirstPerson, world.MainViewRig());
        Step(world);
        Assert.Contains(Said(world), m => m == "Back to you.");
    }

    // Phase 4a's exit criterion in the Sandbox itself (#81): walking up the path to the hut's door cuts
    // to the hut camera for three seconds and back to the player's view, once. The path's trigger is on a
    // layer only the player touches, so the creatures wandering the scene never fire it.
    [Fact]
    public void TheSandboxCutsToTheHut_WhenThePlayerWalksUpThePath_AndBack()
    {
        using var app = HeadlessApp.ForGame(SandboxGame, new global::Sandbox.SandboxModule()).WithEngineContent().Boot();
        var world = app.World;
        var views = world.Resources.Get<CameraViews>();
        var hutCam = world.FindByName("hut_cam");
        var path = world.FindByName("hut path");
        var player = Scenes.Player(world);
        Assert.False(hutCam.IsNull || path.IsNull || player.IsNull);
        Assert.False(world.Get<Camera>(hutCam).Enabled);

        // Two seconds of the scene as it starts — creatures wander, crates fall — and no cut.
        Step(world, 120);
        Assert.False(world.Get<Camera>(hutCam).Enabled);
        Assert.Equal(CameraRigKind.FirstPerson, world.MainViewRig());

        // The player walks onto the path (teleported to it): the cut lands within a few ticks.
        world.Teleport(player, Transform.At(world.Get<GlobalTransform>(path).Current.Position));
        int waited = 0;
        while (!world.Get<Camera>(hutCam).Enabled && waited < 30) { Step(world); waited++; }
        Assert.True(world.Get<Camera>(hutCam).Enabled, "walking up the path did not cut to the hut camera");
        Assert.Equal(hutCam, views.Main.Entity);
        Assert.Equal(CameraRigKind.None, world.MainViewRig());
        Step(world);                                              // its OnCameraOn says what it shows
        Assert.Contains(Said(world), m => m.StartsWith("A hut on the hill", StringComparison.Ordinal));

        int hold = 1;
        while (world.Get<Camera>(hutCam).Enabled && hold < 400) { Step(world); hold++; }
        Assert.Equal(180, hold);                                  // three seconds, to the tick
        Assert.Equal(CameraRigKind.FirstPerson, world.MainViewRig());

        // Once: walking off the path and back on does not cut again.
        world.Teleport(player, Transform.At(world.Get<GlobalTransform>(path).Current.Position + new Vector3(0, 0, 8)));
        Step(world, 10);
        world.Teleport(player, Transform.At(world.Get<GlobalTransform>(path).Current.Position));
        Step(world, 30);
        Assert.False(world.Get<Camera>(hutCam).Enabled);
    }

    private static string SandboxGame => Path.Combine(TestEnv.FolderAbove("Sage.sln"), "games", "Sandbox");

    private static string[] Said(World world) => world.Messages().Messages.ToArray().Select(m => m.Text).ToArray();
}

// Zero allocation per tick for the countdown and the lock, with holds running and the player locked.
[Collection(MeasurementsCollection.Name)]
public class CameraIOAllocationTests
{
    public CameraIOAllocationTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void CountingDownAndLockingAllocateNothing()
    {
        using var app = HeadlessApp.Bare().Boot("alloc");
        var world = app.World;
        world.Resources.Add(new PlayerInput());
        var player = world.Create(Transform.At(Vector3.Zero), "player");
        world.Add(player, new PawnIntent());
        player.AddTag<PlayerControlled>();
        var last = default(Entity);
        for (int i = 0; i < 8; i++)
        {
            var e = world.Create(Transform.At(new Vector3(i, 0, 0)));
            var camera = Camera.Perspective(priority: i);
            world.Add(e, camera);
            world.Add(e, new ScriptedCamera { LockInput = i == 0, Remaining = 1000f });
            last = e;
        }
        for (int i = 0; i < 5; i++) { world.RunFixed(1f / 60f); Profiler.EndFrame(); }   // warm up

        AllocationProbe.AssertNone(200, () => { world.RunFixed(1f / 60f); Profiler.EndFrame(); });
        Assert.True(world.Get<ScriptedCamera>(last).Remaining > 0f);
    }
}
