#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sage.Simulation;

// Simulation-side rendering data (docs/design/06 §4, 07 §4). No MonoGame types: the client's Extract
// phase reads these and builds the RenderSnapshot.

// A mesh the renderer built itself (terrain chunks, later procedural geometry): the GPU buffers live
// in the renderer and components only hold the handle (R9, 06 §4). 0 = none.
public readonly record struct MeshHandle(int Id)
{
    public bool IsEmpty => Id == 0;
}

// Draws a mesh at the entity's interpolated GlobalTransform (06 §3.2), either a mesh asset (Mesh) or
// one the renderer built (Handle, which wins). An empty Material means sage:lit_default.
[Component("sage:mesh_renderer")]
public struct MeshRenderer : IComponent
{
    [AssetKind("mesh")] public AssetPath Mesh;
    public MeshHandle Handle;
    [RecordRef("material")] public RecordId Material;
    public byte Layer;   // sort layer (0..15), before material in the sort key (06 §3.5)
}

// World resource: sky, sun, ambient and fog for everything drawn in this world (06 §3.9). Every World
// has one with defaults; games change it (later: from an environment record, and time of day).
// Colours are linear RGB 0..1.
public sealed class RenderEnvironment
{
    public Vector3 ClearColor = new(0.333f, 0.420f, 0.184f);   // the sky pass is a clear colour in v1
    public bool Fog = true;
    public Vector3 FogColor = new(0.333f, 0.420f, 0.184f);
    public float FogStart = 30f;
    public float FogEnd = 200f;
    public Vector3 SunDirection = Vector3.Normalize(new Vector3(-0.4f, -1f, -0.3f));   // the way the light travels
    public Vector3 SunColor = new(0.85f, 0.82f, 0.75f);
    public Vector3 AmbientSky = new(0.45f, 0.47f, 0.52f);
    public Vector3 AmbientGround = new(0.22f, 0.20f, 0.17f);
}

public enum RenderPass { Opaque, AlphaTested, Transparent }
public enum MaterialBlend { Opaque, AlphaBlend, Additive }   // AlphaBlend is premultiplied (07 §13)
public enum MaterialCull { Back, None }
public enum SamplerFilter { Point, Linear, Anisotropic }
public enum SamplerAddress { Wrap, Clamp }

public sealed class SamplerDesc
{
    public SamplerFilter Filter = SamplerFilter.Linear;
    public SamplerAddress Address = SamplerAddress.Wrap;
}

// A material parameter value (07 §3.3): a number, a 2–4 number array, a texture path, or `rt:<name>`,
// one of the renderer's named render targets (issue #77, decision D3: a mirror, a security camera's
// screen, a minimap). A render target is not an asset, so it is not a path and no mount is asked for it.
[JsonConverter(typeof(MaterialParamJsonConverter))]
public sealed class MaterialParam
{
    internal const string RenderTargetPrefix = "rt:";

    public float[]? Values;
    [AssetKind("texture")] public AssetPath Texture;
    [Experimental("SAGE0123")] public string? RenderTarget;

    public bool IsTexture => !Texture.IsEmpty;
    public override string ToString() =>
        RenderTarget != null ? RenderTargetPrefix + RenderTarget
        : IsTexture ? Texture.ToString() : $"[{string.Join(", ", Values ?? Array.Empty<float>())}]";
}

[SchemaShape("""
    {
      "description": "A material parameter: a number, 1 to 16 numbers, a texture path, or rt:<name> for one of the renderer's render targets.",
      "anyOf": [
        { "type": "number" },
        { "type": "array", "items": { "type": "number" }, "minItems": 1, "maxItems": 16 },
        { "type": "string" }
      ]
    }
    """)]
internal sealed class MaterialParamJsonConverter : JsonConverter<MaterialParam>
{
    public override MaterialParam Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return new MaterialParam { Values = new[] { reader.GetSingle() } };
            case JsonTokenType.String:
                string text = reader.GetString()!;
                if (text.StartsWith(MaterialParam.RenderTargetPrefix, StringComparison.Ordinal))
                {
                    string name = text[MaterialParam.RenderTargetPrefix.Length..];
                    if (name.Length == 0) throw new JsonException("'rt:' needs a render target name, as in \"rt:minimap\"");
                    return new MaterialParam { RenderTarget = name };
                }
                try { return new MaterialParam { Texture = AssetPath.Intern(text) }; }
                catch (ArgumentException ex) { throw new JsonException(ex.Message); }
            case JsonTokenType.StartArray:
                var values = new List<float>(4);
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.Number) throw new JsonException("material param arrays hold numbers");
                    values.Add(reader.GetSingle());
                }
                if (values.Count is < 1 or > 16) throw new JsonException("material param arrays hold 1 to 16 numbers");
                return new MaterialParam { Values = values.ToArray() };
            default:
                throw new JsonException("a material param is a number, an array of numbers or a texture path");
        }
    }

    public override void Write(Utf8JsonWriter writer, MaterialParam value, JsonSerializerOptions options)
    {
        if (value.RenderTarget != null) { writer.WriteStringValue(MaterialParam.RenderTargetPrefix + value.RenderTarget); return; }
        if (value.IsTexture) { writer.WriteStringValue(value.Texture.ToString()); return; }
        writer.WriteStartArray();
        foreach (var v in value.Values ?? Array.Empty<float>()) writer.WriteNumberValue(v);
        writer.WriteEndArray();
    }
}

// A material (07 §3.3): effect + technique + render state + parameters. Every parameter the effect
// uses (other than the engine's frame/object parameters) needs a value here or in the base chain,
// because OpenGL ignores .fx default values; the client checks that when it builds the material.
[Record("material", Plugin = "sage.client")]
public sealed class MaterialRecord
{
    [AssetKind("shader")] public AssetPath Effect;
    public string Technique = "Default";
    public RenderPass Pass = RenderPass.Opaque;
    public MaterialBlend Blend = MaterialBlend.Opaque;
    public MaterialCull Cull = MaterialCull.Back;
    public bool DepthWrite = true;
    public bool DepthTest = true;
    public bool Fog = true;
    public SamplerDesc Sampler = new();
    public Dictionary<string, MaterialParam> Params = new(StringComparer.Ordinal);

    public static readonly RecordId Default = new("sage", "lit_default");
    public static readonly RecordId Error = new("sage", "error");
}
