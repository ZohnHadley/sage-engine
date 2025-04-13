
using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Myra; 
namespace sage_engine;

public class Game1 : Game
{
    private EditorManager editorManager;
    private GraphicsDevice graphics_device;
    private Camera cam;
    private Entity bunnyEntity; 
    EntityContext entContext;
    public Game1()
    {
        // Initialize GraphicsDeviceManager
        editorManager = new  EditorManager(this, 800, 420);
        entContext = EntityContext.getInstance();


        Content.RootDirectory = "Content";
        IsMouseVisible = true;
    }

    protected override void Initialize()
    {
        graphics_device = editorManager.getGraphicsDeviceManager().GraphicsDevice;

        cam = editorManager.getCamera();
        cam.setCamPosition(new Vector3(0, 0, 1));
 

        
        EditorUI.setEditorManager(editorManager);

        base.Initialize();
    }

    protected override void LoadContent()
    {
        // Load the content for the game here

        MyraEnvironment.Game = this;  

        Models.InitializeModels(Content);


        for(int x = 0; x < 5; x++)
        {
            for(int z = 0; z < 5; z++)
            {
                bunnyEntity = entContext.createEntity();
                bunnyEntity.addComponent(new ModelComponent(Models.standforBunny));
                bunnyEntity.transform().position = new Vector3(x, 0, z);
            } 
        }

        entContext.createEntity();
        EditorUI.load(graphics_device);

    }

    protected override void Update(GameTime gameTime)
    {
        if (GamePad.GetState(PlayerIndex.One).Buttons.Back == ButtonState.Pressed || Keyboard.GetState().IsKeyDown(Keys.Escape))
            Exit();


        cam.update(gameTime);
        EditorUI.update(gameTime);

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
         

        //z buffer dept clear
        graphics_device.DepthStencilState = DepthStencilState.Default;
        graphics_device.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.DarkGray, 1.0f, 0);

        ModelRendererSystem.getInstance().render(cam);
        EditorUI.draw(gameTime);

        base.Draw(gameTime);
    }
}
