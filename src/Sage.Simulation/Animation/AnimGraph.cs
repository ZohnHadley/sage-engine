#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Sage.Simulation;

// The animation graph as data (issue #118, docs/design/12 "As built (the animation graph)"): which clip
// or blend of clips a skeleton plays, how it gets from one to the next, and which parts of the body
// each layer moves. A character that idles, walks and runs by its speed, jumps on a trigger, and aims
// with its upper body whatever its legs are doing:
//
//   { "type": "anim_graph", "id": "soldier", "initial": "idle", "fade": 0.2, "ease": "SmoothStep",
//     "params": {
//       "speed":     { "from": "Speed" },                   // metres a second, from the body's velocity
//       "aim_pitch": { "from": "AimPitch" },                // degrees, from PawnIntent
//       "crouch":    { "kind": "Bool" },                    // set through SetAnimParam "crouch 1"
//       "jump":      { "kind": "Trigger" } },               // AnimTrigger "jump"
//     "states": {
//       "idle": { "clip": "idle", "tags": ["grounded"],
//                 "transitions": [ { "to": "move", "when": { "anim_param": "speed", "min": 0.2 } } ] },
//       "move": { "blend": { "x": "speed", "points": [ { "clip": "walk", "x": 1.5 }, { "clip": "run", "x": 4 } ] },
//                 "tags": ["grounded"],
//                 "transitions": [ { "to": "idle", "when": { "anim_param": "speed", "max": 0.1 } } ] },
//       "jump": { "clip": "jump", "loop": false, "fade": 0.1, "transitions": [ { "to": "idle", "after": 0.8 } ] } },
//     "transitions": [ { "to": "jump", "on": "jump" } ],
//     "layers": [
//       { "name": "upper", "mask": ["spine"], "initial": "none",
//         "states": { "none": {},
//                     "aim":  { "blend": { "x": "aim_pitch", "points": [ { "clip": "aim_down", "x": -60 }, { "clip": "aim_up", "x": 60 } ] } } },
//         "transitions": [ { "to": "aim", "on": "raise" }, { "to": "none", "on": "lower" } ] } ] }
//
// **A state** plays a `clip` (by name, from the model the animator names), a `blend` space of clips
// (1D over one float param; 2D when it names a `y` param too), or nothing (a layer's "none": the layer
// lets what is under it show). `loop` (default true), `speed` (playback rate), `tags`, and `fade`/`ease`:
// how long entering it cross-fades, along which curve (the graph's `fade` and `ease` when left out).
//
// **Transitions are 4b's** (`StateTransition`, stepped with `StateMachines.FirstTransition`): `to`, and
// `on` (a trigger param's name), `when` (#89's conditions; `anim_param` reads this animator's params),
// `after` (seconds in the state) and `then` (actions). A state's own are tried first, then the layer's
// top-level ones (from any state, never to the state it is in); the first that matches wins, and a
// layer changes state at most once a tick.
//
// **Layers** are machines of their own over the base (the record's top-level states): each blends its
// pose over what is below it, on the joints its `mask` names and every joint beneath them
// (JointMask.SetBranch; no mask: the whole body), at its `weight`. An `"blend": "Additive"` layer
// (issue #358) adds instead: what its clips do relative to their first frame goes on top of the pose
// below (a flinch, breathing, a lean over whatever the legs are doing).
//
// **Sync markers** (issue #358): a blend's `sync` names clip events (anim_events) that mark matching
// moments in every clip — `["foot_l", "foot_r"]`, the foot plants — and the blend's clips then line up
// at those markers rather than at the same fraction of their length. **Per-transition fades:** a
// transition's own `fade` and `ease` win over the state's.
//
// **Morph targets** (issue #363) are animated like joints: a clip's weight tracks are sampled, blended and
// cross-faded with it, and a layer's `mask` may name morph targets as well as joints (a face layer:
// `"mask": ["blink", "jaw_open"]`; a layer whose mask names none leaves the face to what is below it).
// `morphs` sets targets straight from params every tick, over what the clips gave: lip-sync's
// `"morphs": { "jaw_open": "mouth" }` with SetAnimParam "mouth 0.6".
//
// Compiled once per record load (and again when a reload hands the record new states), so a tick finds
// states, params and clips by index and allocates nothing.
[Record("anim_graph", Plugin = RegistrationOwners.Core)]
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public sealed class AnimGraphRecord
{
    [Property(Tooltip = "The base layer's first state, and where an animator goes when its saved state is not in the graph any more")]
    public string Initial = "";
    [Property(Tooltip = "The base layer's states, by name")]
    public Dictionary<string, AnimState> States = new();
    [Property(Tooltip = "The base layer's transitions from any state, tried after the state's own; never to the state it is in")]
    public List<StateTransition> Transitions = new();
    [Property(Tooltip = "The parameters transitions and blend spaces read: floats, bools and triggers, by name")]
    public Dictionary<string, AnimParam> Params = new();
    [Property(Tooltip = "Layers over the base, in order: each blends over what is below it on the joints its mask names")]
    public List<AnimLayer> Layers = new();
    [Property(Min = 0, Unit = "s", Tooltip = "How long entering a state cross-fades, when the state does not say")]
    public float Fade = 0.2f;
    [Property(Tooltip = "The curve a cross-fade follows, when the state does not say")]
    public Ease Ease = Ease.SmoothStep;
    [Property(Tooltip = "The joint whose motion a state's rootMotion takes out of the pose and gives the body; empty: the skeleton's first joint")]
    public string RootJoint = "";
    [Property(Tooltip = "Morph targets set from a Float or Bool param every tick, over what the clips give: { \"jaw_open\": \"mouth\" } (lip-sync)")]
    public Dictionary<string, string> Morphs = new();

    private Compiled? _compiled;

    internal Compiled Compile()
    {
        var c = _compiled;
        if (c != null && c.Matches(this)) return c;
        return _compiled = new Compiled(this);
    }

    // The graph as the tick reads it.
    internal sealed class Compiled
    {
        private static int _versions;

        private readonly Dictionary<string, AnimState> _states;
        private readonly List<StateTransition> _any;
        private readonly Dictionary<string, AnimParam> _params;
        private readonly List<AnimLayer> _layers;
        private readonly string _initial;
        private readonly float _fade;
        private readonly Ease _ease;
        private readonly string _rootJoint;
        private readonly Dictionary<string, string> _morphs;

        public readonly int Version = Interlocked.Increment(ref _versions);   // never 0: an animator's unset one

        public readonly Layer[] Layers;
        public readonly string[] ParamNames;
        public readonly AnimParamKind[] ParamKinds;
        public readonly AnimParamSource[] ParamSources;
        public readonly float[] ParamDefaults;
        public readonly bool HasSources;
        public readonly int AimPitchParam, AimYawParam;           // -1: the graph has none (#120's AimIk)
        public readonly string[] ClipNames;
        public readonly bool HasRootMotion;                       // some state takes root motion (#357)
        public readonly string[] MorphTargets;                    // `morphs`: each target, and the param it reads (-1: none)
        public readonly int[] MorphParams;
        private readonly Dictionary<string, int> _paramIndex = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _clipIndex = new(StringComparer.Ordinal);

        public Compiled(AnimGraphRecord record)
        {
            _states = record.States ??= new Dictionary<string, AnimState>();
            _any = record.Transitions ??= new List<StateTransition>();
            _params = record.Params ??= new Dictionary<string, AnimParam>();
            _layers = record.Layers ??= new List<AnimLayer>();
            _initial = record.Initial ?? "";
            _fade = record.Fade;
            _ease = record.Ease;
            _rootJoint = record.RootJoint ?? "";
            _morphs = record.Morphs ??= new Dictionary<string, string>();

            ParamNames = new string[_params.Count];
            ParamKinds = new AnimParamKind[_params.Count];
            ParamSources = new AnimParamSource[_params.Count];
            ParamDefaults = new float[_params.Count];
            int p = 0;
            foreach (var (name, param) in _params)
            {
                var def = param ?? new AnimParam();
                ParamNames[p] = name;
                ParamKinds[p] = def.Kind;
                ParamSources[p] = def.Kind == AnimParamKind.Trigger ? AnimParamSource.None : def.From;
                ParamDefaults[p] = def.Kind == AnimParamKind.Trigger ? 0f : def.Kind == AnimParamKind.Bool ? (def.Default != 0f ? 1f : 0f) : def.Default;
                HasSources |= ParamSources[p] != AnimParamSource.None;
                _paramIndex.TryAdd(name, p);
                p++;
            }

            AimPitchParam = ParamIndex(Animators.AimPitchParam);
            AimYawParam = ParamIndex(Animators.AimYawParam);

            var clips = new List<string>();
            Layers = new Layer[1 + _layers.Count];
            Layers[0] = new Layer(this, Animators.BaseLayer, null, 1f, _initial, _states, _any, clips, AnimLayerBlend.Override);
            for (int i = 0; i < _layers.Count; i++)
            {
                var l = _layers[i] ?? new AnimLayer();
                Layers[i + 1] = new Layer(this, string.IsNullOrEmpty(l.Name) ? $"layer{i + 1}" : l.Name, l.Mask, l.Weight,
                                          l.Initial ?? "", l.States ?? new(), l.Transitions ?? new(), clips, l.Blend);
            }
            ClipNames = clips.ToArray();
            foreach (var state in Layers[0].States) HasRootMotion |= state.RootMotion != RootMotionMode.None;

            MorphTargets = new string[_morphs.Count];
            MorphParams = new int[_morphs.Count];
            int m = 0;
            foreach (var (target, param) in _morphs)
            {
                MorphTargets[m] = target ?? "";
                MorphParams[m] = ParamIndex(param);
                m++;
            }
        }

        public bool Matches(AnimGraphRecord record) =>
            ReferenceEquals(_states, record.States) && ReferenceEquals(_any, record.Transitions) && ReferenceEquals(_params, record.Params)
            && ReferenceEquals(_layers, record.Layers) && ReferenceEquals(_morphs, record.Morphs) && _initial == record.Initial && _fade == record.Fade && _ease == record.Ease
            && _rootJoint == (record.RootJoint ?? "");

        public float DefaultFade => _fade;
        public Ease DefaultEase => _ease;
        public string RootJoint => _rootJoint;

        public int ParamIndex(string? name) => name != null && _paramIndex.TryGetValue(name, out int i) ? i : -1;

        internal int ClipIndex(string name, List<string> clips)
        {
            if (_clipIndex.TryGetValue(name, out int i)) return i;
            i = clips.Count;
            clips.Add(name);
            _clipIndex.Add(name, i);
            return i;
        }

        public int LayerIndex(string? name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            for (int i = 0; i < Layers.Length; i++)
                if (string.Equals(Layers[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
    }

    // One layer's machine: states by index, transitions' targets resolved.
    internal sealed class Layer
    {
        public readonly string Name;
        public readonly List<string>? Mask;
        public readonly float Weight;
        public readonly bool Additive;                            // issue #358: adds its clips' motion over what is below
        public readonly string InitialName;
        public readonly int Initial;                              // -1: no such state (a load error)
        public readonly string[] Names;
        public readonly State[] States;
        public readonly List<StateTransition> Any;
        private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<StateTransition, int> _targets = new(ReferenceEqualityComparer.Instance);

        public Layer(Compiled graph, string name, List<string>? mask, float weight, string initial,
                     Dictionary<string, AnimState> states, List<StateTransition> any, List<string> clips, AnimLayerBlend blend)
        {
            Name = name;
            Mask = mask;
            Additive = blend == AnimLayerBlend.Additive;
            Weight = float.IsFinite(weight) ? Math.Clamp(weight, 0f, 1f) : 0f;
            InitialName = initial;
            Any = any;
            Names = new string[states.Count];
            States = new State[states.Count];
            int i = 0;
            foreach (var (stateName, state) in states)
            {
                Names[i] = stateName;
                States[i] = new State(graph, state ?? new AnimState(), clips);
                _index.TryAdd(stateName, i);
                i++;
            }
            Initial = IndexOf(initial);
            foreach (var state in States) Add(state.Source.Transitions);
            Add(Any);
        }

        private void Add(List<StateTransition>? transitions)
        {
            if (transitions == null) return;
            foreach (var t in transitions)
                if (t != null) _targets[t] = IndexOf(t.To);
        }

        public int Count => States.Length;
        public int IndexOf(string? name) => name != null && _index.TryGetValue(name, out int i) ? i : -1;
        public int Target(StateTransition t) => _targets.TryGetValue(t, out int i) ? i : -1;
    }

    // One state: a clip, a blend space or nothing, with its playback settings resolved.
    internal sealed class State
    {
        public readonly AnimState Source;
        public readonly int Clip = -1;                             // an index into Compiled.ClipNames
        public readonly bool Loop;
        public readonly float Speed;
        public readonly float Fade;
        public readonly Ease Ease;
        public readonly int ParamX = -1, ParamY = -1;             // a blend space's params
        public readonly int[] PointClips = Array.Empty<int>();    // 1D: sorted by X
        public readonly float[] PointX = Array.Empty<float>();
        public readonly float[] PointY = Array.Empty<float>();
        public readonly List<string>? Tags;
        public readonly RootMotionMode RootMotion;
        public readonly bool RootMotionY;
        public readonly string[]? Sync;                           // a looping blend's sync marker names (issue #358); null: by normalised time

        public State(Compiled graph, AnimState source, List<string> clips)
        {
            Source = source;
            Loop = source.Loop;
            Speed = float.IsFinite(source.Speed) ? source.Speed : 1f;
            Fade = source.Fade is { } fade && fade >= 0f && float.IsFinite(fade) ? fade : Math.Max(0f, graph.DefaultFade);
            Ease = source.Ease ?? graph.DefaultEase;
            Tags = source.Tags;
            RootMotion = Enum.IsDefined(source.RootMotion) ? source.RootMotion : RootMotionMode.None;
            RootMotionY = source.RootMotionY && RootMotion is RootMotionMode.Translation or RootMotionMode.Full;
            if (!string.IsNullOrEmpty(source.Clip))
            {
                Clip = graph.ClipIndex(source.Clip, clips);
                return;
            }
            if (source.Blend is not { } blend || blend.Points == null || blend.Points.Count == 0) return;

            ParamX = graph.ParamIndex(blend.X);
            ParamY = string.IsNullOrEmpty(blend.Y) ? -1 : graph.ParamIndex(blend.Y);
            var points = new List<AnimBlendPoint>(blend.Points.Count);
            foreach (var point in blend.Points)
                if (point != null && !string.IsNullOrEmpty(point.Clip) && points.Count < Animators.MaxBlendPoints) points.Add(point);
            if (string.IsNullOrEmpty(blend.Y)) points.Sort(static (a, b) => a.X.CompareTo(b.X));
            if (Loop && blend.Sync is { Count: > 0 } sync)
            {
                var names = new List<string>(sync.Count);
                foreach (var name in sync)
                    if (!string.IsNullOrEmpty(name) && !names.Contains(name)) names.Add(name);
                if (names.Count > 0) Sync = names.ToArray();
            }
            PointClips = new int[points.Count];
            PointX = new float[points.Count];
            PointY = new float[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                PointClips[i] = graph.ClipIndex(points[i].Clip, clips);
                PointX[i] = points[i].X;
                PointY[i] = points[i].Y;
            }
        }

        public bool IsBlend => PointClips.Length > 0;
        public bool TwoD => ParamY >= 0 || (Source.Blend != null && !string.IsNullOrEmpty(Source.Blend.Y));
        public bool HasMotion => Clip >= 0 || PointClips.Length > 0;

        public bool HasTag(string tag)
        {
            var tags = Tags;
            if (tags == null) return false;
            for (int i = 0; i < tags.Count; i++)
                if (string.Equals(tags[i], tag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}

// One state of an animation graph: a clip, a blend space, or nothing.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public sealed class AnimState
{
    [Property(Tooltip = "The clip it plays, by name, from the animator's model; leave out for a blend or for nothing")]
    public string Clip = "";
    [Property(Tooltip = "A blend space instead of one clip: 1D over x, 2D over x and y")]
    public AnimBlendSpace? Blend;
    [Property(Tooltip = "Whether the clip loops; a one-shot holds its last frame")]
    public bool Loop = true;
    [Property(Tooltip = "Playback rate: 1 is as authored, 2 twice as fast")]
    public float Speed = 1f;
    [Property(Min = 0, Unit = "s", Tooltip = "How long entering it cross-fades; left out, the graph's fade")]
    public float? Fade;
    [Property(Tooltip = "The curve entering it cross-fades along; left out, the graph's ease")]
    public Ease? Ease;
    [Property(Tooltip = "Free words for whatever reads the animator (Animators.HasTag)")]
    public List<string> Tags = new();
    [Property(Tooltip = "Root motion (base layer only): None, Rotation (turns about up), Translation (moves across the ground) or Full; what it takes out of the root joint moves the body instead, so feet do not slide")]
    public RootMotionMode RootMotion = RootMotionMode.None;
    [Property(Tooltip = "With Translation or Full: the root's height moves the body too, instead of gravity (a climb, a vault); off, only across the ground")]
    public bool RootMotionY;
    [Property(Tooltip = "Where it can go, tried in order: the first that matches wins")]
    public List<StateTransition> Transitions = new();
}

// A blend space: clips placed along one float param (1D) or on a plane of two (2D).
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public sealed class AnimBlendSpace
{
    [Property(Tooltip = "The float param along the first axis")]
    public string X = "";
    [Property(Tooltip = "The float param along the second axis; leave out for a 1D blend")]
    public string Y = "";
    [Property(Tooltip = "The clips and where each sits")]
    public List<AnimBlendPoint> Points = new();
    [Property(Tooltip = "Sync markers: clip event names (anim_events) at matching moments in every clip, such as the foot plants [\"foot_l\", \"foot_r\"]; the clips line up at them (counted from each clip's first of the first name) instead of by fraction of their length. Looping blends only")]
    public List<string> Sync = new();
}

[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public sealed class AnimBlendPoint
{
    [Property(Tooltip = "The clip, by name")]
    public string Clip = "";
    [Property(Tooltip = "Where it sits on the x axis")]
    public float X;
    [Property(Tooltip = "Where it sits on the y axis (2D only)")]
    public float Y;
}

// A layer over the base: a machine of its own, blended on the joints its mask names.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public sealed class AnimLayer
{
    [Property(Tooltip = "Its name, unique in the graph (not \"base\"): saves and anim_debug name it")]
    public string Name = "";
    [Property(Tooltip = "Joints whose branches it moves (each joint and everything beneath it); empty: the whole body")]
    public List<string> Mask = new();
    [Property(Min = 0, Max = 1, Tooltip = "How much of its pose shows over what is below it")]
    public float Weight = 1f;
    [Property(Tooltip = "Override (its pose replaces what is below, on its mask) or Additive (what its clips do relative to their first frame is added on top: a flinch, breathing, a lean)")]
    public AnimLayerBlend Blend = AnimLayerBlend.Override;
    [Property(Tooltip = "Its first state")]
    public string Initial = "";
    [Property(Tooltip = "Its states, by name; one with no clip and no blend lets what is below show")]
    public Dictionary<string, AnimState> States = new();
    [Property(Tooltip = "Its transitions from any state, tried after the state's own")]
    public List<StateTransition> Transitions = new();
}

// How a layer goes over what is below it (issue #358).
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public enum AnimLayerBlend
{
    // Its pose replaces what is below, on its mask, at its weight.
    Override,
    // Its clips' motion relative to their first frame is added to what is below, on its mask, scaled by its weight.
    Additive,
}

[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public enum AnimParamKind
{
    // A number: a blend space's axis, a `when`'s anim_param.
    Float,
    // 0 or 1.
    Bool,
    // Set, then used up by the first tick after: a transition's `on` names it.
    Trigger,
}

// Where the animator system reads a param from every tick, before stepping (None: only what code and
// SetAnimParam set).
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public enum AnimParamSource
{
    None,
    // Metres a second across the ground: the character controller's velocity, else how far the entity moved.
    Speed,
    // Metres a second up (negative: falling).
    VerticalSpeed,
    // PawnIntent.Move: -1..1 sideways (right positive) and forwards.
    MoveX,
    MoveY,
    // PawnIntent.Pitch in degrees (up positive).
    AimPitch,
    // The character controller is on the ground / crouched (bools).
    Grounded,
    Crouching,
    // The fighter's AttackStance (issue #359): its direction as a number (0 none, 1 overhead, 2 right,
    // 3 thrust, 4 left), and whether it holds its guard up (a bool).
    AttackDirection,
    Blocking,
}

[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public sealed class AnimParam
{
    [Property(Tooltip = "Float, Bool or Trigger")]
    public AnimParamKind Kind = AnimParamKind.Float;
    [Property(Tooltip = "Its value until something sets it")]
    public float Default;
    [Property(Tooltip = "Set every tick from the body: Speed, VerticalSpeed, MoveX, MoveY, AimPitch, Grounded, Crouching, AttackDirection, Blocking")]
    public AnimParamSource From = AnimParamSource.None;
}
