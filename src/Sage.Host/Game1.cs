using System;
using System.Diagnostics;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using MonoGame.ImGuiNet;

namespace sage_engine;

public class Game1 : Game
{
    public static ImGuiRenderer GuiRenderer;
    private EditorManager editorManager;
    private GraphicsDeviceManager graphics;
    private GraphicsDevice graphicsDevice;
    private DevCamera cam;
    private Entity bunnyEntity; 
    private EntityContext entContext;

    // Core services from Program (they move onto the Engine object in migration step 3).
    private readonly CVarRegistry cvars;
    private readonly CoreCVars coreCVars;
    private readonly Action onCommandsRegistered;
    private DevConsoleWindow console;
    private StatOverlay stats;
    private readonly Stopwatch frameClock = new Stopwatch();
    private long frame;

    internal Game1(CVarRegistry cvars, CoreCVars coreCVars, Action onCommandsRegistered)
    {
        this.cvars = cvars;
        this.coreCVars = coreCVars;
        this.onCommandsRegistered = onCommandsRegistered;
        // Initialize GraphicsDeviceManager
        graphics = new GraphicsDeviceManager(this);

        Content.RootDirectory = "Content";
        IsMouseVisible = true;
    }

    protected override void Initialize()
    {
    
        editorManager = new  EditorManager();
        editorManager.setGraphicsDeviceManager(graphics, 800, 410);
        graphicsDevice = graphics.GraphicsDevice;
     
        //entity world context
        entContext = EntityContext.getInstance();
        EntityContextListener.getInstance();
        // Bring the entity-context listener up before LoadContent so it is subscribed
        // in time to observe the entities created there.
        //camera
        cam = new DevCamera(graphics, graphics.GraphicsDevice.DisplayMode.AspectRatio, new Vector3(0,0, 0), new Vector3(0, 0, 0)); 
        cam.Position = new Vector3(0, 0, 1);
        
        editorManager.setCamera(cam);
         

        GuiRenderer = new ImGuiRenderer(this);

        // Developer console (`~`) and `stat` overlays; see docs/design/02 and 01 §3.2.
        console = new DevConsoleWindow(cvars, coreCVars);
        stats = new StatOverlay(cvars, coreCVars);
        cvars.RegisterCommand("quit", CVarFlags.None, "Exit the game.", _ => Exit());
        InputSystem.getInstance().OnKeyPressed += key =>
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
        // Load the content for the game here

        UtilAssets.InitializeModels(Content);


        for(int x = 0; x < 1; x++)
        {
            for(int z = 0; z < 1; z++)
            {
                bunnyEntity = entContext.createEntity();
                entContext.addComponentFor(bunnyEntity, new ComponentMeshRenderer(UtilAssets.stanfordBunny));
                bunnyEntity.getComponent<ComponentTransform>().Position = new System.Numerics.Vector3(x, 0, z);
            } 
        }

   
        GuiRenderer.RebuildFontAtlas();
        base.LoadContent();
    }

    protected override void Update(GameTime gameTime)
    {
        Log.SetFrame(++frame);
        InputSystem.getInstance().update((float)gameTime.ElapsedGameTime.TotalSeconds);

        // Escape closes the console first; otherwise it quits (TODO #37: becomes the Menu action in R3).
        bool escape = GamePad.GetState(PlayerIndex.One).Buttons.Back == ButtonState.Pressed ||
                      InputSystem.getInstance().IsKeyPressed(Keys.Escape);
        if (escape && console.IsOpen) console.Close();
        else if (escape) Exit();
        cam.update(gameTime);
        bunnyEntity.getComponent<ComponentTransform>().Billboard(cam.Position.ToNumerics());

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
         
        // clear colour + depth buffer
        graphicsDevice.DepthStencilState = DepthStencilState.Default;
        graphicsDevice.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.DarkOliveGreen, 1.0f, 0);
        graphicsDevice.RasterizerState  = RasterizerState.CullCounterClockwise; // add this to system

        
        ModelRendererSystem.getInstance().render(cam.getViewMatrix(), cam.getProjectionMatrix());
        //quad.Draw(cam);
        base.Draw(gameTime);
        
        // Draw the GUI
        GuiRenderer.BeginLayout(gameTime);
        EditorUI.GetInstance().Draw(this);
        EntityContextMenuUI.getInstance().draw();
        console.Draw();
        stats.Draw();
        GuiRenderer.EndLayout();

        // Real frame time (MonoGame's ElapsedGameTime is the fixed target step, not the measured time).
        float frameSeconds = frameClock.IsRunning ? (float)frameClock.Elapsed.TotalSeconds : 0f;
        frameClock.Restart();
        stats.EndFrame(frameSeconds);
    }
}
