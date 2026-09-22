#nullable enable
using System.Numerics;

namespace sage_engine;

// World resource every World has: where the active camera is, in origin space. The host (or the
// camera rig that owns the view) updates it every frame; gameplay reads it (e.g. "face the player's
// view"), without depending on the editor or the renderer.
public sealed class ActiveCamera
{
    public Vector3 Position;
    public Quaternion Rotation = Quaternion.Identity;
}
