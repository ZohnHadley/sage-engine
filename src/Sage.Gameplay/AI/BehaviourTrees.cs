#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sage.Gameplay;

// Behaviour trees (docs/design/16 §3.4 "Later", REDESIGN §4.3 stage 4, issue #387): a brain beside the
// schedules, from data. A creature whose `ai_state` names a `tree` runs it instead of choosing schedules;
// the same staggered think perceives for it (conditions, hearing, its routine, a game's own conditions),
// and the tree is ticked every tick, as a schedule's task is.
//
//   { "type": "behaviour_tree", "id": "guard",
//     "root": { "selector": [
//       { "when": ["EnemyInMeleeRange", "CanMelee"], "sequence": ["FaceTarget", { "task": "MeleeAttack", "giveUpAfter": 1 }] },
//       { "when": ["SeeEnemy"], "task": "MoveToTarget", "distance": 1.6 },
//       { "when": ["Suspicious"], "sequence": ["FaceNoise", "MoveToNoise", { "task": "Wait", "seconds": 1 }, "ForgetNoise"] },
//       { "sequence": [{ "set": "bored", "to": 1 }, { "task": "Wait", "seconds": 2 }] } ] } }
//
// **Nodes.** A node is one of:
//   - a *task* — the schedules' own vocabulary, `"FaceTarget"` or `{ "task": "Wait", "seconds": 1 }`;
//   - a *condition* — `{ "when": [...], "unless": [...] }` alone: succeeds when they hold, else fails;
//   - the blackboard's `{ "set": "key", "to": 1 }` (succeeds) and `{ "check": "key", "min": 1, "max": 3 }`
//     (succeeds when the number is in range; with neither bound, when it is not 0);
//   - a composite: `sequence` (each in turn, failing at the first that fails), `selector` (the first that
//     does not fail), `utility` (the child whose `score` and `considerations` add up highest, Utility.cs);
//   - a decorator over one node: `invert`, `succeed` (it never fails), `repeat` (`times`; 0 is for ever,
//     stopping at a failure), `cooldown` (after it finishes it fails for `seconds`).
// Any node may carry `name` (for the editor) and `when`/`unless`, its guard.
//
// **Interrupts are guards.** A guard is checked every tick on the way down: a running node whose guard
// stops holding is aborted and fails, so its parent moves on. And a selector's (or utility's) child with
// a guard that *becomes* true preempts a later sibling that is running — the creature drops its patrol
// the think it sees you. A utility node also changes its mind at a think when another child scores
// higher than the one running.
//
// **One task a tick.** As a schedule moves to its next task on the next tick, a tree runs at most one
// task a tick: a sequence that finishes one task starts the next on the following tick. Conditions,
// `set` and `check` take no time and run as they are reached.
//
// The tree's place in itself is not saved (a load starts it from the root, as a load starts a schedule's
// creature thinking again); its blackboard is (AIState.Blackboard).

[Experimental("SAGE0120", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // behaviour trees (#387): may change before 1.0
public enum BehaviourTreeNodeKind
{
    Task,
    Condition,
    Set,
    Check,
    Sequence,
    Selector,
    Utility,
    Invert,
    Succeed,
    Repeat,
    Cooldown,
}

[Record("behaviour_tree", Plugin = "sage.gameplay.ai")]
[Experimental("SAGE0120", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // behaviour trees (#387): may change before 1.0
public sealed class BehaviourTreeRecord
{
    [Property(Tooltip = "The node it ticks: a sequence, selector, utility, decorator, task or condition")]
    public BehaviourTreeNode? Root;

    private BehaviourTreeNode? _compiledFor;
    private CompiledBehaviourTree? _compiled;

    // Its nodes, flattened depth first, made once (again only when a reload replaced the root).
    internal CompiledBehaviourTree Compiled
    {
        get
        {
            if (_compiled == null || !ReferenceEquals(_compiledFor, Root))
            {
                _compiled = new CompiledBehaviourTree(Root);
                _compiledFor = Root;
            }
            return _compiled;
        }
    }

    // How many nodes it has, counted depth first from the root at 0 (the numbering `AIState.TaskIndex` uses).
    public int NodeCount => Compiled.Count;
}

// One node (see the top of the file). Read from JSON by BehaviourTreeNodeJsonConverter.
[JsonConverter(typeof(BehaviourTreeNodeJsonConverter))]
[Experimental("SAGE0120", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // behaviour trees (#387): may change before 1.0
public sealed class BehaviourTreeNode
{
    internal List<BehaviourTreeNode> ChildList = new();
    internal List<string> When = new();
    internal List<string> Unless = new();
    internal string Key = "";                 // set's and check's
    internal float To = 1f;                   // set's
    internal float? Min, Max;                 // check's
    internal int Times;                       // repeat's; 0 is for ever
    internal float Seconds;                   // cooldown's
    internal float Score;                     // what it scores under a utility node
    internal List<AIConsideration> Considerations = new();

    public BehaviourTreeNodeKind Kind { get; internal set; }
    // A name for the editor; "" when it has none.
    public string Name { get; internal set; } = "";
    // A task node's task and argument.
    public AITaskStep Task { get; internal set; }
    public IReadOnlyList<BehaviourTreeNode> Children => ChildList;

    internal bool IsComposite => Kind is BehaviourTreeNodeKind.Sequence or BehaviourTreeNodeKind.Selector or BehaviourTreeNodeKind.Utility;
    internal bool IsDecorator => Kind is BehaviourTreeNodeKind.Invert or BehaviourTreeNodeKind.Succeed or BehaviourTreeNodeKind.Repeat or BehaviourTreeNodeKind.Cooldown;
    internal bool HasGuard => When.Count > 0 || Unless.Count > 0;

    // The JSON key that makes it what it is.
    internal static string KeyOf(BehaviourTreeNodeKind kind) => kind switch
    {
        BehaviourTreeNodeKind.Sequence => "sequence",
        BehaviourTreeNodeKind.Selector => "selector",
        BehaviourTreeNodeKind.Utility => "utility",
        BehaviourTreeNodeKind.Invert => "invert",
        BehaviourTreeNodeKind.Succeed => "succeed",
        BehaviourTreeNodeKind.Repeat => "repeat",
        BehaviourTreeNodeKind.Cooldown => "cooldown",
        BehaviourTreeNodeKind.Set => "set",
        BehaviourTreeNodeKind.Check => "check",
        BehaviourTreeNodeKind.Task => "task",
        _ => "when",
    };
}

// `"FaceTarget"`, `{ "task": "Wait", "seconds": 1 }`, `{ "selector": [ … ] }`, `{ "invert": { … } }`, …
[SchemaShape("""
    {
      "description": "A behaviour tree node: a task (\"FaceTarget\", or { \"task\": \"Wait\", \"seconds\": 1 }), a condition ({ \"when\": [...], \"unless\": [...] }), { \"set\": \"key\", \"to\": 1 }, { \"check\": \"key\", \"min\": 1 }, a composite ({ \"sequence\" | \"selector\" | \"utility\": [nodes] }) or a decorator ({ \"invert\" | \"succeed\" | \"repeat\" | \"cooldown\": node }). Any node may have a name, a when/unless guard, and (under a utility node) a score and considerations.",
      "anyOf": [
        { "type": "string", "pattern": "^[^:]+$" },
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "A name for the editor." },
            "when": { "type": "array", "items": { "type": "string" }, "description": "AI conditions that must all hold for it to run (its guard)." },
            "unless": { "type": "array", "items": { "type": "string" }, "description": "AI conditions none of which may hold for it to run." },
            "score": { "type": "number", "description": "Under a utility node: its score before its considerations." },
            "considerations": { "type": "array", "items": { "type": "object" }, "description": "Under a utility node: what adds to its score." },
            "task": { "type": "string", "minLength": 1, "description": "A task, such as \"Wait\"; its one argument is a number beside it." },
            "set": { "type": "string", "minLength": 1, "description": "A blackboard key to set to \"to\" (default 1)." },
            "to": { "type": "number" },
            "check": { "type": "string", "minLength": 1, "description": "A blackboard key: succeeds when it is within min..max (neither: not 0)." },
            "min": { "type": "number" },
            "max": { "type": "number" },
            "sequence": { "type": "array", "minItems": 1, "description": "Each in turn; fails at the first that fails." },
            "selector": { "type": "array", "minItems": 1, "description": "The first that does not fail." },
            "utility": { "type": "array", "minItems": 1, "description": "The child whose score is highest." },
            "invert": { "description": "Its node, with success and failure swapped." },
            "succeed": { "description": "Its node, which never fails." },
            "repeat": { "description": "Its node again and again (\"times\"; 0 is for ever), until it fails." },
            "times": { "type": "integer", "minimum": 0 },
            "cooldown": { "description": "Its node, which fails for \"seconds\" after it finishes." },
            "seconds": { "type": "number", "minimum": 0 }
          },
          "additionalProperties": { "type": "number" }
        }
      ]
    }
    """)]
internal sealed class BehaviourTreeNodeJsonConverter : JsonConverter<BehaviourTreeNode>
{
    private static readonly AITaskStepJsonConverter Steps = new();

    public override BehaviourTreeNode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new BehaviourTreeNode { Kind = BehaviourTreeNodeKind.Task, Task = Steps.Read(ref reader, typeof(AITaskStep), options) };
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("a behaviour tree node is a task's name (\"FaceTarget\") or an object: { \"sequence\": [...] }, { \"task\": \"Wait\", \"seconds\": 1 }, { \"when\": [\"SeeEnemy\"] }, …");

        var node = new BehaviourTreeNode();
        string? kindKey = null, task = null;
        List<(string Key, float Value)>? numbers = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            string key = reader.GetString()!;
            reader.Read();
            switch (key.ToLowerInvariant())
            {
                case "name": node.Name = Text(ref reader, key); break;
                case "when": node.When = Names(ref reader, key); break;
                case "unless": node.Unless = Names(ref reader, key); break;
                case "score": node.Score = Number(ref reader, key); break;
                case "considerations":
                    node.Considerations = JsonSerializer.Deserialize<List<AIConsideration>>(ref reader, options) ?? new();
                    break;
                case "sequence": Kind(node, ref kindKey, key, BehaviourTreeNodeKind.Sequence); node.ChildList = ReadChildren(ref reader, key, options); break;
                case "selector": Kind(node, ref kindKey, key, BehaviourTreeNodeKind.Selector); node.ChildList = ReadChildren(ref reader, key, options); break;
                case "utility": Kind(node, ref kindKey, key, BehaviourTreeNodeKind.Utility); node.ChildList = ReadChildren(ref reader, key, options); break;
                case "invert": Kind(node, ref kindKey, key, BehaviourTreeNodeKind.Invert); node.ChildList = new() { Read(ref reader, typeof(BehaviourTreeNode), options) }; break;
                case "succeed": Kind(node, ref kindKey, key, BehaviourTreeNodeKind.Succeed); node.ChildList = new() { Read(ref reader, typeof(BehaviourTreeNode), options) }; break;
                case "repeat": Kind(node, ref kindKey, key, BehaviourTreeNodeKind.Repeat); node.ChildList = new() { Read(ref reader, typeof(BehaviourTreeNode), options) }; break;
                case "cooldown": Kind(node, ref kindKey, key, BehaviourTreeNodeKind.Cooldown); node.ChildList = new() { Read(ref reader, typeof(BehaviourTreeNode), options) }; break;
                case "task": Kind(node, ref kindKey, key, BehaviourTreeNodeKind.Task); task = Text(ref reader, key).Trim(); break;
                case "set": Kind(node, ref kindKey, key, BehaviourTreeNodeKind.Set); node.Key = Text(ref reader, key).Trim(); break;
                case "check": Kind(node, ref kindKey, key, BehaviourTreeNodeKind.Check); node.Key = Text(ref reader, key).Trim(); break;
                default:
                    if (reader.TokenType != JsonTokenType.Number)
                        throw new JsonException($"'{key}' is not part of a behaviour tree node (a node is one of task, sequence, selector, utility, invert, succeed, repeat, cooldown, set, check, or a when/unless condition)");
                    (numbers ??= new()).Add((key, reader.GetSingle()));
                    break;
            }
        }

        if (kindKey == null)
        {
            if (!node.HasGuard)
                throw new JsonException("a behaviour tree node needs one of task, sequence, selector, utility, invert, succeed, repeat, cooldown, set, check, or when/unless");
            node.Kind = BehaviourTreeNodeKind.Condition;
        }
        if (node.IsComposite && node.ChildList.Count == 0) throw new JsonException($"a '{kindKey}' needs at least one node");
        if ((node.Kind is BehaviourTreeNodeKind.Set or BehaviourTreeNodeKind.Check) && node.Key.Length == 0)
            throw new JsonException($"'{kindKey}' names a blackboard key, such as \"alerted\"");
        if (node.Kind == BehaviourTreeNodeKind.Task && string.IsNullOrEmpty(task)) throw new JsonException("\"task\" is the name of a task, such as \"Wait\"");

        // The numbers beside the kind: a task's one argument, or the kind's own settings.
        if (numbers != null)
            foreach (var (key, value) in numbers)
            {
                string name = key.ToLowerInvariant();
                switch (node.Kind)
                {
                    case BehaviourTreeNodeKind.Task:
                        if (node.Task.Argument != null) throw new JsonException($"a task takes one argument; this one has '{node.Task.Argument}' and '{key}'");
                        node.Task = new AITaskStep(task!, key, value);
                        continue;
                    case BehaviourTreeNodeKind.Repeat when name == "times":
                        if (value < 0 || value != MathF.Floor(value)) throw new JsonException("'times' is a whole number of times; 0 repeats for ever");
                        node.Times = (int)value;
                        continue;
                    case BehaviourTreeNodeKind.Cooldown when name == "seconds":
                        if (value < 0) throw new JsonException("'seconds' cannot be negative");
                        node.Seconds = value;
                        continue;
                    case BehaviourTreeNodeKind.Set when name == "to": node.To = value; continue;
                    case BehaviourTreeNodeKind.Check when name == "min": node.Min = value; continue;
                    case BehaviourTreeNodeKind.Check when name == "max": node.Max = value; continue;
                }
                throw new JsonException($"a '{kindKey ?? "when"}' node has no '{key}'");
            }
        if (node.Kind == BehaviourTreeNodeKind.Task && node.Task.Task == null) node.Task = new AITaskStep(task!);
        return node;
    }

    private static void Kind(BehaviourTreeNode node, ref string? kindKey, string key, BehaviourTreeNodeKind kind)
    {
        if (kindKey != null) throw new JsonException($"a behaviour tree node is one thing; this one is both '{kindKey}' and '{key}'");
        kindKey = key;
        node.Kind = kind;
    }

    private List<BehaviourTreeNode> ReadChildren(ref Utf8JsonReader reader, string key, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException($"'{key}' is a list of nodes");
        var children = new List<BehaviourTreeNode>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            children.Add(Read(ref reader, typeof(BehaviourTreeNode), options));
        return children;
    }

    private static string Text(ref Utf8JsonReader reader, string key) =>
        reader.TokenType == JsonTokenType.String ? reader.GetString()! : throw new JsonException($"'{key}' must be a string");

    private static float Number(ref Utf8JsonReader reader, string key) =>
        reader.TokenType == JsonTokenType.Number ? reader.GetSingle() : throw new JsonException($"'{key}' must be a number");

    private static List<string> Names(ref Utf8JsonReader reader, string key)
    {
        if (reader.TokenType == JsonTokenType.String) return new List<string> { reader.GetString()! };
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException($"'{key}' is a list of AI condition names");
        var names = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            names.Add(reader.TokenType == JsonTokenType.String ? reader.GetString()! : throw new JsonException($"'{key}' is a list of AI condition names"));
        return names;
    }

    public override void Write(Utf8JsonWriter writer, BehaviourTreeNode value, JsonSerializerOptions options)
    {
        bool plain = value.Name.Length == 0 && !value.HasGuard && value.Score == 0f && value.Considerations.Count == 0;
        if (value.Kind == BehaviourTreeNodeKind.Task && plain && value.Task.Argument == null)
        {
            writer.WriteStringValue(value.Task.Task);
            return;
        }
        writer.WriteStartObject();
        if (value.Name.Length > 0) writer.WriteString("name", value.Name);
        if (value.When.Count > 0) WriteNames(writer, "when", value.When);
        if (value.Unless.Count > 0) WriteNames(writer, "unless", value.Unless);
        if (value.Score != 0f) writer.WriteNumber("score", value.Score);
        if (value.Considerations.Count > 0)
        {
            writer.WritePropertyName("considerations");
            JsonSerializer.Serialize(writer, value.Considerations, options);
        }
        string key = BehaviourTreeNode.KeyOf(value.Kind);
        switch (value.Kind)
        {
            case BehaviourTreeNodeKind.Task:
                writer.WriteString(key, value.Task.Task);
                if (value.Task.Argument != null) writer.WriteNumber(value.Task.Argument, value.Task.Value);
                break;
            case BehaviourTreeNodeKind.Set:
                writer.WriteString(key, value.Key);
                writer.WriteNumber("to", value.To);
                break;
            case BehaviourTreeNodeKind.Check:
                writer.WriteString(key, value.Key);
                if (value.Min is { } min) writer.WriteNumber("min", min);
                if (value.Max is { } max) writer.WriteNumber("max", max);
                break;
            case BehaviourTreeNodeKind.Condition:
                break;
            case BehaviourTreeNodeKind.Sequence or BehaviourTreeNodeKind.Selector or BehaviourTreeNodeKind.Utility:
                writer.WriteStartArray(key);
                foreach (var child in value.ChildList) Write(writer, child, options);
                writer.WriteEndArray();
                break;
            default:
                writer.WritePropertyName(key);
                Write(writer, value.ChildList[0], options);
                if (value.Kind == BehaviourTreeNodeKind.Repeat) writer.WriteNumber("times", value.Times);
                if (value.Kind == BehaviourTreeNodeKind.Cooldown) writer.WriteNumber("seconds", value.Seconds);
                break;
        }
        writer.WriteEndObject();
    }

    private static void WriteNames(Utf8JsonWriter writer, string key, List<string> names)
    {
        writer.WriteStartArray(key);
        foreach (var name in names) writer.WriteStringValue(name);
        writer.WriteEndArray();
    }
}

// A named number of a creature's blackboard (AIState.Blackboard).
[Experimental("SAGE0120", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // behaviour trees (#387): may change before 1.0
public struct AIBlackboardEntry
{
    [Property(Tooltip = "Its name")]
    public string Key;
    [Property(Tooltip = "Its value")]
    public float Value;
}

// Reading and writing a creature's blackboard, for a game's code (a task, a sensed condition, a script).
[Experimental("SAGE0120", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // behaviour trees (#387): may change before 1.0
public static class AIBlackboard
{
    // The value under `key`, or 0 when nothing has set it. Keys ignore case.
    public static float Get(in AIState state, string key)
    {
        var entries = state.Blackboard;
        if (entries == null) return 0f;
        for (int i = 0; i < entries.Count; i++)
            if (string.Equals(entries[i].Key, key, StringComparison.OrdinalIgnoreCase)) return entries[i].Value;
        return 0f;
    }

    public static void Set(ref AIState state, string key, float value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var entries = state.Blackboard ??= new List<AIBlackboardEntry>();
        for (int i = 0; i < entries.Count; i++)
            if (string.Equals(entries[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                entries[i] = new AIBlackboardEntry { Key = entries[i].Key, Value = value };
                return;
            }
        entries.Add(new AIBlackboardEntry { Key = key, Value = value });
    }

    public static float Get(World world, Entity entity, string key) =>
        world.TryGet<AIState>(entity, out var state) ? Get(in state, key) : 0f;

    // False when the entity has no AIState to keep it on.
    public static bool Set(World world, Entity entity, string key, float value)
    {
        if (!world.Has<AIState>(entity)) return false;
        Set(ref world.Get<AIState>(entity), key, value);
        return true;
    }
}

// A tree's nodes flattened depth first (node 0 is the root), with where each one's subtree ends, so
// aborting a node is a range of the per-agent arrays. Masks and tasks are looked up once per table.
internal sealed class CompiledBehaviourTree
{
    public readonly BehaviourTreeNode[] Nodes;
    public readonly int[] End;             // one past the last node of each subtree
    public readonly int[][] Children;
    public readonly string[] Paths;        // where each node is in the record ("root.selector[1]"), for messages

    private ulong[] _when = Array.Empty<ulong>(), _unless = Array.Empty<ulong>();
    private AIConditions? _masksFor;
    private IAITask?[] _tasks = Array.Empty<IAITask?>();
    private AITaskRegistry? _tasksFor;

    public CompiledBehaviourTree(BehaviourTreeNode? root)
    {
        var nodes = new List<BehaviourTreeNode>();
        var paths = new List<string>();
        var ends = new List<int>();
        var children = new List<int[]>();
        if (root != null) Add(root, "root");
        Nodes = nodes.ToArray();
        Paths = paths.ToArray();
        End = ends.ToArray();
        Children = children.ToArray();

        int Add(BehaviourTreeNode node, string path)
        {
            int index = nodes.Count;
            nodes.Add(node);
            paths.Add(path);
            ends.Add(0);
            children.Add(Array.Empty<int>());
            var mine = new int[node.ChildList.Count];
            string key = BehaviourTreeNode.KeyOf(node.Kind);
            for (int i = 0; i < mine.Length; i++)
                mine[i] = Add(node.ChildList[i], node.IsComposite ? $"{path}.{key}[{i}]" : $"{path}.{key}");
            children[index] = mine;
            ends[index] = nodes.Count;
            return index;
        }
    }

    public int Count => Nodes.Length;

    public ulong When(int node, AIConditions names) { Masks(names); return _when[node]; }

    public ulong Unless(int node, AIConditions names) { Masks(names); return _unless[node]; }

    private void Masks(AIConditions names)
    {
        if (ReferenceEquals(_masksFor, names)) return;
        var when = new ulong[Nodes.Length];
        var unless = new ulong[Nodes.Length];
        for (int i = 0; i < Nodes.Length; i++)
        {
            when[i] = names.MaskOf(Nodes[i].When);
            unless[i] = names.MaskOf(Nodes[i].Unless);
        }
        _when = when;
        _unless = unless;
        _masksFor = names;
    }

    public IAITask? Task(int node, AITaskRegistry tasks)
    {
        if (!ReferenceEquals(_tasksFor, tasks))
        {
            _tasks = new IAITask?[Nodes.Length];
            for (int i = 0; i < Nodes.Length; i++)
                if (Nodes[i].Kind == BehaviourTreeNodeKind.Task) _tasks[i] = tasks.Find(Nodes[i].Task.Task);
            _tasksFor = tasks;
        }
        return _tasks[node];
    }
}

// Where one creature is in its tree: per node, the child it is on (or the times a repeat has run; -1 is
// not started), when a cooldown ends, and whether its guard held when last asked (a guard that becomes
// true preempts). Rebuilt from the root when the tree is reloaded, the creature loaded, or the component
// copied to another entity.
internal sealed class BehaviourTreeRun
{
    public readonly BehaviourTreeRecord Record;
    public readonly CompiledBehaviourTree Tree;
    public readonly Entity Owner;
    public readonly int[] Child;
    public readonly float[] Until;
    public readonly bool[] Held;
    public int Leaf = -1;                  // the task node running, or -1

    public BehaviourTreeRun(BehaviourTreeRecord record, Entity owner)
    {
        Record = record;
        Tree = record.Compiled;
        Owner = owner;
        Child = new int[Tree.Count];
        Until = new float[Tree.Count];
        Held = new bool[Tree.Count];
        Array.Fill(Child, -1);
    }

    public bool IsFor(BehaviourTreeRecord record, Entity owner) =>
        ReferenceEquals(Record, record) && ReferenceEquals(Tree, record.Compiled) && Owner == owner;
}

// One tick of one creature's tree: what a task needs, and what the walk down the tree carries.
internal ref struct BehaviourTreeTick
{
    public World World;
    public Entity Entity;
    public ref AIState State;
    public ref PawnIntent Intent;
    public ref Transform Transform;
    public AIProfileRecord Profile;
    public MovementProfileRecord Movement;
    public IPhysicsWorld Space;
    public ActionId Attack;
    public ActionId Block;
    public float Dt;
    public float Time;
    public bool Thought;                   // the creature thought this tick: utility nodes score again
    public RecordStore Records;
    public AITaskRegistry Tasks;
    public AIConditions Names;
    public BehaviourTreeRun Run;
    public bool RanTask;                   // a task ran this tick: the next one waits for the next tick
}

internal static class BehaviourTrees
{
    // Ticks the tree from its root; when the root finishes, it starts again from the root next tick.
    public static void Tick(ref BehaviourTreeTick t)
    {
        if (t.Run.Tree.Count == 0) { t.Intent.Move = Vector2.Zero; return; }
        var status = Node(ref t, 0);
        if (status != AITaskStatus.Running) Reset(ref t, 0);
        if (!t.RanTask) t.Intent.Move = Vector2.Zero;   // nothing is walking it: it stands
    }

    private static AITaskStatus Node(ref BehaviourTreeTick t, int i)
    {
        var tree = t.Run.Tree;
        var node = tree.Nodes[i];
        bool holds = Guard(ref t, i);
        t.Run.Held[i] = holds;
        if (!holds) { Reset(ref t, i); return AITaskStatus.Failed; }

        var children = tree.Children[i];
        switch (node.Kind)
        {
            case BehaviourTreeNodeKind.Condition:
                return AITaskStatus.Succeeded;

            case BehaviourTreeNodeKind.Set:
                AIBlackboard.Set(ref t.State, node.Key, node.To);
                return AITaskStatus.Succeeded;

            case BehaviourTreeNodeKind.Check:
            {
                float value = AIBlackboard.Get(in t.State, node.Key);
                bool inRange = node.Min == null && node.Max == null ? value != 0f
                    : (node.Min == null || value >= node.Min.Value) && (node.Max == null || value <= node.Max.Value);
                return inRange ? AITaskStatus.Succeeded : AITaskStatus.Failed;
            }

            case BehaviourTreeNodeKind.Task:
                return Leaf(ref t, i);

            case BehaviourTreeNodeKind.Sequence:
                for (int c = Math.Max(t.Run.Child[i], 0); c < children.Length; c++)
                {
                    t.Run.Child[i] = c;
                    var status = Node(ref t, children[c]);
                    if (status == AITaskStatus.Running) return status;
                    if (status == AITaskStatus.Failed) { Reset(ref t, i); return status; }
                }
                Reset(ref t, i);
                return AITaskStatus.Succeeded;

            case BehaviourTreeNodeKind.Selector:
            {
                int start = Math.Max(t.Run.Child[i], 0);
                int preempt = Preempting(ref t, children, start);
                if (preempt >= 0) { Reset(ref t, children[start]); start = preempt; }
                for (int c = start; c < children.Length; c++)
                {
                    t.Run.Child[i] = c;
                    var status = Node(ref t, children[c]);
                    if (status == AITaskStatus.Running) return status;
                    if (status == AITaskStatus.Succeeded) { Reset(ref t, i); return status; }
                }
                Reset(ref t, i);
                return AITaskStatus.Failed;
            }

            case BehaviourTreeNodeKind.Utility:
            {
                int current = t.Run.Child[i];
                if (current >= 0 && Preempting(ref t, children, current) is >= 0 and var preempt)
                {
                    Reset(ref t, children[current]);
                    current = preempt;
                }
                else if (current < 0 || t.Thought)
                {
                    int best = Best(ref t, children, current, out _);
                    if (best < 0) { Reset(ref t, i); return AITaskStatus.Failed; }
                    if (current >= 0 && best != current) Reset(ref t, children[current]);
                    current = best;
                }
                t.Run.Child[i] = current;
                var status = Node(ref t, children[current]);
                if (status != AITaskStatus.Running) Reset(ref t, i);
                return status;
            }

            case BehaviourTreeNodeKind.Invert:
            {
                var status = Node(ref t, children[0]);
                return status switch
                {
                    AITaskStatus.Succeeded => AITaskStatus.Failed,
                    AITaskStatus.Failed => AITaskStatus.Succeeded,
                    _ => status,
                };
            }

            case BehaviourTreeNodeKind.Succeed:
                return Node(ref t, children[0]) == AITaskStatus.Running ? AITaskStatus.Running : AITaskStatus.Succeeded;

            case BehaviourTreeNodeKind.Repeat:
            {
                var status = Node(ref t, children[0]);
                if (status == AITaskStatus.Running) return status;
                if (status == AITaskStatus.Failed) { Reset(ref t, i); return status; }
                int done = Math.Max(t.Run.Child[i], 0) + 1;
                if (node.Times > 0 && done >= node.Times) { Reset(ref t, i); return AITaskStatus.Succeeded; }
                t.Run.Child[i] = done;
                return AITaskStatus.Running;   // the next time round starts next tick
            }

            case BehaviourTreeNodeKind.Cooldown:
            {
                bool running = t.Run.Child[i] >= 0;
                if (!running && t.Time < t.Run.Until[i]) return AITaskStatus.Failed;
                t.Run.Child[i] = 0;
                var status = Node(ref t, children[0]);
                if (status == AITaskStatus.Running) return status;
                t.Run.Child[i] = -1;
                t.Run.Until[i] = t.Time + node.Seconds;
                return status;
            }
        }
        return AITaskStatus.Failed;
    }

    // Does node i's guard hold? A node with none always may run.
    private static bool Guard(ref BehaviourTreeTick t, int i)
    {
        var tree = t.Run.Tree;
        ulong when = tree.When(i, t.Names), unless = tree.Unless(i, t.Names);
        return (t.State.Conditions & when) == when && (t.State.Conditions & unless) == 0;
    }

    // The first child before `running` with a guard that has become true since it was last asked; -1 when
    // none. Each earlier guarded child's guard is asked (and remembered) on the way.
    private static int Preempting(ref BehaviourTreeTick t, int[] children, int running)
    {
        int found = -1;
        for (int c = 0; c < running && c < children.Length; c++)
        {
            int child = children[c];
            if (!t.Run.Tree.Nodes[child].HasGuard) continue;
            bool holds = Guard(ref t, child);
            if (holds && !t.Run.Held[child] && found < 0) found = c;
            t.Run.Held[child] = holds;
        }
        return found;
    }

    // A utility node's best child: the highest score above 0 among those whose guards hold; a tie keeps
    // the one running, then goes to the earlier. -1 when none scores above 0.
    private static int Best(ref BehaviourTreeTick t, int[] children, int current, out float top)
    {
        var perception = new AIPerception
        {
            World = t.World,
            Entity = t.Entity,
            Transform = ref t.Transform,
            State = ref t.State,
            Profile = t.Profile,
            Time = t.Time,
            Conditions = t.State.Conditions,
        };
        int best = -1;
        top = 0f;
        for (int c = 0; c < children.Length; c++)
        {
            var node = t.Run.Tree.Nodes[children[c]];
            if (!Guard(ref t, children[c])) continue;
            float score = AIUtility.Score(in perception, t.Names, node.Score, node.Considerations);
            if (score > top || (score == top && score > 0f && c == current))
            {
                top = score;
                best = c;
            }
        }
        return best;
    }

    // A task node: started the first tick it is reached (unless a task already ran this tick, when it waits
    // for the next), then run every tick until it succeeds or fails, exactly as a schedule runs one.
    private static AITaskStatus Leaf(ref BehaviourTreeTick t, int i)
    {
        var run = t.Run;
        bool resuming = run.Leaf == i && t.State.TaskStarted && t.State.TaskIndex == i;
        if (!resuming && t.RanTask) return AITaskStatus.Running;

        var step = run.Tree.Nodes[i].Task;
        var task = run.Tree.Task(i, t.Tasks);
        if (task == null)
        {
            Log.Once(LogCat.AI, LogLevel.Error, $"bt-task:{run.Record.GetHashCode()}:{i}",
                $"{t.Records.Where("behaviour_tree", t.State.Tree, run.Tree.Paths[i])}: behaviour_tree {t.State.Tree}: " +
                $"no AI task named '{step.Task}' is registered; the node fails");
            return AITaskStatus.Failed;
        }
        if (step.Argument != null && task.Argument != null && !string.Equals(step.Argument, task.Argument, StringComparison.OrdinalIgnoreCase))
        {
            Log.Once(LogCat.AI, LogLevel.Error, $"bt-arg:{run.Record.GetHashCode()}:{i}",
                $"{t.Records.Where("behaviour_tree", t.State.Tree, run.Tree.Paths[i])}: behaviour_tree {t.State.Tree}: " +
                $"task '{step.Task}' takes '{task.Argument}', not '{step.Argument}'; the node fails");
            return AITaskStatus.Failed;
        }

        var context = new AITaskContext
        {
            World = t.World,
            Entity = t.Entity,
            State = ref t.State,
            Intent = ref t.Intent,
            Transform = ref t.Transform,
            Profile = t.Profile,
            Movement = t.Movement,
            Space = t.Space,
            Attack = t.Attack,
            Block = t.Block,
            Dt = t.Dt,
            Param = step.Value,
        };

        AITaskStatus status;
        if (!resuming)
        {
            if (run.Leaf >= 0 && run.Leaf != i) Abort(ref t);
            run.Leaf = i;
            t.State.TaskIndex = i;
            t.State.TaskStarted = true;
            t.State.TaskTime = 0;
            status = task.Start(ref context);
            if (status == AITaskStatus.Running) status = task.Run(ref context);
        }
        else
        {
            t.State.TaskTime += t.Dt;
            status = task.Run(ref context);
        }
        t.RanTask = true;

        if (status != AITaskStatus.Running)
        {
            run.Leaf = -1;
            t.State.TaskStarted = false;
            t.State.TaskTime = 0;
            if (status == AITaskStatus.Failed) t.State.Conditions |= (ulong)AICondition.TaskFailed;
        }
        return status;
    }

    // Node i and everything under it back to not started (a cooldown keeps its clock); a task running in
    // it is stopped where it stands.
    private static void Reset(ref BehaviourTreeTick t, int i)
    {
        var run = t.Run;
        int end = run.Tree.End[i];
        for (int n = i; n < end; n++) run.Child[n] = -1;
        if (run.Leaf >= i && run.Leaf < end) Abort(ref t);
    }

    private static void Abort(ref BehaviourTreeTick t)
    {
        t.Run.Leaf = -1;
        t.State.TaskStarted = false;
        t.State.TaskTime = 0;
        t.Intent.Move = Vector2.Zero;
    }

    // Load checks (AIModule): every condition, measure and blackboard key a tree names. Task names are
    // checked when the first world is created, with every task a game adds (CheckTasks).
    public static void Check(Vocabularies vocabularies, BehaviourTreeRecord tree, RecordCheck check)
    {
        if (tree.Root == null) { check.Error(nameof(BehaviourTreeRecord.Root), "a behaviour tree needs a \"root\" node"); return; }
        var compiled = tree.Compiled;
        var conditions = vocabularies.Of<IAICondition>();
        for (int i = 0; i < compiled.Count; i++)
        {
            var node = compiled.Nodes[i];
            foreach (var (field, names) in new[] { ("when", node.When), ("unless", node.Unless) })
                for (int n = 0; n < names.Count; n++)
                    if (!conditions.Contains(names[n]))
                        check.Error($"{compiled.Paths[i]}.{field}[{n}]", $"no AI condition '{names[n]}'" + Spelling.Suggest(names[n], conditions.Ids));
            if (node.Considerations.Count > 0) AIChecks.Considerations(vocabularies, node.Considerations, compiled.Paths[i] + ".", check);
        }
    }

    // Every task node against the registry (CheckTasks): an unknown name or a misnamed argument.
    internal static IEnumerable<(string Path, string Problem)> TaskProblems(BehaviourTreeRecord tree, AITaskRegistry tasks)
    {
        var compiled = tree.Compiled;
        for (int i = 0; i < compiled.Count; i++)
        {
            var node = compiled.Nodes[i];
            if (node.Kind != BehaviourTreeNodeKind.Task) continue;
            var step = node.Task;
            if (tasks.Find(step.Task) is not { } task)
                yield return (compiled.Paths[i], $"no AI task named '{step.Task}'" + Spelling.Suggest(step.Task, tasks.Names));
            else if (step.Argument != null && task.Argument != null && !string.Equals(step.Argument, task.Argument, StringComparison.OrdinalIgnoreCase))
                yield return (compiled.Paths[i], $"task '{step.Task}' takes '{task.Argument}', not '{step.Argument}'");
        }
    }
}
