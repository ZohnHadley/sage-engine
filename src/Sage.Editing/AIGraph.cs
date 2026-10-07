#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace Sage.Editing;

// What an AI graph is drawn from (issue #369): a record whose JSON is a tree of states, or a list of steps.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public enum AIGraphKind
{
    StateMachine,   // `state_machine`: states, nested states and parallel regions; transitions are the edges
    Schedule,       // `ai_schedule`: its tasks, in order
    Routine,        // `routine`: its entries, in order
    BehaviourTree,  // `behaviour_tree` (#387): its nodes from the root, composites and decorators holding theirs
}

// A transition, drawn as an edge: where it goes and what takes it ("on Calm", "after 3s", "when {...}").
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public readonly record struct AIGraphEdge(string To, string Label);

// One node of an AI graph: a state, a task or a routine entry, where it is in the record's JSON (`Path`, as
// RecordDocument reads one) and where it is in the tree.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class AIGraphNode
{
    internal AIGraphNode(string path, string name, string label, int depth, string? parent, int index)
    {
        Path = path;
        Name = name;
        Label = label;
        Depth = depth;
        Parent = parent;
        Index = index;
    }

    public string Path { get; }
    // A state's name (unique across its machine), or a task's; an entry's schedule.
    public string Name { get; }
    // What the view shows: the name, with what matters about it ("Wait  seconds 1.5", "patrol  (parallel)").
    public string Label { get; }
    public int Depth { get; }
    // The path of the node it is inside; null at the top.
    public string? Parent { get; }
    // Its place among the nodes beside it.
    public int Index { get; }
    // The state its parent (or the machine) enters first.
    public bool IsInitial { get; internal set; }
    public bool IsParallel { get; internal set; }
    public IReadOnlyList<string> Children { get; internal set; } = Array.Empty<string>();
    public IReadOnlyList<AIGraphEdge> Edges { get; internal set; } = Array.Empty<AIGraphEdge>();

    public override string ToString() => Label;
}

// The AI graph view's model (issue #369; docs/EDITOR.md "A behaviour-tree view"): a `state_machine`,
// `ai_schedule` or `routine` record open in the record browser, read as a tree of nodes rather than raw
// JSON, edited a node at a time, and asked which of its nodes a running entity is in.
//
// **It is a view of the open RecordDocument, not a copy.** The nodes are read off the document's working
// JSON (again after every change, so an undo in the Records panel shows here at once), and every edit —
// add, remove, move, reparent, rename — is one command in the document's own undo history, so Ctrl+Z,
// `ed_rec_undo` and the save are the record browser's. An edit keeps the record loadable where it can: a
// state added to a machine with none becomes its `initial` (and a parent's, when it is the first inside
// it); a state removed takes the transitions that went to it with it, and an `initial` that named it moves
// to the first state left; a rename renames every `to` and `initial` that named it.
//
// **Live.** `Runners` lists the entities of a world that run the record (a `sage:state_machine` with this
// machine, a `sage:ai_state` on this schedule, a `sage:routine` following this routine) and `Active` the
// paths of the nodes one is in now: every state a machine is in (outer, inner and each parallel region),
// the task a schedule is on. Read through the components' stable ids and declared fields, so the editor
// (a base assembly) needs nothing of the gameplay layer that owns schedules and routines. A routine has
// no live node here: which entry is in force is the gameplay layer's arithmetic on the clock.
//
// A `behaviour_tree` (issue #387) is drawn from its `root`: a composite's (`sequence`, `selector`, `utility`)
// nodes and a decorator's one node are inside it, a node's guard is in its label, and a creature running it
// has the task node it is on active, with every node above it.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class AIGraph
{
    private const string StatesKey = "states", TransitionsKey = "transitions", InitialKey = "initial", ParallelKey = "parallel";

    private List<AIGraphNode>? _nodes;
    private List<AIGraphEdge> _any = new();

    private AIGraph(RecordDocument document, AIGraphKind kind)
    {
        Document = document;
        Kind = kind;
        document.Changed += () => _nodes = null;
    }

    // Whether records of a type are drawn as a graph.
    public static bool Supports(string type) => KindOf(type) != null;

    private static AIGraphKind? KindOf(string type) => type switch
    {
        "state_machine" => AIGraphKind.StateMachine,
        "ai_schedule" => AIGraphKind.Schedule,
        "routine" => AIGraphKind.Routine,
        "behaviour_tree" => AIGraphKind.BehaviourTree,
        _ => null,
    };

    // The graph of an open record; null when its type is not one.
    public static AIGraph? Of(RecordDocument? document) =>
        document != null && KindOf(document.Type) is { } kind ? new AIGraph(document, kind) : null;

    public RecordDocument Document { get; }
    public AIGraphKind Kind { get; }

    // Whether its nodes hold nodes (a machine's states do; a list's steps do not).
    public bool IsTree => Kind is AIGraphKind.StateMachine or AIGraphKind.BehaviourTree;

    // Every node, depth first in the order the record writes them (a parent before what is inside it).
    public IReadOnlyList<AIGraphNode> Nodes => _nodes ??= Build();

    // The machine's own transitions (from any state); empty for a list.
    public IReadOnlyList<AIGraphEdge> AnyStateEdges { get { _ = Nodes; return _any; } }

    // The machine's initial state; "" for a list.
    public string Initial => Kind == AIGraphKind.StateMachine ? Text(Get(Document.Working, InitialKey)) : "";

    // A node by its path, its name (a state's, ignoring case) or its index (`2` or `#2`, for a list); null when none.
    public AIGraphNode? Find(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        foreach (var node in Nodes)
            if (string.Equals(node.Path, key, StringComparison.OrdinalIgnoreCase)) return node;
        if (Kind == AIGraphKind.StateMachine)
            return Nodes.FirstOrDefault(n => string.Equals(n.Name, key, StringComparison.OrdinalIgnoreCase));
        // A tree's node by its name (the first of that name), else by its number counted depth first.
        if (Kind == AIGraphKind.BehaviourTree && Nodes.FirstOrDefault(n => string.Equals(n.Name, key, StringComparison.OrdinalIgnoreCase)) is { } named)
            return named;
        string number = key.StartsWith('#') ? key[1..] : key;
        return int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int i) && i < Nodes.Count ? Nodes[i] : null;
    }

    // ---- Reading the record -------------------------------------------------------------------------

    private List<AIGraphNode> Build()
    {
        var nodes = new List<AIGraphNode>();
        _any = new List<AIGraphEdge>();
        var root = Document.Working;
        switch (Kind)
        {
            case AIGraphKind.StateMachine:
                if (Get(root, StatesKey) is JsonObject states)
                    AddStates(nodes, states, ChildPath("", KeyOf(root, StatesKey)!), null, Text(Get(root, InitialKey)), 0);
                if (Get(root, TransitionsKey) is JsonArray any) _any = Edges(any);
                break;
            case AIGraphKind.BehaviourTree:
                if (KeyOf(root, RootKey) is { } rootKey && root[rootKey] is { } top) AddTreeNode(nodes, top, ChildPath("", rootKey), null, 0, 0);
                break;
            case AIGraphKind.Schedule:
            case AIGraphKind.Routine:
                string listKey = Kind == AIGraphKind.Schedule ? "tasks" : "entries";
                if (Get(root, listKey) is JsonArray list)
                {
                    string at = ChildPath("", KeyOf(root, listKey)!);
                    for (int i = 0; i < list.Count; i++)
                        nodes.Add(new AIGraphNode($"{at}[{i}]", StepName(list[i]), StepLabel(list[i]), 0, null, i));
                }
                break;
        }
        return nodes;
    }

    private static List<string> AddStates(List<AIGraphNode> nodes, JsonObject states, string at, string? parent, string initial, int depth)
    {
        var paths = new List<string>();
        int index = 0;
        foreach (var (name, value) in states)
        {
            string path = ChildPath(at, name);
            var state = value as JsonObject;
            bool parallel = state != null && Get(state, ParallelKey) is JsonValue p && p.TryGetValue(out bool b) && b;
            var node = new AIGraphNode(path, name, name + (parallel ? "  (parallel)" : "") + Tags(state), depth, parent, index++)
            {
                IsInitial = string.Equals(name, initial, StringComparison.OrdinalIgnoreCase),
                IsParallel = parallel,
                Edges = state != null && Get(state, TransitionsKey) is JsonArray t ? Edges(t) : Array.Empty<AIGraphEdge>(),
            };
            nodes.Add(node);
            paths.Add(path);
            if (state != null && Get(state, StatesKey) is JsonObject inner)
                node.Children = AddStates(nodes, inner, ChildPath(path, KeyOf(state, StatesKey)!), path, Text(Get(state, InitialKey)), depth + 1);
        }
        return paths;
    }

    private static string Tags(JsonObject? state) =>
        state != null && Get(state, "tags") is JsonArray tags && tags.Count > 0 ? "  [" + string.Join(", ", tags.Select(Text)) + "]" : "";

    private static List<AIGraphEdge> Edges(JsonArray transitions)
    {
        var edges = new List<AIGraphEdge>();
        foreach (var item in transitions)
        {
            if (item is not JsonObject t) continue;
            var label = new List<string>();
            if (Get(t, "on") is { } on) label.Add("on " + Text(on));
            if (Get(t, "after") is { } after) label.Add("after " + Text(after) + "s");
            if (Get(t, "when") is { } when) label.Add("when " + when.ToJsonString());
            edges.Add(new AIGraphEdge(Text(Get(t, "to")), label.Count == 0 ? "always" : string.Join(", ", label)));
        }
        return edges;
    }

    // A task's name (`"FaceTarget"`, `{ "task": "Wait" }`) or an entry's schedule.
    private string StepName(JsonNode? step) => step switch
    {
        JsonObject o when Kind == AIGraphKind.Schedule => Text(Get(o, "task")),
        JsonObject o => Text(Get(o, "schedule")),
        _ => Text(step),
    };

    private string StepLabel(JsonNode? step)
    {
        if (step is not JsonObject o) return Text(step);
        if (Kind == AIGraphKind.Schedule)
        {
            var args = o.Where(kv => !string.Equals(kv.Key, "task", StringComparison.OrdinalIgnoreCase)).Select(kv => $"{kv.Key} {Text(kv.Value)}");
            return string.Join("  ", new[] { Text(Get(o, "task")) }.Concat(args));
        }
        string days = Get(o, "days") is JsonArray d && d.Count > 0 ? " " + string.Join("/", d.Select(Text)) : "";
        string scene = Get(o, "scene") is { } s && Text(s).Length > 0 ? $" ({Text(s)})" : "";
        return $"{Hour(Get(o, "from"))}-{Hour(Get(o, "to"))}{days}  {Text(Get(o, "schedule"))} @ {Text(Get(o, "at"))}{scene}";
    }

    private static string Hour(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out double h) ? $"{(int)h:00}:{(int)Math.Round((h - Math.Floor(h)) * 60):00}" : "?";

    // ---- Editing --------------------------------------------------------------------------------------

    // Adds a node: a state named `name` inside `parent` (null: at the top), or a task / entry (`parent` must be
    // null) at `index` (-1: at the end). `body` is the new node's JSON; none is an empty state, the bare
    // task name, or an all-day entry running the schedule `name`.
    public bool Add(string? parent, string name, JsonNode? body = null, int index = -1)
    {
        if (string.IsNullOrWhiteSpace(name)) return Refuse("a new node needs a name");
        if (Kind == AIGraphKind.BehaviourTree) return AddTreeNode(parent, name, body, index);
        var working = Clone();
        if (Kind == AIGraphKind.StateMachine)
        {
            if (Nodes.Any(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)))
                return Refuse($"there is already a state '{name}' (names are unique across a machine)");
            JsonObject owner = working;
            if (!string.IsNullOrEmpty(parent))
            {
                if (Find(parent) is not { } p) return Refuse($"no state '{parent}'");
                owner = (JsonObject)At(working, p.Path)!;
            }
            var container = Container(owner, create: true)!;
            Insert(container, name, body?.DeepClone() ?? new JsonObject(), index);
            if (Text(Get(owner, InitialKey)).Length == 0 && !(owner != working && IsParallel(owner))) Put(owner, InitialKey, name);
            return Commit(working, $"Add state '{name}'{(string.IsNullOrEmpty(parent) ? "" : $" in '{Find(parent)!.Name}'")}");
        }

        if (!string.IsNullOrEmpty(parent)) return Refuse($"a {Document.Type}'s steps are a list: they hold nothing");
        var list = List(working, create: true)!;
        JsonNode step = body?.DeepClone() ?? (Kind == AIGraphKind.Schedule
            ? JsonValue.Create(name)
            : new JsonObject { ["from"] = 0, ["to"] = 24, ["schedule"] = name, ["at"] = "" });
        if (index < 0 || index > list.Count) index = list.Count;
        list.Insert(index, step);
        return Commit(working, $"Add {(Kind == AIGraphKind.Schedule ? "task" : "entry")} '{name}' at {index}");
    }

    // Removes a node and everything inside it.
    public bool Remove(string key)
    {
        if (Find(key) is not { } node) return Refuse($"no node '{key}'");
        if (Kind == AIGraphKind.BehaviourTree) return RemoveTreeNode(node);
        var working = Clone();
        if (Kind != AIGraphKind.StateMachine)
        {
            List(working, create: false)!.RemoveAt(node.Index);
            return Commit(working, $"Remove {node.Name} (#{node.Index})");
        }

        var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { node.Name };
        foreach (var inside in Below(node)) gone.Add(inside.Name);
        var owner = OwnerOf(working, node);
        Detach(owner, working, node.Name);
        // What went to a state that is gone goes too, wherever it was written; the initials that named one move on.
        foreach (var state in States(working)) DropTransitionsTo(state, gone);
        DropTransitionsTo(working, gone);
        if (gone.Contains(Text(Get(working, InitialKey))))
            Put(working, InitialKey, Container(working, create: false) is { Count: > 0 } top ? top.First().Key : "");
        return Commit(working, $"Remove state '{node.Name}'" + (gone.Count > 1 ? $" and the {gone.Count - 1} inside it" : ""));
    }

    // Moves a node to `index` among the nodes beside it.
    public bool Move(string key, int index)
    {
        if (Find(key) is not { } node) return Refuse($"no node '{key}'");
        if (Kind == AIGraphKind.BehaviourTree) return MoveTreeNode(node, index);
        var working = Clone();
        if (Kind != AIGraphKind.StateMachine)
        {
            var list = List(working, create: false)!;
            index = Math.Clamp(index, 0, list.Count - 1);
            if (index == node.Index) return false;
            var step = list[node.Index]!.DeepClone();
            list.RemoveAt(node.Index);
            list.Insert(index, step);
            return Commit(working, $"Move {node.Name} #{node.Index} → #{index}");
        }

        var container = Container(OwnerOf(working, node), create: false)!;
        index = Math.Clamp(index, 0, container.Count - 1);
        if (index == node.Index) return false;
        var value = container[KeyOf(container, node.Name)!]!.DeepClone();
        container.Remove(KeyOf(container, node.Name)!);
        Insert(container, node.Name, value, index);
        return Commit(working, $"Move state '{node.Name}' to #{index}");
    }

    // Moves a state inside another (`parent`; null or "": to the top), at `index` there (-1: last). Not
    // into itself or a state inside it. Lists have no parents to move between.
    public bool Reparent(string key, string? parent, int index = -1)
    {
        if (Kind == AIGraphKind.BehaviourTree) return Refuse("a behaviour tree's node moves among its siblings with Move; to put it elsewhere, remove it and add it there");
        if (Kind != AIGraphKind.StateMachine) return Refuse($"a {Document.Type}'s steps are a list: move them with Move");
        if (Find(key) is not { } node) return Refuse($"no state '{key}'");
        AIGraphNode? to = null;
        if (!string.IsNullOrEmpty(parent))
        {
            if ((to = Find(parent)) == null) return Refuse($"no state '{parent}'");
            if (to == node || Below(node).Contains(to)) return Refuse($"'{node.Name}' cannot go inside itself");
        }
        if (to?.Path == node.Parent) return Move(key, index < 0 ? int.MaxValue : index);   // already there: a move among its siblings

        var working = Clone();
        var from = Container(OwnerOf(working, node), create: false)!;
        var value = from[KeyOf(from, node.Name)!]?.DeepClone();
        Detach(OwnerOf(working, node), working, node.Name);
        // A path is made of names, so the destination's is still good with the state taken out.
        var owner = to == null ? working : (JsonObject)At(working, to.Path)!;
        Insert(Container(owner, create: true)!, node.Name, value, index);
        if (Text(Get(owner, InitialKey)).Length == 0 && !(owner != working && IsParallel(owner))) Put(owner, InitialKey, node.Name);
        return Commit(working, $"Move state '{node.Name}' into {(to == null ? "the top" : $"'{to.Name}'")}");
    }

    // Renames a state (and every `to` and `initial` that named it), or a task (its name; its argument stays).
    public bool Rename(string key, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Refuse("a name cannot be empty");
        if (Find(key) is not { } node) return Refuse($"no node '{key}'");
        var working = Clone();
        switch (Kind)
        {
            case AIGraphKind.StateMachine:
                if (Nodes.Any(n => n != node && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)))
                    return Refuse($"there is already a state '{name}'");
                var container = Container(OwnerOf(working, node), create: false)!;
                var pairs = container.Select(kv => (kv.Key, Value: kv.Value?.DeepClone())).ToList();
                container.Clear();
                foreach (var (k, v) in pairs) container[string.Equals(k, node.Name, StringComparison.OrdinalIgnoreCase) ? name : k] = v;
                foreach (var state in States(working).Append(working)) RenameReferences(state, node.Name, name);
                return Commit(working, $"Rename state '{node.Name}' → '{name}'");
            case AIGraphKind.Schedule:
                var list = List(working, create: false)!;
                if (list[node.Index] is JsonObject task) Put(task, "task", name);
                else list[node.Index] = JsonValue.Create(name);
                return Commit(working, $"Rename task #{node.Index} {node.Name} → {name}");
            case AIGraphKind.BehaviourTree:
                // A task node is renamed by its task (its argument stays); any other is given a `name`.
                var at = RecordPath.Parse(node.Path)!;
                var value = RecordPath.Get(working, at);
                if (value is JsonValue) Replace(working, node.Path, JsonValue.Create(name));
                else if (value is JsonObject treeNode) Put(treeNode, KeyOf(treeNode, "task") != null ? "task" : "name", name);
                else return Refuse($"no node '{key}'");
                return Commit(working, $"Rename {node.Name} → {name}");
            default:
                return Refuse("a routine's entries are named by their schedule: set it in the Records panel");
        }
    }

    // ---- Live ---------------------------------------------------------------------------------------

    // The entities of `world` that run this record, in the world's order.
    public IReadOnlyList<Entity> Runners(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var found = new List<Entity>();
        if (world.Engine is not { } engine || !engine.Components.TryComponent(RunnerComponent, out var type)) return found;
        foreach (var entity in world.QueryAll().Entities)
            if (engine.Components.Read(entity, type) is { } value && Field(value, RunnerField) is RecordId id && id == Document.Id)
                found.Add(entity);
        return found;
    }

    // The paths of the nodes `entity` is in now, outer first: empty when it does not run this record (or
    // has not started, or is a routine's).
    public IReadOnlyList<string> Active(World world, Entity entity)
    {
        ArgumentNullException.ThrowIfNull(world);
        var active = new List<string>();
        if (!world.IsAlive(entity) || world.Engine is not { } engine || !engine.Components.TryComponent(RunnerComponent, out var type)
            || engine.Components.Read(entity, type) is not { } value || Field(value, RunnerField) is not RecordId id || id != Document.Id)
            return active;
        switch (Kind)
        {
            case AIGraphKind.StateMachine:
                foreach (var node in Nodes)
                    if (StateMachines.IsIn(world, entity, node.Name)) active.Add(node.Path);
                break;
            case AIGraphKind.Schedule:
                if (Field(value, "taskIndex") is int task && task >= 0 && task < Nodes.Count) active.Add(Nodes[task].Path);
                break;
            case AIGraphKind.BehaviourTree:
                // The running task node is `taskIndex`, counted depth first as the engine counts it; above it,
                // every node it is inside.
                if (Field(value, "taskStarted") is true && Field(value, "taskIndex") is int leaf && leaf >= 0 && leaf < Nodes.Count)
                {
                    for (var node = Nodes[leaf]; node != null; node = node.Parent == null ? null : Nodes.FirstOrDefault(n => n.Path == node.Parent))
                        active.Insert(0, node.Path);
                }
                break;
        }
        return active;
    }

    // Which component says an entity runs a record of this kind, and which of its fields names the record.
    private string RunnerComponent => Kind switch
    {
        AIGraphKind.StateMachine => "sage:state_machine",
        AIGraphKind.Schedule or AIGraphKind.BehaviourTree => "sage:ai_state",
        _ => "sage:routine",
    };

    private string RunnerField => Kind switch
    {
        AIGraphKind.StateMachine => "machine",
        AIGraphKind.Schedule => "schedule",
        AIGraphKind.BehaviourTree => "tree",
        _ => "id",
    };

    private static object? Field(object component, string jsonName)
    {
        foreach (var field in Metadata.Of(component.GetType()).Fields)
            if (string.Equals(field.JsonName, jsonName, StringComparison.OrdinalIgnoreCase)) return field.Get?.Invoke(component);
        return null;
    }

    // ---- Behaviour trees (#387) ------------------------------------------------------------------------

    private const string RootKey = "root";
    private static readonly string[] CompositeKeys = { "sequence", "selector", "utility" };
    private static readonly string[] DecoratorKeys = { "invert", "succeed", "repeat", "cooldown" };

    // A node and what is inside it, depth first: the order the engine numbers them in (AIState.TaskIndex).
    private static void AddTreeNode(List<AIGraphNode> nodes, JsonNode json, string path, string? parent, int depth, int index)
    {
        var node = new AIGraphNode(path, TreeName(json), TreeLabel(json), depth, parent, index);
        nodes.Add(node);
        if (json is not JsonObject obj) return;
        var children = new List<string>();
        foreach (var key in CompositeKeys)
            if (KeyOf(obj, key) is { } k && obj[k] is JsonArray list)
            {
                string at = ChildPath(path, k);
                for (int i = 0; i < list.Count; i++)
                    if (list[i] is { } child)
                    {
                        children.Add($"{at}[{i}]");
                        AddTreeNode(nodes, child, $"{at}[{i}]", path, depth + 1, i);
                    }
            }
        foreach (var key in DecoratorKeys)
            if (KeyOf(obj, key) is { } k && obj[k] is { } child)
            {
                children.Add(ChildPath(path, k));
                AddTreeNode(nodes, child, ChildPath(path, k), path, depth + 1, 0);
            }
        node.Children = children;
    }

    // What a node is called: its `name`, else its task, else what it is ("selector", "repeat", "when").
    private static string TreeName(JsonNode json)
    {
        if (json is not JsonObject obj) return Text(json);
        if (Get(obj, "name") is { } name && Text(name).Length > 0) return Text(name);
        if (Get(obj, "task") is { } task) return Text(task);
        return TreeKind(obj) ?? "when";
    }

    private static string? TreeKind(JsonObject obj) =>
        CompositeKeys.Concat(DecoratorKeys).Concat(new[] { "set", "check", "task" }).FirstOrDefault(k => KeyOf(obj, k) != null);

    private static string TreeLabel(JsonNode json)
    {
        if (json is not JsonObject obj) return Text(json);
        string kind = TreeKind(obj) ?? "";
        string Number(string key) => Get(obj, key) is { } n ? Text(n) : "";
        string what = kind switch
        {
            "task" => string.Join("  ", new[] { Text(Get(obj, "task")) }.Concat(obj
                .Where(kv => kv.Value is JsonValue v && v.TryGetValue(out double _) && !string.Equals(kv.Key, "score", StringComparison.OrdinalIgnoreCase))
                .Select(kv => $"{kv.Key} {Text(kv.Value)}"))),
            "set" => $"set {Text(Get(obj, "set"))} = {(Number("to") is { Length: > 0 } to ? to : "1")}",
            "check" => $"check {Text(Get(obj, "check"))}" + (Number("min").Length + Number("max").Length > 0
                ? $" {(Number("min") is { Length: > 0 } min ? min : "")}..{(Number("max") is { Length: > 0 } max ? max : "")}" : ""),
            "repeat" => Number("times") is { Length: > 0 } times && times != "0" ? $"repeat {times} times" : "repeat",
            "cooldown" => $"cooldown {(Number("seconds") is { Length: > 0 } s ? s : "0")}s",
            "" => "if",
            _ => kind,
        };
        string Names(string key) => Get(obj, key) is JsonArray a ? string.Join(", ", a.Select(Text)) : Get(obj, key) is { } one ? Text(one) : "";
        var guard = new List<string>();
        if (Names("when") is { Length: > 0 } when) guard.Add((kind.Length == 0 ? "" : "when ") + when);
        if (Names("unless") is { Length: > 0 } unless) guard.Add("unless " + unless);
        string label = guard.Count == 0 ? what : kind.Length == 0 ? $"if {string.Join("; ", guard)}" : $"{what}  [{string.Join("; ", guard)}]";
        if (Number("score") is { Length: > 0 } score) label += $"  score {score}";
        return Get(obj, "name") is { } name && Text(name).Length > 0 ? $"{Text(name)}: {label}" : label;
    }

    // The list a node is in (its parent composite's), as a path; null for the root and a decorator's node.
    private static string? ListOf(AIGraphNode node) =>
        node.Path.EndsWith(']') && node.Path.LastIndexOf('[') is > 0 and var open ? node.Path[..open] : null;

    private bool AddTreeNode(string? parent, string name, JsonNode? body, int index)
    {
        var working = Clone();
        var step = body?.DeepClone() ?? JsonValue.Create(name);
        if (string.IsNullOrEmpty(parent))
        {
            if (KeyOf(working, RootKey) is not { } rootKey || working[rootKey] == null)
            {
                Put(working, RootKey, step);
                return Commit(working, $"Add '{name}' as the root");
            }
            parent = ChildPath("", rootKey);
        }
        if (Find(parent) is not { } owner) return Refuse($"no node '{parent}'");
        if (RecordPath.Get(working, RecordPath.Parse(owner.Path)!) is not JsonObject obj
            || CompositeKeys.Select(k => KeyOf(obj, k)).FirstOrDefault(k => k != null) is not { } listKey || obj[listKey] is not JsonArray list)
            return Refuse($"'{owner.Name}' holds no list of nodes: add inside a sequence, selector or utility");
        if (index < 0 || index > list.Count) index = list.Count;
        list.Insert(index, step);
        return Commit(working, $"Add '{name}' in '{owner.Name}' at {index}");
    }

    private bool RemoveTreeNode(AIGraphNode node)
    {
        var working = Clone();
        if (node.Parent == null)
        {
            working.Remove(KeyOf(working, RootKey)!);
            return Commit(working, $"Remove the root, {node.Name}");
        }
        if (ListOf(node) is not { } listPath || RecordPath.Get(working, RecordPath.Parse(listPath)!) is not JsonArray list)
            return Refuse($"'{node.Name}' is a decorator's one node: remove the decorator");
        if (list.Count <= 1) return Refuse($"'{node.Name}' is the last node of its {Find(node.Parent)?.Name}: remove that instead");
        list.RemoveAt(node.Index);
        return Commit(working, $"Remove {node.Name} (#{node.Index} of {Find(node.Parent)?.Name})");
    }

    private bool MoveTreeNode(AIGraphNode node, int index)
    {
        var working = Clone();
        if (ListOf(node) is not { } listPath || RecordPath.Get(working, RecordPath.Parse(listPath)!) is not JsonArray list)
            return Refuse($"'{node.Name}' has no siblings to move among");
        index = Math.Clamp(index, 0, list.Count - 1);
        if (index == node.Index) return false;
        var value = list[node.Index]?.DeepClone();
        list.RemoveAt(node.Index);
        list.Insert(index, value);
        return Commit(working, $"Move {node.Name} #{node.Index} → #{index}");
    }

    // Puts `value` where `path` is (a string node, which has no object to change in place).
    private static void Replace(JsonObject working, string path, JsonNode? value)
    {
        var parts = RecordPath.Parse(path)!;
        var container = RecordPath.Get(working, parts.Take(parts.Count - 1));
        var last = parts[^1];
        if (container is JsonArray array && last.Name == null) array[last.Index] = value;
        else if (container is JsonObject obj && last.Name != null) Put(obj, last.Name, value);
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    private static JsonObject At(JsonObject root, string path) => (JsonObject)RecordPath.Get(root, RecordPath.Parse(path)!)!;

    private JsonObject Clone() => (JsonObject)Document.Working.DeepClone();

    private bool Commit(JsonObject after, string description)
    {
        // Compared as text: DeepEquals ignores the order of an object's keys, and a move is only that.
        if (after.ToJsonString() == Document.Working.ToJsonString()) return false;
        Document.History.Execute(new ReplaceRecordJson(Document, after, description));
        return true;
    }

    private bool Refuse(string why)
    {
        Log.Warn(LogCat.Editor, $"{Document.Type} {Document.Id}: {why}");
        return false;
    }

    // Every node inside `node`, at any depth.
    private IEnumerable<AIGraphNode> Below(AIGraphNode node)
    {
        var children = new Queue<string>(node.Children);
        while (children.Count > 0)
        {
            string path = children.Dequeue();
            var child = Nodes.First(n => n.Path == path);
            yield return child;
            foreach (var c in child.Children) children.Enqueue(c);
        }
    }

    // The object whose `states` hold `node`: the record for a top-level state, else its parent state.
    private JsonObject OwnerOf(JsonObject working, AIGraphNode node) =>
        node.Parent == null ? working : (JsonObject)At(working, node.Parent)!;

    // Takes a state out of its owner's `states`, fixing the owner's `initial` (and dropping an emptied
    // `states` with it, so a parent with nothing in it is a plain state again).
    private static void Detach(JsonObject owner, JsonObject record, string name)
    {
        var container = Container(owner, create: false)!;
        container.Remove(KeyOf(container, name)!);
        bool named = string.Equals(Text(Get(owner, InitialKey)), name, StringComparison.OrdinalIgnoreCase);
        if (container.Count == 0 && owner != record)
        {
            owner.Remove(KeyOf(owner, StatesKey)!);
            if (KeyOf(owner, InitialKey) is { } k) owner.Remove(k);
        }
        else if (named) Put(owner, InitialKey, container.Count > 0 ? container.First().Key : "");
    }

    private static IEnumerable<JsonObject> States(JsonObject owner)
    {
        if (Container(owner, create: false) is not { } states) yield break;
        foreach (var (_, value) in states)
        {
            if (value is not JsonObject state) continue;
            yield return state;
            foreach (var inner in States(state)) yield return inner;
        }
    }

    private static void DropTransitionsTo(JsonObject owner, HashSet<string> gone)
    {
        if (Get(owner, TransitionsKey) is not JsonArray transitions) return;
        for (int i = transitions.Count - 1; i >= 0; i--)
            if (transitions[i] is JsonObject t && gone.Contains(Text(Get(t, "to")))) transitions.RemoveAt(i);
    }

    private static void RenameReferences(JsonObject owner, string from, string to)
    {
        if (string.Equals(Text(Get(owner, InitialKey)), from, StringComparison.OrdinalIgnoreCase)) Put(owner, InitialKey, to);
        if (Get(owner, TransitionsKey) is not JsonArray transitions) return;
        foreach (var item in transitions)
            if (item is JsonObject t && string.Equals(Text(Get(t, "to")), from, StringComparison.OrdinalIgnoreCase)) Put(t, "to", to);
    }

    private static bool IsParallel(JsonObject state) => Get(state, ParallelKey) is JsonValue v && v.TryGetValue(out bool b) && b;

    private static JsonObject? Container(JsonObject owner, bool create)
    {
        if (Get(owner, StatesKey) is JsonObject states) return states;
        if (!create) return null;
        var made = new JsonObject();
        Put(owner, StatesKey, made);
        return made;
    }

    private JsonArray? List(JsonObject record, bool create)
    {
        string key = Kind == AIGraphKind.Schedule ? "tasks" : "entries";
        if (Get(record, key) is JsonArray list) return list;
        if (!create) return null;
        var made = new JsonArray();
        Put(record, key, made);
        return made;
    }

    // Puts `name` into an object at `index` (-1 or past the end: last), keeping the others' order.
    private static void Insert(JsonObject container, string name, JsonNode? value, int index)
    {
        var pairs = container.Select(kv => (kv.Key, Value: kv.Value?.DeepClone())).ToList();
        if (index < 0 || index > pairs.Count) index = pairs.Count;
        pairs.Insert(index, (name, value));
        container.Clear();
        foreach (var (k, v) in pairs) container[k] = v;
    }

    // Names are matched ignoring case, as the record loader matches them.
    private static string? KeyOf(JsonObject obj, string name)
    {
        foreach (var (key, _) in obj)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return key;
        return null;
    }

    private static JsonNode? Get(JsonObject obj, string name) => KeyOf(obj, name) is { } key ? obj[key] : null;

    private static void Put(JsonObject obj, string name, JsonNode? value) => obj[KeyOf(obj, name) ?? name] = value;

    private static string Text(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out string? s) ? s : node?.ToJsonString() ?? "";

    private static string ChildPath(string parent, string name) => RecordDocument.ChildPath(parent, name);
}

// One edit of the AI graph view: the record's JSON as it is after the edit, and as it was. Whole, because
// one gesture (remove a state) can change several places of the record (its transitions, an initial).
internal sealed class ReplaceRecordJson : IEditorCommand
{
    private readonly RecordDocument _document;
    private readonly JsonObject _after;
    private JsonObject? _before;

    public ReplaceRecordJson(RecordDocument document, JsonObject after, string description)
    {
        _document = document;
        _after = after;
        Description = $"{document.Id.Name}: {description}";
    }

    public string Description { get; }

    public void Do()
    {
        _before ??= (JsonObject)_document.Working.DeepClone();
        Fill(_after);
    }

    public void Undo() => Fill(_before!);

    private void Fill(JsonObject from)
    {
        var working = _document.Working;
        working.Clear();
        foreach (var (key, value) in from) working[key] = value?.DeepClone();
    }
}
