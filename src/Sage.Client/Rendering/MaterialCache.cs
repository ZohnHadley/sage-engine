#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace sage_engine;

// The engine's frame- and object-tier parameters of one Effect (07 §3.4), looked up once. Effects
// are shared by every material that uses them; FrameStamp says whether this frame's frame-tier
// parameters are already set on it.
internal sealed class EffectBinding
{
    public static readonly HashSet<string> EngineParams = new(StringComparer.Ordinal)
    {
        "ViewProj", "SunDir", "SunColor", "AmbientSky", "AmbientGround", "FogColor", "FogParams", "Time",
        "World", "Tint", "FogEnabled",
    };

    public EffectBinding(Effect effect)
    {
        Effect = effect;
        EffectParameter? P(string name) => effect.Parameters[name];
        ViewProj = P("ViewProj"); SunDir = P("SunDir"); SunColor = P("SunColor");
        AmbientSky = P("AmbientSky"); AmbientGround = P("AmbientGround");
        FogColor = P("FogColor"); FogParams = P("FogParams"); Time = P("Time");
        World = P("World"); Tint = P("Tint"); FogEnabled = P("FogEnabled");
    }

    public Effect Effect { get; }
    public long FrameStamp = -1;
    public readonly EffectParameter? ViewProj, SunDir, SunColor, AmbientSky, AmbientGround, FogColor, FogParams, Time, World, Tint, FogEnabled;

    public void SetFrame(in RenderView view, in EnvironmentParams env)
    {
        ViewProj?.SetValue(view.ViewProj);
        SunDir?.SetValue(env.SunDirection);
        SunColor?.SetValue(env.SunColor);
        AmbientSky?.SetValue(env.AmbientSky);
        AmbientGround?.SetValue(env.AmbientGround);
        FogColor?.SetValue(env.FogColor);
        FogParams?.SetValue(env.FogParams);
        Time?.SetValue(env.Time);
    }
}

// A material record resolved against its loaded effect (07 §3.4): technique, material parameters and
// cached state objects. Rebuilt when records reload.
internal sealed class MaterialRuntime
{
    public required RecordId Id;
    public required EffectBinding Effect;
    public required EffectTechnique Technique;
    public required (EffectParameter Parameter, MaterialParam Value, Texture2D? Texture)[] Params;
    public required RenderPass Pass;
    public required BlendState Blend;
    public required RasterizerState Raster;
    public required RasterizerState RasterWire;
    public required DepthStencilState Depth;
    public required SamplerState Sampler;
    public required float Fog;
    public bool IsError;
    public int Drawn;        // items drawn with it in frame DrawnFrame (mat_list)
    public long DrawnFrame = -1;
}

// Material records → MaterialRuntime, by compact id (07 §4). Ids stay stable for the process; a
// record hot reload drops the runtimes and they rebuild on next use. A material that can't be built
// (missing effect, unknown technique, a parameter without a value) resolves to sage:error, logged
// once (07 §8).
internal sealed class MaterialCache : IDisposable
{
    private readonly ContentService _content;
    private readonly RecordStore _records;
    private readonly Texture2D _missingTexture;
    private readonly Dictionary<RecordId, int> _ids = new();
    private readonly List<RecordId> _idList = new();
    private MaterialRuntime?[] _runtimes = new MaterialRuntime?[16];
    private readonly Dictionary<Effect, EffectBinding> _effects = new();
    private MaterialRuntime? _error;

    private static readonly RasterizerState WireBack = new() { CullMode = CullMode.CullCounterClockwiseFace, FillMode = FillMode.WireFrame };
    private static readonly RasterizerState WireNone = new() { CullMode = CullMode.None, FillMode = FillMode.WireFrame };
    private static readonly DepthStencilState WriteNoTest = new() { DepthBufferEnable = true, DepthBufferFunction = CompareFunction.Always, DepthBufferWriteEnable = true };

    public MaterialCache(GraphicsDevice device, ContentService content, RecordStore records)
    {
        _content = content;
        _records = records;
        // Missing textures: a magenta/black checker (05 §8), impossible to mistake for real content.
        _missingTexture = new Texture2D(device, 2, 2);
        _missingTexture.SetData(new[] { Color.Magenta, Color.Black, Color.Black, Color.Magenta });
        Resolve(MaterialRecord.Error);   // id 0
    }

    public int Count => _idList.Count;

    public void Dispose() => _missingTexture.Dispose();   // effects and textures belong to the ContentService

    public int Resolve(RecordId id)
    {
        if (id.IsEmpty) id = MaterialRecord.Default;
        if (!_ids.TryGetValue(id, out int index))
        {
            index = _idList.Count;
            _ids[id] = index;
            _idList.Add(id);
            if (index == _runtimes.Length) Array.Resize(ref _runtimes, _runtimes.Length * 2);
        }
        return index;
    }

    // The runtime for an id, building it on first use. Null only if even sage:error can't be built.
    public MaterialRuntime? Get(int id)
    {
        var runtime = _runtimes[id];
        if (runtime != null) return runtime;
        runtime = Build(_idList[id]) ?? ErrorRuntime();
        _runtimes[id] = runtime;
        return runtime;
    }

    // Records reloaded: rebuild everything lazily (ids stay valid).
    public void Invalidate()
    {
        Array.Clear(_runtimes);
        _error = null;
    }

    public IEnumerable<(int Id, RecordId Record, MaterialRuntime? Runtime)> Entries =>
        _idList.Select((r, i) => (i, r, _runtimes[i]));

    private MaterialRuntime? ErrorRuntime()
    {
        if (_error != null) return _error;
        var record = _records.TryGet(MaterialRecord.Error, out MaterialRecord r) ? r
            : new MaterialRecord { Effect = AssetPath.Intern("shaders/error.mgfxo"), Fog = false };
        _error = Build(MaterialRecord.Error, record);
        if (_error == null) Log.Once(LogCat.Shaders, LogLevel.Error, "no-error-material", "sage:error can't be built (shaders/error.mgfxo missing?); broken materials are not drawn");
        else _error.IsError = true;
        return _error;
    }

    private MaterialRuntime? Build(RecordId id)
    {
        if (!_records.TryGet(id, out MaterialRecord record))
        {
            if (id != MaterialRecord.Error)
                Log.Error(LogCat.Shaders, $"Material {id} not found; drawing sage:error");
            return null;
        }
        return Build(id, record);
    }

    private MaterialRuntime? Build(RecordId id, MaterialRecord record)
    {
        if (record.Effect.IsEmpty) { Log.Error(LogCat.Shaders, $"Material {id}: no \"effect\""); return null; }
        var effect = _content.LoadEffect(record.Effect);
        if (effect == null) { Log.Error(LogCat.Shaders, $"Material {id}: effect {record.Effect} didn't load"); return null; }
        if (!_effects.TryGetValue(effect, out var binding)) _effects[effect] = binding = new EffectBinding(effect);

        var technique = effect.Techniques[record.Technique];
        if (technique == null)
        {
            Log.Error(LogCat.Shaders, $"Material {id}: effect {record.Effect} has no technique '{record.Technique}' " +
                                      $"(has {string.Join(", ", effect.Techniques.Select(t => t.Name))})");
            return null;
        }

        // Every parameter the effect uses needs a value: GL ignores .fx defaults (07 §3.3).
        var values = new List<(EffectParameter, MaterialParam, Texture2D?)>();
        var missing = new List<string>();
        foreach (var p in effect.Parameters)
        {
            if (EffectBinding.EngineParams.Contains(p.Name)) continue;
            if (!record.Params.TryGetValue(p.Name, out var value)) { missing.Add(p.Name); continue; }
            string? problem = Check(p, value);
            if (problem != null) { Log.Error(LogCat.Shaders, $"Material {id}: param {p.Name}: {problem}"); return null; }
            Texture2D? texture = null;
            if (value.IsTexture)
            {
                texture = _content.LoadTexture(value.Texture);
                if (texture == null) Log.Warn(LogCat.Shaders, $"Material {id}: texture {value.Texture} missing; using the checker placeholder");
                texture ??= _missingTexture;
            }
            values.Add((p, value, texture));
        }
        if (missing.Count > 0)
        {
            Log.Error(LogCat.Shaders, $"Material {id}: effect {record.Effect} needs values for {string.Join(", ", missing)} (GL has no .fx defaults)");
            return null;
        }
        foreach (var name in record.Params.Keys)
            if (effect.Parameters[name] == null)
                Log.Warn(LogCat.Shaders, $"Material {id}: param '{name}' isn't used by {record.Effect} (typo?); ignored");

        return new MaterialRuntime
        {
            Id = id,
            Effect = binding,
            Technique = technique,
            Params = values.ToArray(),
            Pass = record.Pass,
            Blend = record.Blend switch { MaterialBlend.AlphaBlend => BlendState.AlphaBlend, MaterialBlend.Additive => BlendState.Additive, _ => BlendState.Opaque },
            Raster = record.Cull == MaterialCull.None ? RasterizerState.CullNone : RasterizerState.CullCounterClockwise,
            RasterWire = record.Cull == MaterialCull.None ? WireNone : WireBack,
            Depth = (record.DepthTest, record.DepthWrite) switch
            {
                (true, true) => DepthStencilState.Default,
                (true, false) => DepthStencilState.DepthRead,
                (false, false) => DepthStencilState.None,
                _ => WriteNoTest,
            },
            Sampler = (record.Sampler.Filter, record.Sampler.Address) switch
            {
                (SamplerFilter.Point, SamplerAddress.Clamp) => SamplerState.PointClamp,
                (SamplerFilter.Point, _) => SamplerState.PointWrap,
                (SamplerFilter.Anisotropic, SamplerAddress.Clamp) => SamplerState.AnisotropicClamp,
                (SamplerFilter.Anisotropic, _) => SamplerState.AnisotropicWrap,
                (_, SamplerAddress.Clamp) => SamplerState.LinearClamp,
                _ => SamplerState.LinearWrap,
            },
            Fog = record.Fog ? 1f : 0f,
        };
    }

    private static string? Check(EffectParameter p, MaterialParam value)
    {
        if (p.ParameterType == EffectParameterType.Texture2D || p.ParameterType == EffectParameterType.Texture)
            return value.IsTexture ? null : "needs a texture path";
        if (p.ParameterType != EffectParameterType.Single) return $"type {p.ParameterType} isn't supported in materials";
        if (value.IsTexture) return "needs numbers, not a texture";
        int expected = p.RowCount * p.ColumnCount;
        return value.Values!.Length == expected ? null : $"needs {expected} number(s), got {value.Values.Length}";
    }

    // Sets technique, material parameters and render state (07 §3.4). Called when the material changes.
    public static void Apply(GraphicsDevice device, MaterialRuntime m, bool wireframe)
    {
        var effect = m.Effect.Effect;
        effect.CurrentTechnique = m.Technique;
        foreach (var (p, value, texture) in m.Params)
        {
            if (texture != null) { p.SetValue(texture); continue; }
            var v = value.Values!;
            switch (v.Length)
            {
                case 1: p.SetValue(v[0]); break;
                case 2: p.SetValue(new Vector2(v[0], v[1])); break;
                case 3: p.SetValue(new Vector3(v[0], v[1], v[2])); break;
                case 4: p.SetValue(new Vector4(v[0], v[1], v[2], v[3])); break;
                default: p.SetValue(v); break;
            }
        }
        m.Effect.FogEnabled?.SetValue(m.Fog);
        device.BlendState = m.Blend;
        device.RasterizerState = wireframe ? m.RasterWire : m.Raster;
        device.DepthStencilState = m.Depth;
        device.SamplerStates[0] = m.Sampler;
    }

}
