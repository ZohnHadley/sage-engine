#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// Splat-blended terrain (issue #307, docs/design/14 §3, 07 §11).
//
// Terrain was one tiling texture (`sage:terrain_default`). A `terrain_material` record names up to four
// layer textures, each tiled at its own size, and says where each lies: the first is the ground under
// everything, and each later one is painted over those before it where its height and slope rules say
// (rock on the steep sides, snow above 300 m, sand below the waterline). The rules are worked out per
// vertex into the sector's weight map — four weights a vertex, carried in the chunk mesh — and the
// client's `shaders/terrain.mgfxo` blends the layers by them, with a fine detail texture over the top.
//
//   { "type": "terrain_material", "id": "hills",
//     "layers": [ { "texture": "textures/ground.png", "tile": 8 },
//                 { "texture": "textures/rock.png", "tile": 12, "minSlope": 18, "slopeBlend": 6 } ],
//     "detail": "textures/detail.png", "detailTile": 2, "detailStrength": 0.4 }
//
// A world's `Terrain.Material` (or a `terrain` record's `material`) names it. The weights are functions
// of a vertex's height and normal, and both are the same on either side of a sector edge (the normals
// are worked out across it, #277), so the blend has no seam there either. Without the effect (a build
// without shaders) the client draws the record's `fallback` material, the single texture of before.
[Record("terrain_material", Plugin = RegistrationOwners.Core)]
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
public sealed class TerrainMaterialRecord
{
    [Property(Tooltip = "One to four layers: the first is the ground everywhere, each later one painted over those before it where its rules say")]
    public List<TerrainLayer> Layers = new();

    [AssetKind("texture"), Property(Tooltip = "A grey texture tiled finely over every layer: lighter than mid-grey brightens, darker darkens. Empty: none")]
    public AssetPath Detail;

    [Property(Min = 0.05, Max = 1000, Unit = "m", Tooltip = "How many metres one repeat of the detail texture covers")]
    public float DetailTile = 2f;

    [Property(Min = 0, Max = 1, Tooltip = "How much the detail texture shows: 0 not at all, 1 fully")]
    public float DetailStrength = 0.5f;

    [RecordRef("material"), Property(Tooltip = "What the ground is drawn with where the splat shader is not available (a build without shaders)")]
    public RecordId Fallback = TerrainSplat.DefaultMaterial;
}

// One layer of a terrain material: a texture, how big a tile of it is, and where it lies.
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
public sealed class TerrainLayer
{
    [AssetKind("texture"), Property(Tooltip = "The layer's texture, tiled over the ground")]
    public AssetPath Texture;

    [Property(Min = 0.05, Max = 10000, Unit = "m", Tooltip = "How many metres one repeat of the texture covers")]
    public float Tile = 8f;

    [Property(Min = -100000, Max = 100000, Unit = "m", Tooltip = "Lies only above this height (not on the first layer)")]
    public float MinHeight = TerrainSplat.Lowest;

    [Property(Min = -100000, Max = 100000, Unit = "m", Tooltip = "Lies only below this height (not on the first layer)")]
    public float MaxHeight = TerrainSplat.Highest;

    [Property(Min = 0, Max = 1000, Unit = "m", Tooltip = "How many metres the layer fades in over at minHeight and out at maxHeight")]
    public float HeightBlend = 4f;

    [Property(Min = 0, Max = 90, Unit = "deg", Tooltip = "Lies only where the ground is at least this steep (not on the first layer)")]
    public float MinSlope;

    [Property(Min = 0, Max = 90, Unit = "deg", Tooltip = "Lies only where the ground is at most this steep (not on the first layer)")]
    public float MaxSlope = 90f;

    [Property(Min = 0, Max = 90, Unit = "deg", Tooltip = "How many degrees the layer fades in over at minSlope and out at maxSlope")]
    public float SlopeBlend = 4f;

    // Whether any rule says where the layer lies: on the first layer, a mistake.
    internal bool HasRules => MinHeight > TerrainSplat.Lowest || MaxHeight < TerrainSplat.Highest || MinSlope > 0f || MaxSlope < 90f;
}

// The headless half of the splat: the weight map, worked out from the heights and normals the sector
// already has, and the load checks. The client builds the mesh and draws it.
internal static class TerrainSplat
{
    public const int MaxLayers = 4;
    public const float Lowest = -100000f;
    public const float Highest = 100000f;

    // The effect and technique that blend the layers, and the material terrain is drawn with without one.
    public static readonly AssetPath Effect = AssetPath.Intern("shaders/terrain.mgfxo");
    public const string Technique = "Splat";
    public static readonly RecordId DefaultMaterial = new("sage", "terrain_default");

    // Texture coordinates on a terrain mesh run one unit to a cell of a full sector (8 m), as they did
    // when the ground was one tiling texture; the shader scales them by `metres per unit / tile`.
    public const float MetresPerUv = Terrain.SectorSize / (Terrain.SectorResolution - 1);

    // The weights of the four layers at grid vertex (x, z), packed one byte each, layer 0 lowest. They sum
    // to 255 give or take rounding, and the shader divides by their sum. A function of the vertex's
    // height and normal only, so two sectors that agree on those (#277) agree on the blend.
    public static uint Weights(TerrainMaterialRecord material, Heightfield heights, int x, int z)
    {
        var w = Blend(material, heights[x, z], SlopeOf(heights.VertexNormal(x, z)));
        if (heights.Paint is { } paint) w = SculptApply.Overlay(w, paint[z * heights.Resolution + x]);   // painted over the rules (#372)
        return Pack(w);
    }

    // The slope of a surface with normal `n`, in degrees from flat.
    public static float SlopeOf(Vector3 n) => MathF.Acos(Math.Clamp(n.Y / MathF.Max(n.Length(), 1e-12f), -1f, 1f)) * (180f / MathF.PI);

    // The weights at a height and slope: painter's order, each layer over the ones before it by its
    // coverage. They sum to 1.
    public static Vector4 Blend(TerrainMaterialRecord material, float height, float slope)
    {
        Span<float> w = stackalloc float[MaxLayers];
        w[0] = 1f;
        int count = Math.Min(material.Layers.Count, MaxLayers);
        for (int i = 1; i < count; i++)
        {
            float c = Coverage(material.Layers[i], height, slope);
            for (int j = 0; j < i; j++) w[j] *= 1f - c;
            w[i] = c;
        }
        return new Vector4(w[0], w[1], w[2], w[3]);
    }

    // How much of a layer lies at a height and slope, 0 to 1: inside both its bands, fading over its blends.
    public static float Coverage(TerrainLayer layer, float height, float slope) =>
        Band(height, layer.MinHeight, layer.MaxHeight, layer.HeightBlend, Lowest, Highest)
        * Band(slope, layer.MinSlope, layer.MaxSlope, layer.SlopeBlend, 0f, 90f);

    // 1 between min and max, fading over `blend` centred on each edge; an edge left at its limit is open.
    private static float Band(float v, float min, float max, float blend, float lowest, float highest)
    {
        float lower = min > lowest ? Ramp(v, min, blend) : 1f;
        float upper = max < highest ? 1f - Ramp(v, max, blend) : 1f;
        return lower * upper;
    }

    private static float Ramp(float v, float edge, float blend)
    {
        if (!(blend > 0f)) return v >= edge ? 1f : 0f;
        float t = Math.Clamp((v - (edge - blend * 0.5f)) / blend, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static uint Pack(Vector4 w)
    {
        static uint B(float v) => (uint)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);
        return B(w.X) | (B(w.Y) << 8) | (B(w.Z) << 16) | (B(w.W) << 24);
    }

    public static Vector4 Unpack(uint packed) =>
        new((packed & 0xFF) / 255f, ((packed >> 8) & 0xFF) / 255f, ((packed >> 16) & 0xFF) / 255f, (packed >> 24) / 255f);

    // The parameters of the splat effect for a record (the client's material for it): four layer
    // textures (a missing layer repeats the first, at weight 0), their tiling, and the detail.
    public static Dictionary<string, MaterialParam> Params(TerrainMaterialRecord material, AssetPath none)
    {
        var p = new Dictionary<string, MaterialParam>(StringComparer.Ordinal);
        var tiling = new float[MaxLayers];
        for (int i = 0; i < MaxLayers; i++)
        {
            var layer = material.Layers[i < material.Layers.Count ? i : 0];
            p["Layer" + i] = new MaterialParam { Texture = layer.Texture };
            tiling[i] = MetresPerUv / MathF.Max(layer.Tile, 0.05f);
        }
        p["LayerTiling"] = new MaterialParam { Values = tiling };
        bool detail = !material.Detail.IsEmpty;
        p["Detail"] = new MaterialParam { Texture = detail ? material.Detail : none };
        p["DetailParams"] = new MaterialParam
        {
            Values = new[] { MetresPerUv / MathF.Max(material.DetailTile, 0.05f), detail ? Math.Clamp(material.DetailStrength, 0f, 1f) : 0f },
        };
        return p;
    }

    // Mistakes in a terrain material are load errors, at their lines (issue #22's rule).
    internal static void Check(TerrainMaterialRecord record, RecordCheck check)
    {
        if (record.Layers.Count == 0) check.Error("layers", "has no layers: the first is the ground everywhere");
        else if (record.Layers.Count > MaxLayers)
            check.Error("layers", $"has {record.Layers.Count} layers; the terrain shader blends at most {MaxLayers}");
        for (int i = 0; i < record.Layers.Count; i++)
        {
            string at = $"layers[{i}]";
            var layer = record.Layers[i];
            if (layer == null) { check.Error(at, "is empty"); continue; }
            if (layer.Texture.IsEmpty) check.Error($"{at}.texture", "names no texture");
            if (!(layer.Tile > 0f)) check.Error($"{at}.tile", "must be more than 0 m");
            if (i == 0 && layer.HasRules)
                check.Error(at, "is the first layer, the ground under every other: its height and slope rules would do nothing");
            if (layer.MinHeight > layer.MaxHeight) check.Error($"{at}.minHeight", "is more than maxHeight");
            if (!(layer.MinSlope >= 0f && layer.MinSlope <= 90f)) check.Error($"{at}.minSlope", "must be between 0 and 90 degrees");
            if (!(layer.MaxSlope >= 0f && layer.MaxSlope <= 90f)) check.Error($"{at}.maxSlope", "must be between 0 and 90 degrees");
            if (layer.MinSlope > layer.MaxSlope) check.Error($"{at}.minSlope", "is more than maxSlope");
            if (!(layer.HeightBlend >= 0f)) check.Error($"{at}.heightBlend", "must be 0 m or more");
            if (!(layer.SlopeBlend >= 0f)) check.Error($"{at}.slopeBlend", "must be 0 degrees or more");
        }
        if (!(record.DetailTile > 0f)) check.Error("detailTile", "must be more than 0 m");
        if (!(record.DetailStrength >= 0f && record.DetailStrength <= 1f)) check.Error("detailStrength", "must be between 0 and 1");
    }
}
