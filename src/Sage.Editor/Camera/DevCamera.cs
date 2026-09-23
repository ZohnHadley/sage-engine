using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ImGuiNET;

namespace sage_engine;

// The editor's free-fly camera (docs/design/15): WASD to fly, right-drag to look. It only moves a
// position and yaw/pitch; the host publishes them as the world's ActiveCamera, and CameraExtract
// builds the view and projection from that (06 §3.3). Editor/camera code may read devices directly
// (08 §3.1); moving it onto Editor-context actions is 08 §14 step 4.
internal class DevCamera
{
    // Radians per pixel of mouse travel, times m_sensitivity. A plain per-pixel rate: look speed no
    // longer depends on the window or display size (TODO #39).
    private const float LookRadiansPerPixel = 0.004f;

    private Vector3 _camPosition;
    public Vector3 Position
    {
        get { return _camPosition; }
        set { _camPosition = value; }
    }
    // Orientation (yaw, then pitch), the same rotation that builds camForward; published as ActiveCamera.Rotation.
    public Quaternion Rotation => Quaternion.CreateFromYawPitchRoll(yaw, pitch, 0);

    private Vector3 camForward = Vector3.Forward;
    private readonly Vector3 camUp = Vector3.Up;
    private float yaw;
    private float pitch;
    private readonly float speed = 3f;
    private static readonly float PitchLimit = MathHelper.ToRadians(89f);
    private readonly InputDevices devices;
    private readonly InputActions actions;

    public DevCamera(InputDevices devices, InputActions actions, Vector3 position, Vector3 rotationDegrees)
    {
        this.devices = devices;
        this.actions = actions;
        // Mouse-look is a right-button drag. The drag gesture's threshold + anchor
        // reset on each press is what prevents the old camera "snap" (TODO #19) —
        // no manual first-delta bookkeeping needed.
        devices.Mouse.OnDrag += OnMouseDrag;
        _camPosition = position;

        yaw = MathHelper.ToRadians(rotationDegrees.Y);
        pitch = MathHelper.Clamp(MathHelper.ToRadians(rotationDegrees.X), -PitchLimit, PitchLimit);
        RebuildForward();
    }

    private void RebuildForward()
    {
        camForward = Vector3.Transform(Vector3.Forward, Matrix.CreateFromYawPitchRoll(yaw, pitch, 0));
    }

    // For `cam_set` and, later, the editor's viewport bookmarks.
    public void SetLook(float yawDegrees, float pitchDegrees)
    {
        yaw = MathHelper.WrapAngle(MathHelper.ToRadians(yawDegrees));
        pitch = MathHelper.Clamp(MathHelper.ToRadians(pitchDegrees), -PitchLimit, PitchLimit);
        RebuildForward();
    }

    public void Update(GameTime gameTime)
    {
        // Devices are polled centrally in Game1.Update (before this), so their state is current.
        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;

        Vector3 forward = Vector3.Normalize(camForward);
        Vector3 right = Vector3.Cross(forward, camUp);

        Vector3 move = Vector3.Zero;
        // Don't fly while typing in the console or an editor field (the Editor input context replaces this, 08 §14 step 4).
        bool typing = ImGui.GetIO().WantCaptureKeyboard;
        var keyboard = devices.Keyboard;
        if (!typing && keyboard.IsKeyDown(Keys.W)) move += forward;
        if (!typing && keyboard.IsKeyDown(Keys.S)) move -= forward;
        if (!typing && keyboard.IsKeyDown(Keys.D)) move += right;
        if (!typing && keyboard.IsKeyDown(Keys.A)) move -= right;

        // Normalize so diagonal movement isn't faster, then scale by speed * dt
        // for frame-rate-independent movement.
        if (move != Vector3.Zero)
        {
            move.Normalize();
            _camPosition += move * speed * dt;
        }
    }

    private void OnMouseDrag(MouseButton button, Point delta)
    {
        if (button != MouseButton.RIGHT || ImGui.GetIO().WantCaptureMouse)
            return;   // dragging inside an ImGui window (console, inspector) doesn't turn the camera

        float rate = LookRadiansPerPixel * actions.MouseSensitivity;
        yaw = MathHelper.WrapAngle(yaw - delta.X * rate);
        pitch = MathHelper.Clamp(pitch - delta.Y * rate * (actions.InvertMouseY ? -1 : 1), -PitchLimit, PitchLimit);
        RebuildForward();
    }
}
