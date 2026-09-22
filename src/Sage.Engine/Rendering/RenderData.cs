#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Friflo.Engine.ECS;

namespace sage_engine;

// Simulation-side rendering data (docs/design/06 §4, 07 §4). No MonoGame types: the client's Extract
// phase reads these and builds the RenderSnapshot.

// Draws a mesh asset at the entity's interpolated GlobalTransform (06 §3.2). An empty Material means
// sage:lit_default.
public struct MeshRenderer : IComponent
{
    public AssetPath Mesh;
    public RecordId Material;
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

// A material parameter value (07 §3.3): a number, a 2–4 number array, or a texture path.
[JsonConverter(typeof(MaterialParamJsonConverter))]
public sealed class MaterialParam
{
    public float[]? Values;
    public AssetPath Texture;

    public bool IsTexture => !Texture.IsEmpty;
    public override string ToString() => IsTexture ? Texture.ToString() : $"[{string.Join(", ", Values ?? Array.Empty<float>())}]";
}

internal sealed class MaterialParamJsonConverter : JsonConverter<MaterialParam>
{
    public override MaterialParam Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return new MaterialParam { Values = new[] { reader.GetSingle() } };
            case JsonTokenType.String:
                try { return new MaterialParam { Texture = AssetPath.Intern(reader.GetString()!) }; }
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
        if (value.IsTexture) { writer.WriteStringValue(value.Texture.ToString()); return; }
        writer.WriteStartArray();
        foreach (var v in value.Values ?? Array.Empty<float>()) writer.WriteNumberValue(v);
        writer.WriteEndArray();
    }
}

// A material (07 §3.3): effect + technique + render state + parameters. Every parameter the effect
// uses (other than the engine's frame/object parameters) needs a value here or in the base chain,
// because OpenGL ignores .fx default values; the client checks that when it builds the material.
[Record("material")]
public sealed class MaterialRecord
{
    public AssetPath Effect;
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
