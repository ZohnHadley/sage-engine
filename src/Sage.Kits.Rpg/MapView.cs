#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Sage.UI;

namespace Sage.Kits.Rpg;

// Something the map shows (issue #99): a place, a person, a door. The map draws markers and nothing
// else — no terrain, no picture of the ground — so what is on it is exactly what content marked:
//
//   "components": { "rpg:map_marker": { "label": "@sandbox.map.hut", "style": "sandbox:map_place" } }
//
// Saved like any component of a persistent entity; a plain field set, so no save format change.
[Component("rpg:map_marker")]
public struct MapMarker : IComponent
{
    [Property(Tooltip = "What the map calls it: text, or a localisation key (@ns.key)")]
    public string Label;

    [Property(Tooltip = "The ui_style id its marker is drawn in (ns:name); empty: the map layout's own")]
    public string Style;
}

// The map as a widget screen's view-model (docs/design/13 "As built (the HUD, journal, map and menus)",
// issue #99): markers only, placed around the player — who is in the middle, with north (−Z) up — at
// fractions of the map a layout binds with the `x` and `y` bindings (a point anchor in a `box`). The
// map's reach grows to take in the farthest marker, in whole steps, so it does not breathe as you walk.
//
// Per frame it moves the markers (numbers, nothing allocated); the rows are rebuilt only when the set of
// marked entities, or what one says, changes.
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the RPG screens (#99): SAGE0125, as Sage.UI
[ViewModel("rpg_map")]
public sealed class MapView : IViewModel
{
    public sealed class Marker
    {
        public string Label { get; internal set; } = "";

        // The marker's ui_style, or null for the layout's.
        public string? Style { get; internal set; }

        // Where on the map, 0..1 across (west to east) and down (north to south).
        public float X { get; internal set; }
        public float Y { get; internal set; }

        // How far away, in whole metres (for a tooltip or a list).
        public int Distance { get; internal set; }

        public Entity Entity { get; internal set; }
    }

    // The smallest reach, and the step it grows by, in metres from the player to the map's edge.
    public const float MinimumRange = 20f, RangeStep = 10f;

    private readonly List<Marker> _pool = new();
    private World? _world;
    private Query<Transform, MapMarker> _marked;
    private Query<Transform> _players;
    private int _signature = int.MinValue;

    public List<Marker> Markers { get; } = new();

    // Metres from the middle to the edge.
    public int Range { get; private set; } = (int)MinimumRange;

    public int Count => Markers.Count;

    // Whether there is a player to centre on (the map is about them: without one, markers sit round the origin).
    public bool HasPlayer { get; private set; }

    public void Refresh(in UiBindContext context)
    {
        if (context.World is not { } world) return;
        if (!ReferenceEquals(world, _world))
        {
            _world = world;
            _marked = world.Query<Transform, MapMarker>();
            _players = world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>());
            _signature = int.MinValue;
        }

        var player = context.Subject;
        if (player.IsNull || !world.IsAlive(player))
        {
            player = default;
            foreach (var entity in _players.Entities) { player = entity; break; }
        }
        HasPlayer = !player.IsNull && world.Has<Transform>(player);
        var centre = HasPlayer ? Where(world, player) : Vector3.Zero;

        // The set and what each says: rebuilt when it changes.
        int signature = 17;
        float farthest = 0f;
        foreach (var entity in _marked.Entities)
        {
            if (entity == player) continue;
            var marker = world.Get<MapMarker>(entity);
            signature = signature * 31 + entity.GetHashCode();
            signature = signature * 31 + (marker.Label?.GetHashCode() ?? 0);
            signature = signature * 31 + (marker.Style?.GetHashCode() ?? 0);
            var at = Where(world, entity) - centre;
            farthest = MathF.Max(farthest, MathF.Max(MathF.Abs(at.X), MathF.Abs(at.Z)));
        }
        if (signature != _signature)
        {
            _signature = signature;
            Rebuild(world, player);
        }

        // A reach that holds everything, in whole steps, with a little room so nothing sits on the edge.
        float wanted = MathF.Max(MinimumRange, MathF.Ceiling(farthest * 1.1f / RangeStep) * RangeStep);
        Range = (int)wanted;

        foreach (var marker in Markers)
        {
            if (!world.IsAlive(marker.Entity)) continue;
            var at = Where(world, marker.Entity) - centre;
            marker.X = Math.Clamp(0.5f + at.X / (2f * wanted), 0f, 1f);
            marker.Y = Math.Clamp(0.5f + at.Z / (2f * wanted), 0f, 1f);
            marker.Distance = (int)MathF.Round(new Vector2(at.X, at.Z).Length());
        }
    }

    // Where an entity is in the world: its global transform when it has one (a child), else its own.
    private static Vector3 Where(World world, Entity entity) =>
        world.TryGet<GlobalTransform>(entity, out var global) ? global.Current.Position
        : world.TryGet<Transform>(entity, out var local) ? local.LocalPosition : Vector3.Zero;

    private void Rebuild(World world, Entity player)
    {
        Markers.Clear();
        int used = 0;
        foreach (var entity in _marked.Entities)
        {
            if (entity == player) continue;
            var component = world.Get<MapMarker>(entity);
            if (used == _pool.Count) _pool.Add(new Marker());
            var marker = _pool[used++];
            marker.Entity = entity;
            marker.Label = component.Label ?? "";
            marker.Style = string.IsNullOrEmpty(component.Style) ? null : component.Style;
            Markers.Add(marker);
        }
    }
}
