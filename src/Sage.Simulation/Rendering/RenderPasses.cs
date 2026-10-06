#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Sage.Simulation;

// Where in a frame a render pass draws (REDESIGN §4.7, issue 4h-1). In this order:
//   Shadow                once per world, before any view (4h-4's sun shadow map);
//   Opaque .. Debug       once per view, in the view's target and viewport;
//   PostProcess           once, on the screen, after every view (4h-6);
//   Overlay               once, on the screen, in the Overlay phase: the game's UI (`sage:ui`) and
//                         anything drawn over or under it.
// The four scene stages each draw one run of the view's items and sprites, the run whose sort key
// starts with `RenderStages.SortKeyPass` (06 §3.5): the key already reserved slot 2 for the sky.
[Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public enum RenderStage
{
    Shadow,
    Opaque,
    AlphaTested,
    Sky,
    Transparent,
    Debug,
    PostProcess,
    Overlay,
}

[Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public static class RenderStages
{
    public const int Count = (int)RenderStage.Overlay + 1;

    // Drawn once per view (Opaque to Debug), rather than once per frame.
    public static bool IsPerView(RenderStage stage) => stage is >= RenderStage.Opaque and <= RenderStage.Debug;

    // The top four bits of the sort keys of the items and sprites this stage draws (06 §3.5), or -1
    // for a stage that draws no run of the snapshot.
    public static int SortKeyPass(RenderStage stage) => stage switch
    {
        RenderStage.Opaque => 0,
        RenderStage.AlphaTested => 1,
        RenderStage.Sky => 2,
        RenderStage.Transparent => 3,
        _ => -1,
    };

    // The stage a material's pass draws in.
    public static RenderStage Of(RenderPass pass) => pass switch
    {
        RenderPass.Opaque => RenderStage.Opaque,
        RenderPass.AlphaTested => RenderStage.AlphaTested,
        _ => RenderStage.Transparent,
    };
}

// Declares a render pass, the way [System] declares a system: a stable id ("ns:name"), the stage it
// draws in, and the passes of the same stage it draws after or before ("?id" for one from a plugin
// that may not be installed). Added in a module's Init (`RenderPassRegistry.Add`) and ordered when the
// registry is sealed; passes of one stage that nothing orders keep the order they were added in.
[Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RenderPassAttribute : Attribute
{
    public RenderPassAttribute(string id, RenderStage stage)
    {
        Id = id;
        Stage = stage;
    }

    public string Id { get; }
    public RenderStage Stage { get; }
    public string[] After { get; set; } = Array.Empty<string>();
    public string[] Before { get; set; } = Array.Empty<string>();
}

// One pass as the registry knows it: what `r_passes` lists.
[Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public sealed class RenderPassInfo
{
    internal RenderPassInfo(string id, RenderStage stage, IReadOnlyList<string> after, IReadOnlyList<string> before, Type type, int registration,
                            Type? replaces = null, string? by = null)
    {
        Replaces = replaces;
        By = by;
        Id = id;
        Stage = stage;
        After = after;
        Before = before;
        Type = type;
        Registration = registration;
    }

    public string Id { get; }
    public RenderStage Stage { get; }
    public IReadOnlyList<string> After { get; }
    public IReadOnlyList<string> Before { get; }
    public Type Type { get; }
    internal int Registration { get; }

    // Set when this pass took the place of another under the same id (`Replace`): the type it replaced,
    // and who asked (a plugin or assembly name). For a disabled pass (`RenderPassRegistry.Disabled`), By is who switched it off.
    public Type? Replaces { get; }
    public string? By { get; }
}

// The render passes, in the order they draw (REDESIGN §4.7, issue 4h-1). Headless — the client's
// `RenderPasses` is this over its `IRenderPass` — so the ordering is what the tests prove and the
// renderer only walks `In(stage)`.
//
// Mistakes are load errors, each at the line or the stage that makes them:
//   - a pass without [RenderPass], or an id already taken: Add throws;
//   - adding after the seal (the client seals it in its Start): Add throws (RegistrationSeal);
//   - `Replace(id, pass)` swaps the pass registered under `id` for another in the same stage (it keeps the
//     id, so the passes ordered against it stay ordered); `Disable(id)` leaves it out. Both are applied at
//     the seal, so a plugin may ask before the pass it names is added. Two requests for one id (two
//     replacements, or a replacement and a disable), an id nothing added, or a replacement in another
//     stage are load errors naming who asked. A constraint that names a disabled pass is dropped.
//   - at the seal: an After/Before naming an id nothing added (unless written "?id"), naming a pass in
//     another stage (the stage order already decides it, as with systems' phases), or a cycle: Seal
//     throws, naming the passes.
[Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public class RenderPassRegistry<TPass> where TPass : class
{
    private readonly List<(RenderPassInfo Info, TPass Pass)> _added = new();
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
    private readonly TPass[][] _stages = new TPass[RenderStages.Count][];
    private RenderPassInfo[] _ordered = Array.Empty<RenderPassInfo>();
    private readonly List<Override> _overrides = new();
    private readonly HashSet<string> _disabledIds = new(StringComparer.Ordinal);
    private RenderPassInfo[] _disabled = Array.Empty<RenderPassInfo>();

    private readonly record struct Override(string Id, TPass? Replacement, string By);

    public RenderPassRegistry()
    {
        for (int i = 0; i < _stages.Length; i++) _stages[i] = Array.Empty<TPass>();
    }

    private readonly RegistrationSeal _seal = new("render pass", "the renderer has already ordered its passes without it");

    public bool IsSealed => _seal.IsSealed;

    // Every pass in draw order (stage, then constraints, then the order added). Empty until sealed.
    public IReadOnlyList<RenderPassInfo> Ordered => _ordered;

    // A stage's passes in draw order. Empty until sealed.
    public ReadOnlySpan<TPass> In(RenderStage stage) => _stages[(int)stage];

    public int Count => _added.Count;

    // The passes switched off (`Disable`). Empty until sealed.
    public IReadOnlyList<RenderPassInfo> Disabled => _disabled;

    // Swaps the pass registered under `id` (an engine pass, say "sage:transparent") for `pass`, which must
    // draw in the same stage. It takes the id and the old pass's After/Before as well as its own, so
    // everything ordered against the old one stays ordered. `by` names who asks (default: the pass's assembly).
    public void Replace(string id, TPass pass, string? by = null)
    {
        ArgumentNullException.ThrowIfNull(pass);
        by ??= pass.GetType().Assembly.GetName().Name ?? "?";
        _seal.Check(id);
        Claim(id, by);
        _overrides.Add(new Override(id, pass, by));
    }

    // Leaves the pass registered under `id` out of every frame. `by` names who asks.
    public void Disable(string id, string by)
    {
        ArgumentException.ThrowIfNullOrEmpty(by);
        _seal.Check(id);
        Claim(id, by);
        _overrides.Add(new Override(id, default, by));
    }

    private void Claim(string id, string by)
    {
        foreach (var o in _overrides)
            if (o.Id == id)
                throw new InvalidOperationException(
                    $"Render pass '{id}' is already {(o.Replacement != null ? "replaced" : "disabled")} by {o.By}; {by} cannot also change it. " +
                    "One module owns a pass: drop one of the two.");
    }

    // Applies the replacements and disables to what was added. Errors name who asked.
    private void ApplyOverrides()
    {
        var disabled = new List<RenderPassInfo>();
        foreach (var o in _overrides)
        {
            if (!_ids.TryGetValue(o.Id, out int at))
                throw new InvalidOperationException(
                    $"{o.By} {(o.Replacement != null ? "replaces" : "disables")} render pass '{o.Id}', which no module added: a typo, or a plugin that is not installed.");
            var (old, _) = _added[at];
            if (o.Replacement is { } pass)
            {
                var type = pass.GetType();
                var declared = type.GetCustomAttribute<RenderPassAttribute>(inherit: false)
                    ?? throw new InvalidOperationException($"Render pass {type.Name} has no [RenderPass(\"ns:id\", RenderStage.X)]: declare its stage.");
                if (declared.Stage != old.Stage)
                    throw new InvalidOperationException(
                        $"{o.By} replaces render pass '{o.Id}' ({old.Stage}) with {type.Name}, which draws in {declared.Stage}. A replacement draws in the stage of the pass it replaces.");
                _added[at] = (new RenderPassInfo(o.Id, old.Stage, Union(old.After, declared.After), Union(old.Before, declared.Before), type, old.Registration, old.Type, o.By), pass);
            }
            else
            {
                disabled.Add(new RenderPassInfo(old.Id, old.Stage, old.After, old.Before, old.Type, old.Registration, null, o.By));
                _disabledIds.Add(o.Id);
                _added[at] = default;
            }
        }
        if (disabled.Count > 0)
        {
            _added.RemoveAll(e => e.Info == null);
            _ids.Clear();
            for (int i = 0; i < _added.Count; i++) _ids[_added[i].Info.Id] = i;
        }
        _disabled = disabled.ToArray();
    }

    private static string[] Union(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var list = new List<string>(a);
        foreach (var x in b) if (!list.Contains(x)) list.Add(x);
        return list.ToArray();
    }

    public RenderPassInfo Add(TPass pass)
    {
        ArgumentNullException.ThrowIfNull(pass);
        var type = pass.GetType();
        var declared = type.GetCustomAttribute<RenderPassAttribute>(inherit: false)
            ?? throw new InvalidOperationException($"Render pass {type.Name} has no [RenderPass(\"ns:id\", RenderStage.X)]: declare its id and stage.");
        string id = declared.Id;
        _seal.Check(id);
        if (string.IsNullOrWhiteSpace(id) || id.Contains(' '))
            throw new InvalidOperationException($"Render pass {type.Name} has the id '{id}'; a pass id is \"ns:name\" with no spaces.");
        if (!Enum.IsDefined(declared.Stage))
            throw new InvalidOperationException($"Render pass '{id}' names stage {(int)declared.Stage}, which is not a RenderStage.");
        if (_ids.TryGetValue(id, out int taken))
            throw new InvalidOperationException($"Two render passes claim the id '{id}': {_added[taken].Info.Type.Name} and {type.Name}.");

        var info = new RenderPassInfo(id, declared.Stage, declared.After, declared.Before, type, _added.Count);
        _ids[id] = _added.Count;
        _added.Add((info, pass));
        return info;
    }

    public bool Has(string id) => _ids.ContainsKey(id);

    // Closes the registry and orders it. `stage` says when, for a late Add's message.
    public void Seal(string stage)
    {
        if (_seal.IsSealed) return;
        ApplyOverrides();
        var ordered = new List<(RenderPassInfo Info, TPass Pass)>(_added.Count);
        for (int s = 0; s < RenderStages.Count; s++)
        {
            var inStage = new List<(RenderPassInfo Info, TPass Pass)>();
            foreach (var entry in _added)
                if ((int)entry.Info.Stage == s) inStage.Add(entry);
            var sorted = Sort(inStage, (RenderStage)s);
            _stages[s] = sorted.ConvertAll(e => e.Pass).ToArray();
            ordered.AddRange(sorted);
        }
        _ordered = ordered.ConvertAll(e => e.Info).ToArray();
        _seal.Seal(stage);
    }

    private static string Strip(string name) => name.StartsWith('?') ? name[1..] : name;

    // A stable topological sort of one stage: constraints first, the order added otherwise.
    private List<(RenderPassInfo Info, TPass Pass)> Sort(List<(RenderPassInfo Info, TPass Pass)> passes, RenderStage stage)
    {
        var edges = new List<int>[passes.Count];
        var incoming = new int[passes.Count];
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < passes.Count; i++)
        {
            edges[i] = new List<int>();
            index[passes[i].Info.Id] = i;
        }

        for (int i = 0; i < passes.Count; i++)
        {
            var info = passes[i].Info;
            foreach (string name in info.After)
                if (Resolve(info, name, "after", stage, index) is int from && from != i) { edges[from].Add(i); incoming[i]++; }
            foreach (string name in info.Before)
                if (Resolve(info, name, "before", stage, index) is int to && to != i) { edges[i].Add(to); incoming[to]++; }
        }

        var result = new List<(RenderPassInfo Info, TPass Pass)>(passes.Count);
        var ready = new SortedSet<int>();   // indices are the order added
        for (int i = 0; i < passes.Count; i++) if (incoming[i] == 0) ready.Add(i);
        while (ready.Count > 0)
        {
            int next = ready.Min;
            ready.Remove(next);
            result.Add(passes[next]);
            foreach (int to in edges[next])
                if (--incoming[to] == 0) ready.Add(to);
        }

        if (result.Count != passes.Count)
        {
            var cycle = new List<string>();
            for (int i = 0; i < passes.Count; i++) if (incoming[i] > 0) cycle.Add(passes[i].Info.Id);
            throw new InvalidOperationException($"Render pass ordering cycle in stage {stage}: {string.Join(", ", cycle)}.");
        }
        return result;
    }

    // The index in this stage of the pass `name` names, or null when it is a soft dependency that is
    // not there. Anything else is an error.
    private int? Resolve(RenderPassInfo pass, string name, string relation, RenderStage stage, Dictionary<string, int> index)
    {
        string id = Strip(name);
        if (index.TryGetValue(id, out int at)) return at;
        if (_ids.TryGetValue(id, out int other))
            throw new InvalidOperationException(
                $"Render pass '{pass.Id}' ({stage}) draws {relation} '{id}', which draws in {_added[other].Info.Stage}. " +
                "After/Before orders passes within one stage; the stage order already decides this one.");
        if (name.StartsWith('?') || _disabledIds.Contains(id)) return null;   // a disabled pass constrains nothing
        throw new InvalidOperationException(
            $"Render pass '{pass.Id}' draws {relation} '{id}', which no module added: a typo, or a plugin that is not installed " +
            $"(write \"?{id}\" if that plugin is optional).");
    }
}
