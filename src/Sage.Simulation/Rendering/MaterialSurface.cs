#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Simulation;

// A material's surface (issue #410): the normal, specular, emissive and environment maps and the factors
// that go with them, as lit.fx's parameters. The headless half; the client's MaterialCache asks `TryGet` for
// any parameter of the effect the record's `params` do not set.
//
// No permutations: a map the record does not name is a 1x1 engine texture that changes nothing — a flat
// normal, white for the specular map (the factors alone), white for the emissive map (times an emissive
// colour that is black unless set) and black for the environment — so every lit material is drawn by the
// same technique with the same samplers, whatever it names.
//
//   { "type": "material", "id": "brick", "base": "sage:lit_default",
//     "params": { "Albedo": "textures/brick.png" },
//     "normalMap": "textures/brick_normal.png", "specularMap": "textures/brick_spec.png",
//     "specular": 0.6, "gloss": 0.4 }
internal static class MaterialSurface
{
    // The effect parameters the surface fills (lit.fx's names). A record's `params` may not set them.
    public const string NormalMap = "NormalMap", SpecularMap = "SpecularMap", EmissiveMap = "EmissiveMap",
                        EnvironmentMap = "EnvironmentMap", SurfaceParams = "SurfaceParams", EmissiveColor = "EmissiveColor",
                        Weathering = "Weathering";

    public static readonly string[] Names = { NormalMap, SpecularMap, EmissiveMap, EnvironmentMap, SurfaceParams, EmissiveColor, Weathering };

    // The 1x1 stand-ins (engine_content/textures, made by engine_content/tools/make_engine_textures.py).
    public static readonly AssetPath FlatNormal = AssetPath.Intern("textures/flat_normal.png");
    public static readonly AssetPath White = AssetPath.Intern("textures/white.png");
    public static readonly AssetPath Black = AssetPath.Intern("textures/black.png");

    public static bool IsSurfaceParam(string name) => Array.IndexOf(Names, name) >= 0;

#pragma warning disable SAGE0130   // the surface fields are phase 4h/4n rendering API, still settling
    // The value of surface parameter `name` for a record; false when `name` is not one.
    public static bool TryGet(MaterialRecord record, string name, out MaterialParam value)
    {
        value = name switch
        {
            NormalMap => Texture(record.NormalMap, FlatNormal),
            SpecularMap => Texture(record.SpecularMap, White),
            EmissiveMap => Texture(record.EmissiveMap, White),
            EnvironmentMap => Texture(record.EnvironmentMap, Black),
            // x = highlight strength, y = gloss, z = reflectivity, w = vertex colours (1 or 0): one
            // constant rather than four, as the pixel shader reads them together.
            SurfaceParams => new MaterialParam
            {
                Values = new[] { record.Specular, record.Gloss, record.EnvironmentMap.IsEmpty ? 0f : record.Reflectivity, record.VertexColors ? 1f : 0f },
            },
            EmissiveColor => new MaterialParam { Values = new[] { record.Emissive.X, record.Emissive.Y, record.Emissive.Z } },
            // How much rain shows on it (issue #311): only an opaque material wets (WetnessRules.Weathering).
            Weathering => new MaterialParam { Values = new[] { WetnessRules.Weathering(record) } },
            _ => null!,
        };
        return value != null;
    }

    // Whether the record asks for anything beyond the albedo: an effect without the surface parameters then
    // draws it without, which is worth saying once.
    public static bool Asked(MaterialRecord record) =>
        !record.NormalMap.IsEmpty || !record.SpecularMap.IsEmpty || record.Specular > 0f || !record.EmissiveMap.IsEmpty
        || record.Emissive != Vector3.Zero || record.VertexColors || !record.EnvironmentMap.IsEmpty || record.Reflectivity > 0f;

    // Mistakes in a material's surface are load errors, at their lines (issue #22's rule). That the maps
    // exist is the store's own check of every [AssetKind] field.
    internal static void Check(MaterialRecord record, RecordCheck check)
    {
        foreach (var name in record.Params.Keys)
            if (IsSurfaceParam(name))
                check.Error($"params['{name}']", $"'{name}' is set by the material's surface fields (normalMap, specularMap, specular, gloss, " +
                                                  "emissiveMap, emissive, vertexColors, environmentMap, reflectivity, weathering), not by params");
        if (!(record.Specular >= 0f && record.Specular <= 4f)) check.Error("specular", "must be between 0 and 4");
        if (!(record.Gloss >= 0f && record.Gloss <= 1f)) check.Error("gloss", "must be between 0 and 1");
        if (!(record.Reflectivity >= 0f && record.Reflectivity <= 1f)) check.Error("reflectivity", "must be between 0 and 1");
        if (!(record.Weathering >= 0f && record.Weathering <= 1f)) check.Error("weathering", "must be between 0 and 1");
        if (!(record.Emissive.X >= 0f && record.Emissive.Y >= 0f && record.Emissive.Z >= 0f) || !float.IsFinite(record.Emissive.X + record.Emissive.Y + record.Emissive.Z))
            check.Error("emissive", "must be three numbers of 0 or more");
        if (record.Reflectivity > 0f && record.EnvironmentMap.IsEmpty)
            check.Error("reflectivity", "reflects nothing without an environmentMap");
        if (!record.EmissiveMap.IsEmpty && record.Emissive == Vector3.Zero)
            check.Error("emissiveMap", "glows with emissive's colour, which is 0 0 0: give emissive a colour (1 1 1 for the map as it is)");
    }
#pragma warning restore SAGE0130

    private static MaterialParam Texture(AssetPath path, AssetPath none) => new() { Texture = path.IsEmpty ? none : path };
}
