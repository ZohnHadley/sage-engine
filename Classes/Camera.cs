
using System;
using System.Transactions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

public class Camera
{
    public Vector3 camTarget;
    private Vector3 camPosition;

    public Matrix projectionMatrix; // converts 3d to 2d a.k.a the lens (what the camera cans see)
    public Matrix viewMatrix; // cameras physical position in the world (location and orientation) 
    Vector3 camForward = Vector3.Forward;
    Vector3 camUp = Vector3.Up;
    float mouseAmount = 0.05f;
    private GraphicsDevice graphicsDevice;

    private MouseState mouseState;
    MouseState prevMouseState;

    public Camera(float aspect_ratio, Vector3 position, Vector3 rotation)
    {
        prevMouseState = Mouse.GetState();
        //camTarget in fonrt of camPosition with a distance of 10 from z axis
        camTarget = new Vector3(0, 0, 0);
        camPosition = position;

        projectionMatrix = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(45f), aspect_ratio, 1, 1000); // screen aspect ration and render distance
        viewMatrix = Matrix.CreateLookAt(camPosition, camTarget, Vector3.Up);

    }



    public void update(GameTime gameTime)
    {
        Vector3 direction = camForward;
        direction.Normalize();

        Vector3 normal = Vector3.Cross(direction, camUp); // if y and z then x or if x and z then y or if x and y then z


        KeyboardState keyState = Keyboard.GetState();
        mouseState = Mouse.GetState();

        if (mouseState.LeftButton == ButtonState.Pressed)
        {
            Vector2 pos = Vector2.SmoothStep(new Vector2(prevMouseState.X, prevMouseState.Y), new Vector2(mouseState.X, mouseState.Y), 0.5f);
            float y = mouseState.Y - prevMouseState.Y;
            float x = mouseState.X - prevMouseState.X;

            y *= 480 / (GraphicsAdapter.DefaultAdapter.CurrentDisplayMode.Height * 1.0f);
            x *= 800 / (GraphicsAdapter.DefaultAdapter.CurrentDisplayMode.Width * 1.0f);
            Console.WriteLine("X: " + x + " Y: " + y);

            camForward += x * mouseAmount * normal;
            camForward -= y * mouseAmount * camUp;
            camForward.Normalize();
            
            prevMouseState = mouseState;
        }

        if(mouseState.LeftButton == ButtonState.Released)
        {
            prevMouseState = mouseState;
        }

        if (keyState.IsKeyDown(Keys.W))
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
            //camForward += 1f * 0.05f * normal;
            camPosition.X += 0.25f;
        }

        if (keyState.IsKeyDown(Keys.A))
        {
            //camForward -= 1f * 0.05f * normal;
            camPosition.X -= 0.25f;
        }
        viewMatrix = Matrix.CreateLookAt(camPosition, (camPosition + camForward), camUp);

        //update position in wolrd matrix

    }

}