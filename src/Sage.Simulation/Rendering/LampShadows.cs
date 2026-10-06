#nullable enable
using System;
using System.Numerics;

namespace Sage.Simulation;

// Lamp shadows (issue #315's last part, after the sun's cascades of 4n-11): a point light or a spot cone
// that says `shadows` draws what is around it into a depth map, and the lit shaders darken its light
// where something stands between it and the surface. **The decisions and the maths are here, headless;
// the client's `sage:shadow` only draws what they say**, as for the sun (ShadowMath).
//
//   Opt-in, per light: `PointLight.Shadows` (a `light` part's `"shadows": true`), off by default, so a
//            level's lamps cost nothing until a designer asks for it — no map, no caster view, no draw
//            (test: ALampWithShadowsOffCostsNoMap).
//   A budget: of the frame's lamps that cast, the `r_shadow_lamps` (default 2, at most MaxLamps) nearest
//            the camera get a map (`Choose`; nearest meaning the nearest edge of their range, so a lamp
//            whose light the camera stands in comes first). The rest light without shadows
//            (test: TheNearestLampsGetMapsWithinTheBudget).
//   One atlas: every lamp has a block of 3 x 2 tiles of `r_shadow_lamp_size` texels in one R32F target,
//            the blocks stacked down it (`Atlas`), sized by the budget so it is never remade.
//   A spot light (a cone up to MaxSpotCone) draws one perspective view down its cone into its block's
//            first tile; a point light, or a wider spot, draws a **cube**: six 90° views along +X, -X,
//            +Y, -Y, +Z, -Z, one per tile (`Fit`; tests: ASpotMapLooksDownItsCone, ACubeMapsFacesCoverEveryDirection).
//   The depth stored is the caster's z/w through the face's perspective projection (near `Near`, far the
//            light's range) — the casters are drawn with the same ShadowCaster techniques as the sun's —
//            and the shaders turn a surface's distance along the face's axis into the same z/w
//            (`QueryDepth`) to compare. The lookup (`Project`) is analytic, no matrices in the shader: the
//            face is the major axis of the light-to-surface vector, and its uv the other two axes over it
//            (test: TheShaderLookupMatchesTheCasterMatrices). A point behind an occluder reads the
//            occluder's depth and is shadowed; one in front of it is not (test: APointBehindAnOccluderIsShadowed).
//   Per draw, at most `PerDraw` (2) of the four lamps it is lit by read a map — the renderer puts the
//            ones with maps first (`DrawOrder`), and the shaders look up the first two — which keeps the
//            pixel shaders inside ps_3_0's budget.
//
// Everything is camera-relative, like the renderer (06 §3.3); the lookup's numbers do not depend on where
// the camera is (a direction, and distances from the lamp), so every view reads the one atlas.
internal static class LampShadows
{
    // `r_shadow_lamps`: how many lamps a frame gives a map, and the most it may.
    public const int DefaultLamps = 2;
    public const int MaxLamps = 4;

    // How many of a draw's four lamps the shaders look up (common.fxh MAX_LAMP_SHADOWS).
    public const int PerDraw = 2;

    // `r_shadow_lamp_size`: texels a side of each face.
    public const int DefaultSize = 512;
    public const int MinSize = 16;

    // A cube's faces, and a block's tiles: three across, two down.
    public const int CubeFaces = 6;
    public const int BlockColumns = 3;
    public const int BlockRows = 2;

    // The perspective's near plane, in metres: a caster closer to the lamp than this is not drawn.
    public const float Near = 0.05f;

    // The widest spot drawn as one view (a half-angle, degrees): wider cones are drawn as a cube. The map's
    // field of view is the cone's plus `SpotMargin` a side, so the filter's texels at the edge are on it.
    public const float MaxSpotCone = 60f;
    public const float SpotMargin = 2f;

    // The depth margin: `BiasTexels` texels' worth of the face at the surface's distance (more as the
    // surface turns from the lamp), plus `BiasMetres`.
    public const float BiasTexels = 1.5f;
    public const float BiasMetres = 0.02f;

    // Whether a light can cast: it asks to, and gives light.
    public static bool Casts(in LightSample light) =>
        light.CastsShadows && light.Range > 0f && MathF.Max(MathF.Max(light.Colour.X, light.Colour.Y), light.Colour.Z) > 0f;

    // How near a camera-relative light is, for the budget: the distance from the camera (the origin) to the
    // edge of its range, 0 inside it; then the distance to the light itself, so of two lamps the camera
    // stands in the closer wins.
    public static (float Edge, float Distance) Nearness(in LightSample light)
    {
        float d = light.Position.Length();
        return (MathF.Max(d - light.Range, 0f), d);
    }

    // The lamps that get a map this frame: indices into `lights` (camera-relative), nearest first, at most
    // `budget` (clamped to 0..MaxLamps) and `chosen.Length`. Ties go to the earlier light, so the choice is
    // the same every frame for the same lights.
    public static int Choose(ReadOnlySpan<LightSample> lights, int budget, Span<int> chosen)
    {
        int wanted = Math.Min(Math.Clamp(budget, 0, MaxLamps), chosen.Length);
        if (wanted == 0) return 0;
        Span<float> edges = stackalloc float[MaxLamps];
        Span<float> distances = stackalloc float[MaxLamps];
        int found = 0;
        for (int i = 0; i < lights.Length; i++)
        {
            if (!Casts(lights[i])) continue;
            var (edge, distance) = Nearness(lights[i]);
            if (!float.IsFinite(edge) || !float.IsFinite(distance)) continue;
            int place = found;
            while (place > 0 && (edges[place - 1] > edge || (edges[place - 1] == edge && distances[place - 1] > distance))) place--;
            if (place >= wanted) continue;
            for (int k = Math.Min(found, wanted - 1); k > place; k--)
            {
                edges[k] = edges[k - 1];
                distances[k] = distances[k - 1];
                chosen[k] = chosen[k - 1];
            }
            edges[place] = edge;
            distances[place] = distance;
            chosen[place] = i;
            if (found < wanted) found++;
        }
        return found;
    }

    // A spot light's outer half-angle in degrees, from the cone `LightSample.Spot` carries (0: a point light).
    public static float ConeOf(in LightSample light)
    {
        var spot = light.Spot;
        if (spot == Vector4.Zero) return 0f;
        float s = new Vector3(spot.X, spot.Y, spot.Z).Length();
        if (!(s > 0f)) return 0f;
        float cosInner = spot.W / s;
        float cosOuter = Math.Clamp(cosInner - 1f / s, -1f, 1f);
        return MathF.Acos(cosOuter) * 180f / MathF.PI;
    }

    // Whether a light is drawn as a cube (a point light, or a spot too wide for one view) or one view.
    public static bool IsCube(in LightSample light)
    {
        float cone = ConeOf(light);
        return !(cone > 0f) || cone > MaxSpotCone;
    }

    // The atlas for `lamps` maps (the budget) of `size` texels a face: a block of 3 x 2 tiles each, stacked
    // down the target; the tile shrinks so the whole fits in `maxTexture` a side.
    public static LampShadowAtlas Atlas(int lamps, int size, int maxTexture = ShadowMath.MaxAtlasSize)
    {
        lamps = Math.Clamp(lamps, 1, MaxLamps);
        int tile = Math.Clamp(size, MinSize, Math.Min(maxTexture / BlockColumns, maxTexture / (BlockRows * lamps)));
        return new LampShadowAtlas(lamps, tile);
    }

    // A cube face's axis (the way the light travels through it) and the up its view is made with: +X, -X,
    // +Y, -Y, +Z, -Z; Y's faces use +Z for up, the others +Y. The shaders build the same.
    public static Vector3 FaceAxis(int face) => face switch
    {
        0 => Vector3.UnitX,
        1 => -Vector3.UnitX,
        2 => Vector3.UnitY,
        3 => -Vector3.UnitY,
        4 => Vector3.UnitZ,
        _ => -Vector3.UnitZ,
    };

    public static Vector3 UpFor(Vector3 forward) => MathF.Abs(forward.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;

    // The map of light `light` (camera-relative) in block `slot` of `atlas`: its views (one for a spot,
    // six for a cube) and what the shaders read.
    public static LampShadowFit Fit(in LightSample light, int slot, in LampShadowAtlas atlas)
    {
        bool cube = IsCube(light);
        float far = MathF.Max(light.Range, Near * 2f);
        float halfFov = cube ? 45f : MathF.Min(ConeOf(light) + SpotMargin, MaxSpotCone + SpotMargin);
        float tanHalf = MathF.Tan(halfFov * MathF.PI / 180f);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(2f * halfFov * MathF.PI / 180f, 1f, Near, far);

        var fit = new LampShadowFit
        {
            Position = light.Position,
            Cube = cube,
            Faces = cube ? CubeFaces : 1,
            Slot = slot,
            Near = Near,
            Far = far,
            DepthA = far / (far - Near),
            DepthB = Near * far / (far - Near),
            InvTan = cube ? 0f : 1f / tanHalf,
            BiasSlope = BiasTexels * 2f * tanHalf / atlas.TileSize,
        };
        var (bx, by) = atlas.Block(slot);
        fit.Corner = new Vector2((float)bx / atlas.Width, (float)by / atlas.Height);

        for (int f = 0; f < fit.Faces; f++)
        {
            var forward = cube ? FaceAxis(f) : SpotAxis(light);
            var view = Matrix4x4.CreateLookAt(light.Position, light.Position + forward, UpFor(forward));
            var (tx, ty) = atlas.Tile(slot, f);
            fit.Views[f] = new LampShadowFace(view, projection, forward, tx, ty);
            if (!cube)
            {
                fit.Forward = forward;
                fit.Right = new Vector3(view.M11, view.M21, view.M31);
                fit.Up = new Vector3(view.M12, view.M22, view.M32);
            }
        }
        return fit;
    }

    private static Vector3 SpotAxis(in LightSample light)
    {
        var d = new Vector3(light.Spot.X, light.Spot.Y, light.Spot.Z);
        return d.LengthSquared() > 1e-12f ? Vector3.Normalize(d) : -Vector3.UnitZ;
    }

    // ---- The lookup: common.fxh `LampShadow`, whose tests these are ----

    // Where a camera-relative point falls on a lamp's map: the face (0 for a spot), its uv on that face
    // (0..1, y down), its distance along the face's axis in metres, and whether the map holds it (a cube
    // always does; a spot only inside its view and in front of the lamp).
    public static LampShadowSample Project(in LampShadowFit fit, Vector3 relative)
    {
        var d = relative - fit.Position;
        Vector3 forward, right, up;
        float scale;
        int face = 0;
        if (fit.Cube)
        {
            var a = Vector3.Abs(d);
            int axis = a.X >= a.Y && a.X >= a.Z ? 0 : a.Y >= a.Z ? 1 : 2;
            float component = axis == 0 ? d.X : axis == 1 ? d.Y : d.Z;
            face = axis * 2 + (component >= 0f ? 0 : 1);
            forward = FaceAxis(face);
            var upHint = UpFor(forward);
            right = Vector3.Cross(upHint, -forward);
            up = Vector3.Cross(-forward, right);
            scale = 1f;
        }
        else
        {
            forward = fit.Forward;
            right = fit.Right;
            up = fit.Up;
            scale = fit.InvTan;
        }
        float z = Vector3.Dot(d, forward);
        var ndc = new Vector2(Vector3.Dot(d, right), Vector3.Dot(d, up)) * (scale / MathF.Max(z, 0.001f));
        var uv = new Vector2(ndc.X * 0.5f + 0.5f, 0.5f - ndc.Y * 0.5f);
        bool inside = MathF.Abs(ndc.X) <= 1f && MathF.Abs(ndc.Y) <= 1f && z >= 0.001f;
        return new LampShadowSample(face, uv, z, inside);
    }

    // The depth margin in metres at `distance` along the face, on a surface whose normal is `facing`
    // (dot of normal and the direction to the lamp, 0..1).
    public static float Bias(in LampShadowFit fit, float distance, float facing) =>
        distance * fit.BiasSlope * (2f - Math.Clamp(facing, 0f, 1f)) + BiasMetres;

    // The z/w a surface `distance` metres along the face compares with the map, brought `bias` metres
    // nearer the lamp: what the caster pipeline stores for a caster at that distance.
    public static float QueryDepth(in LampShadowFit fit, float distance, float bias) =>
        fit.DepthA - fit.DepthB / MathF.Max(distance - bias, 0.001f);

    // What a caster at a camera-relative point writes into face `face`'s map: its z/w through the face's
    // view and projection (lit.fx's ShadowCaster, divided per pixel).
    public static float StoredDepth(in LampShadowFit fit, int face, Vector3 relative)
    {
        var clip = Vector4.Transform(new Vector4(relative, 1f), fit.Views[face].ViewProj);
        return clip.Z / clip.W;
    }

    // Where a camera-relative point falls on face `face` through its matrices, as uv (y down): the caster's
    // side of TheShaderLookupMatchesTheCasterMatrices.
    public static Vector2 CasterUv(in LampShadowFit fit, int face, Vector3 relative)
    {
        var clip = Vector4.Transform(new Vector4(relative, 1f), fit.Views[face].ViewProj);
        return new Vector2(clip.X / clip.W * 0.5f + 0.5f, 0.5f - clip.Y / clip.W * 0.5f);
    }

    // Whether a surface at `relative` facing `normal` is lit by the lamp when the map holds `stored` where
    // the surface falls: the shaders' step(depth, sample). Off the map is lit.
    public static bool Lit(in LampShadowFit fit, Vector3 relative, Vector3 normal, float stored)
    {
        var sample = Project(fit, relative);
        if (!sample.Inside) return true;
        var toLamp = fit.Position - relative;
        float length = toLamp.Length();
        float facing = length > 0.001f ? Vector3.Dot(normal, toLamp / length) : 1f;
        return QueryDepth(fit, sample.Distance, Bias(fit, sample.Distance, facing)) <= stored;
    }

    // The order a draw's lamps go to the shader in: those with a map first (their order kept), then the
    // rest. The shaders sum the lamps, so the order changes nothing else; only the first PerDraw are looked
    // up. Writes indices into `order` (as long as `chosen`).
    public static void DrawOrder(ReadOnlySpan<LightSample> chosen, Span<int> order)
    {
        int n = 0;
        for (int i = 0; i < chosen.Length; i++) if (chosen[i].ShadowSlot > 0) order[n++] = i;
        for (int i = 0; i < chosen.Length; i++) if (chosen[i].ShadowSlot <= 0) order[n++] = i;
    }
}

// Where the lamps' maps sit in their one target: `Lamps` blocks of 3 x 2 tiles of `TileSize` texels, block
// k from row 2k down.
internal readonly struct LampShadowAtlas
{
    public LampShadowAtlas(int lamps, int tileSize)
    {
        Lamps = lamps;
        TileSize = tileSize;
    }

    public int Lamps { get; }
    public int TileSize { get; }
    public int Width => LampShadows.BlockColumns * TileSize;
    public int Height => LampShadows.BlockRows * TileSize * Lamps;

    // Block `slot`'s corner, and face `face`'s tile in it, in texels.
    public (int X, int Y) Block(int slot) => (0, slot * LampShadows.BlockRows * TileSize);

    public (int X, int Y) Tile(int slot, int face)
    {
        var (x, y) = Block(slot);
        return (x + face % LampShadows.BlockColumns * TileSize, y + face / LampShadows.BlockColumns * TileSize);
    }
}

// One view a lamp's casters are drawn through, into its tile at (X, Y) texels.
internal readonly struct LampShadowFace
{
    public LampShadowFace(Matrix4x4 view, Matrix4x4 projection, Vector3 forward, int x, int y)
    {
        View = view;
        Projection = projection;
        ViewProj = view * projection;
        Forward = forward;
        X = x;
        Y = y;
    }

    public Matrix4x4 View { get; }
    public Matrix4x4 Projection { get; }
    public Matrix4x4 ViewProj { get; }
    public Vector3 Forward { get; }
    public int X { get; }
    public int Y { get; }
}

[System.Runtime.CompilerServices.InlineArray(LampShadows.CubeFaces)]
internal struct LampShadowFaces
{
    private LampShadowFace _first;
}

// A lamp's map (LampShadows.Fit): its views, and what the shaders read (common.fxh `LampShadow`).
internal struct LampShadowFit
{
    public Vector3 Position;     // the lamp, camera-relative
    public bool Cube;            // six faces; else one spot view
    public int Faces;
    public int Slot;             // its block in the atlas
    public LampShadowFaces Views;
    public float Near, Far;
    public float DepthA, DepthB; // z/w = A - B / distance (the projection's)
    public float InvTan;         // a spot's 1 / tan(half its field of view); 0 for a cube
    public Vector3 Forward, Right, Up;   // a spot's axes (a cube's come from the face)
    public Vector2 Corner;       // its block's corner in the atlas, as uv
    public float BiasSlope;      // metres of margin per metre of distance, for BiasTexels texels
}

// A point on a lamp's map (LampShadows.Project).
internal readonly record struct LampShadowSample(int Face, Vector2 Uv, float Distance, bool Inside);
