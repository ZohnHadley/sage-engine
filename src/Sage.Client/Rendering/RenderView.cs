#nullable enable
using Microsoft.Xna.Framework;

namespace sage_engine;

// World resource: the camera matrices rendering uses this frame. Set by the host in FrameUpdate
// (before Render) from the active camera. Becomes RenderSnapshot.Views with Extract (docs/design/06, R9).
public sealed class RenderView
{
    public Matrix View = Matrix.Identity;
    public Matrix Projection = Matrix.Identity;
}
