#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// Sun shadows (issue 4h-4, REDESIGN §4.7, decision 1): one stable shadow map over the part of the view
// nearest the camera. **The maths is here and the drawing is not**, like the sky's and the lights': where
// the map sits, how big a texel is and which casters it keeps are questions a headless test asks; the
// client's `sage:shadow` pass only draws what `Fit` says (docs/design/06 "As built (sun shadows)").
//
// **Stable** means the map does not shimmer as the camera moves or turns:
//   - it is fitted to a *sphere* around the view's slice (near to `r_shadow_distance`), not to the slice's
//     corners, so turning the camera never changes its size (test: TheFitContainsTheViewSlice);
//   - its centre is snapped to whole texels **in absolute light space** — the origin sector's corner is
//     added back, in double — so a camera move smaller than a texel, or an origin rebase that moves every
//     number in memory by a sector, leaves the grid where it was in the world
//     (tests: ASubTexelCameraMoveKeepsTheTexelGrid, AnOriginRebaseKeepsTheTexelGrid).
//
// **Casters between the sun and the view are kept:** the map's depth range reaches `casterReach` metres
// past the sphere toward the sun, so a cliff behind you still shades the path in front
// (test: CastersBetweenTheSunAndTheViewAreKept).
//
// Everything is camera-relative, like the renderer (06 §3.3): `View` has no translation, and the matrices
// take a position relative to the camera the fit was made for.
[Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public readonly struct ShadowFit
{
    internal ShadowFit(Matrix4x4 view, Matrix4x4 projection, Vector3 center, float radius, float texelSize, float depthRange,
                     float depthBias, int size, double minX, double minY)
    {
        View = view;
        Projection = projection;
        ViewProj = view * projection;
        Center = center;
        Radius = radius;
        TexelSize = texelSize;
        DepthRange = depthRange;
        DepthBias = depthBias;
        Size = size;
        MinX = minX;
        MinY = minY;
    }

    public Matrix4x4 View { get; }          // camera-relative → light space (a rotation)
    public Matrix4x4 Projection { get; }    // light space → the map's clip space (orthographic; z in [0, 1])
    public Matrix4x4 ViewProj { get; }
    public Vector3 Center { get; }          // the slice's sphere, camera-relative
    public float Radius { get; }
    public float TexelSize { get; }         // metres per texel
    public float DepthRange { get; }        // metres the map's [0, 1] depth spans
    public float DepthBias { get; }         // in the map's depth units: what a surface's own depth is compared with a margin of
    public int Size { get; }                // texels per side
    public double MinX { get; }             // the map's corner in absolute light space: a whole number of texels
    public double MinY { get; }
}

[Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public static class ShadowMath
{
    // Decision 1's defaults: `r_shadow_size` and `r_shadow_distance`.
    public const int DefaultSize = 2048;
    public const float DefaultDistance = 60f;

    // How far toward the sun, past the slice's sphere, a caster is still drawn into the map. A low sun
    // throws long shadows: a 20 m tree 150 m away at dusk reaches the path.
    public const float DefaultCasterReach = 200f;

    // Below this sin(elevation) the sun casts nothing: at the horizon a shadow map is all grazing angles.
    public const float MinElevation = 0.01f;

    // The radius is rounded up to this, so a field of view that wobbles by a hair does not change the
    // texel size every frame.
    private const float RadiusStep = 0.25f;

    // The smallest sphere around a perspective view's slice from `near` to `far`, as the distance of its
    // centre along the view direction and its radius. `tanX`/`tanY` are the tangents of half the
    // horizontal and vertical fields of view. Depends only on the lens, never on where the camera looks.
    public static (float CenterDistance, float Radius) PerspectiveSlice(float near, float far, float tanX, float tanY)
    {
        float k2 = tanX * tanX + tanY * tanY;
        // Equidistant from the near and far corners; past the far plane (a wide lens), the far plane's centre.
        float c = MathF.Min(0.5f * (near + far) * (1f + k2), far);
        float toNear = MathF.Sqrt((c - near) * (c - near) + near * near * k2);
        float toFar = MathF.Sqrt((far - c) * (far - c) + far * far * k2);
        return (c, MathF.Max(toNear, toFar));
    }

    // The same for an orthographic view `halfWidth` by `halfHeight` metres.
    public static (float CenterDistance, float Radius) OrthographicSlice(float near, float far, float halfWidth, float halfHeight)
    {
        float half = 0.5f * (far - near);
        return (near + half, MathF.Sqrt(half * half + halfWidth * halfWidth + halfHeight * halfHeight));
    }

    // The shadow map for a view: `camera` in origin space, looking along `forward`, whose slice's sphere is
    // `centerDistance` ahead with `radius`; the sun's light travelling along `sunDirection`; `size` texels
    // a side. `origin` is where origin space's zero is absolutely (`Origin.ToAbsolute(Vector3.Zero)`),
    // which is what keeps the texel grid still across a rebase.
    public static ShadowFit Fit(Vector3 camera, Vector3 forward, float centerDistance, float radius, Vector3 sunDirection,
                                int size, Vector3 origin, float casterReach = DefaultCasterReach)
    {
        if (size < 2) throw new ArgumentOutOfRangeException(nameof(size), "A shadow map is at least two texels a side.");
        var sun = Vector3.Normalize(sunDirection);
        var up = MathF.Abs(sun.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var view = Matrix4x4.CreateLookAt(Vector3.Zero, sun, up);   // camera-relative: no translation

        radius = MathF.Ceiling(MathF.Max(radius, RadiusStep) / RadiusStep) * RadiusStep;
        // One texel spare: the corner snaps down by up to a texel, and the sphere must still fit.
        float texel = 2f * radius / (size - 1);

        var center = Vector3.Normalize(forward) * centerDistance;
        var local = Vector3.Transform(center, view);

        // The camera and the origin, in absolute light space, in double: a sector corner is kilometres out
        // and a texel is centimetres.
        double ax = (double)camera.X + origin.X, ay = (double)camera.Y + origin.Y, az = (double)camera.Z + origin.Z;
        double anchorX = ax * view.M11 + ay * view.M21 + az * view.M31;
        double anchorY = ax * view.M12 + ay * view.M22 + az * view.M32;

        double minX = Math.Floor((anchorX + local.X - radius) / texel) * texel;
        double minY = Math.Floor((anchorY + local.Y - radius) / texel) * texel;
        float left = (float)(minX - anchorX), bottom = (float)(minY - anchorY);
        float right = left + size * texel, top = bottom + size * texel;

        // Light space looks down -Z: nearer the sun is larger z. The near plane is pulled back toward the sun.
        float zNear = -(local.Z + radius + casterReach);
        float zFar = -(local.Z - radius);
        var projection = Matrix4x4.CreateOrthographicOffCenter(left, right, bottom, top, zNear, zFar);

        float range = zFar - zNear;
        float bias = (1.5f * texel + 0.05f) / range;   // a texel and a half, and five centimetres
        return new ShadowFit(view, projection, center, radius, texel, range, bias, size, minX, minY);
    }

    // A camera-relative point in the map's clip space: x and y in [-1, 1] and z in [0, 1] are on the map.
    public static Vector3 ToClip(in ShadowFit fit, Vector3 relative)
    {
        var clip = Vector4.Transform(new Vector4(relative, 1f), fit.ViewProj);
        return new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
    }

    // Whether the map holds a camera-relative point: a caster there is drawn, a surface there is shadowed.
    public static bool Contains(in ShadowFit fit, Vector3 relative)
    {
        var c = ToClip(fit, relative);
        const float E = 1e-4f;
        return c.X >= -1f - E && c.X <= 1f + E && c.Y >= -1f - E && c.Y <= 1f + E && c.Z >= -E && c.Z <= 1f + E;
    }

    // Where a camera-relative point falls on the map, in texels from its corner (x right, y down, as the
    // texture is sampled).
    public static Vector2 TexelOf(in ShadowFit fit, Vector3 relative)
    {
        var c = ToClip(fit, relative);
        return new Vector2((c.X * 0.5f + 0.5f) * fit.Size, (0.5f - c.Y * 0.5f) * fit.Size);
    }

    // How dark the shadows are drawn: the environment's strength (the sky's `shadow`, softened by weather;
    // 1 in a world with no sky), and nothing at all once the sun is at or below the horizon — whatever set
    // the environment (test: ShadowStrengthIsZeroBelowTheHorizon).
    public static float Strength(float environment, Vector3 sunDirection)
    {
        if (sunDirection.LengthSquared() < 1e-12f) return 0f;
        float elevation = -Vector3.Normalize(sunDirection).Y;   // the light travels down when the sun is up
        return elevation <= MinElevation ? 0f : Math.Clamp(environment, 0f, 1f);
    }

    // What casts a shadow (the extract's rule): an opaque material that says so. Alpha-tested and
    // transparent ones do not (no clip in the caster yet), nor do sprites, particles, debug lines or the
    // viewmodel, which the caster view never collects (test: OnlyOpaqueMaterialsThatSaySoCastShadows).
    public static bool Casts(RenderPass pass, bool castShadows) => castShadows && pass == RenderPass.Opaque;
}
