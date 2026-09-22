using System;
using System.Diagnostics;
using Friflo.Engine.ECS;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using MonoGame.ImGuiNet;

namespace sage_engine;

// The host's MonoGame Game: today it is both game and editor (docs/design/01 §10). It owns the
// client-side services as plain instances (no singletons). The game itself (games/Sandbox) is a module
// loaded from game.json; the host provides graphics to modules, loads records, starts modules and
// creates the main World here, once the graphics device exists (01 §5.1).
//
// Main loop (01 §5.2): MonoGame's fixed-step mode is off; each frame the host polls the input devices,
// resolves input contexts and actions (08), latches them into the pending PlayerCommand, runs 0..N
// fixed ticks through FixedStepClock (sim_tickrate), each with its own command, then the Frame
// schedule (Extract → Render, 06) with the interpolation alpha. MonoGame's Update and Draw are one
// pair per frame.
public class Game1 : Game
{
    private readonly Engine engine;
    private readonly Action applyConfig;
    private readonly Action runLaunchCommands;
    private World world;
    private RecordHotReload recordHotReload;
    private CVar<bool> recHotReload;

    private readonly GraphicsDeviceManager graphics;
    private GraphicsDevice graphicsDevice;
    private ImGuiRenderer guiRenderer;
    private EditorManager editorManager;
    private HostCVars hostCVars;
    private InputDevices devices;
    private InputActions actions;
    private readonly CommandLatch latch = new CommandLatch();
    private ActionId moveAction, lookAction, menuAction, toggleConsoleAction;
    private PlayerInput playerInput;
    private DevCamera cam;
    private ActiveCamera activeCamera;
    private EditorUI editorUI;
    private EntityContextMenuUI entityInspector;
    private DevConsoleWindow console;
    private StatOverlay stats;

    private readonly FixedStepClock clock = new FixedStepClock();
    private readonly Stopwatch frameClock = new Stopwatch();
    private FixedStepResult step;
    private long frame;
    private bool screenshotPending;

    internal Game1(Engine engine, Action applyConfig, Action runLaunchCommands)
    {
        this.engine = engine;
        this.applyConfig = applyConfig;
        this.runLaunchCommands = runLaunchCommands;
        graphics = new GraphicsDeviceManager(this);
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        IsFixedTimeStep = false;   // our own accumulator (FixedStepClock) instead of MonoGame's catch-up loop
        graphics.GraphicsProfile = GraphicsProfile.HiDef;   // 32-bit indices, large textures; instancing later (06 §3.7)
    }

    protected override void Initialize()
    {
        editorManager = new EditorManager();
        editorManager.setGraphicsDeviceManager(graphics, 800, 410);
        graphicsDevice = graphics.GraphicsDevice;

        // Input (08): devices, then actions (their bindings are built when the records load).
        var cvars = engine.CVars;
        devices = new InputDevices();
        actions = new InputActions(engine.Actions, engine.Records, devices, cvars);
        moveAction = engine.Actions.Get("Move");
        lookAction = engine.Actions.Get("Look");
        menuAction = engine.Actions.Get("Menu");
        toggleConsoleAction = engine.Actions.Get("ToggleConsole");

        cam = new DevCamera(devices, actions, new Vector3(0, 0, 0), new Vector3(0, 0, 0));
        cam.Position = new Vector3(0, 0, 1);
        editorManager.setCamera(cam);

        guiRenderer = new ImGuiRenderer(this);
        editorUI = new EditorUI();

        // Developer console (`~`), `stat` overlays and loop cvars; see docs/design/02 and 01 §3.2, §5.2.
        hostCVars = new HostCVars(cvars);
        hostCVars.VSync.Changed += _ => ApplyVSync();
        recHotReload = cvars.Register("rec_hotreload", engine.Core.Developer.Value >= 1, CVarFlags.DevOnly,
            "Reload record files (data/**/*.json) when they change on disk.");
        console = new DevConsoleWindow(cvars, engine.Core);
        stats = new StatOverlay(cvars, engine.Core);
        cvars.RegisterCommand("quit", CVarFlags.None, "Exit the game.", _ => Exit());
        cvars.RegisterCommand("screenshot", CVarFlags.None, "Save the next frame as a PNG in the user folder's screenshots/.", _ => screenshotPending = true);
        WorldCommands.Register(cvars, engine);
        CrashReporter.AddSection("GPU", () => $"{GraphicsAdapter.DefaultAdapter.Description}, profile {graphics.GraphicsProfile}");
        Log.Info(LogCat.Render, $"Graphics: {GraphicsAdapter.DefaultAdapter.Description}, {graphics.PreferredBackBufferWidth}x{graphics.PreferredBackBufferHeight}");
        applyConfig();   // config.cfg, now that every cvar and command exists
        ApplyVSync();

        // Records, module Start, then the main world (01 §5.1). Modules install their resources and
        // systems in OnWorldCreated; the game spawns its scene there.
        engine.Records.Load(engine.Vfs);
        if (BuildInfo.IsDevBuild)
            recordHotReload = new RecordHotReload(engine.Records, engine.Vfs);
        engine.Modules.ProvideHostService(new ClientHost(this));
        engine.Modules.ProvideHostService(devices);
        engine.Modules.ProvideHostService(actions);
        engine.Modules.StartAll();
        world = engine.CreateWorld("main");
        activeCamera = world.Resources.Get<ActiveCamera>();
        playerInput = world.Resources.Get<PlayerInput>();
        entityInspector = new EntityContextMenuUI(world);
        runLaunchCommands();   // +args last, with the world up (Program.cs)

        base.Initialize();
    }

    private void ApplyVSync()
    {
        graphics.SynchronizeWithVerticalRetrace = hostCVars.VSync.Value;
        graphics.ApplyChanges();
    }

    protected override void LoadContent()
    {
        guiRenderer.RebuildFontAtlas();
        base.LoadContent();
    }

    // Input, then the simulation: 0..N fixed ticks (01 §5.2).
    protected override void Update(GameTime gameTime)
    {
        Log.SetFrame(++frame);
        float realDt = (float)gameTime.ElapsedGameTime.TotalSeconds;

        // Input (08 §5): devices → contexts (the console's; ImGui's capture from its last frame) → actions.
        devices.Poll();
        var io = ImGui.GetIO();
        actions.SetActive(InputContext.Editor, true);   // the editor host (no Editor map yet: nothing consumed)
        actions.SetActive(InputContext.Console, console.IsOpen);
        actions.UiWantsKeyboard = io.WantCaptureKeyboard;
        actions.UiWantsMouse = io.WantCaptureMouse;
        actions.Update(realDt);

        // Menu closes the console first; otherwise it quits (until there is a menu).
        if (actions.Pressed(toggleConsoleAction)) console.Toggle();
        if (actions.Pressed(menuAction))
        {
            if (console.IsOpen) console.Close();
            else Exit();
        }

        // Everything since the last tick goes into the next PlayerCommand; look applies at frame rate (08 §3.4).
        latch.AddFrame(actions.HeldMask, actions.PressedMask, actions.ReleasedMask, actions.Axis2(moveAction));
        latch.AddLook(actions.Axis2(lookAction));

        // The editor camera runs at the display rate, not the tick rate (smooth at any refresh rate).
        cam.update(gameTime);
        activeCamera.Position = cam.Position.ToNumerics();
        activeCamera.Rotation = cam.Rotation.ToNumerics();
        if (recHotReload.Value) recordHotReload?.Poll();

        step = clock.Advance(realDt, hostCVars.TickRate.Value, hostCVars.MaxFrameTime.Value, hostCVars.TimeScale.Value);
        for (int i = 0; i < step.Ticks; i++)
        {
            playerInput.Command = latch.Sample(world.Tick + 1);   // the tick this command is for
            playerInput.HasCommand = true;
            world.RunFixed(step.TickDt);
        }

        float exitAfter = hostCVars.ExitAfter.Value;
        if (exitAfter > 0 && clock.RealTime >= exitAfter)
        {
            Log.Info(LogCat.Host, $"host_exitafter: {frame} frames, {world.Tick} ticks at sim_tickrate {hostCVars.TickRate.Value} " +
                $"in {clock.RealTime:F2} s ({frame / clock.RealTime:F0} fps, {world.Tick / clock.RealTime:F1} ticks/s)");
            Exit();
        }

        base.Update(gameTime);
    }

    protected override void UnloadContent()
    {
        recordHotReload?.Dispose();
        base.UnloadContent();
    }

    // The Frame schedule (FrameUpdate → Extract → Render → Overlay; the Renderer clears and draws the
    // world's snapshot in Render, 06), then the ImGui layer.
    protected override void Draw(GameTime gameTime)
    {
        world.RunFrame(step.FrameDt, step.Alpha, clock.RealTime);
        base.Draw(gameTime);

        using (Profiler.Begin("Frame.ImGui"))
        {
            guiRenderer.BeginLayout(gameTime);
            editorUI.Draw(this);
            entityInspector.draw();
            console.Draw();
            stats.Draw();
            guiRenderer.EndLayout();
        }

        if (screenshotPending)
        {
            screenshotPending = false;
            SaveScreenshot();
        }

        // Real frame time for the stat overlay.
        float frameSeconds = frameClock.IsRunning ? (float)frameClock.Elapsed.TotalSeconds : 0f;
        frameClock.Restart();
        stats.EndFrame(frameSeconds);
        Profiler.EndFrame();
    }

    // The back buffer as drawn this frame (scene + dev UI), before Present.
    private void SaveScreenshot()
    {
        int w = graphicsDevice.PresentationParameters.BackBufferWidth, h = graphicsDevice.PresentationParameters.BackBufferHeight;
        var pixels = new Color[w * h];
        graphicsDevice.GetBackBufferData(pixels);
        using var texture = new Texture2D(graphicsDevice, w, h);
        texture.SetData(pixels);
        System.IO.Directory.CreateDirectory(UserPaths.Screenshots);
        string file = System.IO.Path.Combine(UserPaths.Screenshots, $"shot-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        using (var stream = System.IO.File.Create(file))
            texture.SaveAsPng(stream, w, h);
        Log.Info(LogCat.Render, $"Screenshot {file}");
    }
}
