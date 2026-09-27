using System;
using System.Diagnostics;
using Friflo.Engine.ECS;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace sage_engine;

// The host's MonoGame Game: today it is both game and editor (docs/design/01 §10). It owns the
// client-side services as plain instances (no singletons). The app — engine, modules, the game — is a
// SageApp that Program.cs created and registered; this class walks it through the rest of its stages
// once the graphics device exists (01 §5.1, REDESIGN §3.2), doing the window's own work in between.
//
// Main loop (01 §5.2): MonoGame's fixed-step mode is off; each frame the host polls the input devices,
// resolves input contexts and actions (08), latches them into the pending PlayerCommand, runs 0..N
// fixed ticks of every world through HostLoop (sim_tickrate), each with its own command, then the Frame
// schedule (Extract → Render, 06) with the interpolation alpha. MonoGame's Update and Draw are one
// pair per frame.
public class Game1 : Game
{
    private readonly SageApp app;
    private readonly Engine engine;
    private readonly HostLoop loop;
    private readonly Action<World> beforeTick;   // cached: a method group passed each frame would allocate
    // Fields marked `= null!` are set in Initialize, which MonoGame calls once the graphics device
    // exists and before the first Update or Draw. The boot sequence moves out of Game1 in phase 1 (#10).
    private World world = null!;
    private RecordHotReload recordHotReload = null!;
    private CVar<bool> recHotReload = null!;


    private readonly GraphicsDeviceManager graphics;
    private GraphicsDevice graphicsDevice = null!;
    private HostCVars hostCVars = null!;
    private InputDevices devices = null!;
    private InputActions actions = null!;
    private readonly CommandLatch latch = new CommandLatch();
    private ActionId moveAction, lookAction, menuAction, toggleConsoleAction;
    private PlayerInput playerInput = null!;
    private ActiveCamera activeCamera = null!;

#if SAGE_DEV
    // The console, the overlays, the free camera and the editor — everything a developer sees and a
    // player does not (15 §3, F28). A Shipping build has no such field, and references neither
    // `Sage.Editor` nor ImGui.
    private DevTools dev = null!;
#endif

    private readonly Stopwatch frameClock = new Stopwatch();
    private long frame;
    private double screenshotAt = -1;   // >= 0: take a screenshot once RealTime passes it
    private double quitAt = -1;         // >= 0: exit once RealTime passes it (`quit <seconds>`)

    internal Game1(SageApp app)
    {
        this.app = app;
        engine = app.Engine;
        loop = new HostLoop(engine);
        beforeTick = BeforeTick;
        graphics = new GraphicsDeviceManager(this);
        // No `Content.RootDirectory`: nothing uses MonoGame's `ContentManager` since R12 — not this
        // host, not the client, not the vendored ImGui renderer. The engine's own `Content/` folder is
        // a VFS mount (05 §3.1), read by `ContentService`, and pointing a `ContentManager` at it would
        // only suggest there was still a pipeline behind it.
        IsMouseVisible = true;
        IsFixedTimeStep = false;   // our own accumulator (FixedStepClock) instead of MonoGame's catch-up loop
        graphics.GraphicsProfile = GraphicsProfile.HiDef;   // 32-bit indices, large textures; instancing later (06 §3.7)
    }

    protected override void Initialize()
    {
        graphics.PreferredBackBufferWidth = 800;
        graphics.PreferredBackBufferHeight = 410;
        graphics.ApplyChanges();
        graphicsDevice = graphics.GraphicsDevice;

        // Input (08): devices, then actions (their bindings are built when the records load).
        var cvars = engine.CVars;
        devices = new InputDevices();
        // Typed characters come from the window, not from key states: the operating system owns the
        // keyboard layout, dead keys and modifiers (08 §3.1, 13 §3). ImGui subscribes to the same
        // event for its own fields; both get every character, and whoever has focus uses it.
        Window.TextInput += (_, e) => devices.PushTyped(e.Character);
        actions = new InputActions(engine.Actions, engine.Records, devices, cvars);
        moveAction = engine.Actions.Get("Move");
        lookAction = engine.Actions.Get("Look");
        menuAction = engine.Actions.Get("Menu");
        toggleConsoleAction = engine.Actions.Get("ToggleConsole");

        // Loop cvars; see docs/design/01 §3.2, §5.2.
        hostCVars = new HostCVars(cvars);
        hostCVars.VSync.Changed += _ => ApplyVSync();
        recHotReload = cvars.Register("rec_hotreload", engine.Core.Developer.Value >= 1, CVarFlags.DevOnly,
            "Reload record files (data/**/*.json) when they change on disk.");

#if SAGE_DEV
        dev = new DevTools(this, engine, devices, actions);
#endif
        cvars.RegisterCommand("quit", CVarFlags.None, "quit [seconds]: exit now, or after this long.", a =>
        {
            // A delay, because a launch line that says `+quit 30` means "run for thirty seconds" to
            // everyone who writes one. Without it the number was silently ignored and the game shut
            // down during startup, which looks exactly like a game that cannot stay open.
            if (a.Count > 0 && float.TryParse(a[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float seconds) && seconds > 0f)
            {
                quitAt = loop.RealTime + seconds;
                Log.Info(LogCat.Console, $"quitting in {seconds:F1}s (`quit` now, Escape, or the window's close button end it sooner)");
                return;
            }
            Exit();
        });
        cvars.RegisterCommand("screenshot", CVarFlags.None, "screenshot [delay]: save a frame as a PNG in the user folder's screenshots/, now or after `delay` seconds.", a =>
            screenshotAt = a.Count > 0 && float.TryParse(a[0], out float delay) ? loop.RealTime + delay : 0);
        WorldCommands.Register(cvars, engine);
        ScaleCommands.Register(cvars, engine);   // scale_spawn / scale_report (R18)
        CrashReporter.AddSection("GPU", () => $"{GraphicsAdapter.DefaultAdapter.Description}, profile {graphics.GraphicsProfile}");
        Log.Info(LogCat.Render, $"Graphics: {GraphicsAdapter.DefaultAdapter.Description}, {graphics.PreferredBackBufferWidth}x{graphics.PreferredBackBufferHeight}");
        app.Configure();   // config.cfg, now that every cvar and command exists
        ApplyVSync();

        // Records, module Start, then the main world (01 §5.1). Modules install their resources and
        // systems in OnWorldCreated; the game spawns its scene there.
        app.LoadContent();
        if (BuildInfo.IsDevBuild)
            recordHotReload = new RecordHotReload(engine.Records, engine.Vfs);
        engine.Modules.ProvideHostService(new ClientHost(this));
        engine.Modules.ProvideHostService(devices);
        engine.Modules.ProvideHostService(actions);
        app.Start();
        world = app.CreateWorld("main");

        activeCamera = world.Resources.Get<ActiveCamera>();
        playerInput = world.Resources.Get<PlayerInput>();

#if SAGE_DEV
        dev.OnWorldCreated(world);
#endif
        app.RunLaunchCommands();   // +args last, with the world up

        base.Initialize();
    }

    // A new document belongs to the game that is loaded: that is whose content folder it is saved into.
    private string NamespaceOfGame()
    {
        foreach (var mount in engine.Vfs.Mounts)
            if (mount.RecordNamespace != "sage") return mount.RecordNamespace;
        return "sage";
    }

    private void ApplyVSync()
    {
        graphics.SynchronizeWithVerticalRetrace = hostCVars.VSync.Value;
        graphics.ApplyChanges();
    }

    protected override void LoadContent()
    {
#if SAGE_DEV
        dev.LoadContent();
#endif
        base.LoadContent();
    }

    // Input, then the simulation: 0..N fixed ticks (01 §5.2).
    protected override void Update(GameTime gameTime)
    {
        Log.SetFrame(++frame);
        float realDt = (float)gameTime.ElapsedGameTime.TotalSeconds;

        // A console script paces itself with `wait` (02 §4.2), which means somebody has to age it.
        // Real time, not simulation time: a script that waits two seconds means two seconds even
        // when the game is paused, which is what an automated check wants.
        engine.CVars.Pump(realDt);

        // Input (08 §5): devices → contexts (the console's; ImGui's capture from its last frame) → actions.
        devices.Poll(IsActive);   // input the window is not the target of is not input (#60)
#if SAGE_DEV
        bool uiWantsMouse = dev.WantsMouse, uiWantsKeyboard = dev.WantsKeyboard;
#else
        const bool uiWantsMouse = false, uiWantsKeyboard = false;
#endif
        actions.SetActive(InputContext.Editor, true);   // the editor host (no Editor map yet: nothing consumed)
#if SAGE_DEV
        actions.SetActive(InputContext.Console, dev.ConsoleIsOpen);
#else
        actions.SetActive(InputContext.Console, false);
#endif
        actions.UiWantsKeyboard = uiWantsKeyboard;
        actions.UiWantsMouse = uiWantsMouse;
        actions.Update(realDt);

        // Menu closes the console first; otherwise it quits (until there is a menu).
#if SAGE_DEV
        if (actions.Pressed(toggleConsoleAction)) dev.ToggleConsole();
#endif
        if (actions.Pressed(menuAction))
        {
#if SAGE_DEV
            if (dev.ConsoleIsOpen) dev.CloseConsole();
            else Exit();
#else
            Exit();
#endif
        }

        // Everything since the last tick goes into the next PlayerCommand; look applies at frame rate (08 §3.4).
        latch.AddFrame(actions.HeldMask, actions.PressedMask, actions.ReleasedMask, actions.Axis2(moveAction));
        latch.AddLook(actions.Axis2(lookAction));

        // Something in the simulation asked the player to face a particular way (a teleport, a map's
        // player start). It goes in here rather than on the pawn, because this is where the view angles
        // actually live — see `PlayerInput.RequestView`.
        if (playerInput.TryTakeView(out float wantedYaw, out float wantedPitch))
            latch.SetView(wantedYaw, wantedPitch);

        // The free camera runs at the display rate, not the tick rate (smooth at any refresh rate), and
        // hands the camera back to a rig that claimed it. Without the dev tools a rig is the only thing
        // that drives the camera, which is what a played game wants.
#if SAGE_DEV
        dev.Update(gameTime, activeCamera);
#else
        activeCamera.RigEnabled = true;
#endif
        if (recHotReload.Value) recordHotReload?.Poll();

        // Every world ticks; the one the player is in gets the player's command for each tick. (One
        // latch, sampled once per tick: a pressed edge belongs to exactly one tick of one world.)
        loop.Update(realDt, hostCVars.TickRate.Value, hostCVars.MaxFrameTime.Value, hostCVars.TimeScale.Value, beforeTick);

        float exitAfter = hostCVars.ExitAfter.Value;
        if (exitAfter > 0 && loop.RealTime >= exitAfter)
        {
            Log.Info(LogCat.Host, $"host_exitafter: {frame} frames, {world.Tick} ticks at sim_tickrate {hostCVars.TickRate.Value} " +
                $"in {loop.RealTime:F2} s ({frame / loop.RealTime:F0} fps, {world.Tick / loop.RealTime:F1} ticks/s)");
            Exit();
        }

        base.Update(gameTime);
    }

    // The player's world gets the command for the tick it is about to run; the others tick without one.
    private void BeforeTick(World ticking)
    {
        if (ticking != world) return;
        playerInput.Command = latch.Sample(world.Tick + 1);   // the tick this command is for
        playerInput.HasCommand = true;
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
        loop.Frame();
        base.Draw(gameTime);

#if SAGE_DEV
        using (Profiler.Begin("Frame.ImGui"))
            dev.Draw(gameTime);
#endif

        if (quitAt >= 0 && loop.RealTime >= quitAt) { quitAt = -1; Exit(); }

        if (screenshotAt >= 0 && loop.RealTime >= screenshotAt)
        {
            screenshotAt = -1;
            SaveScreenshot();
        }

        devices.EndFrame();   // typed characters have had their frame (08 §3.1)

        // Real frame time for the stat overlay.
        float frameSeconds = frameClock.IsRunning ? (float)frameClock.Elapsed.TotalSeconds : 0f;
        frameClock.Restart();
#if SAGE_DEV
        dev.EndFrame(frameSeconds);
#endif
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
