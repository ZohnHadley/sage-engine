#nullable enable
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Editing;

// The camera that has the screen, as the viewport's tools see it (issue #221): a ray through a pixel to
// pick and drag with (EditorPicking.RayFrom), and the pixel a point in the world lands on, to draw the
// gizmo over the picture. `Size` is the viewport in pixels, top left at (0, 0): in the editor that is the
// whole screen, because the panels lie over the picture's edges rather than squeezing it (§10f).
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public readonly record struct ViewportCamera(CameraView View, Vector2 Size)
{
    private CameraPose Pose => new(View.Position, View.Rotation);

    public EditorRay? RayThrough(Vector2 pixel) => View.Projection == CameraProjection.Orthographic
        ? EditorPicking.RayFromOrthographic(Pose, View.OrthoHeight, Size, pixel)
        : EditorPicking.RayFrom(Pose, View.FovY, Size, pixel);

    // The pixel `point` (origin space) is drawn at, or null when it is behind the camera (or on its
    // plane), where a perspective divide would turn it inside out.
    public Vector2? ToScreen(Vector3 point)
    {
        if (Size.X <= 0f || Size.Y <= 0f) return null;
        var view = CameraMath.View(View.Position, View.Rotation);
        float near = View.Near > 0f ? View.Near : 0.05f, far = View.Far > near ? View.Far : 1000f;
        var projection = View.Projection == CameraProjection.Orthographic
            ? CameraMath.Orthographic(View.OrthoHeight, Size.X / Size.Y, near, far)
            : CameraMath.Perspective(View.FovY, Size.X / Size.Y, near, far);
        var clip = Vector4.Transform(new Vector4(point, 1f), view * projection);
        if (clip.W <= 1e-5f) return null;
        if (View.Projection != CameraProjection.Orthographic && Vector3.Dot(point - View.Position, CameraMath.Forward(View.Rotation)) <= 1e-4f)
            return null;
        float x = clip.X / clip.W, y = clip.Y / clip.W;
        return new Vector2((x + 1f) * 0.5f * Size.X, (1f - y) * 0.5f * Size.Y);
    }

    // A gizmo `pixels` tall at `origin`, in metres (GizmoMath.ScreenConstantSize; an orthographic view
    // has one scale everywhere).
    public float SizeAt(Vector3 origin, float pixels) => View.Projection == CameraProjection.Orthographic
        ? (Size.Y > 0f ? View.OrthoHeight * pixels / Size.Y : 0f)
        : GizmoMath.ScreenConstantSize(View.Position, origin, View.FovY, Size.Y, pixels);
}
