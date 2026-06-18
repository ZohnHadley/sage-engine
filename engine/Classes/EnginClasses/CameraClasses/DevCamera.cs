
using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace sage_engine;
internal class DevCamera 
{

    private GraphicsDeviceManager graphics_device_manager;

    private Vector3 _camTarget;
    public Vector3 Target{
        get{return _camTarget;}
        set{_camTarget=value;}    
    }
    private Vector3 _camPosition;
    public Vector3 Position
    {
        get{return _camPosition;}
        set{_camPosition=value;}
    }
    private Matrix projectionMatrix; // converts 3d to 2d a.k.a the lens (what the camera cans see)
    private Matrix viewMatrix; // cameras physical position in the world (location and orientation) 

    private Vector3 camForward = Vector3.Forward;
    private Vector3 camUp = Vector3.Up;
    private float yaw;
    private float pitch;
    private float mouseAmount = 1f;
    private float speed = 10;
    private static readonly float PitchLimit = MathHelper.ToRadians(89f);


    public DevCamera(GraphicsDeviceManager graphicsDeviceManager, float aspect_ratio, Vector3 position, Vector3 rotation)
    {
        // Mouse-look is a right-button drag. The drag gesture's threshold + anchor
        // reset on each press is what prevents the old camera "snap" (TODO #19) —
        // no manual first-delta bookkeeping needed.
        InputSystem.getInstance().OnMouseDrag += OnMouseDrag;
        graphics_device_manager = graphicsDeviceManager;
        _camPosition = position;

        yaw = MathHelper.ToRadians(rotation.Y);
        pitch = MathHelper.Clamp(MathHelper.ToRadians(rotation.X), -PitchLimit, PitchLimit);
        rebuildForward();

        _camTarget = camForward;
        projectionMatrix = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(45f), aspect_ratio, 0.01f, 1000);
        viewMatrix = Matrix.CreateLookAt(_camPosition, (_camPosition + camForward), Vector3.Up);
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


    public void update(GameTime gameTime)
    {
        // Movement is polled here each frame; mouse-look is still applied via the
        // mouse event subscriptions. InputSystem is polled centrally in Game1.Update
        // (before this), so its state is current.
        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        InputSystem input = InputSystem.getInstance();

        Vector3 forward = camForward;
        forward.Normalize();
        Vector3 right = Vector3.Cross(forward, camUp);

        Vector3 move = Vector3.Zero;
        if (input.IsKeyDown(Keys.W)) move += forward;
        if (input.IsKeyDown(Keys.S)) move -= forward;
        if (input.IsKeyDown(Keys.D)) move += right;
        if (input.IsKeyDown(Keys.A)) move -= right;

        // Normalize so diagonal movement isn't faster, then scale by speed * dt
        // for frame-rate-independent movement.
        if (move != Vector3.Zero)
        {
            move.Normalize();
            _camPosition += move * speed * dt;
        }

        viewMatrix = Matrix.CreateLookAt(_camPosition, (_camPosition + camForward), camUp);
    }

    private void OnMouseDrag(MouseButton button, Point delta)
    {
        if (button != MouseButton.RIGHT)
            return;

        // Scale the raw pixel delta by the back-buffer / display ratio so look
        // sensitivity is consistent across window/display resolutions.
        float mouse_x = delta.X * (graphics_device_manager.PreferredBackBufferWidth / (GraphicsAdapter.DefaultAdapter.CurrentDisplayMode.Width * 1.0f));
        float mouse_y = delta.Y * (graphics_device_manager.PreferredBackBufferHeight / (GraphicsAdapter.DefaultAdapter.CurrentDisplayMode.Height * 1.0f));

        yaw -= mouse_x * mouseAmount * 0.01f;
        pitch -= mouse_y * mouseAmount * 0.01f;
        yaw = MathHelper.WrapAngle(yaw);
        pitch = MathHelper.Clamp(pitch, -PitchLimit, PitchLimit);
        rebuildForward();
    }
}