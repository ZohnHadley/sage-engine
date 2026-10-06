#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Surface materials (issue #410): lit.fx's normal, specular, emissive and environment maps and vertex
// colours, named by a material record's surface fields. What is headless is here: the tangents and colours a
// model's vertices carry (MeshGeometry), the parameters a record gives the effect with a 1x1 stand-in for
// every map it leaves out (MaterialSurface), its load checks, the stand-ins themselves, the shader's
// declarations, and the Sandbox's wall and moving lamp. The drawing is the client's (the smoke run).
public class SurfaceMaterialTests
{
    public SurfaceMaterialTests() { _ = TestEnv.UserRoot; }

    private static string Repo => TestEnv.FolderAbove("Sage.sln");
    private static string SandboxGame => Path.Combine(Repo, "games", "Sandbox");

    // ---- tangents and colours ------------------------------------------------------------------------

    // A one-metre quad facing +Z, x across and y up, with u along `uAcross` (1: +x, -1: -x) and v running down
    // the image as y goes up (glTF's way), and optionally its own TANGENT and COLOR_0.
    private static byte[] Quad(float uAcross = 1f, Vector4? tangent = null, Vector4? colour = null)
    {
        var model = SharpGLTF.Schema2.ModelRoot.CreateModel();
        var node = model.UseScene("scene").CreateNode("quad");
        var mesh = model.CreateMesh("quad");
        var primitive = mesh.CreatePrimitive();
        primitive.DrawPrimitiveType = SharpGLTF.Schema2.PrimitiveType.TRIANGLES;

        var positions = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0) };
        var normals = Enumerable.Repeat(Vector3.UnitZ, 4).ToArray();
        float u0 = uAcross > 0 ? 0 : 1, u1 = 1 - u0;
        var uvs = new[] { new Vector2(u0, 1), new Vector2(u1, 1), new Vector2(u1, 0), new Vector2(u0, 0) };
        Attribute(model, primitive, "POSITION", positions, SharpGLTF.Schema2.DimensionType.VEC3, bounds: true);
        Attribute(model, primitive, "NORMAL", normals, SharpGLTF.Schema2.DimensionType.VEC3);
        Attribute(model, primitive, "TEXCOORD_0", uvs, SharpGLTF.Schema2.DimensionType.VEC2);
        if (tangent is { } t) Attribute(model, primitive, "TANGENT", Enumerable.Repeat(t, 4).ToArray(), SharpGLTF.Schema2.DimensionType.VEC4);
        if (colour is { } c) Attribute(model, primitive, "COLOR_0", Enumerable.Repeat(c, 4).ToArray(), SharpGLTF.Schema2.DimensionType.VEC4);

        var indices = new uint[] { 0, 1, 2, 0, 2, 3 };
        var index = model.CreateAccessor("indices");
        index.SetIndexData(model.UseBufferView(MemoryMarshal.AsBytes(indices.AsSpan()).ToArray(), 0, null, 0, SharpGLTF.Schema2.BufferMode.ELEMENT_ARRAY_BUFFER),
                           0, indices.Length, SharpGLTF.Schema2.IndexEncodingType.UNSIGNED_INT);
        primitive.SetIndexAccessor(index);
        node.Mesh = mesh;
        return model.WriteGLB().ToArray();
    }

    private static void Attribute<T>(SharpGLTF.Schema2.ModelRoot model, SharpGLTF.Schema2.MeshPrimitive primitive, string name, T[] values,
                                     SharpGLTF.Schema2.DimensionType dimensions, bool bounds = false) where T : unmanaged
    {
        var accessor = model.CreateAccessor(name);
        var view = model.UseBufferView(MemoryMarshal.AsBytes(values.AsSpan()).ToArray(), 0, null, 0, SharpGLTF.Schema2.BufferMode.ARRAY_BUFFER);
        accessor.SetVertexData(view, 0, values.Length, new SharpGLTF.Memory.AttributeFormat(dimensions, SharpGLTF.Schema2.EncodingType.FLOAT, false));
        if (bounds) accessor.UpdateBounds();
        primitive.SetVertexAccessor(name, accessor);
    }

    private static MeshVertex[] Vertices(byte[] glb) =>
        Assert.Single(MeshGeometry.ReadGlb(new MemoryStream(glb), "quad")!.Parts).Rigid!;

    private static void Near(Vector4 expected, Vector4 actual) =>
        Assert.True(Vector4.Distance(expected, actual) < 1e-4f, $"expected {expected}, got {actual}");

    [Fact]
    public void TangentsAreWorkedOutFromTheTextureCoordinatesWhenAModelHasNone()
    {
        // u along +x: the tangent is +x, and cross(normal, tangent) = +y is up the image, so w = +1.
        foreach (var v in Vertices(Quad())) Near(new Vector4(1, 0, 0, 1), v.Tangent);
        // The texture mirrored: u runs along -x, and the bitangent has to be turned round to point up it.
        foreach (var v in Vertices(Quad(uAcross: -1))) Near(new Vector4(-1, 0, 0, -1), v.Tangent);
    }

    [Fact]
    public void AModelsOwnTangentsAndVertexColoursAreKept_AndColourIsWhiteWithoutOne()
    {
        var own = Vertices(Quad(tangent: new Vector4(0, 1, 0, -1), colour: new Vector4(1, 0.5f, 0, 1)));
        foreach (var v in own)
        {
            Near(new Vector4(0, 1, 0, -1), v.Tangent);   // the file's, not worked out (that would be +x, +1)
            Assert.Equal(new VertexColour(255, 128, 0, 255), v.Colour);
        }
        Assert.All(Vertices(Quad()), v => Assert.Equal(VertexColour.White, v.Colour));
    }

    [Fact]
    public void ATangentWithNoUsableTextureCoordinatesIsStillAcrossTheNormal()
    {
        var positions = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY };
        var normals = new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ };
        var tangents = new Vector4[3];
        MeshGeometry.Tangents(positions, normals, new Vector2[3], new[] { 0, 1, 2 }, tangents);   // every uv (0,0)
        foreach (var t in tangents)
        {
            Assert.Equal(0f, Vector3.Dot(new Vector3(t.X, t.Y, t.Z), Vector3.UnitZ), 5);
            Assert.Equal(1f, new Vector3(t.X, t.Y, t.Z).Length(), 5);
            Assert.Equal(1f, MathF.Abs(t.W));
        }
    }

    // The Sandbox's wall carries its own tangents (tools/make_brick_wall.py): they agree with the ones the
    // engine works out from its UVs, so the script and the loader hold the same convention, the glTF one.
    [Fact]
    public void TheSandboxWallsOwnTangentsAreTheOnesTheEngineWouldWorkOut()
    {
        string path = Path.Combine(SandboxGame, "content", "models", "brick_wall.glb");
        var part = Assert.Single(MeshGeometry.ReadGlb(File.OpenRead(path), "brick_wall")!.Parts);
        var vertices = part.Rigid!;
        Assert.Equal(24, vertices.Length);
        var worked = new Vector4[vertices.Length];
        MeshGeometry.Tangents(vertices.Select(v => v.Position).ToArray(), vertices.Select(v => v.Normal).ToArray(),
                              vertices.Select(v => v.TextureCoordinate).ToArray(), part.Indices, worked);
        for (int i = 0; i < vertices.Length; i++) Near(worked[i], vertices[i].Tangent);
    }

    [Fact]
    public void ASkinnedModelsVerticesHaveTangentsAcrossTheirNormals()
    {
        string path = Path.Combine(Repo, "tests", "games", "skeletal", "content", "models", "mannequin.glb");
        var mesh = MeshGeometry.ReadGlb(File.OpenRead(path), "mannequin")!;
        var skinned = mesh.Parts.Where(p => p.Skinned != null).SelectMany(p => p.Skinned!).ToList();
        Assert.NotEmpty(skinned);
        foreach (var v in skinned)
        {
            var t = new Vector3(v.Tangent.X, v.Tangent.Y, v.Tangent.Z);
            Assert.Equal(1f, t.Length(), 4);
            Assert.True(MathF.Abs(Vector3.Dot(t, v.Normal)) < 1e-4f);
            Assert.Equal(1f, MathF.Abs(v.Tangent.W));
            Assert.Equal(VertexColour.White, v.Colour);
        }
    }

    // ---- the material record -------------------------------------------------------------------------

    private static HeadlessApp Boot(string records) =>
        HeadlessApp.Simulation()
            .File("data/materials.json", records)
            .File("textures/n.png", "").File("textures/s.png", "").File("textures/e.png", "").File("textures/sky.png", "")
            .OnRegistered(a => a.Records.Register<MaterialRecord>())   // the client's record type
            .Boot("surface");

    private static float[] Values(MaterialRecord record, string name)
    {
        Assert.True(MaterialSurface.TryGet(record, name, out var value));
        return value.Values!;
    }

    private static AssetPath Map(MaterialRecord record, string name)
    {
        Assert.True(MaterialSurface.TryGet(record, name, out var value));
        Assert.True(value.IsTexture);
        return value.Texture;
    }

#pragma warning disable SAGE0130   // the surface fields
    [Fact]
    public void AMaterialRecordNamesEachMap_AndTheEffectGetsThemWithItsFactors()
    {
        using var app = Boot("""
        [ { "type": "material", "id": "lit", "effect": "shaders/lit.mgfxo", "params": { "AlbedoColor": [1, 1, 1, 1] } },
          { "type": "material", "id": "armour", "base": "lit",
            "normalMap": "textures/n.png", "specularMap": "textures/s.png", "specular": 1.5, "gloss": 0.8,
            "emissiveMap": "textures/e.png", "emissive": [2, 1, 0.5], "vertexColors": true,
            "environmentMap": "textures/sky.png", "reflectivity": 0.3 } ]
        """);
        Assert.True(app.Records.ErrorCount == 0, string.Join("\n", app.Records.LoadErrors));
        var armour = app.Records.Get<MaterialRecord>(new RecordId("sage", "armour"));

        Assert.Equal(AssetPath.Intern("textures/n.png"), Map(armour, MaterialSurface.NormalMap));
        Assert.Equal(AssetPath.Intern("textures/s.png"), Map(armour, MaterialSurface.SpecularMap));
        Assert.Equal(AssetPath.Intern("textures/e.png"), Map(armour, MaterialSurface.EmissiveMap));
        Assert.Equal(AssetPath.Intern("textures/sky.png"), Map(armour, MaterialSurface.EnvironmentMap));
        Assert.Equal(new[] { 1.5f, 0.8f, 0.3f, 1f }, Values(armour, MaterialSurface.SurfaceParams));
        Assert.Equal(new[] { 2f, 1f, 0.5f }, Values(armour, MaterialSurface.EmissiveColor));
        Assert.True(MaterialSurface.Asked(armour));
        Assert.False(MaterialSurface.TryGet(armour, "AlbedoColor", out _));   // not a surface parameter
    }

    // No permutations: a material that names no map gets a 1x1 texture for each that changes nothing, and
    // factors that add nothing, so it draws as it did before the surface existed, with the same technique.
    [Fact]
    public void MissingMapsFallBackToOnePixelTexturesThatChangeNothing()
    {
        var plain = new MaterialRecord();
        Assert.Equal(MaterialSurface.FlatNormal, Map(plain, MaterialSurface.NormalMap));
        Assert.Equal(MaterialSurface.White, Map(plain, MaterialSurface.SpecularMap));   // the factors as they are
        Assert.Equal(MaterialSurface.White, Map(plain, MaterialSurface.EmissiveMap));   // times a black emissive
        Assert.Equal(MaterialSurface.Black, Map(plain, MaterialSurface.EnvironmentMap));
        Assert.Equal(new[] { 0f, 0.5f, 0f, 0f }, Values(plain, MaterialSurface.SurfaceParams));   // no highlight, no vertex colours
        Assert.Equal(new[] { 0f, 0f, 0f }, Values(plain, MaterialSurface.EmissiveColor));
        Assert.False(MaterialSurface.Asked(plain));

        // The stand-ins are engine content: one pixel each, a normal straight out of the surface and black.
        string textures = Path.Combine(Repo, "engine_content", "textures");
        Assert.Equal(new byte[] { 128, 128, 255, 255 }, OnePixel(Path.Combine(textures, "flat_normal.png")));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, OnePixel(Path.Combine(textures, "black.png")));
        Assert.True(File.Exists(Path.Combine(textures, "white.png")));
        // And the engine's own lit materials leave every map out.
        string engine = File.ReadAllText(Path.Combine(Repo, "engine_content", "data", "materials.json"));
        Assert.DoesNotContain("normalMap", engine);
    }

    // The RGBA of a 1x1, 8-bit RGBA PNG with one IDAT (as make_engine_textures.py writes them).
    private static byte[] OnePixel(string path)
    {
        byte[] png = File.ReadAllBytes(path);
        Assert.Equal(1, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));   // width
        Assert.Equal(1, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));   // height
        int at = 8;
        while (at < png.Length)
        {
            int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            string tag = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            if (tag == "IDAT")
            {
                using var z = new ZLibStream(new MemoryStream(png, at + 8, length), CompressionMode.Decompress);
                var row = new byte[5];
                z.ReadExactly(row);
                return row[1..];   // after the filter byte
            }
            at += 12 + length;
        }
        throw new InvalidDataException("no IDAT");
    }

    [Fact]
    public void SurfaceMistakesAreLoadErrors()
    {
        using var app = Boot("""
        [ { "type": "material", "id": "lit", "effect": "shaders/lit.mgfxo" },
          { "type": "material", "id": "doubled", "base": "lit", "params": { "NormalMap": "textures/n.png" } },
          { "type": "material", "id": "glare", "base": "lit", "specular": 5 },
          { "type": "material", "id": "dull", "base": "lit", "specular": -1 },
          { "type": "material", "id": "polish", "base": "lit", "gloss": 1.5 },
          { "type": "material", "id": "mirror", "base": "lit", "reflectivity": 0.5 },
          { "type": "material", "id": "dark_glow", "base": "lit", "emissiveMap": "textures/e.png" },
          { "type": "material", "id": "anti_glow", "base": "lit", "emissive": [1, -1, 0] },
          { "type": "material", "id": "fine", "base": "lit", "normalMap": "textures/n.png", "specular": 4, "gloss": 1,
            "environmentMap": "textures/sky.png", "reflectivity": 1, "emissiveMap": "textures/e.png", "emissive": [1, 1, 1] } ]
        """);
        string errors = string.Join("\n", app.Records.LoadErrors);
        Assert.Equal(7, app.Records.ErrorCount);
        Assert.Contains("doubled: 'NormalMap' is set by the material's surface fields", errors);
        Assert.Contains("glare: must be between 0 and 4", errors);
        Assert.Contains("dull: must be between 0 and 4", errors);
        Assert.Contains("polish: must be between 0 and 1", errors);
        Assert.Contains("mirror: reflects nothing without an environmentMap", errors);
        Assert.Contains("dark_glow: glows with emissive's colour, which is 0 0 0", errors);
        Assert.Contains("anti_glow: must be three numbers of 0 or more", errors);
        Assert.DoesNotContain("fine", errors);
    }
#pragma warning restore SAGE0130

    // ---- the shader ----------------------------------------------------------------------------------

    // lit.fx cannot be compiled on Linux (mgfxc needs Wine), so its contract with MaterialSurface is read
    // from its text: every surface parameter is declared, and every sampler has a register of its own, none
    // of them the shadow map's s1.
    [Fact]
    public void LitFxDeclaresEverySurfaceParameterWithASamplerRegisterOfItsOwn()
    {
        string lit = File.ReadAllText(Path.Combine(Repo, "engine_content", "shaders", "lit.fx"));
        foreach (var map in new[] { MaterialSurface.NormalMap, MaterialSurface.SpecularMap, MaterialSurface.EmissiveMap, MaterialSurface.EnvironmentMap })
            Assert.Matches($@"(?m)^texture {map};", lit);
        Assert.Matches($@"(?m)^float4 {MaterialSurface.SurfaceParams};", lit);
        Assert.Matches($@"(?m)^float3 {MaterialSurface.EmissiveColor};", lit);

        var registers = Regex.Matches(lit, @"(?m)^sampler \w+ : register\((s\d+)\)").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(new[] { "s0", "s2", "s3", "s4", "s5" }, registers);
        Assert.Contains("register(s1)", File.ReadAllText(Path.Combine(Repo, "engine_content", "shaders", "common.fxh")));
        // Every vertex input (rigid, skinned and instanced, issue 4n-5) reads the tangent and the colour the
        // client's VertexMesh and VertexSkinned carry.
        Assert.Equal(3, Regex.Matches(lit, @"float4 Tangent\s+: TANGENT0;").Count);
        Assert.Equal(3, Regex.Matches(lit, @"float4 Color\s+: COLOR0;").Count);
    }

    // ---- the Sandbox ---------------------------------------------------------------------------------

    // The issue's "done": the Sandbox shows a normal-mapped wall lit by a moving lamp.
    [Fact]
    public void TheSandboxHasANormalMappedWallLitByALampGoingRoundInFrontOfIt()
    {
        using var app = HeadlessApp.ForGame(SandboxGame, new global::Sandbox.SandboxModule()).WithEngineContent()
            .OnRegistered(a => a.Records.Register<MaterialRecord>())
            .Boot();
        Assert.True(app.Records.ErrorCount == 0, string.Join("\n", app.Records.LoadErrors));

        var brick = app.Records.Get<MaterialRecord>(new RecordId("sandbox", "brick_wall"));
        Assert.Equal(AssetPath.Intern("textures/brick_wall_normal.png"), Map(brick, MaterialSurface.NormalMap));
        Assert.Equal(AssetPath.Intern("textures/brick_wall_spec.png"), Map(brick, MaterialSurface.SpecularMap));
        Assert.True(Values(brick, MaterialSurface.SurfaceParams)[0] > 0f);   // it shines
        var glow = app.Records.Get<MaterialRecord>(new RecordId("sandbox", "lamp_glow"));
        Assert.True(Values(glow, MaterialSurface.EmissiveColor).Sum() > 0f);

        var world = app.World;
        Step(world);
        var wall = Assert.Single(world.Query<MeshRenderer>().Entities.ToEntityList(), e => e.Name == "brick wall");
        Assert.Equal(new RecordId("sandbox", "brick_wall"), world.Get<MeshRenderer>(wall).Material);
        var lamp = Assert.Single(world.Query<PointLight>().Entities.ToEntityList(), e => e.Name == "wall lamp");
        Assert.True(world.Get<PointLight>(lamp).Lit);

        // Round a circle: always the radius from its middle, and somewhere else a second later.
        var orbit = world.Get<global::Sandbox.Orbit>(lamp);
        var middle = world.Get<Transform>(lamp).LocalPosition - global::Sandbox.OrbitSystem.Offset(orbit, orbit.Angle);
        var wallAt = world.Get<Transform>(wall).LocalPosition;
        var facing = Vector3.Transform(Vector3.UnitZ, world.Get<Transform>(wall).LocalRotation);   // the bricks' side
        var seen = new List<Vector3>();
        for (int second = 0; second < 6; second++)
        {
            for (int i = 0; i < 60; i++) Step(world);
            var at = world.Get<Transform>(lamp).LocalPosition;
            Assert.Equal(0.9f, Vector3.Distance(middle, at), 2);
            // In front of the bricks, in a plane 0.9 m out from them, and never past the wall's edges.
            Assert.Equal(0.9f, Vector3.Dot(at - wallAt, facing), 1);
            Assert.True(Vector3.Distance(wallAt, at) < 2.2f, $"the lamp at {at} wandered from the wall at {wallAt}");
            seen.Add(at);
        }
        for (int i = 1; i < seen.Count; i++) Assert.True(Vector3.Distance(seen[i - 1], seen[i]) > 0.3f, "the lamp stood still");
    }

    private static void Step(World world)
    {
        world.RunFixed(1f / 60f);
        world.RunFrame(1f / 60f, 1f);
    }
}
