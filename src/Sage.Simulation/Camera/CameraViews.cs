#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// One resolved view: a camera the director chose for a target this frame, with every value made
// usable (see CameraDirector). A plain value — copy it, keep it past the frame; nothing in it points
// back into the ECS except `Entity`, which is an id.
//
// Units and spaces:
//   Position   origin space (the same space as GlobalTransform; rendering subtracts it, 06 §3.3)
//   Rotation   the camera's orientation; it looks down its local -Z with +Y up (TransformMath)
//   FovY       **radians**, vertical (the component's is degrees), in (0, π)
//   OrthoHeight  metres of world the viewport shows top to bottom, > 0
//   Near, Far  metres, 0 < Near < Far
//   Viewport   normalised rect of the target from its top-left, never empty
[Experimental("SAGE0123")]
public struct CameraView
{
    public Entity Entity;               // the camera it came from; null (IsNull) = ActiveCamera (no camera entity, or cam_free)
    public string Target;               // render target name; "" = the screen. Never null.
    public CameraViewport Viewport;     // normalised, clamped, never empty
    public Vector3 Position;            // origin space
    public Quaternion Rotation;
    public CameraProjection Projection;
    public float FovY;                  // radians
    public float OrthoHeight;           // metres
    public float Near;
    public float Far;
    public int Priority;                // the camera's; int.MinValue for the ActiveCamera fallback

    public readonly bool IsScreen => string.IsNullOrEmpty(Target);
    public readonly bool FromActiveCamera => Entity.IsNull;
    public readonly Vector3 Forward => Vector3.Transform(TransformMath.Forward, Rotation);
    public readonly Vector3 Up => Vector3.Transform(TransformMath.Up, Rotation);

    // The matrices, from CameraMath. `aspect` is the viewport's width over height in pixels
    // (CameraMath.Aspect): only the target knows its size.
    public readonly Matrix4x4 View => CameraMath.View(Position, Rotation);
    public readonly Matrix4x4 ViewRelative => CameraMath.ViewRelative(Rotation);
    public readonly Matrix4x4 ProjectionMatrix(float aspect) => CameraMath.Projection(this, aspect);
}

// World resource (every world an Engine creates): the views to draw this frame, written by the
// CameraDirector at the end of its run in FrameUpdate and read from Extract on. **The contract the
// renderer builds on** (#77):
//
//   - At most one view per target. Off-screen targets come first, in ordinal order of their names,
//     and the screen view (Target == "") is **last**, so drawing in list order renders every target
//     before the screen that might show it.
//   - `Main` / `MainIndex` is the screen view: MainIndex is -1 when there is none (a headless world with
//     no ActiveCamera and no screen camera). With a camera entity on the screen it is that camera's; with
//     none it is made from ActiveCamera (a null Entity), so a renderer reading only this resource draws
//     exactly what one reading ActiveCamera did.
//   - While `cam_free` flies the editor camera (ActiveCamera.RigEnabled is false), the screen view is
//     ActiveCamera's whatever the camera entities say; off-screen views are unaffected.
//   - Pooled: the backing array grows and is reused, never shrinks, and a frame allocates nothing.
//     Hold a view by value, never a span or ref across frames.
//   - `Version` goes up every time the director resolves, so a reader can tell a fresh list from last
//     frame's (a paused Frame schedule still runs, so it normally moves every frame).
[Experimental("SAGE0123")]
public sealed class CameraViews
{
    private CameraView[] _views = new CameraView[4];

    public int Count { get; private set; }
    public int MainIndex { get; private set; } = -1;
    public long Version { get; private set; }

    public ReadOnlySpan<CameraView> Views => new(_views, 0, Count);

    public ref readonly CameraView this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            return ref _views[index];
        }
    }

    public bool HasMain => MainIndex >= 0;

    // The screen view; throws when there is none (check HasMain, or use TryGetMain).
    public ref readonly CameraView Main
    {
        get
        {
            if (MainIndex < 0) throw new InvalidOperationException("No screen view this frame (CameraViews.HasMain is false)");
            return ref _views[MainIndex];
        }
    }

    public bool TryGetMain(out CameraView view)
    {
        view = MainIndex >= 0 ? _views[MainIndex] : default;
        return MainIndex >= 0;
    }

    // The view drawing to `target` ("" = the screen), if any.
    public int IndexOf(string target)
    {
        for (int i = 0; i < Count; i++)
            if (string.Equals(_views[i].Target, target, StringComparison.Ordinal)) return i;
        return -1;
    }

    // ---- The director's side -----------------------------------------------------------------------

    internal void Begin()
    {
        Count = 0;
        MainIndex = -1;
    }

    // One per target: a camera replaces the one already there only by beating it — higher priority, or
    // the same priority and a lower entity id, so a tie resolves the same way every frame and in every
    // run whatever order the query visits them in.
    internal void Offer(in CameraView view)
    {
        int at = IndexOf(view.Target);
        if (at < 0)
        {
            if (Count == _views.Length) Array.Resize(ref _views, _views.Length * 2);
            _views[Count++] = view;
            return;
        }
        ref var held = ref _views[at];
        if (view.Priority > held.Priority || (view.Priority == held.Priority && view.Entity.Id < held.Entity.Id))
            held = view;
    }

    // The screen's view, whoever offered one: replaces it outright (cam_free, the ActiveCamera fallback).
    internal void SetScreen(in CameraView view)
    {
        int at = IndexOf("");
        if (at >= 0) { _views[at] = view; return; }
        if (Count == _views.Length) Array.Resize(ref _views, _views.Length * 2);
        _views[Count++] = view;
    }

    // Off-screen targets by name, the screen last. An insertion sort: a handful of views, no allocation.
    internal void End()
    {
        for (int i = 1; i < Count; i++)
        {
            var item = _views[i];
            int j = i - 1;
            while (j >= 0 && Compare(_views[j], item) > 0)
            {
                _views[j + 1] = _views[j];
                j--;
            }
            _views[j + 1] = item;
        }
        MainIndex = Count > 0 && _views[Count - 1].IsScreen ? Count - 1 : -1;
        Version++;
    }

    private static int Compare(in CameraView a, in CameraView b)
    {
        if (a.IsScreen != b.IsScreen) return a.IsScreen ? 1 : -1;
        return string.CompareOrdinal(a.Target, b.Target);
    }

    // Origin rebasing (Origin.Rebase): the views hold origin-space positions until the next resolve.
    internal void Rebase(Vector3 offset)
    {
        for (int i = 0; i < Count; i++) _views[i].Position += offset;
    }
}
