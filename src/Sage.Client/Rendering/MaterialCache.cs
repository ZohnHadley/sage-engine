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
        "ShadowCascadeRects", "ShadowCascadeBias",       // its cascades (issue 4n-11), frame tier
        "Lightmap",   // a lightmapped draw's baked light (issue #313), set per draw
        "SceneDepth", "SoftParams", "SoftRect",   // a soft particle run's depth and fade (issue 4n-6), set per run
        "LampShadowMap", "LampShadowAtlas",       // the lamps' shadow atlas (issue #315), frame tier
        "LampShadowForward", "LampShadowRight", "LampShadowUp", "LampShadowCorner",   // its first two lamps' maps, per draw
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
        Lightmap = P("Lightmap");
        ShadowCaster = effect.Techniques["ShadowCaster"];
        ShadowCasterSkinned = effect.Techniques["ShadowCasterSkinned"];
        ShadowCascadeRects = P("ShadowCascadeRects"); ShadowCascadeBias = P("ShadowCascadeBias");
        ShadowCasterAlphaTest = effect.Techniques[ShadowMath.CasterTechnique(RenderPass.AlphaTested, skinned: false)];
        ShadowCasterAlphaTestSkinned = effect.Techniques[ShadowMath.CasterTechnique(RenderPass.AlphaTested, skinned: true)];
        ShadowCasterInstanced = effect.Techniques[Instancing.TechniqueFor("ShadowCaster")];
        SceneDepth = P("SceneDepth"); SoftParams = P("SoftParams"); SoftRect = P("SoftRect");
        LampShadowMap = P("LampShadowMap"); LampShadowAtlas = P("LampShadowAtlas");
        LampShadowForward = P("LampShadowForward"); LampShadowRight = P("LampShadowRight");
        LampShadowUp = P("LampShadowUp"); LampShadowCorner = P("LampShadowCorner");
    }

    public Effect Effect { get; }
    public long FrameStamp = -1;
    public readonly EffectParameter? ViewProj, SunDir, SunColor, AmbientSky, AmbientGround, FogColor, FogParams, Time, World, Tint, FogEnabled;
    public readonly EffectParameter? LightPositions, LightColors, LightCount, LightSpots;
    public readonly EffectParameter? Bones;   // float4x3[SkinMath.MaxBones]: object tier, skinned draws only
    public readonly EffectParameter? ShadowViewProj, ShadowParams, ShadowMap;   // issue 4h-4
    public readonly EffectParameter? Lightmap;   // texture: object tier, lightmapped draws only (issue #313)
    public readonly EffectTechnique? ShadowCaster, ShadowCasterSkinned;          // what draws a caster into the map
    public readonly EffectParameter? ShadowCascadeRects, ShadowCascadeBias;      // issue 4n-11
    public readonly EffectTechnique? ShadowCasterAlphaTest, ShadowCasterAlphaTestSkinned;   // a cut-out caster (4n-11)
    public readonly EffectParameter? SceneDepth, SoftParams, SoftRect;          // soft particles (issue 4n-6)
    public readonly EffectParameter? LampShadowMap, LampShadowAtlas;             // lamp shadows (issue #315)
    public readonly EffectParameter? LampShadowForward, LampShadowRight, LampShadowUp, LampShadowCorner;

    // The lamps' maps this view reads (null: none drawn), and the per-draw arrays for the first PerDraw lamps.
    private LampShadowFrame? _lampMaps;
    private bool _lampsSet;   // whether the arrays on the effect hold a map (else they all say "none")
    private readonly Vector4[] _lampForward = new Vector4[LampShadows.PerDraw];
    private readonly Vector4[] _lampRight = new Vector4[LampShadows.PerDraw];
    private readonly Vector4[] _lampUp = new Vector4[LampShadows.PerDraw];
    private readonly Vector4[] _lampCorner = new Vector4[LampShadows.PerDraw];
    private readonly int[] _lightOrder = new int[LightRules.PerObject];

    // The cascades' matrices and rectangles, reused (set once a view).
    private readonly Matrix[] _cascadeViewProj = new Matrix[ShadowMath.MaxCascades];
    private readonly Vector4[] _cascadeRects = new Vector4[ShadowMath.MaxCascades];
    public readonly EffectTechnique? ShadowCasterInstanced;                      // a run of one mesh's casters in one draw (issue 4n-5)

    // The four lights this draw is lit by (06 §3.9). Reused arrays: this is set per item, and a frame
    // with a thousand items would otherwise allocate two arrays a thousand times (02 §4.6).
    private readonly Vector3[] _positions = new Vector3[LightRules.PerObject];
    private readonly Vector4[] _colours = new Vector4[LightRules.PerObject];
    private readonly Vector4[] _spots = new Vector4[LightRules.PerObject];

    public void SetLights(ReadOnlySpan<LightSample> chosen)
    {
        if (LightCount == null) return;                 // an effect that does not light, such as debug lines

        // The lamps with a shadow map first (issue #315; LampShadows.DrawOrder): the shaders look up the first two.
        var order = _lightOrder.AsSpan(0, chosen.Length);
        LampShadows.DrawOrder(chosen, order);
        for (int i = 0; i < chosen.Length; i++)
        {
            ref readonly var light = ref chosen[order[i]];
            _positions[i] = new Vector3(light.Position.X, light.Position.Y, light.Position.Z);
            _colours[i] = new Vector4(light.Colour.X, light.Colour.Y, light.Colour.Z, light.Range);
            _spots[i] = new Vector4(light.Spot.X, light.Spot.Y, light.Spot.Z, light.Spot.W);
        }
        if (LampShadowCorner != null) SetLampShadows(chosen, order);

        LightCount.SetValue((float)chosen.Length);
        if (chosen.Length == 0) return;                 // nothing may be read past the count

        LightPositions?.SetValue(_positions);
        LightColors?.SetValue(_colours);
        LightSpots?.SetValue(_spots);
    }

    // The first PerDraw lamps' maps (common.fxh `LampShadow`): each one's axes, its z/w curve and where its
    // block is, or w = 0 in LampShadowCorner for a lamp without one. Nothing is set while no lamp of any draw
    // has had a map since the arrays last said "none": a level without lamp shadows sets nothing per draw.
    private void SetLampShadows(ReadOnlySpan<LightSample> chosen, ReadOnlySpan<int> order)
    {
        bool any = false;
        for (int k = 0; k < LampShadows.PerDraw; k++)
        {
            int slot = k < chosen.Length && _lampMaps != null ? chosen[order[k]].ShadowSlot : 0;
            if (slot <= 0 || slot > _lampMaps!.Count)
            {
                _lampForward[k] = _lampRight[k] = _lampUp[k] = _lampCorner[k] = Vector4.Zero;
                continue;
            }
            ref readonly var fit = ref _lampMaps.Fits[slot - 1];
            _lampForward[k] = new Vector4(fit.Forward.X, fit.Forward.Y, fit.Forward.Z, fit.InvTan);
            _lampRight[k] = new Vector4(fit.Right.X, fit.Right.Y, fit.Right.Z, fit.DepthA);
            _lampUp[k] = new Vector4(fit.Up.X, fit.Up.Y, fit.Up.Z, fit.DepthB);
            _lampCorner[k] = new Vector4(fit.Corner.X, fit.Corner.Y, fit.BiasSlope, 1f);
            any = true;
        }
        if (!any && !_lampsSet) return;
        _lampsSet = any;
        LampShadowForward?.SetValue(_lampForward);
        LampShadowRight?.SetValue(_lampRight);
        LampShadowUp?.SetValue(_lampUp);
        LampShadowCorner!.SetValue(_lampCorner);
    }

    public void SetFrame(in RenderView view, in EnvironmentParams env, in ShadowFrame shadow, Texture2D? shadowMap, Texture2D none,
                         LampShadowFrame? lampMaps = null, Texture2D? lampMap = null)
    {
        // The lamps' atlas (issue #315): x, y = one texel in uv, z = texels a side of a face, w = the depth margin in metres.
        _lampMaps = lampMap != null ? lampMaps : null;
        if (LampShadowMap != null)
        {
            LampShadowMap.SetValue(_lampMaps != null ? lampMap : none);
            var atlas = _lampMaps?.Atlas ?? new LampShadowAtlas(1, LampShadows.MinSize);
            LampShadowAtlas?.SetValue(new Vector4(1f / atlas.Width, 1f / atlas.Height, atlas.TileSize, LampShadows.BiasMetres));
        }

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
        bool on = shadow.Drawn && shadowMap != null && env.ShadowStrength > 0f && shadow.Count > 0;
        var bias = Vector4.Zero;
        var toCaster = on ? Matrix.CreateTranslation(view.CameraPosition - shadow.Camera) : Matrix.Identity;
        for (int k = 0; k < ShadowMath.MaxCascades; k++)
        {
            // Cascades past the count are never chosen (ShadowParams.y); they are set to something anyway.
            ref readonly var cascade = ref shadow.Cascades[Math.Min(k, Math.Max(shadow.Count - 1, 0))];
            _cascadeViewProj[k] = on ? toCaster * cascade.ViewProj : Matrix.Identity;
            _cascadeRects[k] = on ? cascade.Rect : new Vector4(0f, 0f, 1f, 1f);
            float b = on ? cascade.Bias : 0f;
            if (k == 0) bias.X = b; else if (k == 1) bias.Y = b; else bias.Z = b;
        }
        ShadowViewProj?.SetValue(_cascadeViewProj);
        ShadowCascadeRects?.SetValue(_cascadeRects);
        ShadowCascadeBias?.SetValue(bias);
        // x = strength (0: none), y = cascades, z = texels a side of each, w = 1 / that.
        ShadowParams.SetValue(on ? new Vector4(env.ShadowStrength, shadow.Count, shadow.Size, 1f / shadow.Size) : new Vector4(0f, 1f, 1f, 1f));
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
    public EffectTechnique? Instanced; // the technique's instanced twin (Instancing.TechniqueFor, issue 4n-5), if the effect has one
    // The technique's soft twin and its instanced twin (SoftParticles.TechniqueFor, issue 4n-6), for a
    // Transparent material whose effect has them and reads SceneDepth; null draws soft particles hard.
    public EffectTechnique? Soft, SoftInstanced;
    // The effect's `Lightmapped` technique, for a mesh with a baked lightmap (issue #313): only for a material
    // drawn with `Default`, since it is Default's lighting with the lightmap in it. Any other technique
    // (AlphaTest, Unlit, a game's) draws such a mesh as it would any other.
    public EffectTechnique? Lightmapped;
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

    // The record behind a material id, for `r_snapshot_dump`: "?" past the end.
    public string NameOf(int id) => (uint)id < (uint)_idList.Count ? _idList[id].ToString() : "?";

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

        // Soft particles (issue 4n-6): only a transparent material, which is not in the depth it would read.
        var soft = record.Pass == RenderPass.Transparent && binding.SceneDepth != null && binding.SoftParams != null
            ? effect.Techniques[SoftParticles.TechniqueFor(record.Technique)] : null;
        return new MaterialRuntime
        {
            Id = id,
            Effect = binding,
            Technique = technique,
            Skinned = effect.Techniques["Skinned"],
            Instanced = effect.Techniques[Instancing.TechniqueFor(record.Technique)],
            Soft = soft,
            SoftInstanced = soft == null ? null : effect.Techniques[Instancing.TechniqueFor(SoftParticles.TechniqueFor(record.Technique))],
            Lightmapped = technique.Name == "Default" ? effect.Techniques["Lightmapped"] : null,
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
