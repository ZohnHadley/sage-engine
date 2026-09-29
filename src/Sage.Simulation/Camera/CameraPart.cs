#nullable enable
using System.Diagnostics.CodeAnalysis;

namespace Sage.Simulation;

// "camera": { "projection": "Orthographic", "orthoHeight": 12, "priority": 10 }
// "camera": { "fovY": 70, "target": "security_monitor", "viewport": { "x": 0, "y": 0, "width": 0.5, "height": 1 } }
//
// A camera, starting from the defaults a `Camera` component written by hand does not have (enabled,
// 45°, the whole screen): the options are the component's fields, so `{}` is a working perspective
// camera on the screen. Where it looks from is the entity's transform; place it, parent it, or let a
// rig drive it (CameraPose). The engine's own, like the director that reads it.
[Experimental("SAGE0123")]
[PrefabPart("camera", Plugin = RegistrationOwners.Core)]
public sealed class CameraPart : IPrefabPart
{
    [Property(Tooltip = "Perspective (3D) or Orthographic (2D, top-down, isometric)")]
    public CameraProjection Projection = CameraProjection.Perspective;
    [Property(Min = 1, Max = 179, Unit = "deg", Category = "Perspective", Tooltip = "Vertical field of view")]
    public float FovY = Camera.DefaultFovY;
    [Property(Min = 0, Unit = "m", Category = "Orthographic", Tooltip = "How much world the viewport shows, top to bottom")]
    public float OrthoHeight = Camera.DefaultOrthoHeight;
    [Property(Min = 0, Unit = "m", Tooltip = "Near clip plane")]
    public float Near = Camera.DefaultNear;
    [Property(Min = 0, Unit = "m", Tooltip = "Far clip plane")]
    public float Far = Camera.DefaultFar;
    [Property(Tooltip = "Of the enabled cameras drawing to one target, the highest wins")]
    public int Priority;
    [Property(Tooltip = "Off: never chosen, whatever its priority")]
    public bool Enabled = true;
    [Property(Category = "Output", Tooltip = "The part of the target it draws to, 0..1 from the top-left; empty = all of it")]
    public CameraViewport Viewport = CameraViewport.Full;
    [Property(Category = "Output", Tooltip = "The render target it draws to, by name; empty = the screen")]
    public string Target = "";

    public void Apply(in PrefabPartContext ctx)
    {
        if (Near <= 0f || Far <= Near) ctx.Warn($"near {Near} / far {Far} is not a depth range; the defaults are used");
        if (Projection == CameraProjection.Perspective && (FovY <= 0f || FovY >= 180f))
            ctx.Warn($"fovY {FovY} is not a field of view in degrees; {Camera.DefaultFovY} is used");
        ctx.World.Add(ctx.Entity, new Camera
        {
            Projection = Projection,
            FovY = FovY,
            OrthoHeight = OrthoHeight,
            Near = Near,
            Far = Far,
            Priority = Priority,
            Enabled = Enabled,
            Viewport = Viewport,
            Target = Target ?? "",
        });
    }
}
