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
// Main loop (01 §5.2): MonoGame's fixed-step mode is off; each frame the host polls input, runs
// 0..N fixed ticks through FixedStepClock (sim_tickrate), then the Frame schedule with the
// interpolation alpha. MonoGame's Update and Draw are one pair per frame.
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
    private InputSystem input;
    private DevCamera cam;
    private RenderView renderView;
    private ActiveCamera activeCamera;
    private EditorUI editorUI;
    private EntityContextMenuUI entityInspector;
    private DevConsoleWindow console;
    private StatOverlay stats;

    private readonly FixedStepClock clock = new FixedStepClock();
    private readonly Stopwatch frameClock = new Stopwatch();
    private FixedStepResult step;
    private long frame;

    internal Game1(Engine engine, Action applyConfig, Action runLaunchCommands)
    {
        this.engine = engine;
        this.applyConfig = applyConfig;
        this.runLaunchCommands = runLaunchCommands;
        graphics = new GraphicsDeviceManager(this);
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        IsFixedTimeStep = false;   // our own accumulator (FixedStepClock) instead of MonoGame's catch-up loop
    }

    protected override void Initialize()
    {
        editorManager = new EditorManager();
        editorManager.setGraphicsDeviceManager(graphics, 800, 410);
        graphicsDevice = graphics.GraphicsDevice;

        input = new InputSystem();
        cam = new DevCamera(input, graphics, graphics.GraphicsDevice.DisplayMode.AspectRatio, new Vector3(0, 0, 0), new Vector3(0, 0, 0));
        cam.Position = new Vector3(0, 0, 1);
        editorManager.setCamera(cam);

        guiRenderer = new ImGuiRenderer(this);
        editorUI = new EditorUI();

        // Developer console (`~`), `stat` overlays and loop cvars; see docs/design/02 and 01 §3.2, §5.2.
        var cvars = engine.CVars;
        hostCVars = new HostCVars(cvars);
        hostCVars.VSync.Changed += _ => ApplyVSync();
        recHotReload = cvars.Register("rec_hotreload", engine.Core.Developer.Value >= 1, CVarFlags.DevOnly,
            "Reload record files (data/**/*.json) when they change on disk.");
        console = new DevConsoleWindow(cvars, engine.Core);
        stats = new StatOverlay(cvars, engine.Core);
        cvars.RegisterCommand("quit", CVarFlags.None, "Exit the game.", _ => Exit());
        WorldCommands.Register(cvars, engine);
        input.OnKeyPressed += key =>
        {
            if (key == Keys.OemTilde) console.Toggle();
        };
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
        engine.Modules.StartAll();
        world = engine.CreateWorld("main");
        renderView = world.Resources.Get<RenderView>();
        activeCamera = world.Resources.Get<ActiveCamera>();
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
        input.update(realDt);

        // Escape closes the console first; otherwise it quits (TODO #37: becomes the Menu action in R3).
        bool escape = GamePad.GetState(PlayerIndex.One).Buttons.Back == ButtonState.Pressed ||
                      input.IsKeyPressed(Keys.Escape);
        if (escape && console.IsOpen) console.Close();
        else if (escape) Exit();

        // The editor camera runs at the display rate, not the tick rate (smooth at any refresh rate).
        cam.update(gameTime);
        activeCamera.Position = cam.Position.ToNumerics();
        activeCamera.Rotation = cam.Rotation.ToNumerics();
        if (recHotReload.Value) recordHotReload?.Poll();

        step = clock.Advance(realDt, hostCVars.TickRate.Value, hostCVars.MaxFrameTime.Value, hostCVars.TimeScale.Value);
        for (int i = 0; i < step.Ticks; i++)
            world.RunFixed(step.TickDt);

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

    // The Frame schedule (FrameUpdate → Extract → Render → Overlay), then the ImGui layer.
    protected override void Draw(GameTime gameTime)
    {
        graphicsDevice.DepthStencilState = DepthStencilState.Default;
        graphicsDevice.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.DarkOliveGreen, 1.0f, 0);
        graphicsDevice.RasterizerState = RasterizerState.CullCounterClockwise; // add this to system

        renderView.View = cam.getViewMatrix();
        renderView.Projection = cam.getProjectionMatrix();
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

        // Real frame time for the stat overlay.
        float frameSeconds = frameClock.IsRunning ? (float)frameClock.Elapsed.TotalSeconds : 0f;
        frameClock.Restart();
        stats.EndFrame(frameSeconds);
        Profiler.EndFrame();
    }
}
