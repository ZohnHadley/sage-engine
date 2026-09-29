using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ImGuiNET;

namespace Sage.Editor;

// The editor's free-fly camera (docs/design/15): WASD to fly, right-drag to look. It only moves a
// position and yaw/pitch; DevTools drives a camera entity with them (a `DebugCamera`, issue #81), which
// the director resolves like any other camera and mirrors into ActiveCamera when it has the screen.
// Editor/camera code may read devices directly (08 §3.1); moving it onto Editor-context actions is
// 08 §14 step 4.
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
    // Orientation (yaw, then pitch), the same rotation that builds camForward; the debug camera's pose.
    public Quaternion Rotation => Quaternion.CreateFromYawPitchRoll(yaw, pitch, 0);

    private Vector3 camForward = Vector3.Forward;
    private readonly Vector3 camUp = Vector3.Up;
    private float yaw;
    private float pitch;
    private readonly float speed = 3f;
    private static readonly float PitchLimit = MathHelper.ToRadians(89f);
    private readonly InputDevices devices;
    private readonly InputActions actions;

    // Whether it flies at all this frame: only while something shows it (the screen, or the editor
    // viewport with the mouse or focus on it). Hidden, it stays where it was rather than following the
    // player's WASD around (issue #81: DevTools decides, each frame).
    public bool Active { get; set; } = true;

    // The mouse is over the editor viewport's picture: a right-drag there turns the camera even though
    // ImGui has the mouse (it does over any of its windows).
    public bool MouseOverViewport { get; set; }

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

    // Looks the way `rotation` does (its forward; roll is dropped): `cam_free` starts from the view that
    // had the screen rather than from wherever the free camera was left.
    public void LookAlong(Quaternion rotation)
    {
        var forward = Vector3.Transform(Vector3.Forward, rotation);
        if (forward.LengthSquared() < 1e-8f) return;
        forward.Normalize();
        yaw = MathHelper.WrapAngle(MathF.Atan2(-forward.X, -forward.Z));
        pitch = MathHelper.Clamp(MathF.Asin(Math.Clamp(forward.Y, -1f, 1f)), -PitchLimit, PitchLimit);
        RebuildForward();
    }

    public void Update(GameTime gameTime)
    {
        // Devices are polled centrally in Game1.Update (before this), so their state is current.
        if (!Active) return;
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
        if (button != MouseButton.RIGHT || !Active || (ImGui.GetIO().WantCaptureMouse && !MouseOverViewport))
            return;   // dragging inside an ImGui window (console, inspector) doesn't turn the camera; the viewport's picture does

        float rate = LookRadiansPerPixel * actions.MouseSensitivity;
        yaw = MathHelper.WrapAngle(yaw - delta.X * rate);
        pitch = MathHelper.Clamp(pitch - delta.Y * rate * (actions.InvertMouseY ? -1 : 1), -PitchLimit, PitchLimit);
        RebuildForward();
    }
}
