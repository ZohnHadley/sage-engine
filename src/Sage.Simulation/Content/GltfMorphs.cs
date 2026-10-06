#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using SharpGLTF.Schema2;

namespace Sage.Simulation;

// A `.glb`'s morph targets (blend shapes, issue #363) as one list for the whole model, which the mesh
// reader (deltas per primitive, MeshGeometry) and the animation reader (the skeleton's targets and the
// clips' weight tracks, GltfAnimationReader) both read, so a weight's index means the same target to each.
//
// A target is known by name: the mesh's `extras.targetNames` (what Blender and most exporters write; an
// older exporter's on the first primitive is read too), else "target<i>". Meshes are walked in the file's
// order, and a name two meshes share is one target: a head and its eyelashes both blink with one weight.
// A target's rest weight is the first mesh's `weights` that has it (0 without).
internal sealed class GltfMorphs
{
    public static readonly GltfMorphs None = new(Array.Empty<string>(), Array.Empty<float>(), new Dictionary<int, int[]>());

    private readonly Dictionary<int, int[]> _byMesh;

    private GltfMorphs(string[] names, float[] rest, Dictionary<int, int[]> byMesh)
    {
        Names = names;
        Rest = rest;
        _byMesh = byMesh;
    }

    public string[] Names { get; }
    public float[] Rest { get; }
    public int Count => Names.Length;

    // For the mesh's own target i, the model's target index; null for a mesh without targets.
    public int[]? Of(Mesh? mesh) => mesh != null && _byMesh.TryGetValue(mesh.LogicalIndex, out var map) ? map : null;

    public static GltfMorphs Read(ModelRoot root)
    {
        List<string>? names = null;
        List<float>? rest = null;
        Dictionary<int, int[]>? byMesh = null;
        foreach (var mesh in root.LogicalMeshes)
        {
            int count = 0;
            foreach (var primitive in mesh.Primitives) count = Math.Max(count, primitive.MorphTargetsCount);
            if (count == 0) continue;
            names ??= new List<string>();
            rest ??= new List<float>();
            byMesh ??= new Dictionary<int, int[]>();
            var fileNames = TargetNames(mesh);
            var weights = mesh.MorphWeights;
            var map = new int[count];
            for (int i = 0; i < count; i++)
            {
                string name = fileNames != null && i < fileNames.Count && fileNames[i]?.GetValueKind() == System.Text.Json.JsonValueKind.String
                              && fileNames[i]!.GetValue<string>() is { Length: > 0 } given ? given : $"target{i}";
                int index = names.IndexOf(name);
                if (index < 0)
                {
                    index = names.Count;
                    names.Add(name);
                    float w = weights != null && i < weights.Count ? weights[i] : 0f;
                    rest.Add(float.IsFinite(w) ? w : 0f);
                }
                map[i] = index;
            }
            byMesh[mesh.LogicalIndex] = map;
        }
        return names == null ? None : new GltfMorphs(names.ToArray(), rest!.ToArray(), byMesh!);
    }

    private static JsonArray? TargetNames(Mesh mesh)
    {
        if (mesh.Extras is JsonObject extras && extras["targetNames"] is JsonArray names) return names;
        foreach (var primitive in mesh.Primitives)
            if (primitive.Extras is JsonObject p && p["targetNames"] is JsonArray old) return old;
        return null;
    }
}
