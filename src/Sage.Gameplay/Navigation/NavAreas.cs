#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Gameplay;

// Ground that costs different amounts to cross, and ground some may not cross at all (docs/design/16
// §3.4, issue #271): "a road is cheap, mud and water are dear, the town is closed to wolves".
//
// **An area is a record**, and the ground says which area it is in one of three ways, the first that
// applies winning:
//   - a `nav_area` volume (the part of that name: a box, centred on the entity) — a road painted over a
//     field, a camp the bandits keep out of; where two overlap, the dearer wins;
//   - a water volume (#262), when an area says `"water": true`;
//   - what the ground is made of (#270): an area's `surfaces` are physics_material ids, so the gravel of
//     a road's brush texture, a collider's `surface` or a terrain layer is a road without anybody marking
//     it twice.
// Ground none of them names is area 0: it costs 1 a metre and is closed to nobody.
//
// **Costs multiply distance**: 1 is ordinary ground, 0.5 a road (a creature walks twice as far to stay on
// it), 3 mud. A cost under 1 is allowed; the planner's estimate is scaled by the cheapest cost there is,
// so it still finds the cheapest way. **Forbidden** names factions (16 §3.5) that never plan through the
// area: a creature of one stops at its edge rather than walking in after what it chases.
//
// **Areas are the navmesh's.** A span takes its area when its tile is baked, a tile's regions are split
// where the area changes, and both searches weigh a step by the areas it crosses. The local grid, where
// there is no mesh, knows none of it.
//
//   { "type": "nav_area", "id": "road", "cost": 0.5, "surfaces": ["gravel", "cobbles"] },
//   { "type": "nav_area", "id": "river", "cost": 4, "water": true },
//   { "type": "nav_area", "id": "town", "forbidden": ["wolves"] }
[Record("nav_area", Plugin = "sage.gameplay.ai")]
public sealed class NavAreaRecord
{
    [Property(Min = 0.1f, Max = 100, Tooltip = "What a metre of it costs a planner, against 1 for ground no area names: 0.5 a road, 3 mud")]
    public float Cost = 1f;
    [Property(Tooltip = "Factions that never plan a way through it, and stop at its edge")]
    public List<RecordRef<FactionRecord>> Forbidden = new();
    [Property(Tooltip = "What ground made of these physics_materials is: a road's gravel, a bog's mud")]
    public List<RecordRef<PhysicsMaterialRecord>> Surfaces = new();
    [Property(Tooltip = "Every water volume is this area (one nav_area volume over it still wins)")]
    public bool Water;
}

// A box of area: the `nav_area` part. Axis-aligned and centred on the entity, like a water volume.
[Component("sage:nav_area")]
public struct NavArea : IComponent
{
    [RecordRef("nav_area"), Property(Tooltip = "Which area the ground inside is")]
    public RecordId Area;
    [Property(Min = 0, Unit = "m", Tooltip = "Full extents of the box, centred on the entity; ground whose floor is inside it is the area")]
    public Vector3 Size;
}

// "nav_area": { "area": "road", "size": [4, 2, 60] }
[PrefabPart("nav_area", Plugin = "sage.gameplay.ai")]
public sealed class NavAreaPart : IPrefabPart
{
    [Property(Tooltip = "The nav_area record the ground inside is")]
    public RecordRef<NavAreaRecord> Area;
    [Property(Min = 0, Unit = "m", Tooltip = "Full extents of the box, centred on the entity")]
    public Vector3 Size;

    public void Apply(in PrefabPartContext ctx)
    {
        if (Area.IsEmpty) { ctx.Error("a nav_area needs the \"area\" it marks"); return; }
        if (Size.X <= 0f || Size.Y <= 0f || Size.Z <= 0f) { ctx.Error("a nav_area needs a \"size\" with three positive extents"); return; }
        ctx.World.Add(ctx.Entity, new NavArea { Area = Area, Size = Size });
    }
}

// ---- what the planner reads ----------------------------------------------------------------------------

// The areas as numbers: index 0 is ordinary ground, 1..255 the nav_area records in id order. Built from
// the records when a world first plans, and again after a reload.
internal sealed class NavAreas
{
    public const byte Ground = 0;
    private const int MaxAreas = 255;

    private readonly float[] _cost = new float[MaxAreas + 1];
    private readonly List<RecordId>[] _forbidden = new List<RecordId>[MaxAreas + 1];
    private readonly Dictionary<RecordId, byte> _bySurface = new();
    private readonly Dictionary<RecordId, byte> _byId = new();
    private readonly List<RecordId> _ids = new();

    public static readonly NavAreas None = new();

    private NavAreas()
    {
        _cost[0] = 1f;
        MinCost = 1f;
    }

    // How many areas there are besides ordinary ground: none, and nothing about areas costs anything.
    public int Count { get; private set; }

    // The cheapest cost of all, ground included: what the planners' estimates are scaled by.
    public float MinCost { get; private set; }

    // The area every water volume is, or 0.
    public byte Water { get; private set; }

    public float Cost(byte area) => _cost[area];

    public RecordId IdOf(byte area) => area == Ground || area > Count ? default : _ids[area - 1];

    public byte OfSurface(RecordId surface) => !surface.IsEmpty && _bySurface.TryGetValue(surface, out var area) ? area : Ground;

    public byte Of(RecordId id) => !id.IsEmpty && _byId.TryGetValue(id, out var area) ? area : Ground;

    public static NavAreas From(RecordStore? records)
    {
        if (records == null || records.TypeNameOf(typeof(NavAreaRecord)) is not { } type) return None;
        var areas = new NavAreas();
        foreach (var id in records.Ids(type))
        {
            if (!records.TryGet(id, out NavAreaRecord record)) continue;
            if (areas.Count == MaxAreas)
            {
                Log.Once(LogCat.AI, LogLevel.Warn, "nav-area-count", $"more than {MaxAreas} nav_area records: {id} and those after it are ordinary ground");
                break;
            }
            byte index = (byte)++areas.Count;
            areas._ids.Add(id);
            areas._byId[id] = index;
            areas._cost[index] = Math.Clamp(float.IsFinite(record.Cost) ? record.Cost : 1f, 0.1f, 100f);
            areas.MinCost = MathF.Min(areas.MinCost, areas._cost[index]);
            if (record.Forbidden.Count > 0)
            {
                var list = new List<RecordId>(record.Forbidden.Count);
                foreach (var faction in record.Forbidden) if (!faction.IsEmpty) list.Add(faction);
                areas._forbidden[index] = list;
            }
            foreach (var surface in record.Surfaces)
                if (!surface.IsEmpty) areas._bySurface.TryAdd(surface, index);
            if (record.Water && areas.Water == Ground) areas.Water = index;
        }
        return areas;
    }

    // Which areas a member of `faction` may not plan through.
    public NavFilter FilterFor(RecordId faction)
    {
        var filter = new NavFilter();
        if (faction.IsEmpty) return filter;
        for (int i = 1; i <= Count; i++)
            if (_forbidden[i] is { } list && list.Contains(faction)) filter.Forbid((byte)i);
        return filter;
    }
}

// The areas one plan may not enter: 256 bits, by value, so a plan allocates nothing to know them.
internal struct NavFilter
{
    private ulong _a, _b, _c, _d;

    public readonly bool Any => (_a | _b | _c | _d) != 0;

    public void Forbid(byte area)
    {
        ulong bit = 1UL << (area & 63);
        switch (area >> 6)
        {
            case 0: _a |= bit; break;
            case 1: _b |= bit; break;
            case 2: _c |= bit; break;
            default: _d |= bit; break;
        }
    }

    public readonly bool Forbids(byte area)
    {
        ulong word = (area >> 6) switch { 0 => _a, 1 => _b, 2 => _c, _ => _d };
        return (word & (1UL << (area & 63))) != 0;
    }
}

// A box of area in absolute metres, as a bake reads it: a nav_area volume or a water volume.
internal readonly record struct NavAreaBox(Vector3 Min, Vector3 Max, byte Area, bool IsWater);
