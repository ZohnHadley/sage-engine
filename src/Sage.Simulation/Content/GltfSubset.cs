#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Sage.Simulation;

// How a glTF feature is handled when a model is read (issue #321).
internal enum GltfSupport
{
    Supported,   // read and drawn as the file says
    Ignored,     // read past on purpose (the engine takes it from elsewhere); a warning when the file has it and it matters
    Rejected,    // the file does not load: one named error per feature, never a model that silently draws wrong
}

// One row of the glTF subset table.
internal sealed record GltfFeature(string Id, GltfSupport Support, string Description, string Message);

// The glTF subset the engine reads (issue #321, docs/design/05 §8) in one list, and the check that holds a
// file to it. `Features` is the single source: the checks below name rows of it, the error a modder sees is
// the row's message, and a guide's table is copied from it (`Table`). The check reads the file's JSON chunk
// itself, before SharpGLTF decodes anything, so a feature the library would quietly decode wrongly (sparse
// accessors, compressed geometry) is caught by name.
internal static class GltfSubset
{
    public const string BinaryGlb = "glb";
    public const string TextGltf = "text-gltf";
    public const string ExternalBuffers = "external-buffers";
    public const string MorphTargets = "morph-targets";
    public const string SparseAccessors = "sparse-accessors";
    public const string Draco = "draco";
    public const string Meshopt = "meshopt";
    public const string RequiredExtension = "required-extension";
    public const string PrimitiveMode = "primitive-mode";
    public const string MoreInfluences = "more-influences";
    public const string TooManyJoints = "too-many-joints";
    public const string MultipleSkins = "multiple-skins";
    public const string NotGltf = "not-gltf";
    public const string NoGeometry = "no-geometry";
    public const string SecondUv = "texcoord-1";
    public const string VertexColour = "color-0";
    public const string ExtraUvOrColour = "extra-attribute-sets";
    public const string Materials = "materials";
    public const string Animations = "animations";
    public const string Scene = "scene-graph";

    // Required extensions the engine can read geometry through (they change nothing it uses, or SharpGLTF
    // decodes them): anything else a file marks required is rejected, since it says the file cannot be read without it.
    private static readonly string[] AllowedRequired =
    {
        "KHR_materials_unlit", "KHR_materials_emissive_strength", "KHR_texture_transform", "KHR_mesh_quantization", "KHR_lights_punctual",
    };

    public static readonly IReadOnlyList<GltfFeature> Features = new GltfFeature[]
    {
        new(BinaryGlb, GltfSupport.Supported, "Binary glTF 2.0 (.glb), buffers embedded", "read"),
        new(Scene, GltfSupport.Supported, "Default scene (or every root node), node hierarchy and transforms (baked into the vertices of a rigid mesh)", "read"),
        new("positions-normals-uv0", GltfSupport.Supported, "POSITION, NORMAL, TEXCOORD_0, TANGENT (worked out from TEXCOORD_0 when absent), triangle indices (any index width)", "read"),
        new(VertexColour, GltfSupport.Supported, "COLOR_0 (vec3 or vec4, float or normalised integer), white without one", "read"),
        new(SecondUv, GltfSupport.Supported, "TEXCOORD_1: a second UV set (lightmaps), kept beside the vertices as `Uv1`", "read"),
        new("skinning", GltfSupport.Supported, "One skin of up to 64 joints, JOINTS_0 and WEIGHTS_0 (four influences per vertex)", "read"),
        new("primitive-triangles", GltfSupport.Supported, "Primitive modes TRIANGLES, TRIANGLE_STRIP and TRIANGLE_FAN", "read"),
        new(MorphTargets, GltfSupport.Supported, "Morph targets (blend shapes): POSITION and NORMAL deltas, named by the mesh's extras.targetNames, at the mesh's weights; a skinned mesh's are animated by clips' weight tracks (TANGENT deltas are not read)", "read"),
        new(Materials, GltfSupport.Ignored, "The file's materials and textures: the material record named by whatever draws the mesh decides", "ignored"),
        new(Animations, GltfSupport.Ignored, "Animation clips are read by the animation reader, not the mesh reader; cameras and lights are not read", "ignored"),
        new(ExtraUvOrColour, GltfSupport.Ignored, "TEXCOORD_2 and up, COLOR_1 and up: dropped, with a warning", "ignored"),

        new(NotGltf, GltfSupport.Rejected, "A file that is not glTF 2.0 (bad header, broken JSON chunk)", "is not a readable glTF 2.0 binary"),
        new(TextGltf, GltfSupport.Rejected, "Text .gltf with its buffers in separate files", "is a text .gltf; only the binary .glb is read, so export it as 'glTF Binary (.glb)' with the buffers embedded"),
        new(ExternalBuffers, GltfSupport.Rejected, "A buffer stored in a separate file (a `uri` that is not a data: URI)", "keeps a buffer in a separate file; embed it by exporting 'glTF Binary (.glb)'"),
        new(SparseAccessors, GltfSupport.Rejected, "Sparse accessors", "has sparse accessors, which are not read; export without 'sparse' accessors"),
        new(Draco, GltfSupport.Rejected, "KHR_draco_mesh_compression", "uses Draco mesh compression (KHR_draco_mesh_compression); export without compression"),
        new(Meshopt, GltfSupport.Rejected, "EXT_meshopt_compression / KHR_meshopt_compression", "uses meshopt compression; export without compression"),
        new(RequiredExtension, GltfSupport.Rejected, "Any other extension the file lists in extensionsRequired", "requires an extension the engine does not read"),
        new(PrimitiveMode, GltfSupport.Rejected, "Primitive modes POINTS, LINES, LINE_LOOP and LINE_STRIP", "has a primitive that is not drawn as triangles (points or lines)"),
        new(MoreInfluences, GltfSupport.Rejected, "More than four joint influences per vertex (JOINTS_1 / WEIGHTS_1)", "has more than four joint influences per vertex (JOINTS_1); limit weights to 4 on export"),
        new(TooManyJoints, GltfSupport.Rejected, $"A skin of more than {SkinMath.MaxBones} joints", $"has a skin of more than {SkinMath.MaxBones} joints, which is all a draw can pose"),
        new(MultipleSkins, GltfSupport.Rejected, "More than one skin", "has more than one skin; only one skin per model is read"),
        new(NoGeometry, GltfSupport.Rejected, "A file with no drawable triangle primitive", "has no drawable primitives"),
    };

    private static GltfFeature Feature(string id) => Features.First(f => f.Id == id);

    // "Model 'x': glTF feature 'draco' is not supported: uses Draco mesh compression ...". One per feature per file.
    public static string Error(string id, string model, string? detail = null) =>
        $"Model '{model}': glTF feature '{id}' is not supported: the file {Feature(id).Message}" + (detail == null ? "" : $" ({detail})");

    // A table for a guide, from the list above.
    public static string Table() =>
        string.Join('\n', Features.Select(f => $"| {f.Id} | {f.Support} | {f.Description} |"));

    // Checks a .glb's bytes against the subset and returns one error per unsupported feature (empty when the
    // file is fine) plus warnings for what is read past. Reads only the header and the JSON chunk.
    public static List<string> Check(ReadOnlySpan<byte> glb, string model, List<string>? warnings = null)
    {
        var errors = new List<string>();
        if (glb.Length > 0 && glb[0] == (byte)'{')
        {
            errors.Add(Error(TextGltf, model));
            return errors;
        }
        JsonDocument doc;
        try
        {
            if (glb.Length < 20 || BitConverter.ToUInt32(glb) != 0x46546C67 /* "glTF" */ || BitConverter.ToUInt32(glb[4..]) != 2)
                throw new FormatException("bad GLB header");
            int jsonLength = checked((int)BitConverter.ToUInt32(glb[12..]));
            if (BitConverter.ToUInt32(glb[16..]) != 0x4E4F534A /* "JSON" */ || 20 + jsonLength > glb.Length)
                throw new FormatException("bad JSON chunk");
            doc = JsonDocument.Parse(glb.Slice(20, jsonLength).ToArray());
        }
        catch (Exception ex) when (ex is FormatException or JsonException or OverflowException or ArgumentException)
        {
            errors.Add(Error(NotGltf, model, ex.Message));
            return errors;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var seen = new HashSet<string>();
            void Add(string id, string? detail = null)
            {
                if (seen.Add(id + detail)) errors.Add(Error(id, model, detail));
            }

            // Extensions: compression gets its own name, anything else required that we do not read too.
            foreach (string list in new[] { "extensionsRequired", "extensionsUsed" })
                if (root.TryGetProperty(list, out var names) && names.ValueKind == JsonValueKind.Array)
                    foreach (var n in names.EnumerateArray())
                    {
                        string ext = n.GetString() ?? "";
                        if (ext == "KHR_draco_mesh_compression") Add(Draco);
                        else if (ext is "EXT_meshopt_compression" or "KHR_meshopt_compression") Add(Meshopt);
                        else if (list == "extensionsRequired" && Array.IndexOf(AllowedRequired, ext) < 0) Add(RequiredExtension, ext);
                    }

            if (ArrayOf(root, "buffers") is { } buffers)
                foreach (var b in buffers.EnumerateArray())
                    if (b.TryGetProperty("uri", out var uri) && uri.GetString() is { } u && !u.StartsWith("data:", StringComparison.Ordinal))
                        Add(ExternalBuffers, u);

            if (ArrayOf(root, "accessors") is { } accessors)
                foreach (var a in accessors.EnumerateArray())
                    if (a.TryGetProperty("sparse", out _)) { Add(SparseAccessors); break; }

            if (ArrayOf(root, "skins") is { } skins)
            {
                if (skins.GetArrayLength() > 1) Add(MultipleSkins, $"{skins.GetArrayLength()} skins");
                foreach (var s in skins.EnumerateArray())
                    if (ArrayOf(s, "joints") is { } joints && joints.GetArrayLength() > SkinMath.MaxBones)
                        Add(TooManyJoints, $"{joints.GetArrayLength()} joints");
            }

            bool extraSets = false;
            if (ArrayOf(root, "meshes") is { } meshes)
                foreach (var mesh in meshes.EnumerateArray())
                    foreach (var prim in Items(mesh, "primitives"))
                    {
                        if (prim.TryGetProperty("mode", out var mode) && mode.TryGetInt32(out int m) && m is >= 0 and <= 3)
                            Add(PrimitiveMode, m switch { 0 => "POINTS", 1 => "LINES", 2 => "LINE_LOOP", _ => "LINE_STRIP" });
                        if (prim.TryGetProperty("extensions", out var ex) && ex.ValueKind == JsonValueKind.Object)
                        {
                            if (ex.TryGetProperty("KHR_draco_mesh_compression", out _)) Add(Draco);
                        }
                        if (prim.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
                            foreach (var attr in attrs.EnumerateObject())
                            {
                                if (attr.Name is "JOINTS_1" or "WEIGHTS_1" || (attr.Name.StartsWith("JOINTS_") || attr.Name.StartsWith("WEIGHTS_")) && !attr.Name.EndsWith("_0"))
                                    Add(MoreInfluences);
                                else if (attr.Name.StartsWith("TEXCOORD_") && !attr.Name.EndsWith("_0") && !attr.Name.EndsWith("_1")
                                         || attr.Name.StartsWith("COLOR_") && !attr.Name.EndsWith("_0"))
                                    extraSets = true;
                            }
                    }
            if (ArrayOf(root, "bufferViews") is { } views)
                foreach (var v in views.EnumerateArray())
                    if (v.TryGetProperty("extensions", out var ex) && ex.ValueKind == JsonValueKind.Object
                        && (ex.TryGetProperty("EXT_meshopt_compression", out _) || ex.TryGetProperty("KHR_meshopt_compression", out _)))
                    { Add(Meshopt); break; }

            if (extraSets && errors.Count == 0)
                warnings?.Add($"Model '{model}': TEXCOORD_2 and up and COLOR_1 and up are not read (only TEXCOORD_0/1 and COLOR_0 are)");
        }
        return errors;
    }

    private static IEnumerable<JsonElement> Items(JsonElement element, string name) =>
        ArrayOf(element, name) is { } list ? list.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static JsonElement? ArrayOf(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v : null;
}
