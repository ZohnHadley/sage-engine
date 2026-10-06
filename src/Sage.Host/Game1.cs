using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Sage.Host;

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
    private World inputWorld = null!;   // the world the player's commands go to: `world`, or the editor's play world
    private RecordHotReload recordHotReload = null!;
    private IDisposable? modWatch;   // dev: a changed mod.json says "restart to apply" (4j-3)
    private CVar<bool> recHotReload = null!;
    private CVar<bool> captureMouse = null!;   // m_capture (#334)


    private readonly GraphicsDeviceManager graphics;
    private GraphicsDevice graphicsDevice = null!;
    private HostCVars hostCVars = null!;
    private InputDevices devices = null!;
    private InputActions actions = null!;
    private readonly CommandLatch latch = new CommandLatch();
    private ActionId moveAction, lookAction, menuAction, toggleConsoleAction;
    private PlayerInput playerInput = null!;

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
    private PixelCheckCapture pixelCheck = null!;   // `r_pixelcheck` (issue #318)

    private readonly string? dumpRegistry;   // -dump-registry: write RegistryDump here once booted, then quit
    // -edit [placements-or-scene] (issue #219): null in a game run; "" opens the game's start scene. Its
    // world is an edit world, nothing in it is simulated, and the free camera has the screen.
    private readonly string? edit;
    private bool followingWindow;       // OnClientSizeChanged is writing vid_width / vid_height
    private bool Editing => edit != null;
#if SAGE_DEV
    private World PlayerWorld => dev.PlayWorld ?? world;
#else
    private World PlayerWorld => world;
#endif

    internal Game1(SageApp app, string? dumpRegistry = null, string? edit = null)
    {
        this.app = app;
        this.dumpRegistry = dumpRegistry;
        this.edit = edit;
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
        // The back buffer keeps what was drawn on it when a render target is bound and unbound
        // (issue #77): with DiscardContents MonoGame clears it to purple on the way back, so a world
        // that draws a minimap after the screen world has drawn the screen would wipe the picture.
        graphics.PreparingDeviceSettings += (_, e) =>
            e.GraphicsDeviceInformation.PresentationParameters.RenderTargetUsage = RenderTargetUsage.PreserveContents;
    }

    protected override void Initialize()
    {
        graphics.PreferredBackBufferWidth = 800;    // vid_width / vid_height, once the cvars exist and config.cfg ran
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
        captureMouse = cvars.Register("m_capture", true, CVarFlags.Archive, "Hold the mouse cursor (hidden, centred) while the player looks around; it is let go for screens, the console, the editor and when the window loses focus.");
        actions = new InputActions(engine.Actions, engine.Records, devices, cvars, engine.Rebinds);
        moveAction = engine.Actions.Get("Move");
        lookAction = engine.Actions.Get("Look");
        menuAction = engine.Actions.Get("Menu");
        toggleConsoleAction = engine.Actions.Get("ToggleConsole");

        // Loop cvars; see docs/design/01 §3.2, §5.2.
        hostCVars = new HostCVars(cvars);
        hostCVars.VSync.Changed += _ => ApplyVSync();
        hostCVars.Width.Changed += _ => ApplyWindowSize();
        hostCVars.Height.Changed += _ => ApplyWindowSize();
        // The window can be resized by dragging its edges (or maximised), in a game and in the editor:
        // the back buffer follows, and everything that draws reads its size every frame (the views,
        // the UI, ImGui), so nothing else has to be told.
        Window.AllowUserResizing = true;
        Window.ClientSizeChanged += (_, _) => OnClientSizeChanged();
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
        pixelCheck = new PixelCheckCapture(engine);
        pixelCheck.Register(cvars);
        CrashReporter.AddSection("GPU", () => $"{GraphicsAdapter.DefaultAdapter.Description}, profile {graphics.GraphicsProfile}");
        app.Configure();   // config.cfg, now that every cvar and command exists
        ApplyVSync();
        ApplyWindowSize();
        Log.Info(LogCat.Render, $"Graphics: {GraphicsAdapter.DefaultAdapter.Description}, {graphics.PreferredBackBufferWidth}x{graphics.PreferredBackBufferHeight}");

        // Records, module Start, then the main world (01 §5.1). Modules install their resources and
        // systems in OnWorldCreated; the game spawns its scene there.
        app.LoadContent();
        if (BuildInfo.IsDevBuild)
        {
            recordHotReload = new RecordHotReload(engine.Records, engine.Vfs);
#pragma warning disable SAGE0132 // the host ships with the engine that declares the mods API
            modWatch = engine.ModManager.WatchManifests();
#pragma warning restore SAGE0132
        }
        engine.Modules.ProvideHostService(new ClientHost(this));
        engine.Modules.ProvideHostService(devices);
        // A save's thumbnail (issue #285): the last frame drawn, shrunk, handed over when the save is taken.
#pragma warning disable SAGE0131 // the host ships with the engine that declares the save API
        engine.Saves.Thumbnail = CaptureThumbnail;
#pragma warning restore SAGE0131
        engine.Modules.ProvideHostService(actions);
        app.Start();
#if SAGE_DEV
        // The editor (`-edit`, issue #219): an edit world in place of the main one. Play-in-editor (#226)
        // will make a play world beside it.
        var editTarget = Editing ? ResolveEditTarget() : default;
        world = Editing ? CreateEditWorld(editTarget) : app.CreateWorld("main");
#else
        world = app.CreateWorld("main");
#endif

        // Sampled every frame whether or not anything reads it: a game without the character plugin
        // has no pawn to command, and its PlayerInput is only ever written (issue #13).
        playerInput = world.Resources.GetOrAdd(() => new PlayerInput());
        inputWorld = world;
        engine.Demos.PlayerWorld = () => PlayerWorld;   // what `record` records (issue #333)

#if SAGE_DEV
        dev.OnWorldCreated(world);
        if (Editing) dev.BeginEditing(editTarget);
#endif
        app.RunLaunchCommands();   // +args last, with the world up

        // Everything is registered now — the client's commands, the editor's, the host's own — which is
        // why the dump is written here and not by a headless app, which has no window and so none of
        // those (tools/check_docs.py reads it; docs/REDESIGN.md §4.8).
        if (dumpRegistry != null)
        {
            try
            {
                RegistryDump.Write(engine, dumpRegistry);
                Log.Info(LogCat.Host, $"Wrote the registry to {dumpRegistry}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error(LogCat.Host, $"-dump-registry: {ex.Message}");
            }
            quitAt = 0;   // on the first frame
        }

        base.Initialize();
    }

#if SAGE_DEV
#pragma warning disable SAGE0133, SAGE0121 // the editor's model and the start scene: the dev host ships with the engine that declares them
    // What `-edit <name>` names; a name that is neither a placements document nor a scene is an error in
    // the log, and the editor opens the start scene instead (a typo should not cost the session).
    private EditTarget ResolveEditTarget()
    {
        if (EditTarget.TryResolve(engine.Records, engine.Scenes.Start, edit, out var target, out string error)) return target;
        Log.Error(LogCat.Editor, $"{error}; opening the start scene");
        EditTarget.TryResolve(engine.Records, engine.Scenes.Start, null, out target, out _);
        return target;
    }

    private World CreateEditWorld(EditTarget target) => app.CreateEditWorld("edit", target.Scene);
#pragma warning restore SAGE0133, SAGE0121
#endif

    // A new document belongs to the game that is loaded: that is whose content folder it is saved into.
    private string NamespaceOfGame()
    {
        foreach (var mount in engine.Vfs.Mounts)
            if (mount.RecordNamespace != "sage") return mount.RecordNamespace;
        return "sage";
    }

    // host_maxfps: sleep out the rest of the frame (coarse sleep, then spin the last millisecond).
    private void LimitFrameRate()
    {
        int cap = hostCVars.MaxFps.Value;
        double now = limiterClock.Elapsed.TotalSeconds;
        if (cap > 0)
        {
            double wait = FrameLimiter.Remaining(cap, lastFrameStart, now);
            while (wait > 0)
            {
                if (wait > 0.002) System.Threading.Thread.Sleep((int)((wait - 0.001) * 1000));
                else System.Threading.Thread.SpinWait(50);
                now = limiterClock.Elapsed.TotalSeconds;
                wait = FrameLimiter.Remaining(cap, lastFrameStart, now);
            }
        }
        lastFrameStart = now;
    }

    private readonly System.Diagnostics.Stopwatch limiterClock = System.Diagnostics.Stopwatch.StartNew();
    private double lastFrameStart;

    private void ApplyVSync()
    {
        graphics.SynchronizeWithVerticalRetrace = hostCVars.VSync.Value;
        graphics.ApplyChanges();
    }

    // `vid_width` / `vid_height`: the back buffer follows, and the screen's views with it (each view
    // takes its aspect from its own viewport, 06 §3.4a).
    private void ApplyWindowSize()
    {
        if (followingWindow) return;   // the cvars are being written from the window's own size
        int width = hostCVars.Width.Value, height = hostCVars.Height.Value;
        if (graphics.PreferredBackBufferWidth == width && graphics.PreferredBackBufferHeight == height) return;
        graphics.PreferredBackBufferWidth = width;
        graphics.PreferredBackBufferHeight = height;
        graphics.ApplyChanges();
        Log.Info(LogCat.Render, $"Window {width}x{height}");
    }

    // The player dragged the window to a new size: the back buffer takes it, and `vid_width` / `vid_height`
    // record it (Archive, so the next run opens at the size this one closed at). A minimised window
    // reports 0x0 and keeps its buffer; a size below the cvars' range is drawn at, but saved clamped.
    private void OnClientSizeChanged()
    {
        var bounds = Window.ClientBounds;
        if (followingWindow || bounds.Width <= 0 || bounds.Height <= 0) return;
        if (graphics.PreferredBackBufferWidth == bounds.Width && graphics.PreferredBackBufferHeight == bounds.Height) return;
        followingWindow = true;
        try
        {
            graphics.PreferredBackBufferWidth = bounds.Width;
            graphics.PreferredBackBufferHeight = bounds.Height;
            graphics.ApplyChanges();
            hostCVars.Width.Value = Math.Clamp(bounds.Width, HostCVars.MinWidth, HostCVars.MaxWidth);
            hostCVars.Height.Value = Math.Clamp(bounds.Height, HostCVars.MinHeight, HostCVars.MaxHeight);
        }
        finally { followingWindow = false; }
        Log.Info(LogCat.Render, $"Window resized to {bounds.Width}x{bounds.Height}");
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
        LimitFrameRate();
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
        // The editor (`-edit`) reads its own map and nothing walks: there is no pawn. A game run has no
        // Editor context at all, so the editor's keys never reach a played game.
        // Playing in the editor (#226) is a game run again until it stops.
#if SAGE_DEV
        bool playing = dev.PlayWorld != null;
#else
        const bool playing = false;
#endif
        actions.SetActive(InputContext.Editor, Editing && !playing);
        actions.SetActive(InputContext.Gameplay, !Editing || playing);
#if SAGE_DEV
        actions.SetActive(InputContext.Console, dev.ConsoleIsOpen);
#else
        actions.SetActive(InputContext.Console, false);
#endif
        actions.UiWantsKeyboard = uiWantsKeyboard;
        actions.UiWantsMouse = uiWantsMouse;
        UpdateMouseCapture(uiWantsMouse, playing);
        actions.Update(realDt);

        // Menu closes the console first; otherwise it quits (until there is a menu). Not in the editor,
        // where Escape is too easy a key to lose work to: File > Exit (or `quit`) leaves it.
#if SAGE_DEV
        if (actions.Pressed(toggleConsoleAction)) dev.ToggleConsole();
#endif
        if (actions.Pressed(menuAction))
        {
#if SAGE_DEV
            if (dev.ConsoleIsOpen) dev.CloseConsole();
            else if (playing) dev.StopPlaying();   // Escape leaves play, not the editor (#226)
            else if (!Editing) Exit();
#else
            Exit();
#endif
        }

        // The player's world: the play world while the editor plays (#226), else the one the host made.
        if (PlayerWorld != inputWorld)
        {
            inputWorld = PlayerWorld;
            playerInput = inputWorld.Resources.GetOrAdd(() => new PlayerInput());
        }

        // Everything since the last tick goes into the next PlayerCommand; look applies at frame rate (08 §3.4).
        latch.AddFrame(actions.HeldMask, actions.PressedMask, actions.ReleasedMask, actions.Axis2(moveAction));
        latch.AddLook(actions.Axis2(lookAction));

        // Something in the simulation asked the player to face a particular way (a teleport, a map's
        // player start). It goes in here rather than on the pawn, because this is where the view angles
        // actually live — see `PlayerInput.RequestView`.
        if (playerInput.TryTakeView(out float wantedYaw, out float wantedPitch))
            latch.SetView(wantedYaw, wantedPitch);

        // The free camera runs at the display rate, not the tick rate (smooth at any refresh rate). It is
        // a camera entity (issue #81) that draws only where no other camera does, or over all of them
        // with `cam_free`. Without the dev tools the game's cameras are all there is, which is what a
        // played game wants.
#if SAGE_DEV
        dev.Update(gameTime);
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

    // Hold the cursor while the player looks around and let it go for anything that wants a pointer: a screen,
    // the console, the controls screen's capture, the developer UI, the editor's viewport, a window that is
    // not ours (#334). The decision is `MouseCapturePolicy` and the delta's seams `MouseCaptureTracker`, both
    // headless-tested; this gathers their inputs and applies the answer.
    private bool wasCaptured;
    private string? lastWhy = "";
    private void UpdateMouseCapture(bool uiWantsMouse, bool playing)
    {
        var w = inputWorld;
        bool pawn = false;
        foreach (var _ in w.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities) { pawn = true; break; }
#pragma warning disable SAGE0125   // widget screens (#97): the host asks the stack whether one is open, nothing more
        bool screenOpen = (w.Resources.TryGet<ScreenStack>(out var screens) && screens != null && screens.IsOpen)
            || (w.Resources.TryGet<Sage.UI.UiScreenStack>(out var widgets) && widgets != null && widgets.IsOpen);
#pragma warning restore SAGE0125
#if SAGE_DEV
        bool console = dev.ConsoleIsOpen;
#else
        const bool console = false;
#endif
        var inputs = new MouseCaptureInputs
        {
            PawnPossessed = pawn,
            ScreenOpen = screenOpen,
            ConsoleOpen = console,
            ControlsCapturing = engine.Rebinds.Capturing,
            UiWantsMouse = uiWantsMouse,
            EditorViewport = Editing && !playing,
            WindowFocused = IsActive,
            Enabled = captureMouse.Value,
        };
        string? why = MouseCapturePolicy.Why(inputs);
        var bounds = Window.ClientBounds;
        var step = devices.Mouse.ApplyCapture(why == null, new Point(bounds.Width / 2, bounds.Height / 2));
        IsMouseVisible = !step.Captured;
        if (step.Captured != wasCaptured || why != lastWhy)
        {
            if (step.Captured != wasCaptured)
                Log.Info(LogCat.Input, step.Captured ? "Mouse captured" : $"Mouse released ({why})");
            wasCaptured = step.Captured;
            lastWhy = why;
        }
    }

    // The player's world gets the command for the tick it is about to run; the others tick without one.
    private void BeforeTick(World ticking)
    {
        if (ticking != inputWorld) return;
        playerInput.Command = latch.Sample(ticking.Tick + 1);   // the tick this command is for
        playerInput.HasCommand = true;
    }

    protected override void UnloadContent()
    {
        recordHotReload?.Dispose();
        modWatch?.Dispose();
        base.UnloadContent();
    }

    // The Frame schedule (FrameUpdate → Extract → Render → Overlay; the Renderer clears and draws the
    // world's snapshot in Render, 06), then the ImGui layer.
    protected override void Draw(GameTime gameTime)
    {
        loop.Frame();
        base.Draw(gameTime);
        // `r_pixelcheck` (issue #318) reads the frame the game drew, its UI included, before the developer's
        // ImGui windows are drawn over it.
        pixelCheck.AfterDraw(graphicsDevice);

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
        WorkStats.EndFrame();   // this frame's jobs, loads and uploads, for `stat render` / `stat assets` (#300)
    }

    // What a save keeps of the screen (issue #285): the back buffer, at most 320 pixels wide. A save is
    // taken at a tick boundary, in Update, so this is the frame last drawn: the one the player saw when
    // they pressed F5. Read on this thread, which owns the device; encoded and written by the save's writer.
#pragma warning disable SAGE0131 // the host ships with the engine that declares the save API
    private SaveThumbnail? CaptureThumbnail()
    {
        int w = graphicsDevice.PresentationParameters.BackBufferWidth, h = graphicsDevice.PresentationParameters.BackBufferHeight;
        if (w <= 0 || h <= 0) return null;
        var pixels = new Color[w * h];
        graphicsDevice.GetBackBufferData(pixels);
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            var c = pixels[i];
            rgba[i * 4] = c.R;
            rgba[i * 4 + 1] = c.G;
            rgba[i * 4 + 2] = c.B;
            rgba[i * 4 + 3] = 255;   // a thumbnail is opaque, whatever the back buffer's alpha says
        }
        return new SaveThumbnail(w, h, rgba).Shrink(320);
    }
#pragma warning restore SAGE0131

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
