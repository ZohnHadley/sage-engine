#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Sage.Simulation;
using Xunit;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The glTF subset the engine reads (issue #321): a file that uses a feature outside it fails with one named
// error per feature, and the second UV set and the vertex colours come through. Files are built in memory
// (SharpGLTF writes a valid one, and `Patch` edits its JSON chunk to add the feature under test).
public class GltfSubsetTests
{
    public GltfSubsetTests() { _ = TestEnv.UserRoot; }

    private static void Attribute<T>(SharpGLTF.Schema2.ModelRoot model, SharpGLTF.Schema2.MeshPrimitive primitive, string name, T[] values,
                                     SharpGLTF.Schema2.DimensionType dimensions, bool bounds = false) where T : unmanaged
    {
        var accessor = model.CreateAccessor(name);
        var view = model.UseBufferView(MemoryMarshal.AsBytes(values.AsSpan()).ToArray(), 0, null, 0, SharpGLTF.Schema2.BufferMode.ARRAY_BUFFER);
        accessor.SetVertexData(view, 0, values.Length, new SharpGLTF.Memory.AttributeFormat(dimensions, SharpGLTF.Schema2.EncodingType.FLOAT, false));
        if (bounds) accessor.UpdateBounds();
        primitive.SetVertexAccessor(name, accessor);
    }

    // One triangle, optionally with a second UV set and vertex colours.
    private static byte[] Triangle(Vector2[]? uv1 = null, bool colours = false, float x = 1f)
    {
        var model = SharpGLTF.Schema2.ModelRoot.CreateModel();
        var node = model.UseScene("scene").CreateNode("tri");
        var mesh = model.CreateMesh("tri");
        var primitive = mesh.CreatePrimitive();
        primitive.DrawPrimitiveType = SharpGLTF.Schema2.PrimitiveType.TRIANGLES;
        Attribute(model, primitive, "POSITION", new[] { Vector3.Zero, new Vector3(x, 0, 0), new Vector3(0, 1, 0) }, SharpGLTF.Schema2.DimensionType.VEC3, bounds: true);
        Attribute(model, primitive, "NORMAL", Enumerable.Repeat(Vector3.UnitZ, 3).ToArray(), SharpGLTF.Schema2.DimensionType.VEC3);
        Attribute(model, primitive, "TEXCOORD_0", new[] { Vector2.Zero, Vector2.UnitX, Vector2.UnitY }, SharpGLTF.Schema2.DimensionType.VEC2);
        if (uv1 != null) Attribute(model, primitive, "TEXCOORD_1", uv1, SharpGLTF.Schema2.DimensionType.VEC2);
        if (colours)
            Attribute(model, primitive, "COLOR_0", new[] { new Vector4(1, 0, 0, 1), new Vector4(0, 1, 0, 1), new Vector4(0, 0, 1, 0.5f) }, SharpGLTF.Schema2.DimensionType.VEC4);
        var indices = new uint[] { 0, 1, 2 };
        var index = model.CreateAccessor("indices");
        index.SetIndexData(model.UseBufferView(MemoryMarshal.AsBytes(indices.AsSpan()).ToArray(), 0, null, 0, SharpGLTF.Schema2.BufferMode.ELEMENT_ARRAY_BUFFER),
                           0, indices.Length, SharpGLTF.Schema2.IndexEncodingType.UNSIGNED_INT);
        primitive.SetIndexAccessor(index);
        node.Mesh = mesh;
        return model.WriteGLB().ToArray();
    }

    // Rewrites a .glb's JSON chunk (the BIN chunk is kept as it is).
    private static byte[] Patch(byte[] glb, Action<JsonObject> edit)
    {
        int jsonLength = BitConverter.ToInt32(glb, 12);
        var json = JsonNode.Parse(Encoding.UTF8.GetString(glb, 20, jsonLength))!.AsObject();
        edit(json);
        var text = Encoding.UTF8.GetBytes(json.ToJsonString());
        int padded = (text.Length + 3) & ~3;
        var rest = glb.AsSpan(20 + jsonLength).ToArray();   // the BIN chunk
        using var output = new MemoryStream();
        var writer = new BinaryWriter(output);
        writer.Write(0x46546C67u); writer.Write(2u); writer.Write((uint)(12 + 8 + padded + rest.Length));
        writer.Write((uint)padded); writer.Write(0x4E4F534Au);
        writer.Write(text);
        for (int i = text.Length; i < padded; i++) writer.Write((byte)' ');
        writer.Write(rest);
        return output.ToArray();
    }

    private static JsonObject Primitive(JsonObject json) =>
        json["meshes"]!.AsArray()[0]!["primitives"]!.AsArray()[0]!.AsObject();

    private static IReadOnlyList<string> Errors(byte[] glb, string name = "tri.glb")
    {
        Assert.Null(MeshGeometry.ReadGlb(new MemoryStream(glb), name, out var errors));
        return errors;
    }

    private static byte[] Joints(int count, int skins = 1)
    {
        return Patch(Triangle(), json =>
        {
            var array = new JsonArray();
            for (int s = 0; s < skins; s++)
                array.Add(new JsonObject { ["joints"] = new JsonArray(Enumerable.Range(0, count).Select(_ => (JsonNode)JsonValue.Create(0)).ToArray()) });
            json["skins"] = array;
        });
    }

    public static TheoryData<string, string, string> Rejections() => new()
    {
        { GltfSubset.MorphTargets, "morph targets", "m" },
        { GltfSubset.SparseAccessors, "sparse", "s" },
        { GltfSubset.Draco, "Draco", "d" },
        { GltfSubset.Meshopt, "meshopt", "o" },
        { GltfSubset.RequiredExtension, "FAKE_extension", "r" },
        { GltfSubset.PrimitiveMode, "LINES", "p" },
        { GltfSubset.MoreInfluences, "JOINTS_1", "i" },
        { GltfSubset.ExternalBuffers, "tri.bin", "e" },
        { GltfSubset.TooManyJoints, "65 joints", "j" },
        { GltfSubset.MultipleSkins, "2 skins", "k" },
        { GltfSubset.TextGltf, "text .gltf", "t" },
        { GltfSubset.NotGltf, "not a readable", "n" },
    };

    private static byte[] Broken(string code) => code switch
    {
        "m" => Patch(Triangle(), j => Primitive(j)["targets"] = new JsonArray(new JsonObject { ["POSITION"] = 0 })),
        "s" => Patch(Triangle(), j => j["accessors"]!.AsArray()[0]!["sparse"] = new JsonObject { ["count"] = 1 }),
        "d" => Patch(Triangle(), j =>
        {
            j["extensionsRequired"] = new JsonArray("KHR_draco_mesh_compression");
            Primitive(j)["extensions"] = new JsonObject { ["KHR_draco_mesh_compression"] = new JsonObject { ["bufferView"] = 0 } };
        }),
        "o" => Patch(Triangle(), j => j["extensionsUsed"] = new JsonArray("EXT_meshopt_compression")),
        "r" => Patch(Triangle(), j => j["extensionsRequired"] = new JsonArray("FAKE_extension")),
        "p" => Patch(Triangle(), j => Primitive(j)["mode"] = 1),
        "i" => Patch(Triangle(), j => Primitive(j)["attributes"]!["JOINTS_1"] = 0),
        "e" => Patch(Triangle(), j => j["buffers"]!.AsArray()[0]!["uri"] = "tri.bin"),
        "j" => Joints(65),
        "k" => Joints(4, skins: 2),
        "t" => Encoding.UTF8.GetBytes("{ \"asset\": { \"version\": \"2.0\" } }"),
        _ => Encoding.UTF8.GetBytes("this is an OBJ, honest"),
    };

    [Theory]
    [MemberData(nameof(Rejections))]
    public void AFileWithAnUnsupportedFeatureFailsWithOneErrorNamingTheFileAndTheFeature(string feature, string detail, string code)
    {
        var errors = Errors(Broken(code), "models/thing.glb");
        string error = Assert.Single(errors);
        Assert.Contains("models/thing.glb", error);
        Assert.Contains($"glTF feature '{feature}'", error);
        Assert.Contains(detail, error);
    }

    [Fact]
    public void EachRejectedFeatureOfTheTableHasATestOrIsTheNoGeometryCase()
    {
        var tested = Rejections().Select(r => (string)r[0]).ToHashSet();
        var rejected = GltfSubset.Features.Where(f => f.Support == GltfSupport.Rejected).Select(f => f.Id).ToHashSet();
        rejected.Remove(GltfSubset.NoGeometry);
        Assert.Equal(rejected.OrderBy(x => x), tested.OrderBy(x => x));
        Assert.Equal(GltfSubset.Features.Count, GltfSubset.Features.Select(f => f.Id).Distinct().Count());
        Assert.Equal(GltfSubset.Features.Count, GltfSubset.Table().Split('\n').Length);
    }

    [Fact]
    public void SeveralUnsupportedFeaturesGiveOneErrorEach()
    {
        var glb = Patch(Triangle(), j =>
        {
            Primitive(j)["mode"] = 0;
            Primitive(j)["targets"] = new JsonArray(new JsonObject { ["POSITION"] = 0 });
            j["extensionsRequired"] = new JsonArray("KHR_draco_mesh_compression", "FAKE_extension");
        });
        var errors = Errors(glb);
        Assert.Equal(4, errors.Count);
        foreach (string feature in new[] { GltfSubset.PrimitiveMode, GltfSubset.MorphTargets, GltfSubset.Draco, GltfSubset.RequiredExtension })
            Assert.Single(errors, e => e.Contains($"'{feature}'"));
    }

    [Fact]
    public void AFileWithNoTrianglesIsRejectedByName()
    {
        var glb = Patch(Triangle(), j => j["nodes"]!.AsArray()[0]!.AsObject().Remove("mesh"));
        Assert.Contains($"'{GltfSubset.NoGeometry}'", Assert.Single(Errors(glb)));
    }

    [Fact]
    public void TheSupportedSubsetStillLoads()
    {
        var glb = Patch(Triangle(), j =>
        {
            Primitive(j)["mode"] = 4;
            j["extensionsRequired"] = new JsonArray("KHR_texture_transform");
            j["extensionsUsed"] = new JsonArray("KHR_texture_transform", "KHR_materials_unlit");
        });
        var mesh = MeshGeometry.ReadGlb(new MemoryStream(glb), "ok.glb", out var errors);
        Assert.NotNull(mesh);
        Assert.Empty(errors);
    }

    [Fact]
    public void ThirdUvSetsAreIgnoredWithAWarningNotAnError()
    {
        var glb = Patch(Triangle(), j => Primitive(j)["attributes"]!["TEXCOORD_2"] = Primitive(j)["attributes"]!["TEXCOORD_0"]!.DeepClone());
        var mesh = MeshGeometry.ReadGlb(new MemoryStream(glb), "ok.glb", out var errors);
        Assert.NotNull(mesh);
        Assert.Empty(errors);
        Assert.Contains("TEXCOORD_2", string.Join('\n', GltfCheckWarnings(glb)));
    }

    private static List<string> GltfCheckWarnings(byte[] glb)
    {
        var warnings = new List<string>();
        GltfSubset.Check(glb, "ok.glb", warnings);
        return warnings;
    }

    // ---- the second UV set and vertex colours ---------------------------------------------------------

    [Fact]
    public void TheSecondUvSetAndTheColoursAreRead()
    {
        var uv1 = new[] { new Vector2(0.1f, 0.2f), new Vector2(0.3f, 0.4f), new Vector2(0.5f, 0.6f) };
        var part = Assert.Single(MeshGeometry.ReadGlb(new MemoryStream(Triangle(uv1, colours: true)), "lm.glb")!.Parts);
        Assert.Equal(uv1, part.Uv1);
        Assert.Equal(new VertexColour(255, 0, 0, 255), part.Rigid![0].Colour);
        Assert.Equal(new VertexColour(0, 0, 255, 128), part.Rigid[2].Colour);
        // The first set is untouched by the second.
        Assert.Equal(Vector2.UnitX, part.Rigid[1].TextureCoordinate);
    }

    [Fact]
    public void AFileWithoutASecondUvSetHasNoneAndWhiteColours()
    {
        var part = Assert.Single(MeshGeometry.ReadGlb(new MemoryStream(Triangle()), "plain.glb")!.Parts);
        Assert.Null(part.Uv1);
        Assert.All(part.Rigid!, v => Assert.Equal(VertexColour.White, v.Colour));
    }

    [Fact]
    public void TheSecondUvSetSurvivesTheCook()
    {
        var uv1 = new[] { new Vector2(0.1f, 0.2f), new Vector2(0.3f, 0.4f), new Vector2(0.5f, 0.6f) };
        foreach (var source in new[] { Triangle(uv1), Triangle() })
        {
            var loose = MeshGeometry.ReadGlb(new MemoryStream(source), "lm.glb")!;
            var file = new MemoryStream();
            CookedMesh.Write(file, loose, SourceStamp.Of(source));
            file.Position = 0;
            var cooked = CookedMesh.Read(file, out _);
            Assert.Equal(loose.Parts[0].Uv1, cooked.Parts[0].Uv1);
            Assert.Equal(loose.Parts[0].Rigid, cooked.Parts[0].Rigid);
        }
    }

    // ---- reload ---------------------------------------------------------------------------------------

    // The renderer's hot reload (Renderer.ReloadMesh) is the client's: it re-runs ContentService.LoadModel
    // and swaps GPU buffers, which a headless test cannot reach. What it relies on is here: reading the same
    // path again gives the file's new geometry, and a broken edit is a named error that leaves the old
    // result with whoever holds it.
    [Fact]
    public void ReadingAChangedFileAgainGivesTheNewGeometryAndABrokenOneANamedError()
    {
        var first = MeshGeometry.ReadGlb(new MemoryStream(Triangle(x: 1f)), "m.glb")!;
        var second = MeshGeometry.ReadGlb(new MemoryStream(Triangle(x: 3f, uv1: new[] { Vector2.Zero, Vector2.UnitX, Vector2.UnitY })), "m.glb")!;
        Assert.NotEqual(first.BoundsRadius, second.BoundsRadius);
        Assert.Null(first.Parts[0].Uv1);
        Assert.NotNull(second.Parts[0].Uv1);

        var broken = Patch(Triangle(), j => Primitive(j)["targets"] = new JsonArray(new JsonObject { ["POSITION"] = 0 }));
        Assert.Null(MeshGeometry.ReadGlb(new MemoryStream(broken), "m.glb", out var errors));
        Assert.Contains("morph-targets", Assert.Single(errors));
        Assert.Equal(1f, first.Parts[0].Rigid![1].Position.X);
    }
}
