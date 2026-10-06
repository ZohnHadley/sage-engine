#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Sage.Simulation;

// The animator on an entity (issue #118): which anim_graph it runs, the model whose skeleton and clips
// it plays, and where each layer is. Saved by name, like a state machine:
//
//   "sage:animator": { "Graph": "game:soldier", "Model": "models/soldier.glb",
//     "Layers": [ { "Name": "base", "State": "move", "Time": 1.25, "Phase": 0.4,
//                   "From": "idle", "FromPhase": 0.9, "Fade": 0.1, "FadeDuration": 0.2, "FadeEase": "SmoothStep" },
//                 { "Name": "upper", "State": "none", "Time": 3, "Phase": 0, "From": null, ... } ],
//     "Params": [ { "Name": "speed", "Value": 2.5 }, { "Name": "crouch", "Value": 0 } ] }
//
// A state that is not in the graph any more (a save from before an edit, a hot reload) sends its layer
// to the layer's initial state, with a warning; a layer or param that is gone is dropped, a new one
// starts at its initial state or default. The pose is never saved: it is sampled again from these.
[Component("sage:animator")]
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public struct Animator : IComponent
{
    [RecordRef("anim_graph"), Property(Tooltip = "The anim_graph record it runs")]
    public RecordId Graph;
    [AssetKind("mesh"), Property(Tooltip = "The skinned .glb whose skeleton and clips it plays")]
    public AssetPath Model;
    [Property(Tooltip = "Each layer's state, time and cross-fade, the base first; filled on its first tick")]
    public AnimatorLayer[]? Layers;
    [Property(Tooltip = "Each param's value, by name; filled on its first tick")]
    public AnimatorParam[]? Params;
    // Frozen by Animators.Suspend (issue #244): no stepping, no sampling, and its pose is left to whoever
    // suspended it (a ragdoll) to rewrite. A new field with a default: saves from before it load unsuspended.
    [Property(Tooltip = "Frozen by Animators.Suspend: it neither steps nor writes its pose until Resume (a ragdoll owns the pose meanwhile)")]
    [Experimental(AnimationRagdollApi.Experimental, UrlFormat = AnimationRagdollApi.Url)]
    public bool Suspended;

    // Its pose's slot in the world's AnimatorPoses (0: none yet), and the compiled graph its indices were
    // resolved against. Neither is saved: both are found again after a load.
    [Transient] internal int Slot;
    [Transient] internal int Version;
}

// One layer of an animator: the state it is in and the one it is fading from.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public struct AnimatorLayer
{
    [Property(Tooltip = "The layer's name (\"base\" for the graph's own states)")]
    public string? Name;
    [Property(Tooltip = "The state it is in, by name")]
    public string? State;
    [Property(Min = 0, Unit = "s", Tooltip = "Seconds in that state (what a transition's after reads)")]
    public float Time;
    [Property(Min = 0, Tooltip = "How far through its clip or blend: 0..1 a loop (wrapping), clamped at 1 for a one-shot")]
    public float Phase;
    [Property(Tooltip = "The state it is cross-fading from; null when it is not fading")]
    public string? From;
    [Property(Min = 0, Tooltip = "How far through the state it is fading from")]
    public float FromPhase;
    [Property(Min = 0, Unit = "s", Tooltip = "Seconds into the cross-fade")]
    public float Fade;
    [Property(Min = 0, Unit = "s", Tooltip = "How long the cross-fade takes")]
    public float FadeDuration;
    [Property(Tooltip = "The curve the cross-fade follows")]
    public Ease FadeEase;

    // Indices into the compiled layer (-1: none); found again by name.
    internal int Index;
    internal int FromIndex;
    // Entered since the last step, by code (Animators.Play) or as a new animator's initial state: its
    // next step raises the events at the clip's very start too (issue #119). Not saved.
    internal bool Entered;
    // Fading from a pose snapshot (Animators.PlayFrom, issue #244) rather than from a state: the pose is
    // the instance's (AnimatorPoses.Instance.Snapshots), never saved, so a load mid-fade has none and
    // Resolve drops the fade (the layer shows its state directly).
    internal bool FromSnapshot;

    // Cross-fading from a state, or from a pose snapshot (Animators.PlayFrom; From is null then).
    public readonly bool Fading => From != null || FromSnapshot;
}

[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public struct AnimatorParam
{
    public string? Name;
    public float Value;
}

// "animator": { "graph": "soldier", "model": "models/soldier.glb" }. Reads the model when it is applied
// (content time, never a tick), and says which clips the graph names that the model lacks. With no
// `model`, the skinned_mesh part's mesh (#117) beside it: the skeleton it draws is the one it poses.
[PrefabPart("animator", Plugin = RegistrationOwners.Core, After = new[] { "skinned_mesh" })]
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public sealed class AnimatorPart : IPrefabPart
{
    [RecordRef("anim_graph"), Property(Tooltip = "The anim_graph record it runs")]
    public RecordId Graph;
    [AssetKind("mesh"), Property(Tooltip = "The skinned .glb whose skeleton and clips it plays; left out, the skinned_mesh part's mesh")]
    public AssetPath Model;

    public void Apply(in PrefabPartContext ctx)
    {
        if (Graph.IsEmpty)
        {
            ctx.Warn("an animator part needs a \"graph\" (an anim_graph record)");
            return;
        }
        var model = Model;
        if (model.IsEmpty && ctx.World.TryGet<SkinnedMeshRenderer>(ctx.Entity, out var skinned)) model = skinned.Mesh;
        if (model.IsEmpty) ctx.Warn("an animator part needs a \"model\" (a skinned .glb) or a skinned_mesh beside it; it will stand still");
        ctx.World.Add(ctx.Entity, new Animator { Graph = Graph, Model = model });
        if (model.IsEmpty || !ctx.World.Resources.TryGet<GltfAnimationReader>(out var reader) || reader == null) return;
        if (reader.Load(model) is not { } set) return;
        if (!ctx.World.Resources.TryGet<RecordStore>(out var records) || records == null) return;
        if (!records.TryGet(Graph, out AnimGraphRecord graph)) return;
        foreach (var clip in graph.Compile().ClipNames)
            if (set.FindClip(clip) == null)
                Log.Once(LogCat.Animation, LogLevel.Warn, $"anim-clip:{Graph}:{model}:{clip}",
                    $"{ctx.Where}: anim_graph {Graph} plays '{clip}', which {set.Source} has no clip called; that state stands at rest");
    }
}

// Reading and driving animators: params, triggers, states, tags and the sampled pose. Registered by the
// engine (Engine's constructor, sage.core), like state machines.
//
// **The pose, for #117 (skinning) and #120 (attachments, IK):** AnimatorSystem ("sage.animation.animator",
// Phase.Animation) rewrites each animator's pooled SkeletonPose from scratch every tick (from its last
// sample: LOD samples distant ones less often), fills its ModelSpace and registers it in SkeletonPoses,
// which passes it to SkinPoses. IK and attachments adjust it in Phase.Late, so they never compound.
// `TryGetPose` hands it out. The pose belongs to the world's AnimatorPoses: never keep it past the tick,
// never Dispose it. While the animator is suspended (Suspend, PlayFrom: AnimatorSuspend.cs, #244) the
// pose stays registered and whoever suspended it rewrites it instead.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public static partial class Animators
{
    public const string SystemId = "sage.animation.animator";
    public const string SetParamInput = "SetAnimParam";
    public const string TriggerInput = "AnimTrigger";

    // The output an animator fires for every clip event it crosses, with the event's name as its value
    // (issue #119): `{ "output": "OnAnimEvent", "target": "door", "input": "Open" }` wired to a lever's pull.
    public const string AnimEventOutput = "OnAnimEvent";

    // Params copied into the entity's AimIk (#120) every tick, degrees to radians, when both exist.
    public const string AimPitchParam = "aim_pitch";
    public const string AimYawParam = "aim_yaw";

    // The base layer's name in saves, anim_debug and StateOf.
    public const string BaseLayer = "base";

    // The most points a blend space may have (its weights are worked out on the stack).
    public const int MaxBlendPoints = 16;

    internal const string LodDistanceCVar = "anim_lod_distance";

    internal static void Register(Engine engine)
    {
        // Routed to the animator (issue #91): an entity without one is refused naming sage:animator.
        engine.Inputs.Register<Animator>(SetParamInput, static (World world, in IOContext io) => SetParamFromText(world, io.Self, io.Parameter));
        engine.Inputs.Register<Animator>(TriggerInput, static (World world, in IOContext io) =>
        {
            if (!SetTrigger(world, io.Self, io.Parameter.Trim()))
                Log.Warn(LogCat.Events, $"I/O: {TriggerInput}({io.Parameter}) at {World.Describe(io.Self)}: no such trigger param");
        });
        engine.Records.AddCheck<AnimGraphRecord>(Check);
        engine.Outputs.Declare(AnimEventOutput, "A clip event this animator's graph crossed (anim_events, a sprite sheet's frame events); hands on the event's name.");

        engine.CVars.Register(LodDistanceCVar, 30f, CVarFlags.None,
            "Animation LOD: animators farther than this from the main camera sample their pose every 2nd tick, past twice it every 4th " +
            "(their states and times still step every tick); 0 = every animator every tick.", 0f, 100000f);
        engine.CVars.RegisterCommand("anim_debug", CVarFlags.None,
            "anim_debug [filter]: every animator's layers (state, time, cross-fade, blend weights), params and LOD rate, and every sprite's clip and frame.", a =>
        {
            string filter = a.Count > 0 ? a[0] : "";
            int shown = 0;
            foreach (var world in engine.Worlds)
            {
                foreach (var e in world.Query<Animator>().Entities)
                {
                    string label = World.Describe(e);
                    if (filter.Length > 0 && !label.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                    Log.Info(LogCat.Console, $"[{world.Name}] {Describe(world, e)}");
                    shown++;
                }
                if (world.Resources.TryGet<IAnimDebugSource>(out var extra) && extra != null)
                {
                    var lines = new List<string>();
                    extra.Describe(world, filter, lines);
                    foreach (var line in lines) Log.Info(LogCat.Console, $"[{world.Name}] {line}");
                    shown += lines.Count;
                }
            }
            Log.Info(LogCat.Console, $"anim_debug: {shown} animator(s) and sprite(s)");
        });
    }

    // ---- params ----------------------------------------------------------------------------------------

    // Sets a float or bool param (a bool reads any non-zero as 1). False when there is no animator or no
    // such param. A param with a `from` is overwritten on the next tick.
    public static bool SetParam(World world, Entity entity, string name, float value)
    {
        if (!TryParams(world, entity, out var graph, out var values)) return false;
        int i = graph.ParamIndex(name);
        if (i < 0) return false;
        values[i].Value = graph.ParamKinds[i] == AnimParamKind.Float ? value : value != 0f ? 1f : 0f;
        return true;
    }

    public static bool SetParam(World world, Entity entity, string name, bool value) => SetParam(world, entity, name, value ? 1f : 0f);

    // Sets a trigger: the next tick's transitions with `on` its name may take it; then it is used up.
    public static bool SetTrigger(World world, Entity entity, string name)
    {
        if (!TryParams(world, entity, out var graph, out var values)) return false;
        int i = graph.ParamIndex(name);
        if (i < 0 || graph.ParamKinds[i] != AnimParamKind.Trigger) return false;
        values[i].Value = 1f;
        return true;
    }

    // A param's value (a bool or a set trigger: 1), or null.
    public static float? GetParam(World world, Entity entity, string name)
    {
        if (!TryParams(world, entity, out var graph, out var values)) return null;
        int i = graph.ParamIndex(name);
        return i < 0 ? null : values[i].Value;
    }

    // Reads a param without the graph: what the anim_param condition asks. Allocation-free.
    internal static bool TryReadParam(World world, Entity entity, string name, out float value)
    {
        value = 0f;
        if (entity.IsNull || !world.IsAlive(entity) || !world.TryGet<Animator>(entity, out var a) || a.Params is not { } values) return false;
        for (int i = 0; i < values.Length; i++)
        {
            if (!string.Equals(values[i].Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            value = values[i].Value;
            return true;
        }
        return false;
    }

    // "name value" (value: a number, true/false, on/off; left out: 1). Only a trigger's name: sets it.
    // Allocation-free when it succeeds: a wire may send one every tick (the name is matched as a span).
    private static void SetParamFromText(World world, Entity entity, string text)
    {
        var span = text.AsSpan().Trim();
        int space = span.IndexOf(' ');
        var name = space < 0 ? span : span[..space];
        var rest = space < 0 ? ReadOnlySpan<char>.Empty : span[(space + 1)..].Trim();
        if (!TryParams(world, entity, out var graph, out var values))
        {
            Log.Warn(LogCat.Events, $"I/O: {SetParamInput}({text}) at {World.Describe(entity)}: its animator has no graph yet");
            return;
        }
        int i = -1;
        for (int p = 0; p < graph.ParamNames.Length && i < 0; p++)
            if (name.Equals(graph.ParamNames[p], StringComparison.OrdinalIgnoreCase)) i = p;
        if (i < 0)
        {
            string missing = name.ToString();
            Log.Warn(LogCat.Events, $"I/O: {SetParamInput}({text}) at {World.Describe(entity)}: no param '{missing}'" + Spelling.Suggest(missing, graph.ParamNames));
            return;
        }
        if (graph.ParamKinds[i] == AnimParamKind.Trigger)
        {
            values[i].Value = 1f;
            return;
        }
        float value;
        if (rest.IsEmpty) value = 1f;
        else if (float.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out float number)) value = number;
        else if (rest.Equals("true", StringComparison.OrdinalIgnoreCase) || rest.Equals("on", StringComparison.OrdinalIgnoreCase)) value = 1f;
        else if (rest.Equals("false", StringComparison.OrdinalIgnoreCase) || rest.Equals("off", StringComparison.OrdinalIgnoreCase)) value = 0f;
        else
        {
            Log.Warn(LogCat.Events, $"I/O: {SetParamInput}({text}) at {World.Describe(entity)}: expected \"name value\" (a number, true or false)");
            return;
        }
        values[i].Value = graph.ParamKinds[i] == AnimParamKind.Float ? value : value != 0f ? 1f : 0f;
    }

    // The animator's graph and its param values, shaped to that graph (filled if this is before its
    // first tick).
    private static bool TryParams(World world, Entity entity, [NotNullWhen(true)] out AnimGraphRecord.Compiled? graph,
                                  [NotNullWhen(true)] out AnimatorParam[]? values)
    {
        graph = null;
        values = null;
        if (entity.IsNull || !world.IsAlive(entity) || !world.Has<Animator>(entity)) return false;
        ref var a = ref world.Get<Animator>(entity);
        if (Compiled(world, a.Graph) is not { } g) return false;
        AnimatorStepper.Resolve(entity, ref a, g);
        graph = g;
        values = a.Params!;
        return true;
    }

    // ---- states and tags -------------------------------------------------------------------------------

    // The state a layer is in ("base" or null: the base layer), or null (no animator, no such layer, not
    // started).
    public static string? StateOf(World world, Entity entity, string? layer = null)
    {
        if (entity.IsNull || !world.IsAlive(entity) || !world.TryGet<Animator>(entity, out var a) || a.Layers is not { } layers) return null;
        string want = string.IsNullOrEmpty(layer) ? BaseLayer : layer;
        foreach (var l in layers)
            if (string.Equals(l.Name, want, StringComparison.OrdinalIgnoreCase)) return string.IsNullOrEmpty(l.State) ? null : l.State;
        return null;
    }

    // Whether the state some layer is in carries `tag` (a layer fading into a state is in it).
    public static bool HasTag(World world, Entity entity, string tag)
    {
        if (entity.IsNull || !world.IsAlive(entity) || !world.Has<Animator>(entity)) return false;
        ref var a = ref world.Get<Animator>(entity);
        if (a.Layers is not { } layers || Compiled(world, a.Graph) is not { } g) return false;
        AnimatorStepper.Resolve(entity, ref a, g);
        for (int i = 0; i < layers.Length && i < g.Layers.Length; i++)
        {
            int s = a.Layers![i].Index;
            if (s >= 0 && g.Layers[i].States[s].HasTag(tag)) return true;
        }
        return false;
    }

    // How much of `clip` the layer's current state plays (0..1; a clip state: 1 for its clip; a blend:
    // the clip's weight at the params now). Not the cross-fade: what the state itself is made of.
    public static float ClipWeight(World world, Entity entity, string clip, string? layer = null)
    {
        if (entity.IsNull || !world.IsAlive(entity) || !world.Has<Animator>(entity)) return 0f;
        ref var a = ref world.Get<Animator>(entity);
        if (Compiled(world, a.Graph) is not { } g) return 0f;
        AnimatorStepper.Resolve(entity, ref a, g);
        int l = g.LayerIndex(layer);
        if (l < 0 || a.Layers![l].Index < 0) return 0f;
        var state = g.Layers[l].States[a.Layers[l].Index];
        Span<float> weights = stackalloc float[MaxBlendPoints];
        int count = AnimatorStepper.Weights(state, a.Params!, weights);
        float total = 0f;
        for (int i = 0; i < count; i++)
        {
            int c = state.IsBlend ? state.PointClips[i] : state.Clip;
            if (string.Equals(g.ClipNames[c], clip, StringComparison.Ordinal)) total += weights[i];
        }
        return total;
    }

    // Goes to `state` on a layer now (a cross-fade by the state's fade). False when there is no such state.
    public static bool Play(World world, Entity entity, string state, string? layer = null)
    {
        if (entity.IsNull || !world.IsAlive(entity) || !world.Has<Animator>(entity)) return false;
        ref var a = ref world.Get<Animator>(entity);
        if (Compiled(world, a.Graph) is not { } g) return false;
        AnimatorStepper.Resolve(entity, ref a, g);
        int l = g.LayerIndex(layer);
        if (l < 0) return false;
        int to = g.Layers[l].IndexOf(state);
        if (to < 0) return false;
        AnimatorStepper.Change(g, g.Layers[l], ref a.Layers![l], to);
        return true;
    }

    // The clip a layer shows now and how far into it, in seconds: its state's clip, or the heaviest clip
    // of its blend (issue #119). What drives a sprite (whose animator's leaves are sheet clips) and what
    // a viewmodel or a sound can ask. The cross-fade is not counted: the state it is going to. False
    // when there is no animator, no such layer, or its state plays nothing.
    public static bool TryGetClip(World world, Entity entity, [NotNullWhen(true)] out string? clip, out float time, string? layer = null)
    {
        clip = null;
        time = 0f;
        if (entity.IsNull || !world.IsAlive(entity) || !world.Has<Animator>(entity)) return false;
        ref var a = ref world.Get<Animator>(entity);
        if (a.Layers == null || Compiled(world, a.Graph) is not { } g) return false;
        AnimatorStepper.Resolve(entity, ref a, g);
        int l = g.LayerIndex(layer);
        if (l < 0) return false;
        ref var s = ref a.Layers![l];
        if (s.Index < 0) return false;
        int c = AnimatorStepper.HeaviestClip(g.Layers[l].States[s.Index], a.Params!);
        if (c < 0) return false;
        clip = g.ClipNames[c];
        if (world.Resources.TryGet<AnimatorPoses>(out var poses) && poses != null && poses.Find(a.Slot, entity) is { } instance
            && c < instance.Clips.Length && instance.Clips[c] is { } resolved)
            time = SyncMarkers.ClipPhase(g.Layers[l].States[s.Index], resolved, s.Phase) * resolved.Duration;
        return true;
    }

    // An anim_graph made in code for a sprite that stands in one clip and swings with another (issue
    // #119: how content from before graphs keeps its sprite fighters swinging, Sage.Gameplay's upgrade).
    // `initial` is "idle", playing `idle` (looping; empty: nothing); on the trigger `trigger` it goes to
    // "swing", which plays `swing` once — again from its start on another trigger — and goes back to
    // "idle" when the clip is done (anim_finished). No cross-fades: sprites cannot blend. Added to
    // `records` as a runtime record, sage:sprite_swing, made again when asked with other names; its id.
    public static RecordId SpriteSwingGraph(RecordStore records, string idle, string swing, string trigger)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(idle);
        ArgumentNullException.ThrowIfNull(swing);
        ArgumentNullException.ThrowIfNull(trigger);
        var id = new RecordId(ComponentSchema.EngineNamespace, "sprite_swing");
        if (records.TryGet(id, out AnimGraphRecord made) && made.States.TryGetValue("idle", out var i) && i != null && i.Clip == idle
            && made.States.TryGetValue("swing", out var sw) && sw != null && sw.Clip == swing && made.Params.ContainsKey(trigger))
            return id;

        var back = new StateTransition { To = "idle", When = new AnimFinishedCondition() };
        var graph = new AnimGraphRecord
        {
            Initial = "idle",
            Fade = 0f,
            Params = new Dictionary<string, AnimParam> { [trigger] = new AnimParam { Kind = AnimParamKind.Trigger } },
            States = new Dictionary<string, AnimState>
            {
                ["idle"] = new AnimState { Clip = idle },
                ["swing"] = new AnimState
                {
                    Clip = swing,
                    Loop = false,
                    Transitions = new List<StateTransition> { new() { To = "swing", On = trigger }, back },
                },
            },
            Transitions = new List<StateTransition> { new() { To = "swing", On = trigger } },
        };
        records.AddRuntime(id, graph);
        return id;
    }

    // ---- the pose --------------------------------------------------------------------------------------

    // The entity's pose, as AnimatorSystem last sampled it (ModelSpace filled). False when it has no
    // animator, or no pose yet (before its first tick, or its model did not load).
    public static bool TryGetPose(World world, Entity entity, [NotNullWhen(true)] out SkeletonPose? pose) =>
        TryGetPose(world, entity, out pose, out _);

    // And whether it was sampled this tick (animation LOD skips some ticks for distant animators).
    public static bool TryGetPose(World world, Entity entity, [NotNullWhen(true)] out SkeletonPose? pose, out bool sampledThisTick)
    {
        pose = null;
        sampledThisTick = false;
        if (entity.IsNull || !world.IsAlive(entity) || !world.TryGet<Animator>(entity, out var a)) return false;
        if (!world.Resources.TryGet<AnimatorPoses>(out var poses) || poses == null) return false;
        if (poses.Find(a.Slot, entity) is not { Pose: { } p } instance) return false;
        pose = p;
        sampledThisTick = instance.SampledTick == world.Tick;
        return true;
    }

    // ---- debugging -------------------------------------------------------------------------------------

    // One animator, in words (anim_debug).
    public static string Describe(World world, Entity entity)
    {
        var sb = new StringBuilder(World.Describe(entity));
        if (!world.IsAlive(entity) || !world.Has<Animator>(entity)) return sb.Append(": no animator").ToString();
        ref var a = ref world.Get<Animator>(entity);
        sb.Append(": graph ").Append(a.Graph).Append(", model ").Append(a.Model.IsEmpty ? "(none)" : a.Model.ToString());
        if (a.Suspended) sb.Append(", suspended");
        if (Compiled(world, a.Graph) is not { } g) return sb.Append(" (no such anim_graph)").ToString();
        AnimatorStepper.Resolve(entity, ref a, g);
        AnimatorPoses.Instance? instance = null;
        if (world.Resources.TryGet<AnimatorPoses>(out var poses) && poses != null) instance = poses.Find(a.Slot, entity);
        sb.Append(instance?.Pose == null ? ", no pose" : $", pose of {instance.Pose.JointCount} joints");
        if (instance != null) sb.Append(CultureInfo.InvariantCulture, $", LOD every {instance.Interval} tick(s)");
        Span<float> weights = stackalloc float[MaxBlendPoints];
        for (int l = 0; l < g.Layers.Length; l++)
        {
            ref var s = ref a.Layers![l];
            var layer = g.Layers[l];
            sb.Append("\n  ").Append(layer.Name).Append(layer.Additive ? " (additive)" : "").Append(": ").Append(s.State ?? "(none)")
              .Append(CultureInfo.InvariantCulture, $" t={s.Time:F2}s phase={s.Phase:F2}");
            if (s.Fading)
                sb.Append(CultureInfo.InvariantCulture, $", fading from {s.From ?? "a pose snapshot"} ({s.Fade:F2}/{s.FadeDuration:F2}s {s.FadeEase})");
            if (s.Index >= 0 && layer.States[s.Index].RootMotion != RootMotionMode.None)
                sb.Append(", root motion ").Append(layer.States[s.Index].RootMotion).Append(layer.States[s.Index].RootMotionY ? " (with height)" : "");
            if (s.Index >= 0 && layer.States[s.Index].IsBlend)
            {
                var state = layer.States[s.Index];
                int count = AnimatorStepper.Weights(state, a.Params!, weights);
                sb.Append(state.Sync != null ? ", blend synced on " + string.Join('/', state.Sync) : ", blend");
                for (int i = 0; i < count; i++)
                    sb.Append(CultureInfo.InvariantCulture, $" {g.ClipNames[state.PointClips[i]]}={weights[i]:F2}");
            }
        }
        if (a.Params!.Length > 0)
        {
            sb.Append("\n  params:");
            foreach (var p in a.Params) sb.Append(CultureInfo.InvariantCulture, $" {p.Name}={p.Value:0.##}");
        }
        return sb.ToString();
    }

    internal static AnimGraphRecord.Compiled? Compiled(World world, RecordId graph)
    {
        if (graph.IsEmpty || !world.Resources.TryGet<RecordStore>(out var records) || records == null) return null;
        return records.TryGet(graph, out AnimGraphRecord record) ? record.Compile() : null;
    }

    // ---- content ---------------------------------------------------------------------------------------

    // Load: initial states that are states, targets that are states, blend spaces over float params,
    // `on` names that are triggers, layers with names of their own.
    private static void Check(AnimGraphRecord record, RecordCheck check)
    {
        var g = record.Compile();
        if (!(record.Fade >= 0f) || !float.IsFinite(record.Fade)) check.Error("Fade", $"{record.Fade} is not a time in seconds");
        foreach (var (name, param) in record.Params)
        {
            if (string.IsNullOrWhiteSpace(name)) check.Error("Params", "a param needs a name");
            if (param == null) continue;
            if (param.Kind == AnimParamKind.Trigger && param.From != AnimParamSource.None)
                check.Error($"Params['{name}'].From", "a trigger is set by inputs and code, never from the body");
            if (param.Kind == AnimParamKind.Bool && param.From is not (AnimParamSource.None or AnimParamSource.Grounded or AnimParamSource.Crouching))
                check.Warn($"Params['{name}'].From", $"{param.From} is a number; a Bool reads it as 1 when it is not 0");
        }

        var layerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Animators.BaseLayer };
        for (int l = 0; l < g.Layers.Length; l++)
        {
            var layer = g.Layers[l];
            string at = l == 0 ? "" : $"Layers[{l - 1}].";
            if (l > 0)
            {
                var source = record.Layers[l - 1];
                if (source == null) { check.Error($"Layers[{l - 1}]", "an empty layer"); continue; }
                if (string.IsNullOrEmpty(source.Name)) check.Error($"{at}Name", "a layer needs a name");
                else if (!layerNames.Add(source.Name)) check.Error($"{at}Name", $"'{source.Name}' is taken (\"base\" is the graph's own states)");
                if (!(source.Weight >= 0f && source.Weight <= 1f)) check.Error($"{at}Weight", $"{source.Weight} is not a weight in [0, 1]");
                if (source.Mask != null)
                    foreach (var joint in source.Mask)
                        if (string.IsNullOrWhiteSpace(joint)) check.Error($"{at}Mask", "an empty joint name");
            }
            CheckLayer(g, layer, at, check);
        }
    }

    private static void CheckLayer(AnimGraphRecord.Compiled g, AnimGraphRecord.Layer layer, string at, RecordCheck check)
    {
        if (layer.Count == 0)
        {
            check.Error($"{at}States", "a layer needs at least one state");
            return;
        }
        if (string.IsNullOrEmpty(layer.InitialName)) check.Error($"{at}Initial", "an \"initial\" state is needed");
        else if (layer.Initial < 0) check.Error($"{at}Initial", $"'{layer.InitialName}' is not one of its states" + Spelling.Suggest(layer.InitialName, layer.Names));

        for (int s = 0; s < layer.Count; s++)
        {
            var state = layer.States[s];
            string path = $"{at}States['{layer.Names[s]}']";
            var source = state.Source;
            if (!string.IsNullOrEmpty(source.Clip) && source.Blend != null)
                check.Error(path, "a state plays a clip or a blend, not both");
            if (source.Fade is { } fade && (!(fade >= 0f) || !float.IsFinite(fade))) check.Error($"{path}.Fade", $"{fade} is not a time in seconds");
            if (!float.IsFinite(source.Speed)) check.Error($"{path}.Speed", $"{source.Speed} is not a rate");
            // Root motion (issue #357) is the base layer's: a layer over it moves joints, never the body.
            if (source.RootMotion != RootMotionMode.None && at.Length > 0)
                check.Warn($"{path}.RootMotion", "root motion is taken from the base layer's states only; this layer's is ignored");
            if (source.RootMotionY && source.RootMotion is not (RootMotionMode.Translation or RootMotionMode.Full))
                check.Warn($"{path}.RootMotionY", "rootMotionY moves the body up and down only with a rootMotion of Translation or Full");
            if (source.Blend is { } blend && string.IsNullOrEmpty(source.Clip)) CheckBlend(g, blend, $"{path}.Blend", check);
            if (source.Blend is { Sync.Count: > 0 } synced)
            {
                // Sync markers (issue #358) line up a looping blend's clips.
                foreach (var name in synced.Sync)
                    if (string.IsNullOrWhiteSpace(name)) check.Error($"{path}.Blend.Sync", "an empty marker name");
                if (!source.Loop) check.Warn($"{path}.Blend.Sync", "sync markers line up looping blends only; this one-shot plays by normalised time");
            }
            CheckTransitions(g, layer, source.Transitions, $"{path}.Transitions", check, s);
        }
        CheckTransitions(g, layer, layer.Any, $"{at}Transitions", check, -1);
    }

    private static void CheckBlend(AnimGraphRecord.Compiled g, AnimBlendSpace blend, string path, RecordCheck check)
    {
        if (blend.Points == null || blend.Points.Count == 0) check.Error($"{path}.Points", "a blend space needs at least one point");
        else if (blend.Points.Count > Animators.MaxBlendPoints) check.Error($"{path}.Points", $"a blend space has at most {Animators.MaxBlendPoints} points");
        else
            for (int i = 0; i < blend.Points.Count; i++)
                if (blend.Points[i] is not { } p || string.IsNullOrEmpty(p.Clip)) check.Error($"{path}.Points[{i}]", "a point needs a \"clip\"");
                else if (!float.IsFinite(p.X) || !float.IsFinite(p.Y)) check.Error($"{path}.Points[{i}]", "a point sits at finite numbers");
        CheckAxis(g, blend.X, $"{path}.X", check, required: true);
        CheckAxis(g, blend.Y, $"{path}.Y", check, required: false);
    }

    private static void CheckAxis(AnimGraphRecord.Compiled g, string name, string path, RecordCheck check, bool required)
    {
        if (string.IsNullOrEmpty(name))
        {
            if (required) check.Error(path, "a blend space needs the param it blends over");
            return;
        }
        int i = g.ParamIndex(name);
        if (i < 0) check.Error(path, $"'{name}' is not one of its params" + Spelling.Suggest(name, g.ParamNames));
        else if (g.ParamKinds[i] != AnimParamKind.Float) check.Error(path, $"'{name}' is a {g.ParamKinds[i]}; a blend space reads a Float");
    }

    private static void CheckTransitions(AnimGraphRecord.Compiled g, AnimGraphRecord.Layer layer, List<StateTransition>? transitions,
                                         string path, RecordCheck check, int state)
    {
        if (transitions == null) return;
        for (int i = 0; i < transitions.Count; i++)
        {
            string at = $"{path}[{i}]";
            if (transitions[i] is not { } t) { check.Error(at, "an empty transition"); continue; }
            if (string.IsNullOrEmpty(t.To)) check.Error(at, "a transition needs a \"to\" (the state it goes to)");
            else if (layer.Target(t) < 0) check.Error($"{at}.To", $"'{t.To}' is not one of the layer's states" + Spelling.Suggest(t.To, layer.Names));
            if (!(t.After >= 0f) || !float.IsFinite(t.After)) check.Error($"{at}.After", $"{t.After} is not a time in seconds");
            if (t.Fade is { } fade && (!(fade >= 0f) || !float.IsFinite(fade))) check.Error($"{at}.Fade", $"{fade} is not a time in seconds");
            if (!string.IsNullOrEmpty(t.On))
            {
                int p = g.ParamIndex(t.On);
                if (p < 0 || g.ParamKinds[p] != AnimParamKind.Trigger)
                    check.Error($"{at}.On", $"'{t.On}' is not one of its trigger params: an anim_graph's `on` is a trigger (AnimTrigger, Animators.SetTrigger)");
            }
            bool always = string.IsNullOrEmpty(t.On) && t.When == null && !(t.After > 0f);
            if (always && state >= 0 && layer.Target(t) == state)
                check.Warn(at, "a transition to its own state with no on, when or after is taken every tick");
            else if (always && state < 0)
                check.Warn(at, "a transition from any state with no on, when or after is taken from every other state at once");
        }
    }
}

// `{ "anim_param": "speed", "min": 0.2 }`, `{ "anim_param": "crouch", "eq": 1 }`: a param of the animator
// on the entity doing the asking (the context's other; its subject when there is none) equals `eq`
// (when given) and is inside [min, max]. No animator or no such param: false.
[Condition("anim_param", Plugin = RegistrationOwners.Core)]
internal sealed class AnimParamCondition : ICondition
{
    [EntryValue, Property(Tooltip = "The animator param to test")]
    public string Name = "";
    [Property(Tooltip = "The value it must equal (leave out for any)")]
    public float? Eq;
    [Property(Tooltip = "The least it may be")]
    public float Min = float.NegativeInfinity;
    [Property(Tooltip = "The most it may be")]
    public float Max = float.PositiveInfinity;

    public bool Test(in ConditionContext context, out string why)
    {
        why = "not now";
        var entity = context.Other.IsNull ? context.Subject : context.Other;
        if (!Animators.TryReadParam(context.World, entity, Name, out float value)) return false;
        return (Eq is not { } eq || value == eq) && value >= Min && value <= Max;
    }
}

// `{ "anim_finished": "base" }`: the layer (a name; "base" or empty: the base) of the animator on the
// entity doing the asking is in a one-shot state whose clip has played to its end (issue #119). What a
// graph uses to leave an attack or a reload when its clip is done, without a number that must follow the
// art: `{ "to": "idle", "when": { "anim_finished": "base" } }`. A looping state never finishes.
[Condition("anim_finished", Plugin = RegistrationOwners.Core)]
internal sealed class AnimFinishedCondition : ICondition
{
    [EntryValue, Property(Tooltip = "The layer to look at; empty or \"base\": the base layer")]
    public string Layer = "";

    public bool Test(in ConditionContext context, out string why)
    {
        why = "its clip is still playing";
        var world = context.World;
        var entity = context.Other.IsNull ? context.Subject : context.Other;
        if (entity.IsNull || !world.IsAlive(entity) || !world.TryGet<Animator>(entity, out var a) || a.Layers is not { } layers) return false;
        if (Animators.Compiled(world, a.Graph) is not { } g) return false;
        int l = g.LayerIndex(Layer);
        if (l < 0 || l >= layers.Length) return false;
        var s = layers[l];
        if (s.Index < 0 || s.Index >= g.Layers[l].Count) return false;
        var state = g.Layers[l].States[s.Index];
        return !state.Loop && state.HasMotion && s.Phase >= 1f;
    }
}
