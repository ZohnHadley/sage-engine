
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Myra; 
namespace sage_engine;

public class Game1 : Game
{
    private GraphicsDeviceManager graphics_device_manager;
    private GraphicsDevice graphics_device;
    private Camera cam;
    private EntityManager entityManager;
    private Entity dragonHead1;
    private Entity dragonHead2;
    private Entity dragonHead3;


    public Game1()
    {
        // Initialize GraphicsDeviceManager
        graphics_device_manager = new GraphicsDeviceManager(this);


        Content.RootDirectory = "Content";
        IsMouseVisible = true;
    }

    protected override void Initialize()
    {
        graphics_device_manager.PreferredBackBufferWidth = 800;
        graphics_device_manager.PreferredBackBufferHeight = 640;
        graphics_device_manager.ApplyChanges();

        graphics_device = graphics_device_manager.GraphicsDevice;


        cam = new Camera(GraphicsDevice.DisplayMode.AspectRatio, new Vector3(0, 0, 12), new Vector3(0, 0, 0));
        entityManager = new EntityManager(cam);


        base.Initialize();
    }

    protected override void LoadContent()
    {


        MyraEnvironment.Game = this;


        Models.InitializeModels(Content);
        dragonHead1 = new Entity("ent1", Models.debug_low_poly_arms, cam.camTarget, new Vector3(-90, 0, 0));
        dragonHead2 = new Entity("ent2", Models.debug_monkey_head, new Vector3(0, 5, 0), new Vector3(0, 0, 0));
        dragonHead3 = new Entity("ent3", Models.debug_monkey_head, new Vector3(6, 0, 5), new Vector3(0, 0, 0));
        entityManager.addEntity(dragonHead1);
        entityManager.addEntity(dragonHead2);
        entityManager.addEntity(dragonHead3);

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
        graphics_device.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.CornflowerBlue, 1.0f, 0);

        entityManager.renderEntities();

        EditorUI.draw();

        base.Draw(gameTime);
    }
}
