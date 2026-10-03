#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace Sage.Editing;

// An input a target takes (issue #225): its name, and which components of the target answer it (empty
// when only a global handler does: `Kill`, `Say`, `Fire`, which any entity takes).
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public readonly record struct WireInput(string Name, IReadOnlyList<string> Components, bool Global);

// An output a wire may listen for: one the engine or a plugin declared (`Description` says what it means),
// or a name some wire of the document already uses (`Declared` false). Nothing lists the outputs one
// component can fire, so an output is a free name with these suggestions beside it.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public readonly record struct WireOutput(string Name, string Description, bool Declared);

// One wire as the viewport draws it: from the placement's position to its target's, in origin space.
// `To` is null when the target is not in the world (a name nothing has) or is `!self` and the like.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public readonly record struct WireLine(Placement Source, int Index, Connection Wire, Vector3 From, Vector3? To);

// One wire into or out of the entity the link view is about (issue #276): who sends it, which wire of
// theirs it is, and every entity it reaches now with where each stands — one for a name, each member for
// a group (`@lamps`), none when the target is not in the world (drawn red). `Problem` says why a wire
// cannot work (an input the target does not take, an empty group), or is null.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed record WireLink(Entity Source, int Index, Connection Wire, Vector3 From,
                              IReadOnlyList<Entity> Targets, IReadOnlyList<Vector3> To, string? Problem)
{
    public bool Resolved => Targets.Count > 0;
}

// The link view of one entity (issue #276, 04 §9 "I/O links drawn between entities"): the wires that
// leave it and the wires that reach it, from anything in the world that has wires (a placement, a map
// entity), with their targets resolved the way the dispatch would resolve them now.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed record LinkView(Entity Entity, IReadOnlyList<WireLink> Outgoing, IReadOnlyList<WireLink> Incoming);

// What the I/O panel shows for one placement (issue #225): its wires, the outputs it may name, and, for a
// target, the inputs it takes. Wires reference their target by *name* (that is what a wire stores), so a
// target is a placement with a name, or any entity of the world that has one.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class WiringModel
{
    public WiringModel(EditDocument document, Placement placement)
    {
        Document = document;
        Placement = placement;
    }

    public EditDocument Document { get; }
    public Placement Placement { get; }

    public IReadOnlyList<Connection> Wires => Placement.Outputs;

    // The declared outputs, then the names the document's wires use that nothing declared, each sorted.
    public IReadOnlyList<WireOutput> Outputs()
    {
        var outputs = Document.Engine.Outputs;
        var list = outputs.Names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new WireOutput(n, outputs.Describe(n) ?? "", true)).ToList();
        foreach (var name in Document.Placements.SelectMany(p => p.Outputs).Select(w => w.Output)
                     .Where(n => n.Length > 0 && !outputs.Has(n)).Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            list.Add(new WireOutput(name, "used by a wire in this document", false));
        return list;
    }

    // The inputs the entity a name stands for takes, by name; empty when nothing of that name is in the world.
    public IReadOnlyList<WireInput> InputsOf(string target) =>
        Wiring.TargetEntity(Document, target) is { IsNull: false } entity ? Wiring.InputsOf(Document.Engine, entity) : Array.Empty<WireInput>();

    // Where each wire of the document runs, for the lines drawn between wired entities: one line per
    // entity it reaches, so a wire to a group (`@lamps`) fans out to every member (issue #276), and a
    // line with no `To` for a wire whose target is not in the world.
    public static IReadOnlyList<WireLine> Lines(EditDocument document)
    {
        var lines = new List<WireLine>();
        if (!document.IsOpen) return lines;
        var members = new List<Entity>();
        foreach (var placement in document.Placements)
        {
            if (placement.Outputs.Count == 0) continue;
            var from = ViewportTools.OriginOf(document, placement);
            for (int i = 0; i < placement.Outputs.Count; i++)
            {
                var wire = placement.Outputs[i];
                if (!IOTargets.IsSelector(wire.Target))
                {
                    lines.Add(new WireLine(placement, i, wire, from, Wiring.PositionOf(document, wire.Target)));
                    continue;
                }
                members.Clear();
                IOTargets.Members(document.World, wire.Target, members);
                if (members.Count == 0) lines.Add(new WireLine(placement, i, wire, from, null));
                foreach (var member in members) lines.Add(new WireLine(placement, i, wire, from, Wiring.PositionOf(document, member)));
            }
        }
        return lines;
    }

    // The link view of a placement's entity (issue #276).
    public static LinkView Links(EditDocument document, Placement placement) => Links(document, document.EntityOf(placement));

    // The wires that leave `entity` and the wires that reach it, from every wired entity in the document's
    // world. A wire reaches it when its target names it, or is a group it is in.
    public static LinkView Links(EditDocument document, Entity entity)
    {
        var outgoing = new List<WireLink>();
        var incoming = new List<WireLink>();
        if (entity.IsNull || !document.World.IsAlive(entity)) return new LinkView(entity, outgoing, incoming);

        foreach (var source in document.World.Query<IOConnections>().Entities.ToEntityList())
        {
            var wires = source.GetComponent<IOConnections>().Wires;
            if (wires == null) continue;
            for (int i = 0; i < wires.Length; i++)
            {
                bool mine = source == entity;
                var targets = Wiring.TargetsOf(document, source, wires[i].Target);
                bool reaches = targets.Contains(entity);
                if (!mine && !reaches) continue;
                var link = new WireLink(source, i, wires[i], Wiring.PositionOf(document, source), targets,
                                        targets.Select(t => Wiring.PositionOf(document, t)).ToList(),
                                        Wiring.Check(document, wires[i].Target, wires[i].Input));
                if (mine) outgoing.Add(link);
                if (reaches) incoming.Add(link);
            }
        }
        return new LinkView(entity, outgoing, incoming);
    }

    // The last inputs sent to or from `entity` in `world` (the play world, while playing: the edit world
    // never ticks), newest first, from its entity I/O history.
    public static IReadOnlyList<IORecord> Recent(World world, Entity entity, int max = 16)
    {
        var list = new List<IORecord>();
        if (!entity.IsNull && world.Resources.TryGet<EntityIO>(out var io) && io != null) io.HistoryOf(entity, list, max);
        return list;
    }
}

// Editing wires (issue #225): each a `SetOutputs`, so each is one undo step and the placement's entity is
// re-spawned with the new wiring. A target is named, because a wire stores a name.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public static class Wiring
{
    // `!self`, `!activator` and `!caller` name whoever the wire is about when it fires: nothing to check.
    public static bool IsSpecial(string target) => target.StartsWith('!');

    // The entity a wire's target name means now: the document's placement of that name, else any entity of that name.
    public static Entity TargetEntity(EditDocument document, string target)
    {
        if (target.Length == 0 || IsSpecial(target) || IOTargets.IsSelector(target)) return default;
        if (document.Find(target) is { } placement) return document.EntityOf(placement);
        return document.World.FindByName(target);
    }

    internal static Vector3? PositionOf(EditDocument document, string target)
    {
        if (target.Length == 0 || IsSpecial(target) || IOTargets.IsSelector(target)) return null;
        if (document.Find(target) is { } placement) return ViewportTools.OriginOf(document, placement);
        var entity = document.World.FindByName(target);
        return !entity.IsNull && entity.TryGetComponent<GlobalTransform>(out var global) ? global.Current.Position : null;
    }

    // Where an entity stands: its placement's position, else its world transform.
    internal static Vector3 PositionOf(EditDocument document, Entity entity)
    {
        if (document.PlacementOf(entity) is { } placement) return ViewportTools.OriginOf(document, placement);
        if (entity.TryGetComponent<GlobalTransform>(out var global)) return global.Current.Position;
        return entity.TryGetComponent<Transform>(out var local) ? local.LocalPosition : Vector3.Zero;
    }

    // The entities a wire on `source` reaches now: `!self` is the source, a group every member, a name
    // the placement or entity of that name; `!activator` and `!caller` none (they are about a firing).
    public static IReadOnlyList<Entity> TargetsOf(EditDocument document, Entity source, string target)
    {
        var list = new List<Entity>();
        if (target.Length == 0) return list;
        if (IsSpecial(target))
        {
            if (string.Equals(target, "!self", StringComparison.OrdinalIgnoreCase)) list.Add(source);
            return list;
        }
        if (IOTargets.IsSelector(target)) { IOTargets.Members(document.World, target, list); return list; }
        if (TargetEntity(document, target) is { IsNull: false } entity) list.Add(entity);
        return list;
    }

    // What `entity` takes: every input with a handler on one of its components, and the global ones.
    public static IReadOnlyList<WireInput> InputsOf(Engine engine, Entity entity)
    {
        var inputs = engine.Inputs;
        var list = new List<WireInput>();
        foreach (var name in inputs.Names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            if (inputs.Takes(entity, name))
                list.Add(new WireInput(name, inputs.ComponentsTaking(name), inputs.TryGet(name, out _)));
        return list;
    }

    // Whether the wire could work: null, or why not (naming what the target does take). A name only
    // content listens for (a state machine's `on`) is accepted, as nothing here can say who hears it.
    public static string? Check(EditDocument document, string target, string input)
    {
        if (input.Length == 0) return "no input named";
        if (target.Length == 0) return "no target named";
        if (IsSpecial(target)) return document.Engine.Inputs.Has(input) ? null : $"nothing takes '{input}' (io_list lists the inputs)";
        if (IOTargets.IsSelector(target))
        {
            // A group (issue #276): it must say something, and what it sends must be taken by a member.
            if (IOTargets.Problem(document.Engine, target) is { } problem) return problem;
            if (!document.Engine.Inputs.Has(input)) return $"nothing takes '{input}' (io_list lists the inputs)";
            var members = new List<Entity>();
            IOTargets.Members(document.World, target, members);
            if (members.Count == 0) return $"nothing in the world is in '{target}' yet (give entities that group, or check the name)";
            if (!members.Any(m => document.Engine.Inputs.Takes(m, input))
                && document.Engine.Inputs.Names.Contains(input, StringComparer.OrdinalIgnoreCase))
                return $"none of the {members.Count} member(s) of '{target}' takes '{input}'";
            return null;
        }
        var entity = TargetEntity(document, target);
        if (entity.IsNull) return $"no placement or entity called '{target}' (a wire finds its target by name)";
        var inputs = document.Engine.Inputs;
        if (inputs.Takes(entity, input)) return null;
        if (inputs.Has(input) && !inputs.Names.Contains(input, StringComparer.OrdinalIgnoreCase)) return null;
        var takes = InputsOf(document.Engine, entity).Select(i => i.Name).ToList();
        return $"'{target}' does not take '{input}'; it takes: {(takes.Count == 0 ? "nothing" : string.Join(", ", takes))}";
    }

    // The name a placement can be wired to by: its own, else null (a wire cannot refer to it).
    public static string? NameOf(Placement placement) => placement.Name.Length > 0 ? placement.Name : null;

    // Gives a nameless placement a name from its prefab (`post` → `post`, or `post_2`): one undo step.
    public static string NameIt(EditDocument document, Placement placement)
    {
        if (placement.Name.Length > 0) return placement.Name;
        string name = ViewportTools.UniqueName(document, placement.Prefab.Id.Name);
        document.History.EndMerge();
        document.Execute(new SetPlacement(document, placement, PlacementFields.Of(placement) with { Name = name }));
        return name;
    }

    // Appends `wire` to the placement's outputs; false, with the reason, when it could not work.
    public static bool Add(EditDocument document, Placement source, Connection wire, [NotNullWhen(false)] out string? error)
    {
        error = Check(document, wire.Target, wire.Input);
        if (error == null && wire.Output.Length == 0) error = "no output named";
        if (error != null) return false;
        var wires = source.Outputs.ToList();
        wires.Add(wire);
        Set(document, source, wires);
        return true;
    }

    // Replaces wire `index`; false when there is none or the new one could not work.
    public static bool Update(EditDocument document, Placement source, int index, Connection wire, [NotNullWhen(false)] out string? error)
    {
        error = null;
        if (index < 0 || index >= source.Outputs.Count) error = $"no wire {index + 1}";
        else error = Check(document, wire.Target, wire.Input) ?? (wire.Output.Length == 0 ? "no output named" : null);
        if (error != null) return false;
        var wires = source.Outputs.ToList();
        wires[index] = wire;
        Set(document, source, wires);
        return true;
    }

    // Removes the wires at `indices` in one step; how many went.
    public static int Remove(EditDocument document, Placement source, params int[] indices)
    {
        var drop = indices.Where(i => i >= 0 && i < source.Outputs.Count).ToHashSet();
        if (drop.Count == 0) return 0;
        Set(document, source, source.Outputs.Where((_, i) => !drop.Contains(i)).ToList());
        return drop.Count;
    }

    private static void Set(EditDocument document, Placement source, List<Connection> wires)
    {
        document.History.EndMerge();
        document.Execute(new SetOutputs(document, source, wires));
    }

    // A wire as one line of text, as `ed_wires` lists it.
    public static string Describe(Connection wire) =>
        $"{wire.Output} -> {wire.Target}.{wire.Input}"
        + (wire.Parameter.Length > 0 ? $" \"{wire.Parameter}\"" : "")
        + (wire.Delay > 0 ? $" after {wire.Delay.ToString("0.###", CultureInfo.InvariantCulture)}s" : "")
        + (wire.Times >= 0 ? $" x{wire.Times}" : "")
        + (wire.Requires != null ? $" requires {wire.Requires.GetType().Name}" : "");

    // ---- The console ---------------------------------------------------------------------------------

    public static void Register(CVarRegistry cvars, Func<EditDocument?> document)
    {
        cvars.RegisterCommand("ed_wire", CVarFlags.DevOnly,
            "ed_wire <from> <output> <to> <input> [delay] [value]: wire a placement's output to a named target's input; one undo.", a =>
        {
            if (!Open(document, a.Name, out var doc)) return;
            if (a.Count < 4) { Log.Warn(LogCat.Console, "ed_wire <from> <output> <to> <input> [delay] [value]"); return; }
            if (doc.Find(a[0]) is not { } source) { Log.Warn(LogCat.Console, $"ed_wire: no placement called '{a[0]}' in {doc.Id}"); return; }
            float delay = 0f;
            int next = 4;
            if (a.Count > next)
            {
                if (!float.TryParse(a[next], NumberStyles.Float, CultureInfo.InvariantCulture, out delay) || delay < 0f)
                { Log.Warn(LogCat.Console, "ed_wire: the delay is seconds, 0 or more"); return; }
                next++;
            }
            string value = string.Join(' ', Enumerable.Range(next, Math.Max(a.Count - next, 0)).Select(i => a[i]));
            var wire = new Connection { Output = a[1], Target = a[2], Input = a[3], Delay = delay, Parameter = value };
            if (!Add(doc, source, wire, out var error)) { Log.Warn(LogCat.Console, $"ed_wire: {error}"); return; }
            Log.Info(LogCat.Console, $"wired {AddPlacement.Label(source)}: {Describe(wire)}");
        });

        cvars.RegisterCommand("ed_unwire", CVarFlags.DevOnly,
            "ed_unwire <from> <index|output>: remove a placement's wire by its number in ed_wires, or every wire of an output; one undo.", a =>
        {
            if (!Open(document, a.Name, out var doc)) return;
            if (a.Count < 2) { Log.Warn(LogCat.Console, "ed_unwire <from> <index|output>"); return; }
            if (doc.Find(a[0]) is not { } source) { Log.Warn(LogCat.Console, $"ed_unwire: no placement called '{a[0]}' in {doc.Id}"); return; }
            int[] drop = int.TryParse(a[1], NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                ? new[] { number - 1 }
                : Enumerable.Range(0, source.Outputs.Count)
                    .Where(i => string.Equals(source.Outputs[i].Output, a[1], StringComparison.OrdinalIgnoreCase)).ToArray();
            int removed = Remove(doc, source, drop);
            if (removed == 0) Log.Warn(LogCat.Console, $"ed_unwire: {AddPlacement.Label(source)} has no wire '{a[1]}' (ed_wires {AddPlacement.Label(source)})");
            else Log.Info(LogCat.Console, $"removed {removed} wire(s) from {AddPlacement.Label(source)}");
        });

        cvars.RegisterCommand("ed_wires", CVarFlags.None, "ed_wires <name>: a placement's wires, numbered.", a =>
        {
            if (!Open(document, a.Name, out var doc)) return;
            if (a.Count == 0) { Log.Warn(LogCat.Console, "ed_wires <name>"); return; }
            if (doc.Find(a.Rest) is not { } source) { Log.Warn(LogCat.Console, $"ed_wires: no placement called '{a.Rest}' in {doc.Id}"); return; }
            if (source.Outputs.Count == 0) { Log.Info(LogCat.Console, $"{AddPlacement.Label(source)} has no wires"); return; }
            for (int i = 0; i < source.Outputs.Count; i++)
                Log.Info(LogCat.Console, $"  {i + 1}. {Describe(source.Outputs[i])}");
        });
    }

    private static bool Open(Func<EditDocument?> document, string command, [NotNullWhen(true)] out EditDocument? doc)
    {
        doc = document();
        if (doc == null) { Log.Warn(LogCat.Console, "no world yet: the editor's commands work once a world exists"); return false; }
        if (!doc.IsOpen) { Log.Warn(LogCat.Console, $"{command}: no document is open"); return false; }
        return true;
    }
}
