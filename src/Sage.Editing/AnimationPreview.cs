#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace Sage.Editing;

// What the preview shows: nothing yet, one clip scrubbed by hand, or the graph running.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public enum AnimationPreviewMode
{
    None,
    Clip,
    Graph,
}

// A graph the preview can open, and the skinned model it plays (empty when no prefab's animator names one).
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public readonly record struct AnimationSubject(RecordId Graph, AssetPath Model);

// A joint where the pose has it, in the model's space; Parent is -1 for a root.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public readonly record struct PreviewJoint(string Name, int Parent, Vector3 Position);

// A socket (skeleton_sockets) where the pose has it, in the model's space.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public readonly record struct PreviewSocket(string Name, string Joint, Vector3 Position, Quaternion Rotation);

// A clip event on a clip's timeline: seconds from its start, and the same as a fraction of the clip.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public readonly record struct PreviewMarker(string Name, float Time, float Fraction);

// A param of the graph as the preview has set it.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public readonly record struct PreviewParam(string Name, AnimParamKind Kind, float Value, AnimParamSource From);

// A clip event the running graph raised: the preview's clock when it did, and its name.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public readonly record struct PreviewEvent(float Time, string Name);

// One layer of the running graph: its states, where it is, and the clip that shows most in it with that
// clip's events.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed record PreviewLayer(string Name, IReadOnlyList<string> States, string? State, float Time, float Phase, string? FadingFrom,
                                  string? Clip, float ClipTime, float ClipDuration, IReadOnlyList<PreviewMarker> Markers,
                                  IReadOnlyList<(string Clip, float Weight)> Weights);

// The editor's animation preview, without the drawing (issue #362, phase 4p): a graph or a model's clips,
// outside the edited world, with everything a person tuning them wants to see.
//
// - **Clips.** Any clip of the model, scrubbed to a time by hand (or played on), sampled straight from the
//   clip (PoseSampler) with no graph in the way; its events (anim_events records, frame events) are
//   markers on its timeline.
// - **The graph.** The real animator, in a world of its own (`Animators.CreatePreviewWorld`: the engine's
//   records and models, and an animator system that reads no param from the body), so what the preview
//   shows is what a game would do: transitions, blend spaces, layers, cross-fades, triggers and clip
//   events. Every param is the preview's to set — a float, a bool or a trigger, whatever the graph declares
//   and whatever its `from` — and any state of any layer can be entered by hand. Its clock runs when told
//   (Advance, Step), so a test steps it exactly and the panel at the display's rate. The events it raises
//   are kept, newest last.
// - **Both.** The pose's joints and the model's sockets in the model's space, for drawing the skeleton.
//
// Generic over whatever a graph declares: nothing here names a param, a state or a layer, so the
// features graphs grow (root motion, additive layers, morph-target params) are listed and driven as they
// come. Headless: `Sage.Editor` draws it, `anim_preview*` (AnimationPreviewCommands) drives it from the console.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class AnimationPreview : IDisposable
{
    public const float TickSeconds = 1f / 60f;
    private const int KeptEvents = 32;

    private readonly Engine _engine;
    private World? _world;
    private Entity _entity;
    private SkeletonPose? _clipPose;
    private readonly List<PreviewEvent> _events = new();
    private readonly List<PreviewJoint> _joints = new();
    private readonly List<PreviewSocket> _sockets = new();
    private List<(string Name, SkeletonSocket Socket)> _socketDefs = new();
    private bool _poseDirty = true;

    public AnimationPreview(Engine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public Engine Engine => _engine;
    public AnimationPreviewMode Mode { get; private set; }
    public RecordId Graph { get; private set; }
    public AssetPath Model { get; private set; }
    public AnimationSet? Set { get; private set; }
    // Why the last Open failed (empty when it did not).
    public string Error { get; private set; } = "";
    public bool IsOpen => Set != null || !Graph.IsEmpty;

    // Something was opened (a graph or a model's clips), by a button or a command: the panel comes forward.
    public event Action? Opened;

    // The model's clips, in the file's order.
    public IReadOnlyList<string> Clips => Set == null ? Array.Empty<string>() : Set.Clips.Select(c => c.Name).ToList();

    // ---- what there is to preview ------------------------------------------------------------------

    // Every anim_graph, with the model a prefab's animator (or the skinned_mesh beside it) plays it on.
    public static IReadOnlyList<AnimationSubject> Subjects(RecordStore records)
    {
        ArgumentNullException.ThrowIfNull(records);
        return records.Ids("anim_graph").OrderBy(i => i.ToString(), StringComparer.Ordinal)
                      .Select(id => new AnimationSubject(id, ModelFor(records, id))).ToList();
    }

    // The model the first prefab (by id) whose animator runs `graph` plays it on; empty when none does.
    public static AssetPath ModelFor(RecordStore records, RecordId graph)
    {
        ArgumentNullException.ThrowIfNull(records);
        foreach (var id in records.Ids("prefab").OrderBy(i => i.ToString(), StringComparer.Ordinal))
        {
            if (!records.TryGet(id, out PrefabRecord prefab) || prefab.Parts is not { } parts) continue;
            if (parts["animator"] is not JsonObject animator || Text(animator["graph"]) is not { Length: > 0 } named) continue;
            if (RecordId.Parse(named, id.Namespace) != graph) continue;
            string? model = Text(animator["model"]);
            if (string.IsNullOrEmpty(model) && parts["skinned_mesh"] is JsonObject skinned) model = Text(skinned["mesh"]);
            if (!string.IsNullOrEmpty(model)) return AssetPath.Intern(model);
        }
        return default;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    // ---- opening ----------------------------------------------------------------------------------

    // Runs `graph` on `model` (empty: the model a prefab plays it on), from its initial states with its
    // params at their defaults. False, with Error saying why, when there is no such graph, or a model was
    // named that cannot be read. A graph with no model at all (a sprite's) still runs: its states, params
    // and transitions step, with no pose.
    public bool Open(RecordId graph, AssetPath model = default)
    {
        Error = "";
        if (!_engine.Records.TryGet(graph, out AnimGraphRecord _))
        {
            Error = $"no anim_graph {graph}";
            return false;
        }
        if (model.IsEmpty) model = ModelFor(_engine.Records, graph);
        AnimationSet? set = null;
        if (!model.IsEmpty && (set = _engine.Animations.Load(model)) == null)
        {
            Error = $"{model} has no skeleton or could not be read";
            return false;
        }
        Reset();
        Graph = graph;
        Model = model;
        Set = set;
        LoadSockets();
        _world = Animators.CreatePreviewWorld(_engine);
        _world.Resources.Add<IAnimationEventSink>(new Sink(this));
        _entity = _world.Create(Transform.Identity, "anim preview");
        _world.Add(_entity, new Animator { Graph = graph, Model = model });
        Mode = AnimationPreviewMode.Graph;
        Tick(TickSeconds);   // a first pose: the initial states, a tick in
        Opened?.Invoke();
        return true;
    }

    // Shows `model`'s clips with no graph (Mode Clip, on its first clip).
    public bool OpenModel(AssetPath model)
    {
        Error = "";
        if (model.IsEmpty || _engine.Animations.Load(model) is not { } set)
        {
            Error = model.IsEmpty ? "no model" : $"{model} has no skeleton or could not be read";
            return false;
        }
        Reset();
        Model = model;
        Set = set;
        LoadSockets();
        if (set.Clips.Count > 0) ShowClip(set.Clips[0].Name);
        else Mode = AnimationPreviewMode.Clip;
        Opened?.Invoke();
        return true;
    }

    public void Close()
    {
        Reset();
        Error = "";
    }

    public void Dispose() => Reset();

    private void Reset()
    {
        _world?.Dispose();
        _world = null;
        _entity = default;
        _clipPose?.Dispose();
        _clipPose = null;
        _events.Clear();
        _joints.Clear();
        _sockets.Clear();
        _socketDefs = new();
        Graph = default;
        Model = default;
        Set = null;
        Clip = null;
        ClipTime = 0f;
        Time = 0f;
        Mode = AnimationPreviewMode.None;
        _poseDirty = true;
    }

    private void LoadSockets()
    {
        _socketDefs = new();
        if (Model.IsEmpty || _engine.Records.TypeNameOf(typeof(SkeletonSocketsRecord)) == null) return;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in _engine.Records.Ids("skeleton_sockets").OrderBy(i => i.ToString(), StringComparer.Ordinal))
        {
            if (!_engine.Records.TryGet(id, out SkeletonSocketsRecord record) || record.Model != Model) continue;
            foreach (var (name, socket) in record.Sockets.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
                if (socket != null && seen.Add(name)) _socketDefs.Add((name, socket));
        }
    }

    // ---- clips ------------------------------------------------------------------------------------

    // The clip shown in Clip mode, and the time it is scrubbed to (seconds from its start).
    public string? Clip { get; private set; }
    public float ClipTime { get; private set; }
    public bool ClipLoops { get; set; } = true;
    public float ClipDuration => FindClip(Clip)?.Duration ?? 0f;

    // Shows `clip` at `time` (Mode Clip; the graph, if one is open, waits where it is). False when the
    // model has no such clip.
    public bool ShowClip(string clip, float time = 0f)
    {
        if (FindClip(clip) is null) return false;
        Clip = clip;
        Mode = AnimationPreviewMode.Clip;
        Scrub(time);
        return true;
    }

    // Back to the running graph, when one is open.
    public bool ShowGraph()
    {
        if (_world == null) return false;
        Mode = AnimationPreviewMode.Graph;
        _poseDirty = true;
        return true;
    }

    // Moves the clip shown to `time`: wrapped into the clip when it loops, clamped when it does not.
    public void Scrub(float time)
    {
        if (FindClip(Clip) is not { } clip) return;
        ClipTime = PoseSampler.ClipTime(clip, time, ClipLoops);
        _poseDirty = true;
    }

    // The clip shown's events.
    public IReadOnlyList<PreviewMarker> ClipMarkers => Markers(FindClip(Clip));

    // The events of `clip` (a clip of the model), as timeline markers.
    public IReadOnlyList<PreviewMarker> MarkersOf(string clip) => Markers(FindClip(clip));

    private static IReadOnlyList<PreviewMarker> Markers(AnimationClip? clip)
    {
        if (clip == null || clip.Events.Count == 0) return Array.Empty<PreviewMarker>();
        float duration = clip.Duration;
        return clip.Events.Select(e => new PreviewMarker(e.Name, e.Time, duration > 0f ? Math.Clamp(e.Time / duration, 0f, 1f) : 0f)).ToList();
    }

    private AnimationClip? FindClip(string? name) => name == null || Set == null ? null : Set.FindClip(name);

    // ---- the graph --------------------------------------------------------------------------------

    // Seconds the graph has run since it was opened.
    public float Time { get; private set; }

    // Advance's clock: Playing false holds it, Speed scales it.
    public bool Playing { get; set; } = true;
    public float Speed { get; set; } = 1f;

    // The panel's per-frame call: `seconds` of real time at Speed, while Playing, in ticks of TickSeconds
    // (the remainder carries over). In Clip mode with Playing, the clip plays on instead.
    public void Advance(float seconds)
    {
        if (!Playing || !(seconds > 0f) || !float.IsFinite(seconds)) return;
        float scaled = seconds * Math.Clamp(Speed, 0f, 10f);
        if (Mode == AnimationPreviewMode.Clip)
        {
            Scrub(ClipTime + scaled);
            return;
        }
        _carry += scaled;
        int ticks = 0;
        while (_carry >= TickSeconds && ticks++ < 30)
        {
            _carry -= TickSeconds;
            Tick(TickSeconds);
        }
        if (_carry > TickSeconds) _carry = 0f;   // a long stall is dropped, not caught up
    }

    private float _carry;

    // Runs the graph `seconds` on, in ticks of TickSeconds (whatever Playing says): what a test and
    // `anim_preview_step` call.
    public void Step(float seconds = TickSeconds)
    {
        int ticks = Math.Max(1, (int)MathF.Round(seconds / TickSeconds));
        for (int i = 0; i < ticks; i++) Tick(TickSeconds);
    }

    private void Tick(float dt)
    {
        if (_world == null) return;
        _world.RunFixed(dt);
        Time += dt;
        _poseDirty = true;
    }

    // The graph's params, as the preview has them now.
    public IReadOnlyList<PreviewParam> Params
    {
        get
        {
            if (_world == null || !_engine.Records.TryGet(Graph, out AnimGraphRecord graph)) return Array.Empty<PreviewParam>();
            var list = new List<PreviewParam>();
            foreach (var (name, def) in graph.Params)
            {
                var d = def ?? new AnimParam();
                float value = Animators.GetParam(_world, _entity, name) ?? d.Default;
                list.Add(new PreviewParam(name, d.Kind, value, d.From));
            }
            return list;
        }
    }

    // Sets a float or bool param (a bool reads any value but 0 as 1). A trigger is set whatever the value.
    public bool SetParam(string name, float value)
    {
        if (_world == null) return false;
        var param = Params.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (param.Name == null) return false;
        return param.Kind == AnimParamKind.Trigger ? Animators.SetTrigger(_world, _entity, param.Name) : Animators.SetParam(_world, _entity, param.Name, value);
    }

    // Sets a trigger: the next tick's transitions may take it.
    public bool Trigger(string name) => _world != null && Animators.SetTrigger(_world, _entity, name);

    // Puts `layer` (empty: the base) in `state` at once, cross-fading as a transition into it would.
    public bool PlayState(string state, string? layer = null)
    {
        if (_world == null || !Animators.Play(_world, _entity, state, string.IsNullOrEmpty(layer) ? null : layer)) return false;
        Mode = AnimationPreviewMode.Graph;
        Tick(TickSeconds);
        return true;
    }

    // Each layer of the graph, the base first.
    public IReadOnlyList<PreviewLayer> Layers
    {
        get
        {
            if (_world == null || !_engine.Records.TryGet(Graph, out AnimGraphRecord graph)) return Array.Empty<PreviewLayer>();
            var animator = _world.Get<Animator>(_entity);
            var result = new List<PreviewLayer>();
            var defs = new List<(string Name, Dictionary<string, AnimState> States)> { (Animators.BaseLayer, graph.States) };
            for (int i = 0; i < graph.Layers.Count; i++)
                defs.Add((string.IsNullOrEmpty(graph.Layers[i]?.Name) ? $"layer{i + 1}" : graph.Layers[i].Name, graph.Layers[i]?.States ?? new()));
            for (int l = 0; l < defs.Count; l++)
            {
                var (name, states) = defs[l];
                AnimatorLayer s = animator.Layers is { } layers && l < layers.Length ? layers[l] : default;
                string? layerName = l == 0 ? null : name;
                string? clip = null;
                float clipTime = 0f;
                if (Animators.TryGetClip(_world, _entity, out var found, out float t, layerName))
                {
                    clip = found;
                    clipTime = t;
                }
                var weights = new List<(string, float)>();
                if (s.State != null && states.TryGetValue(s.State, out var state) && state?.Blend is { } blend)
                    foreach (var point in blend.Points.Where(p => p != null && !string.IsNullOrEmpty(p.Clip)))
                        weights.Add((point.Clip, Animators.ClipWeight(_world, _entity, point.Clip, layerName)));
                var c = FindClip(clip);
                result.Add(new PreviewLayer(name, states.Keys.ToList(), s.State, s.Time, s.Phase, s.Fading ? s.From ?? "(a pose)" : null,
                                            clip, clipTime, c?.Duration ?? 0f, Markers(c), weights));
            }
            return result;
        }
    }

    // The clip events the graph raised, oldest first (the last 32).
    public IReadOnlyList<PreviewEvent> Events => _events;

    private sealed class Sink : IAnimationEventSink
    {
        private readonly AnimationPreview _preview;
        public Sink(AnimationPreview preview) => _preview = preview;

        public void Raise(World world, Entity entity, string name)
        {
            var events = _preview._events;
            if (events.Count == KeptEvents) events.RemoveAt(0);
            events.Add(new PreviewEvent(_preview.Time, name));
        }
    }

    // ---- the pose ---------------------------------------------------------------------------------

    // The joints, where the pose shown has them (empty with no model).
    public IReadOnlyList<PreviewJoint> Joints
    {
        get
        {
            Refresh();
            return _joints;
        }
    }

    // The model's sockets, where the pose shown has them.
    public IReadOnlyList<PreviewSocket> Sockets
    {
        get
        {
            Refresh();
            return _sockets;
        }
    }

    private void Refresh()
    {
        if (!_poseDirty) return;
        _poseDirty = false;
        _joints.Clear();
        _sockets.Clear();
        if (CurrentPose() is not { } pose) return;
        var skeleton = pose.Skeleton;
        var model = pose.ModelSpace;
        for (int j = 0; j < skeleton.JointCount; j++)
            _joints.Add(new PreviewJoint(skeleton.NameOf(j), skeleton.Parents[j], model[j].Translation));
        foreach (var (name, socket) in _socketDefs)
        {
            int joint = skeleton.IndexOf(socket.Joint);
            if (joint < 0) continue;
            var m = BoneAttachments.OffsetMatrix(socket.Offset, socket.Angles) * model[joint];
            Matrix4x4.Decompose(m, out _, out var rotation, out var at);
            _sockets.Add(new PreviewSocket(name, socket.Joint, at, rotation));
        }
    }

    private SkeletonPose? CurrentPose()
    {
        if (Mode == AnimationPreviewMode.Clip)
        {
            if (Set == null || FindClip(Clip) is not { } clip) return null;
            _clipPose ??= new SkeletonPose(Set.Skeleton);
            PoseSampler.Sample(clip, ClipTime, ClipLoops, _clipPose);
            PoseSampler.ToModelSpace(Set.Skeleton, _clipPose);
            return _clipPose;
        }
        return _world != null && Animators.TryGetPose(_world, _entity, out var pose) ? pose : null;
    }

    // ---- in words ---------------------------------------------------------------------------------

    // What anim_preview prints: what is open, its clips with their events, the sockets, and (a graph) its
    // params and layers.
    public string Describe()
    {
        if (!IsOpen) return "anim_preview: nothing open";
        var text = new StringBuilder("anim_preview: ");
        text.Append(Graph.IsEmpty ? "clips" : $"graph {Graph}").Append(" on ").Append(Model.IsEmpty ? "(no model)" : Model.ToString());
        if (Set != null) text.Append(CultureInfo.InvariantCulture, $", {Set.Skeleton.JointCount} joints, {Set.Clips.Count} clip(s)");
        text.Append(", ").Append(Mode == AnimationPreviewMode.Clip ? $"showing clip {Clip} at {ClipTime:F2}s" : $"running, t={Time:F2}s");
        if (Set != null)
            foreach (var clip in Set.Clips)
            {
                text.Append(CultureInfo.InvariantCulture, $"\n  clip {clip.Name} ({clip.Duration:F2}s)");
                foreach (var m in Markers(clip)) text.Append(CultureInfo.InvariantCulture, $" | {m.Name}@{m.Time:F2}s");
            }
        foreach (var s in Sockets) text.Append(CultureInfo.InvariantCulture, $"\n  socket {s.Name} on {s.Joint} at ({s.Position.X:F2}, {s.Position.Y:F2}, {s.Position.Z:F2})");
        if (_world != null)
        {
            var ps = Params;
            if (ps.Count > 0) text.Append("\n  params:").Append(string.Concat(ps.Select(p => string.Create(CultureInfo.InvariantCulture, $" {p.Name}={p.Value:0.##}"))));
            text.Append(DescribeLayers());
        }
        return text.ToString();
    }

    // The layers in one line each: state, time, fade, the clip that shows most and the blend's weights.
    public string DescribeLayers()
    {
        var text = new StringBuilder();
        foreach (var l in Layers)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n  layer {l.Name}: {l.State ?? "(none)"} t={l.Time:F2}s");
            if (l.FadingFrom != null) text.Append(", fading from ").Append(l.FadingFrom);
            if (l.Clip != null) text.Append(CultureInfo.InvariantCulture, $", clip {l.Clip} at {l.ClipTime:F2}/{l.ClipDuration:F2}s");
            foreach (var (clip, weight) in l.Weights) text.Append(CultureInfo.InvariantCulture, $" {clip}={weight:F2}");
        }
        return text.ToString();
    }
}
