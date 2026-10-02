#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sage.Simulation;

// What a placement said, kept by an entity that no content places any more (issue #279, phase 4m-5).
//
// A placed entity is content's: a save writes it with its `source`, and a load (or a sector waking) places
// the placement again — prefab, `overrides` and `outputs` — and lays the saved state over it. One that
// walked out of its sector (ContentBaseline.Leave) is a runtime spawn from then on, rebuilt from its prefab
// alone; until this its placement's overrides (a part's options — a `state_machine`'s `machine`, a
// collider's radius — are not state, so the diff never had them) and its wires (IOConnections is
// transient: content says it again) were lost the first time it slept or a save was loaded.
//
// So a root that no content places writes what it was placed with beside its diff:
//
//   { "id": "…", "prefab": "game:house", "overrides": { "parts": { "state_machine": { "machine": "door_b" } } },
//     "outputs": [ { "Output": "OnStateChanged", "Target": "bell", "Input": "Ring", "fired": 1 } ],
//     "diff": true, "components": { … } }
//
// and a load or a waking cell spawns it from its prefab *with* those overrides — so its diff is laid over
// the baseline it was taken against — and wires it again. A wire's `fired` count goes with it, so a wire
// with `times` that fired while its entity slept in a dormant cell (where the `entity_io` resource cannot
// see it) still knows. Its prefab's children come back with it as they always did, by their derived ids.
// A runtime spawn the game made with overrides (World.Spawn(prefab, …, overrides)) keeps them the same way.
internal static class KeptPlacement
{
    public const string OverridesKey = "overrides";
    public const string OutputsKey = "outputs";
    private const string FiredKey = "fired";

    // Beside a saved root that no content places: its placement's overrides and wires, when it has any.
    public static void Write(World world, Entity entity, JsonObject saved, JsonSerializerOptions records)
    {
        if (world.TryGet<PrefabOverridden>(entity, out var overridden) && overridden.Overrides is { IsEmpty: false } overrides)
        {
            var body = new JsonObject();
            if (overrides.Components is { Count: > 0 }) body["components"] = overrides.Components.DeepClone();
            if (overrides.Parts is { Count: > 0 }) body["parts"] = overrides.Parts.DeepClone();
            saved[OverridesKey] = body;
        }

        if (world.TryGet<IOConnections>(entity, out var io) && io.Wires is { Length: > 0 } wires)
        {
            var list = new JsonArray();
            foreach (var wire in wires)
            {
                if (wire == null) continue;
                if (JsonSerializer.SerializeToNode(wire, records) is not JsonObject written) continue;
                if (wire.Fired > 0) written[FiredKey] = wire.Fired;
                list.Add(written);
            }
            if (list.Count > 0) saved[OutputsKey] = list;
        }
    }

    // The overrides a saved runtime spawn was placed with; null for none.
    public static PrefabOverrides? Overrides(JsonObject saved)
    {
        if (saved[OverridesKey] is not JsonObject body) return null;
        var overrides = new PrefabOverrides
        {
            Components = Section(body, "components"),
            Parts = Section(body, "parts"),
        };
        return overrides.IsEmpty ? null : overrides;
    }

    private static JsonObject? Section(JsonObject body, string name)
    {
        foreach (var (key, value) in body)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase) && value is JsonObject section)
                return (JsonObject)section.DeepClone();
        return null;
    }

    // Its wires again, as its placement attached them (PlacementWires.Attach), with how often each fired.
    // A wire that cannot be read is dropped with a warning: the entity comes back without it.
    public static void Attach(World world, Entity entity, JsonObject saved, JsonSerializerOptions records, string where)
    {
        if (saved[OutputsKey] is not JsonArray list || list.Count == 0 || world.Has<IOConnections>(entity)) return;
        var wires = new List<Connection>(list.Count);
        foreach (var node in list)
        {
            if (node is not JsonObject written) continue;
            var body = (JsonObject)written.DeepClone();
            int fired = 0;
            foreach (var (key, value) in written)
                if (string.Equals(key, FiredKey, StringComparison.OrdinalIgnoreCase))
                {
                    body.Remove(key);
                    if (value is JsonValue v && v.TryGetValue(out int n)) fired = Math.Max(0, n);
                }
            try
            {
                if (body.Deserialize<Connection>(records) is { } wire)
                {
                    wire.Fired = fired;
                    wires.Add(wire);
                }
            }
            catch (JsonException e)
            {
                Log.Warn(LogCat.Save, $"{where}: a wire of {World.Describe(entity)} could not be read ({e.Message}); it is dropped");
            }
        }
        if (wires.Count > 0) world.Add(entity, new IOConnections { Wires = wires.ToArray() });
    }
}
