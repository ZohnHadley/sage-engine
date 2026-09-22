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
// client-side services as plain instances (no singletons); the Engine and the main World come from
// Program. The bunny test scene moves to games/Sandbox in migration step 5.
public class Game1 : Game
{
    private readonly Engine engine;
    private readonly World world;
    private readonly Action onCommandsRegistered;

    private readonly GraphicsDeviceManager graphics;
    private GraphicsDevice graphicsDevice;
    private ImGuiRenderer guiRenderer;
    private EditorManager editorManager;
    private InputSystem input;
    private DevCamera cam;
    private ModelRendererSystem modelRenderer;
    private EditorUI editorUI;
    private EntityContextMenuUI entityInspector;
    private DevConsoleWindow console;
    private StatOverlay stats;
    private Entity bunnyEntity;

    private readonly Stopwatch frameClock = new Stopwatch();
    private long frame;

    internal Game1(Engine engine, World world, Action onCommandsRegistered)
    {
        this.engine = engine;
        this.world = world;
        this.onCommandsRegistered = onCommandsRegistered;
        graphics = new GraphicsDeviceManager(this);
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
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

        modelRenderer = new ModelRendererSystem(world);
        guiRenderer = new ImGuiRenderer(this);
        editorUI = new EditorUI();
        entityInspector = new EntityContextMenuUI(world);

        // Developer console (`~`) and `stat` overlays; see docs/design/02 and 01 §3.2.
        var cvars = engine.CVars;
        console = new DevConsoleWindow(cvars, engine.Core);
        stats = new StatOverlay(cvars, engine.Core);
        cvars.RegisterCommand("quit", CVarFlags.None, "Exit the game.", _ => Exit());
        cvars.RegisterCommand("ent_list", CVarFlags.None, "ent_list [filter]: list entities in the main world.", a =>
        {
            string filter = a.Count > 0 ? a[0] : "";
            int shown = 0;
            foreach (var e in world.QueryAll().Entities)
            {
                string label = World.Describe(e);
                if (filter.Length > 0 && !label.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                Log.Info(LogCat.Console, $"  {label}: {e.Components.Count} components");
                shown++;
            }
            Log.Info(LogCat.Console, $"{shown} of {world.EntityCount} entities");
        });
        input.OnKeyPressed += key =>
        {
            if (key == Keys.OemTilde) console.Toggle();
        };
        CrashReporter.AddSection("GPU", () => $"{GraphicsAdapter.DefaultAdapter.Description}, profile {graphics.GraphicsProfile}");
        Log.Info(LogCat.Render, $"Graphics: {GraphicsAdapter.DefaultAdapter.Description}, {graphics.PreferredBackBufferWidth}x{graphics.PreferredBackBufferHeight}");
        onCommandsRegistered();   // config.cfg + launch args, now that every command exists

        base.Initialize();
    }

    protected override void LoadContent()
    {
        UtilAssets.InitializeModels(Content);

        for (int x = 0; x < 1; x++)
        {
            for (int z = 0; z < 1; z++)
            {
                bunnyEntity = world.Create(Transform.At(new System.Numerics.Vector3(x, 0, z)), "bunny");
                world.Add(bunnyEntity, new ModelRenderer { Model = UtilAssets.stanfordBunny });
            }
        }
        Log.Info(LogCat.World, $"Test scene: {world.EntityCount} entities in '{world.Name}'");

        guiRenderer.RebuildFontAtlas();
        base.LoadContent();
    }

    protected override void Update(GameTime gameTime)
    {
        Log.SetFrame(++frame);
        input.update((float)gameTime.ElapsedGameTime.TotalSeconds);

        // Escape closes the console first; otherwise it quits (TODO #37: becomes the Menu action in R3).
        bool escape = GamePad.GetState(PlayerIndex.One).Buttons.Back == ButtonState.Pressed ||
                      input.IsKeyPressed(Keys.Escape);
        if (escape && console.IsOpen) console.Close();
        else if (escape) Exit();
        cam.update(gameTime);

        // The bunny may have been deleted from the inspector.
        if (world.IsAlive(bunnyEntity))
            TransformMath.Billboard(ref world.Get<Transform>(bunnyEntity), cam.Position.ToNumerics());

        world.FlushCommands();
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        // clear colour + depth buffer
        graphicsDevice.DepthStencilState = DepthStencilState.Default;
        graphicsDevice.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.DarkOliveGreen, 1.0f, 0);
        graphicsDevice.RasterizerState = RasterizerState.CullCounterClockwise; // add this to system

        modelRenderer.render(cam.getViewMatrix(), cam.getProjectionMatrix());
        base.Draw(gameTime);

        // Draw the GUI
        guiRenderer.BeginLayout(gameTime);
        editorUI.Draw(this);
        entityInspector.draw();
        console.Draw();
        stats.Draw();
        guiRenderer.EndLayout();

        // Real frame time (MonoGame's ElapsedGameTime is the fixed target step, not the measured time).
        float frameSeconds = frameClock.IsRunning ? (float)frameClock.Elapsed.TotalSeconds : 0f;
        frameClock.Restart();
        stats.EndFrame(frameSeconds);
    }
}
