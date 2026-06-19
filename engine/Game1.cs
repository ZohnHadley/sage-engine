using System;
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
    public Game1()
    {
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
                bunnyEntity.getComponent<ComponentTransform>().Position = new Vector3(x, 0, z);
            } 
        }

   
        GuiRenderer.RebuildFontAtlas();
        base.LoadContent();
    }

    protected override void Update(GameTime gameTime)
    {
        if (GamePad.GetState(PlayerIndex.One).Buttons.Back == ButtonState.Pressed || Keyboard.GetState().IsKeyDown(Keys.Escape))
            Exit();

        InputSystem.getInstance().update(gameTime);
        cam.update(gameTime);
        bunnyEntity.getComponent<ComponentTransform>().Billboard(cam.Position);

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
         
        //z buffer dept clear
        graphicsDevice.DepthStencilState = DepthStencilState.Default;
        graphicsDevice.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.DarkOliveGreen, 1.0f, 0);
        graphicsDevice.RasterizerState  = RasterizerState.CullCounterClockwise; // add this to system

        
        ModelRendererSystem.getInstance().render(cam);
        //quad.Draw(cam);
        base.Draw(gameTime);
        
        // Draw the GUI
        GuiRenderer.BeginLayout(gameTime);
        EditorUI.GetInstance().Draw(this);
        EntityContextMenuUI.getInstance().draw();
        GuiRenderer.EndLayout();
    }
}
