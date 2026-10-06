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
// **Cascades** (issue 4n-11): the view from its near plane to `r_shadow_distance` is cut into up to three
// slices (`CascadeSplits`, a blend of even and logarithmic splits), and each slice gets its own map fitted
// as above (`FitCascades`), so the ground at your feet has centimetre texels and the hillside 100 m off
// still has a shadow. Each cascade is stable on its own: its own sphere, its own snapped grid
// (test: EveryCascadeKeepsItsTexelGrid). The maps share one render target, laid out by `Atlas`, and a
// surface reads the first cascade that holds it with a texel to spare for the filter (`CascadeOf`, the
// shader's choice; test: EachCascadeHoldsItsSliceAndTheNearerOnesAreFiner).
//
// **The sun's direction is quantised** (`QuantiseSun`): its elevation and azimuth snap to a step
// (`r_shadow_sun_step` degrees), so the light's axes, and with them every cascade's texel grid, hold
// still while the sun creeps across the sky, and move once a step instead of every frame
// (test: AQuantisedSunHoldsTheGridStill). Lighting still uses the true sun.
//
// **Alpha-tested materials cast cut-out shadows** (`Casts`, `CasterTechnique`): a leaf card or a fence
// drawn with an AlphaTested material is drawn into the map with its texture's alpha clipped at its
// `AlphaCutoff`, as the scene draws it; sprites do the same, turned to face the sun (`BillboardAxes`)
// (test: ALeafBillboardCastsACutOutShadow).
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
    // Decision 1's defaults: `r_shadow_size` (texels a side of each cascade) and `r_shadow_distance`.
    // Three cascades reach well past the single map's 60 m (issue 4n-11).
    public const int DefaultSize = 2048;
    public const float DefaultDistance = 150f;

    // Cascades (issue 4n-11): `r_shadow_cascades`, 1 to MaxCascades (the shaders read three).
    public const int DefaultCascades = 3;
    public const int MaxCascades = 3;

    // How far the splits lean from even (0) toward logarithmic (1): mostly logarithmic, so the nearest
    // cascade is a few metres deep, with enough of the even split that the far ones are not enormous.
    public const float DefaultSplitBlend = 0.75f;

    // The largest render target the atlas of cascades may be, a side (MonoGame's HiDef limit).
    public const int MaxAtlasSize = 4096;

    // `r_shadow_sun_step`: the sun's direction snaps to this many degrees of elevation and azimuth.
    public const float DefaultSunStep = 0.25f;

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
        var sun = Vector3.Normalize(sunDirection);
        var up = MathF.Abs(sun.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var view = Matrix4x4.CreateLookAt(Vector3.Zero, sun, up);   // camera-relative: no translation

        if (size < 4) throw new ArgumentOutOfRangeException(nameof(size), "A shadow map is at least four texels a side.");
        radius = MathF.Ceiling(MathF.Max(radius, RadiusStep) / RadiusStep) * RadiusStep;
        // Three texels spare: the corner snaps down by up to a texel, and the filter's texel on each side
        // of the sphere must still be on this map (CascadeOf keeps one texel off each edge).
        float texel = 2f * radius / (size - 3);

        var center = Vector3.Normalize(forward) * centerDistance;
        var local = Vector3.Transform(center, view);

        // The camera and the origin, in absolute light space, in double: a sector corner is kilometres out
        // and a texel is centimetres.
        double ax = (double)camera.X + origin.X, ay = (double)camera.Y + origin.Y, az = (double)camera.Z + origin.Z;
        double anchorX = ax * view.M11 + ay * view.M21 + az * view.M31;
        double anchorY = ax * view.M12 + ay * view.M22 + az * view.M32;

        double minX = (Math.Floor((anchorX + local.X - radius) / texel) - 1) * texel;
        double minY = (Math.Floor((anchorY + local.Y - radius) / texel) - 1) * texel;
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

    // What casts a shadow (the extract's rule): an opaque or alpha-tested material that says so — an
    // alpha-tested one with its cut-out (issue 4n-11; `CasterTechnique`), meshes and sprites alike.
    // Transparent ones do not, nor do particles, decals, debug lines or the viewmodel, which the caster
    // views never collect (tests: OnlyOpaqueMaterialsThatSaySoCastShadows, AlphaTestedMaterialsCastCutOutShadows).
    public static bool Casts(RenderPass pass, bool castShadows) =>
        castShadows && (pass == RenderPass.Opaque || pass == RenderPass.AlphaTested);

    // The technique a caster is drawn into the map with. An opaque mesh needs only its depth, so it is
    // drawn with the default effect's `ShadowCaster` (`ShadowCasterSkinned`) whatever its material; an
    // alpha-tested one is drawn with its own material's effect and parameters and that effect's
    // `ShadowCasterAlphaTest` (`ShadowCasterAlphaTestSkinned`), which clips at its `AlphaCutoff` as the
    // scene does. lit.fx and sprite.fx have them; an effect without one casts nothing, said once.
    public static string CasterTechnique(RenderPass pass, bool skinned) => pass == RenderPass.AlphaTested
        ? (skinned ? "ShadowCasterAlphaTestSkinned" : "ShadowCasterAlphaTest")
        : (skinned ? "ShadowCasterSkinned" : "ShadowCaster");

    // Whether a texel of a cut-out caster is drawn into the map: the shaders' `clip(alpha - cutoff)`,
    // which keeps alpha at the cutoff and discards what is below it.
    public static bool CutOutKeeps(float alpha, float cutoff) => alpha - cutoff >= 0f;

    // ---- Cascades (issue 4n-11) ----

    // Where each of `splits.Length` cascades ends, from `near` to `far`: a blend of the even split and the
    // logarithmic one (`blend` 0 even, 1 logarithmic). The last is `far`; each is past the one before.
    public static void CascadeSplits(float near, float far, Span<float> splits, float blend = DefaultSplitBlend)
    {
        int n = splits.Length;
        if (n == 0) return;
        if (!(far > near)) throw new ArgumentOutOfRangeException(nameof(far), "The far end is past the near one.");
        float logNear = MathF.Max(near, 1e-3f);
        blend = Math.Clamp(blend, 0f, 1f);
        for (int i = 1; i < n; i++)
        {
            float f = (float)i / n;
            float even = near + (far - near) * f;
            float log = logNear * MathF.Pow(far / logNear, f);
            splits[i - 1] = even + (log - even) * blend;
        }
        splits[n - 1] = far;
    }

    // The maps of a cascaded view: `fits.Length` cascades (1 to MaxCascades) from `near` to `far`, each
    // `Fit` to its own slice's sphere, with `splits` (as long as `fits`) where each ends. The lens is the
    // view's: `orthographic` false with `lensX`/`lensY` the tangents of half its fields of view, or true with
    // them its half width and height in metres.
    public static void FitCascades(Vector3 camera, Vector3 forward, bool orthographic, float lensX, float lensY, float near, float far,
                                   Vector3 sunDirection, int size, Vector3 origin, Span<ShadowFit> fits, Span<float> splits,
                                   float casterReach = DefaultCasterReach, float blend = DefaultSplitBlend)
    {
        if (fits.Length is < 1 or > MaxCascades) throw new ArgumentOutOfRangeException(nameof(fits), $"1 to {MaxCascades} cascades.");
        if (splits.Length != fits.Length) throw new ArgumentException("One split per cascade.", nameof(splits));
        CascadeSplits(near, far, splits, blend);
        float from = near;
        for (int i = 0; i < fits.Length; i++)
        {
            var (centre, radius) = orthographic
                ? OrthographicSlice(from, splits[i], lensX, lensY)
                : PerspectiveSlice(from, splits[i], lensX, lensY);
            fits[i] = Fit(camera, forward, centre, radius, sunDirection, size, origin, casterReach);
            from = splits[i];
        }
    }

    // Which cascade a camera-relative point is shadowed by: the first whose map holds it with a texel to
    // spare on every side (the 2x2 filter reads its neighbour), within its depth range — common.fxh's
    // `ShadowLit` makes the same choice. -1: none (the sun is unshadowed there).
    public static int CascadeOf(ReadOnlySpan<ShadowFit> fits, Vector3 relative)
    {
        for (int i = 0; i < fits.Length; i++)
        {
            var c = ToClip(fits[i], relative);
            float edge = 1f - 2f / fits[i].Size;   // one texel, in clip units
            if (MathF.Abs(c.X) <= edge && MathF.Abs(c.Y) <= edge && c.Z >= 0f && c.Z <= 1f) return i;
        }
        return -1;
    }

    // How the cascades share one render target: `cascades` maps of `CascadeSize` texels a side (`size`,
    // or less, so the whole is at most `maxTexture` a side), side by side in up to two columns and rows.
    public static ShadowAtlas Atlas(int cascades, int size, int maxTexture = MaxAtlasSize)
    {
        cascades = Math.Clamp(cascades, 1, MaxCascades);
        int columns = cascades == 1 ? 1 : 2;
        int rows = cascades <= 2 ? 1 : 2;
        int cascadeSize = Math.Max(4, Math.Min(size, maxTexture / Math.Max(columns, rows)));
        return new ShadowAtlas(cascades, cascadeSize, columns, rows);
    }

    // ---- The sun (issue 4n-11) ----

    // The sun's direction with its elevation and azimuth snapped to `stepDegrees` (0: as it is). Two
    // directions within the same step give the same answer, so the same light axes and the same grids.
    public static Vector3 QuantiseSun(Vector3 sunDirection, float stepDegrees)
    {
        if (sunDirection.LengthSquared() < 1e-12f) return sunDirection;
        var d = Vector3.Normalize(sunDirection);
        if (!(stepDegrees > 0f)) return d;
        double step = stepDegrees * Math.PI / 180.0;
        double elevation = Math.Asin(Math.Clamp(-d.Y, -1f, 1f));   // the light travels down when the sun is up
        double azimuth = Math.Atan2(d.X, d.Z);
        elevation = Math.Round(elevation / step) * step;
        azimuth = Math.Round(azimuth / step) * step;
        double c = Math.Cos(elevation);
        return Vector3.Normalize(new Vector3((float)(c * Math.Sin(azimuth)), (float)-Math.Sin(elevation), (float)(c * Math.Cos(azimuth))));
    }

    // Which way a billboard's quad runs when it is drawn into a map: turned to face the sun, as the
    // sprite batcher turns it to face a camera whose axes are the light's (`View`). A cylindrical one
    // (a tree, a character) stays upright and turns about Y only; a spherical one faces the sun fully.
    // Its shadow is then its silhouette as the sun sees it, whichever way the player looks.
    public static (Vector3 Right, Vector3 Up) BillboardAxes(in ShadowFit fit, bool cylindrical)
    {
        var right = new Vector3(fit.View.M11, fit.View.M21, fit.View.M31);
        var up = new Vector3(fit.View.M12, fit.View.M22, fit.View.M32);
        if (!cylindrical) return (right, up);
        var flat = new Vector3(right.X, 0f, right.Z);
        return (flat.LengthSquared() > 1e-8f ? Vector3.Normalize(flat) : right, Vector3.UnitY);
    }
}

// How the cascades of a shadow map share its one render target (issue 4n-11): `Cascades` squares of
// `CascadeSize` texels in `Columns` by `Rows`, cascade k at column k % Columns, row k / Columns.
[Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public readonly struct ShadowAtlas
{
    internal ShadowAtlas(int cascades, int cascadeSize, int columns, int rows)
    {
        Cascades = cascades;
        CascadeSize = cascadeSize;
        Columns = columns;
        Rows = rows;
    }

    public int Cascades { get; }
    public int CascadeSize { get; }
    public int Columns { get; }
    public int Rows { get; }
    public int Width => Columns * CascadeSize;
    public int Height => Rows * CascadeSize;

    // Cascade k's corner in the target, in texels.
    public (int X, int Y) Corner(int cascade) => (cascade % Columns * CascadeSize, cascade / Columns * CascadeSize);
}
