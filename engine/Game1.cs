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
    private Camera cam;
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
        //camera
        cam = new Camera(graphics, graphics.GraphicsDevice.DisplayMode.AspectRatio, new Vector3(0,0, 0), new Vector3(0, 0, 0)); 
        cam.setCamPosition(new Vector3(0, 0, 1));
        
        editorManager.setCamera(cam);
         

        GuiRenderer = new ImGuiRenderer(this);
        base.Initialize();
    }

    protected override void LoadContent()
    {
        // Load the content for the game here

        UtilAssets.InitializeModels(Content);


        for(int x = 0; x < 80; x++)
        {
            for(int z = 0; z < 80; z++)
            {
                bunnyEntity = entContext.createEntity();
                entContext.addComponentFor(bunnyEntity, new ComponentMeshRenderer(UtilAssets.stanfordBunny));
                bunnyEntity.getComponent<ComponentTransform>().position = new Vector3(x, 0, z);
            } 
        }

   
        GuiRenderer.RebuildFontAtlas();
        base.LoadContent();
    }

    protected override void Update(GameTime gameTime)
    {
        if (GamePad.GetState(PlayerIndex.One).Buttons.Back == ButtonState.Pressed || Keyboard.GetState().IsKeyDown(Keys.Escape))
            Exit();


        cam.update(gameTime);

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
