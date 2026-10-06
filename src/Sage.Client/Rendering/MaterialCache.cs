#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Sage.Client;

// The engine's frame- and object-tier parameters of one Effect (07 §3.4), looked up once. Effects
// are shared by every material that uses them; FrameStamp says whether this frame's frame-tier
// parameters are already set on it.
internal sealed class EffectBinding
{
    public static readonly HashSet<string> EngineParams = new(StringComparer.Ordinal)
    {
        "ViewProj", "SunDir", "SunColor", "AmbientSky", "AmbientGround", "FogColor", "FogParams", "Time",
        "World", "Tint", "FogEnabled", "LightPositions", "LightColors", "LightCount", "LightSpots",
        "Bones",   // the skinned draw's palette (issue #117), set per draw
        "ShadowViewProj", "ShadowParams", "ShadowMap",   // the sun's shadow map (issue 4h-4), frame tier
    };

    public EffectBinding(Effect effect)
    {
        Effect = effect;
        EffectParameter? P(string name) => effect.Parameters[name];
        ViewProj = P("ViewProj"); SunDir = P("SunDir"); SunColor = P("SunColor");
        AmbientSky = P("AmbientSky"); AmbientGround = P("AmbientGround");
        FogColor = P("FogColor"); FogParams = P("FogParams"); Time = P("Time");
        World = P("World"); Tint = P("Tint"); FogEnabled = P("FogEnabled");
        LightPositions = P("LightPositions"); LightColors = P("LightColors"); LightCount = P("LightCount");
        LightSpots = P("LightSpots");   // spot lights' cones (issue #314); an effect without them lights all round
        Bones = P("Bones");
        ShadowViewProj = P("ShadowViewProj"); ShadowParams = P("ShadowParams"); ShadowMap = P("ShadowMap");
        ShadowCaster = effect.Techniques["ShadowCaster"];
        ShadowCasterSkinned = effect.Techniques["ShadowCasterSkinned"];
    }

    public Effect Effect { get; }
    public long FrameStamp = -1;
    public readonly EffectParameter? ViewProj, SunDir, SunColor, AmbientSky, AmbientGround, FogColor, FogParams, Time, World, Tint, FogEnabled;
    public readonly EffectParameter? LightPositions, LightColors, LightCount, LightSpots;
    public readonly EffectParameter? Bones;   // float4x3[SkinMath.MaxBones]: object tier, skinned draws only
    public readonly EffectParameter? ShadowViewProj, ShadowParams, ShadowMap;   // issue 4h-4
    public readonly EffectTechnique? ShadowCaster, ShadowCasterSkinned;          // what draws a caster into the map

    // The four lights this draw is lit by (06 §3.9). Reused arrays: this is set per item, and a frame
    // with a thousand items would otherwise allocate two arrays a thousand times (02 §4.6).
    private readonly Vector3[] _positions = new Vector3[LightRules.PerObject];
    private readonly Vector4[] _colours = new Vector4[LightRules.PerObject];
    private readonly Vector4[] _spots = new Vector4[LightRules.PerObject];

    public void SetLights(ReadOnlySpan<LightSample> chosen)
    {
        if (LightCount == null) return;                 // an effect that does not light, such as debug lines

        for (int i = 0; i < chosen.Length; i++)
        {
            _positions[i] = new Vector3(chosen[i].Position.X, chosen[i].Position.Y, chosen[i].Position.Z);
            _colours[i] = new Vector4(chosen[i].Colour.X, chosen[i].Colour.Y, chosen[i].Colour.Z, chosen[i].Range);
            _spots[i] = new Vector4(chosen[i].Spot.X, chosen[i].Spot.Y, chosen[i].Spot.Z, chosen[i].Spot.W);
        }

        LightCount.SetValue((float)chosen.Length);
        if (chosen.Length == 0) return;                 // nothing may be read past the count

        LightPositions?.SetValue(_positions);
        LightColors?.SetValue(_colours);
        LightSpots?.SetValue(_spots);
    }

    public void SetFrame(in RenderView view, in EnvironmentParams env, in ShadowFrame shadow, Texture2D? shadowMap, Texture2D none)
    {
        ViewProj?.SetValue(view.ViewProj);
        SunDir?.SetValue(env.SunDirection);
        SunColor?.SetValue(env.SunColor);
        AmbientSky?.SetValue(env.AmbientSky);
        AmbientGround?.SetValue(env.AmbientGround);
        FogColor?.SetValue(env.FogColor);
        FogParams?.SetValue(env.FogParams);
        Time?.SetValue(env.Time);
        if (ShadowParams == null) return;   // an effect that reads no shadow

        // The sun's shadow map (issue 4h-4). It was drawn relative to the main view's camera; this view's
        // positions are relative to its own, so the difference goes in front. Strength 0 (no map this
        // frame, `r_shadows 0`, night) makes the shader ignore what it samples, and it samples a real
        // texture anyway rather than whatever the slot last held.
        bool on = shadow.Drawn && shadowMap != null && env.ShadowStrength > 0f;
        if (on)
        {
            ShadowViewProj?.SetValue(Matrix.CreateTranslation(view.CameraPosition - shadow.Camera) * shadow.ViewProj);
            ShadowParams.SetValue(new Vector4(env.ShadowStrength, 1f / shadow.Size, shadow.Bias, shadow.Size));
        }
        else
        {
            ShadowViewProj?.SetValue(Matrix.Identity);
            ShadowParams.SetValue(new Vector4(0f, 1f, 0f, 1f));
        }
        ShadowMap?.SetValue(on ? shadowMap : none);
    }
}

// A material record resolved against its loaded effect (07 §3.4): technique, material parameters and
// cached state objects. Rebuilt when records reload.
internal sealed class MaterialRuntime
{
    public required RecordId Id;
    public required EffectBinding Effect;
    public required EffectTechnique Technique;
    public EffectTechnique? Skinned;   // the effect's `Skinned` technique, for skinned meshes (issue #117)
    public bool WarnedNoSkin;
    public bool CastShadows;           // the record's `castShadows` (issue 4h-4; ShadowMath.Casts)
    public required (EffectParameter Parameter, MaterialParam Value, Texture2D? Texture)[] Params;
    public required RenderPass Pass;
    public required BlendState Blend;
    public required RasterizerState Raster;
    public required RasterizerState RasterWire;
    public required DepthStencilState Depth;
    public required SamplerFilter Filter;      // the record's sampler; TextureSampling picks the state (issue #317)
    public required SamplerAddress Address;
    public required float Fog;
    public EffectParameter? Albedo;   // the material's "Albedo" texture param, overridden per sprite sheet
    public bool Surface;              // the effect draws surface maps (issue #410), in sampler slots 2 to 5
    public ulong SampledTargets;      // bit n: a param samples render target n (`rt:<name>`); not drawn into it
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
    // The scope each material's textures load at (issue #308): the strongest it was resolved for. A
    // streaming world's materials load theirs at the sectors' scope, so they go when the sectors do.
    private AssetScope[] _scopes = new AssetScope[16];
    // Ids whose material *and* the sage:error fallback both failed to build. Remembered until the next
    // Invalidate, so a broken material costs one attempt and one log line, not one per draw per frame
    // (272,780 lines in eight seconds with the shaders missing, 2026-09-27).
    private bool[] _unbuildable = new bool[16];
    private readonly Dictionary<Effect, EffectBinding> _effects = new();
    private MaterialRuntime? _error;
    private bool _errorUnbuildable;

    private static readonly RasterizerState WireBack = new() { CullMode = CullMode.CullCounterClockwiseFace, FillMode = FillMode.WireFrame };
    private static readonly RasterizerState WireNone = new() { CullMode = CullMode.None, FillMode = FillMode.WireFrame };
    private static readonly DepthStencilState WriteNoTest = new() { DepthBufferEnable = true, DepthBufferFunction = CompareFunction.Always, DepthBufferWriteEnable = true };

    private readonly RenderTargetPool _targets;

    public MaterialCache(GraphicsDevice device, ContentService content, RecordStore records, RenderTargetPool targets)
    {
        _content = content;
        _records = records;
        _targets = targets;
        // Missing textures: the content service's checker (05 §8), texture id 0.
        _missingTexture = content.MissingTexture;
        Resolve(MaterialRecord.Error, AssetScope.Engine);   // id 0
    }

    public int Count => _idList.Count;

    public Texture2D MissingTexture => _missingTexture;

    public void Dispose() { }   // effects and textures (the checker too) belong to the ContentService

    // A material id, for the game (its textures stay for the process).
    public int Resolve(RecordId id) => Resolve(id, AssetScope.Game);

    // A material id for something drawn at a scope: its textures load at the strongest scope it was asked for.
    public int Resolve(RecordId id, AssetScope scope)
    {
        if (id.IsEmpty)
        {
            id = MaterialRecord.Default;   // the engine's default is everybody's: its textures stay
            scope = AssetScopes.Stronger(scope, AssetScope.Game);
        }
        if (!_ids.TryGetValue(id, out int index))
        {
            index = _idList.Count;
            _ids[id] = index;
            _idList.Add(id);
            if (index == _runtimes.Length)
            {
                Array.Resize(ref _runtimes, _runtimes.Length * 2);
                Array.Resize(ref _unbuildable, _runtimes.Length);
                Array.Resize(ref _scopes, _runtimes.Length);
            }
            _scopes[index] = scope;
        }
        else if (scope < _scopes[index]) _scopes[index] = scope;
        return index;
    }

    // The runtime for an id, building it on first use. Null only if even sage:error can't be built.
    public MaterialRuntime? Get(int id)
    {
        var runtime = _runtimes[id];
        if (runtime != null || _unbuildable[id]) return runtime;
        runtime = Build(_idList[id], _scopes[id]) ?? ErrorRuntime();
        _runtimes[id] = runtime;
        _unbuildable[id] = runtime == null;
        return runtime;
    }

    // Records reloaded: rebuild everything lazily (ids stay valid).
    public void Invalidate()
    {
        Array.Clear(_runtimes);
        Array.Clear(_unbuildable);
        _error = null;
        _errorUnbuildable = false;
        _effects.Clear();   // an effect may have been reloaded under us; its bindings are dead
    }

    public IEnumerable<(int Id, RecordId Record, MaterialRuntime? Runtime)> Entries =>
        _idList.Select((r, i) => (i, r, _runtimes[i]));

    private MaterialRuntime? ErrorRuntime()
    {
        if (_error != null || _errorUnbuildable) return _error;
        var record = _records.TryGet(MaterialRecord.Error, out MaterialRecord r) ? r
            : new MaterialRecord { Effect = AssetPath.Intern("shaders/error.mgfxo"), Fog = false };
        _error = Build(MaterialRecord.Error, record, AssetScope.Engine);
        _errorUnbuildable = _error == null;
        if (_error == null) Log.Once(LogCat.Shaders, LogLevel.Error, "no-error-material", "sage:error can't be built (shaders/error.mgfxo missing?); broken materials are not drawn");
        else _error.IsError = true;
        return _error;
    }

    private MaterialRuntime? Build(RecordId id, AssetScope scope)
    {
        if (!_records.TryGet(id, out MaterialRecord record))
        {
            // A terrain material (issue #307) is drawn as a material of the splat effect, made from it.
            if (_records.TryGet(id, out TerrainMaterialRecord terrain)) return Build(id, SplatMaterial(terrain), scope);
            if (id != MaterialRecord.Error)
                Log.Error(LogCat.Shaders, $"Material {id} not found; drawing sage:error");
            return null;
        }
        return Build(id, record, scope);
    }

    // ---- terrain materials (issue #307) ----------------------------------------------------------------

    private static readonly AssetPath White = AssetPath.Intern("textures/white.png");

    private static MaterialRecord SplatMaterial(TerrainMaterialRecord terrain) => new()
    {
        Effect = TerrainSplat.Effect,
        Technique = TerrainSplat.Technique,
        Params = TerrainSplat.Params(terrain, White),
    };

    // What terrain named `material` (a world's `Terrain.Material`) is drawn with: the terrain material
    // itself, whose layers the mesh then carries weights for (`splat`), or — with no such record, or no
    // splat effect to draw it (a build without shaders) — a plain material and no weights, said once.
    public RecordId TerrainLook(RecordId material, out TerrainMaterialRecord? splat)
    {
        splat = null;
        if (material.IsEmpty) return TerrainSplat.DefaultMaterial;
        if (!_records.TryGet(material, out TerrainMaterialRecord record))
        {
            Log.Once(LogCat.Shaders, LogLevel.Error, "terrain-material:" + material,
                     $"Terrain material {material} not found; drawing {TerrainSplat.DefaultMaterial}");
            return TerrainSplat.DefaultMaterial;
        }
        var fallback = record.Fallback.IsEmpty ? TerrainSplat.DefaultMaterial : record.Fallback;
        var effect = _content.LoadEffect(TerrainSplat.Effect);
        if (effect == null || effect.Techniques[TerrainSplat.Technique] == null)
        {
            Log.Once(LogCat.Shaders, LogLevel.Warn, "terrain-fallback:" + material,
                     $"Terrain material {material}: {TerrainSplat.Effect} (technique '{TerrainSplat.Technique}') is not available " +
                     $"(a build without shaders?); drawing its fallback {fallback}");
            return fallback;
        }
        splat = record;
        return material;
    }

    private MaterialRuntime? Build(RecordId id, MaterialRecord record, AssetScope scope)
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
        ulong sampled = 0;
        foreach (var p in effect.Parameters)
        {
            if (EffectBinding.EngineParams.Contains(p.Name)) continue;
            if (PostChain.EngineParams.Contains(p.Name)) continue;   // a post effect's picture and night (issue 4h-6)
            // A surface parameter (issue #410) comes from the record's surface fields, or a 1x1 stand-in.
            if (!record.Params.TryGetValue(p.Name, out var value) && !MaterialSurface.TryGet(record, p.Name, out value))
            { missing.Add(p.Name); continue; }
            string? problem = Check(p, value);
            if (problem != null) { Log.Error(LogCat.Shaders, $"Material {id}: param {p.Name}: {problem}"); return null; }
            Texture2D? texture = null;
            if (value.RenderTarget != null)
            {
                // `rt:<name>` (issue #77, D3): the target's texture, made now if nothing has drawn it yet.
                // A target remade later (a new size) invalidates the cache, so this is rebuilt.
                int target = _targets.Id(value.RenderTarget);
                texture = _targets.Texture(target);
                if (target < 64) sampled |= 1UL << target;
            }
            else if (value.IsTexture)
            {
                texture = _content.LoadTexture(value.Texture, scope);
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
        if (effect.Parameters[MaterialSurface.SurfaceParams] == null && MaterialSurface.Asked(record))
            Log.Warn(LogCat.Shaders, $"Material {id}: {record.Effect} draws no surface maps (normalMap, specular, emissive...); they are ignored");

        return new MaterialRuntime
        {
            Id = id,
            Effect = binding,
            Technique = technique,
            Skinned = effect.Techniques["Skinned"],
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
            Filter = record.Sampler.Filter,
            Address = record.Sampler.Address,
            Fog = record.Fog ? 1f : 0f,
            CastShadows = record.CastShadows,
            Albedo = effect.Parameters["Albedo"],
            Surface = effect.Parameters[MaterialSurface.SurfaceParams] != null,
            SampledTargets = sampled,
        };
    }

    private static string? Check(EffectParameter p, MaterialParam value)
    {
        bool texture = value.IsTexture || value.RenderTarget != null;
        if (p.ParameterType == EffectParameterType.Texture2D || p.ParameterType == EffectParameterType.Texture)
            return texture ? null : "needs a texture path or rt:<name>";
        if (p.ParameterType != EffectParameterType.Single) return $"type {p.ParameterType} isn't supported in materials";
        if (texture) return "needs numbers, not a texture";
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
        var sampler = TextureSampling.For(m.Filter, m.Address);
        device.SamplerStates[0] = sampler;
        // lit.fx's surface maps (issue #410) sample as the albedo does; s1 is the shadow map's (common.fxh).
        if (m.Surface)
            for (int slot = 2; slot <= 5; slot++) device.SamplerStates[slot] = sampler;
    }

}
