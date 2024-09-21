
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using sage_engine;

public class Camera
{
    public Vector3 camTarget;
    private Vector3 camPosition;

    public Matrix projectionMatrix; // converts 3d to 2d a.k.a the lens (what the camera cans see)
    public Matrix viewMatrix; // cameras physical position in the world (location and orientation) 

    private Vector3 camForward = Vector3.Forward;
    private Vector3 camUp = Vector3.Up;
    private float mouseAmount = 0.005f;

    KeyboardState keyState;
    private MouseState mouseState;
    private MouseState prevMouseState;
     

    public Camera(float aspect_ratio, Vector3 position, Vector3 rotation)
    {
        prevMouseState = Mouse.GetState();
        //camTarget in fonrt of camPosition with a distance of 10 from z axis
        camTarget = new Vector3(0, 0, 0);
        camPosition = position;
        //set rotation 
        camForward = Vector3.Transform(camForward, Matrix.CreateFromYawPitchRoll(MathHelper.ToRadians(rotation.Y), MathHelper.ToRadians(rotation.X), MathHelper.ToRadians(rotation.Z)));
        projectionMatrix = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(45f), aspect_ratio, 1, 1000); // screen aspect ration and render distance
        viewMatrix = Matrix.CreateLookAt(camPosition, (camPosition + camForward), Vector3.Up);

    }



    public void update(GameTime gameTime)
    {
        keyState = Keyboard.GetState();
        mouseState = Mouse.GetState();

        if (EditorUI.getIsHovered() == false)
        {
            
            editorCameraMovement(keyState, mouseState);
        }


        viewMatrix = Matrix.CreateLookAt(camPosition, (camPosition + camForward), camUp);

    }

    private void editorCameraMovement(KeyboardState keyState, MouseState mouseState)
    {


        Vector3 direction = camForward;
        direction.Normalize();
        Vector3 normal = Vector3.Cross(direction, camUp);

        if (mouseState.LeftButton == ButtonState.Pressed)
        {
            Vector2 pos = Vector2.SmoothStep(new Vector2(prevMouseState.X, prevMouseState.Y), new Vector2(mouseState.X, mouseState.Y), 0.5f);
            float y = mouseState.Y - prevMouseState.Y;
            float x = mouseState.X - prevMouseState.X;

            y *= 480 / (GraphicsAdapter.DefaultAdapter.CurrentDisplayMode.Height * 1.0f);
            x *= 800 / (GraphicsAdapter.DefaultAdapter.CurrentDisplayMode.Width * 1.0f);

            camForward += x * mouseAmount * normal;
            camForward -= y * mouseAmount * camUp;
            camForward.Normalize();

            prevMouseState = mouseState;
        }

        if (mouseState.LeftButton == ButtonState.Released)
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
            camPosition += normal * 0.25f;
        }

        if (keyState.IsKeyDown(Keys.A))
        {
            camPosition -= normal * 0.25f;
        }
    }

}