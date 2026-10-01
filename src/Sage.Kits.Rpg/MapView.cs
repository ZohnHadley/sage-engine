#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Sage.UI;

namespace Sage.Kits.Rpg;

// Something the map shows (issue #99): a place, a person, a door. The map draws markers and nothing
// else — no terrain, no picture of the ground — so what is on it is exactly what content marked:
//
//   "components": { "map_marker": { "label": "@sandbox.map.hut", "style": "sandbox:map_place" } }
//
// Saved like any component of a persistent entity; a plain field set, so no save format change.
[Component("sage:map_marker")]   // the engine's namespace, as the kit is the engine's own: content writes it bare, `map_marker`
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
//
// **Fast travel** (issue 4g-7, over 4g-5's `travel_point`s): a travel point the player has discovered (the
// saved `TravelLog`) in the scene the world is in is a marker you can choose — its own entity's marker, when
// it has a `map_marker` and is placed, else a marker of its own where the log says it is, which is how a
// point in a sector that is asleep stays on the map. `canTravel` says so, `travelHours` is what the journey
// costs (`Travel.HoursTo`), and activating its row travels there (`Travel.ToPoint`). `destinations` lists
// every discovered point, in any scene, the same way, for a layout that shows a list beside the map.
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

        // A discovered travel point this marker stands for (4g-7): choosing it travels there.
        [System.Diagnostics.CodeAnalysis.Experimental(RpgKitModule.OpenWorld, UrlFormat = RpgKitModule.ExperimentalUrl)]
        public TravelDestination? Point { get; internal set; }

        [System.Diagnostics.CodeAnalysis.Experimental(RpgKitModule.OpenWorld, UrlFormat = RpgKitModule.ExperimentalUrl)]
        public bool CanTravel => Point != null;

        // What the journey costs, in game hours to a tenth ("2.5"); empty for a marker that is no travel point.
        [System.Diagnostics.CodeAnalysis.Experimental(RpgKitModule.OpenWorld, UrlFormat = RpgKitModule.ExperimentalUrl)]
        public string TravelHours { get; internal set; } = "";

        internal int Tenths = -1;
        internal Vector3 Absolute;   // a point marker's place, absolute metres
    }

    // The smallest reach, and the step it grows by, in metres from the player to the map's edge.
    public const float MinimumRange = 20f, RangeStep = 10f;

    private readonly List<Marker> _pool = new();
    private World? _world;
    private Query<Transform, MapMarker> _marked;
    private Query<Transform> _players;
    private int _signature = int.MinValue;

    public List<Marker> Markers { get; } = new();

    // Every discovered travel point, in any scene, in the order found: rows that travel when chosen (4g-7).
    [System.Diagnostics.CodeAnalysis.Experimental(RpgKitModule.OpenWorld, UrlFormat = RpgKitModule.ExperimentalUrl)]
    public List<Marker> Destinations { get; } = new();

    [System.Diagnostics.CodeAnalysis.Experimental(RpgKitModule.OpenWorld, UrlFormat = RpgKitModule.ExperimentalUrl)]
    public bool HasDestinations => Destinations.Count > 0;

    private readonly List<Marker> _destinationPool = new();

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
        var scene = SceneOf(world);
        var points = world.Resources.TryGet<TravelLog>(out var log) && log != null ? log.Points : null;
        signature = signature * 31 + scene.GetHashCode();
        signature = signature * 31 + (points?.Count ?? 0);
        signature = signature * 31 + (points == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(points));   // a load's new log
        if (points != null)
            foreach (var point in points)
                if (point.Scene == scene)
                {
                    var at = world.Origin().ToOrigin(point.At) - centre;
                    farthest = MathF.Max(farthest, MathF.Max(MathF.Abs(at.X), MathF.Abs(at.Z)));
                }
        if (signature != _signature)
        {
            _signature = signature;
            Rebuild(world, player, scene, points);
        }

        // A reach that holds everything, in whole steps, with a little room so nothing sits on the edge.
        float wanted = MathF.Max(MinimumRange, MathF.Ceiling(farthest * 1.1f / RangeStep) * RangeStep);
        Range = (int)wanted;

        foreach (var marker in Markers)
        {
            Vector3 at;
            if (!marker.Entity.IsNull)
            {
                if (!world.IsAlive(marker.Entity)) continue;
                at = Where(world, marker.Entity) - centre;
            }
            else at = world.Origin().ToOrigin(marker.Absolute) - centre;
            marker.X = Math.Clamp(0.5f + at.X / (2f * wanted), 0f, 1f);
            marker.Y = Math.Clamp(0.5f + at.Z / (2f * wanted), 0f, 1f);
            marker.Distance = (int)MathF.Round(new Vector2(at.X, at.Z).Length());
            Cost(world, marker);
        }
        foreach (var destination in Destinations) Cost(world, destination);
    }

    // Choosing a marker or a destination that is a travel point travels there (Travel.ToPoint, 4g-7).
    public bool Activate(Widget widget, in UiBindContext context)
    {
        if (context.World is not { } world || UiScreen.RowOf(widget) is not Marker { Point: { } point }) return false;
        string label = point.Label.Length > 0 ? point.Label : point.Name;
        return Travel.ToPoint(world, label);
    }

    // The journey's cost in tenths of an hour, as words only when it changes.
    private static void Cost(World world, Marker marker)
    {
        if (marker.Point is not { } point) return;
        int tenths = (int)Math.Round(Travel.HoursTo(world, point) * 10);
        if (tenths == marker.Tenths) return;
        marker.Tenths = tenths;
        marker.TravelHours = (tenths / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static RecordId SceneOf(World world) => Scenes.Current(world);

    // Where an entity is in the world: its global transform when it has one (a child), else its own.
    private static Vector3 Where(World world, Entity entity) =>
        world.TryGet<GlobalTransform>(entity, out var global) ? global.Current.Position
        : world.TryGet<Transform>(entity, out var local) ? local.LocalPosition : Vector3.Zero;

    private void Rebuild(World world, Entity player, RecordId scene, List<TravelDestination>? points)
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
            marker.Point = null;
            marker.Tenths = -1;
            marker.TravelHours = "";
            Markers.Add(marker);
        }

        // The travel points: on the entity's own marker when it has one here, else a marker of their own.
        Destinations.Clear();
        if (points == null) return;
        int destinations = 0;
        foreach (var point in points)
        {
            if (destinations == _destinationPool.Count) _destinationPool.Add(new Marker());
            var row = _destinationPool[destinations++];
            Fill(row, default, point, null);
            Destinations.Add(row);

            if (point.Scene != scene) continue;
            Marker? own = null;
            foreach (var marker in Markers)
                if (marker.Point == null && !marker.Entity.IsNull && marker.Entity.Name is { } name && name.Length > 0 &&
                    string.Equals(name, point.Name, StringComparison.Ordinal) && world.Has<TravelPoint>(marker.Entity))
                {
                    own = marker;
                    break;
                }
            if (own != null)
            {
                own.Point = point;
                own.Tenths = -1;
                continue;
            }
            if (used == _pool.Count) _pool.Add(new Marker());
            var made = _pool[used++];
            Fill(made, default, point, TravelStyle);
            Markers.Add(made);
        }
    }

    private static void Fill(Marker marker, Entity entity, TravelDestination point, string? style)
    {
        marker.Entity = entity;
        marker.Label = point.Label.Length > 0 ? point.Label : point.Name;
        marker.Style = style;
        marker.Point = point;
        marker.Absolute = point.At;
        marker.Tenths = -1;
        marker.TravelHours = "";
    }

    // The ui_style a travel point's own marker is drawn in, when its entity has no map_marker of its own (or
    // is asleep): a game restyles `rpg:map_travel` with a patch.
    [System.Diagnostics.CodeAnalysis.Experimental(RpgKitModule.OpenWorld, UrlFormat = RpgKitModule.ExperimentalUrl)]
    public const string TravelStyle = "rpg:map_travel";
}
