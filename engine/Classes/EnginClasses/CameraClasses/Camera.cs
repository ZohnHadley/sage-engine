
using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace sage_engine;
internal class Camera 
{

    private GraphicsDeviceManager graphics_device_manager;

    private Vector3 camTarget;
    private Vector3 camPosition;

    private Matrix projectionMatrix; // converts 3d to 2d a.k.a the lens (what the camera cans see)
    private Matrix viewMatrix; // cameras physical position in the world (location and orientation) 

    private Vector3 camForward = Vector3.Forward;
    private Vector3 camUp = Vector3.Up;
    private float yaw;
    private float pitch;
    private float mouseAmount = 1f;
    private float speed = 10;
    private static readonly float PitchLimit = MathHelper.ToRadians(89f);

    KeyboardState keyState;
    private MouseState mouseState;
    private MouseState prevMouseState;
     

    public Camera(GraphicsDeviceManager graphicsDeviceManager, float aspect_ratio, Vector3 position, Vector3 rotation)
    {
        graphics_device_manager = graphicsDeviceManager;
        prevMouseState = Mouse.GetState();
        camPosition = position;

        yaw = MathHelper.ToRadians(rotation.Y);
        pitch = MathHelper.Clamp(MathHelper.ToRadians(rotation.X), -PitchLimit, PitchLimit);
        rebuildForward();

        camTarget = camForward;
        projectionMatrix = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(45f), aspect_ratio, 0.01f, 1000);
        viewMatrix = Matrix.CreateLookAt(camPosition, (camPosition + camForward), Vector3.Up);
    }

    private void rebuildForward()
    {
        camForward = Vector3.Transform(Vector3.Forward, Matrix.CreateFromYawPitchRoll(yaw, pitch, 0));
    }

    public Matrix getProjectionMatrix()
    {
        return projectionMatrix;
    }

    public Matrix getViewMatrix()
    {
        return viewMatrix;
    }

    public Vector3 getCamPosition()
    {
        return camPosition;
    }

    public Vector3 getCamTarget()
    {
        return camTarget;
    }

    public Vector3 getCamForward()
    {
        return camForward;
    }

    public Vector3 getCamUp()
    {
        return camUp;
    }

    public void setCamPosition(Vector3 position)
    {
        camPosition = position;
    }

    public void setCamTarget(Vector3 target)
    {
        camTarget = target;
    } 


    public void update(GameTime gameTime)
    {
        keyState = Keyboard.GetState();
        mouseState = Mouse.GetState();

        viewMatrix = Matrix.CreateLookAt(camPosition, (camPosition + camForward), camUp);
        CustomMovement(gameTime, keyState, mouseState);
    } 

    private void CustomMovement(GameTime gameTime, KeyboardState keyState, MouseState mouseState)
    {
        Vector3 direction = camForward;

        direction.Normalize();
        Vector3 normal = Vector3.Cross(direction, camUp);
 
        float mouse_y = mouseState.Y - prevMouseState.Y;
        float mouse_x = mouseState.X - prevMouseState.X;

        bool risingEdge = mouseState.RightButton == ButtonState.Pressed
                          && prevMouseState.RightButton == ButtonState.Released;

        if (mouseState.RightButton == ButtonState.Pressed && !risingEdge)
        {
            mouse_y *= graphics_device_manager.PreferredBackBufferHeight / (GraphicsAdapter.DefaultAdapter.CurrentDisplayMode.Height * 1.0f);
            mouse_x *= graphics_device_manager.PreferredBackBufferWidth / (GraphicsAdapter.DefaultAdapter.CurrentDisplayMode.Width * 1.0f);

            yaw -= mouse_x * mouseAmount * 0.01f;
            pitch -= mouse_y * mouseAmount * 0.01f;
            yaw = MathHelper.WrapAngle(yaw);
            pitch = MathHelper.Clamp(pitch, -PitchLimit, PitchLimit);
            rebuildForward();
        }

        prevMouseState = mouseState;

        if (keyState.IsKeyDown(Keys.W))
        {
            //move camera forward in direction of rotation 
            camPosition = Vector3.Lerp(camPosition, camPosition + (direction * speed) * (float)gameTime.ElapsedGameTime.TotalSeconds, 0.15f);
        }

        if (keyState.IsKeyDown(Keys.S))
        {
            camPosition = Vector3.Lerp(camPosition, camPosition - (direction * speed) * (float)gameTime.ElapsedGameTime.TotalSeconds, 0.15f);
        }

        if (keyState.IsKeyDown(Keys.D))
        {
            camPosition = Vector3.Lerp(camPosition, camPosition + (normal* speed) * (float)gameTime.ElapsedGameTime.TotalSeconds, 0.15f);
        }

        if (keyState.IsKeyDown(Keys.A))
        {
            camPosition = Vector3.Lerp(camPosition, camPosition - (normal* speed) * (float)gameTime.ElapsedGameTime.TotalSeconds, 0.15f);
        }
    }
 

}