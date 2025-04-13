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
    EntityContext entContext;

    public Game1()
    {
        // Initialize GraphicsDeviceManager
        graphics = new GraphicsDeviceManager(this);



        Content.RootDirectory = "Content";
        IsMouseVisible = true;
    }

    protected override void Initialize()
    {
    
        editorManager = new  EditorManager(this, 930, 640);
        editorManager.setGraphicsDeviceManager(graphics);
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

        Models.InitializeModels(Content);


        for(int x = 0; x < 1; x++)
        {
            for(int z = 0; z < 2; z++)
            {
                bunnyEntity = entContext.createEntity();
                bunnyEntity.addComponent(new ModelComponent(Models.standforBunny));
                bunnyEntity.transform().position = new Vector3(x, 0, z);
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
        graphicsDevice.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.DarkGray, 1.0f, 0);
        graphicsDevice.RasterizerState  = RasterizerState.CullCounterClockwise; // add this to system
        ModelRendererSystem.getInstance().render(cam);
        base.Draw(gameTime);
        
        // Draw the GUI
        GuiRenderer.BeginLayout(gameTime);
        EntityContextMenuUI.getInstance().draw();
        GuiRenderer.EndLayout();
    }
}
