
using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using sage_engine;

public class Camera
{

    private GraphicsDeviceManager graphics_device_manager;

    private Vector3 camTarget;
    private Vector3 camPosition;

    private Matrix projectionMatrix; // converts 3d to 2d a.k.a the lens (what the camera cans see)
    private Matrix viewMatrix; // cameras physical position in the world (location and orientation) 

    private Vector3 camForward = Vector3.Forward;
    private Vector3 camUp = Vector3.Up;
    private float mouseAmount = 1f;

    KeyboardState keyState;
    private MouseState mouseState;
    private MouseState prevMouseState;
     

    public Camera(GraphicsDeviceManager graphicsDeviceManager, float aspect_ratio, Vector3 position, Vector3 rotation)
    {
        graphics_device_manager = graphicsDeviceManager;
        prevMouseState = Mouse.GetState();
        //camTarget in fonrt of camPosition with a distance of 10 from z axis
        camPosition = position;
        //set rotation 
        camForward = Vector3.Transform(camForward, Matrix.CreateFromYawPitchRoll(MathHelper.ToRadians(rotation.Y), MathHelper.ToRadians(rotation.X), MathHelper.ToRadians(rotation.Z)));
        camTarget = camForward;
        projectionMatrix = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(45f), aspect_ratio, 0.01f, 1000); // screen aspect ration and render distance
        viewMatrix = Matrix.CreateLookAt(camPosition, (camPosition + camForward), Vector3.Up);

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

        if (EditorUI.getIsHovered() == false)
        {
            
            //editorCameraMovement(keyState, mouseState);
            CustomMovement(keyState, mouseState);
        }


        viewMatrix = Matrix.CreateLookAt(camPosition, (camPosition + camForward), camUp);

    } 

    private void CustomMovement(KeyboardState keyState, MouseState mouseState)
    {
        Vector3 direction = camForward;

        direction.Normalize();
        Vector3 normal = Vector3.Cross(direction, camUp);
 
        float mouse_y = mouseState.Y - prevMouseState.Y;
        float mouse_x = mouseState.X - prevMouseState.X;
        if (mouseState.RightButton == ButtonState.Pressed)
        {

            mouse_y *= graphics_device_manager.PreferredBackBufferHeight / (GraphicsAdapter.DefaultAdapter.CurrentDisplayMode.Height * 1.0f);
            mouse_x *= graphics_device_manager.PreferredBackBufferWidth / (GraphicsAdapter.DefaultAdapter.CurrentDisplayMode.Width * 1.0f);

            camForward -= mouse_y * mouseAmount * camUp  * 0.01f;
            camForward += mouse_x * mouseAmount * normal * 0.01f;
            camForward.Normalize();

            prevMouseState = mouseState;
        }else if (mouseState.RightButton == ButtonState.Released){
            prevMouseState = mouseState;
        }

        /*if (keyState.IsKeyDown(Keys.W))
        {
            //move camera forward in direction of rotation 
            camPosition += direction * 0.25f;
        }

        if (keyState.IsKeyDown(Keys.S))
        {
            camPosition -= direction * 0.25f;
        }

        if (keyState.IsKeyDown(Keys.D))
        {
            camPosition += normal * 0.25f;
        }

        if (keyState.IsKeyDown(Keys.A))
        {
            camPosition -= normal * 0.25f;
        }*/
    }

}