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
// issue #99): markers placed around the player — who is in the middle, with north (−Z) up — at fractions
// of the map a layout binds with the `x` and `y` bindings (a point anchor in a `box`). The map's reach
// grows to take in the farthest marker, in whole steps, so it does not breathe as you walk.
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
//
// **Depth** (issue #349): the scene's `area_map` gives a `picture` under the markers, placed by `pictureX`,
// `pictureY`, `pictureW` and `pictureH` (the `x`, `y`, `w` and `h` bindings, in a `clip` box), and `fog`,
// a row per square of it the player has not been near (MapDiscovery), placed the same way. A quest the
// player tracks (Quests.Track) marks its targets — the stage's `target` and each unmet objective's — as
// markers with `quest` set, in `rpg:map_quest`. The buttons `zoom_in`, `zoom_out`, `pan_north`,
// `pan_south`, `pan_east`, `pan_west` and `recentre` zoom and pan it; `youX` and `youY` are where the
// player is then.
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

        // A tracked quest's target (issue #349): its label is the quest's.
        public bool Quest { get; internal set; }
        public RecordId QuestId { get; internal set; }

        // Out of the fog (always, on a map without it, and for a quest's target): a layout hides the rest by it.
        public bool Discovered { get; internal set; } = true;

        internal int Tenths = -1;
        internal Vector3 Absolute;   // a point marker's place, absolute metres
        internal string Target = ""; // a quest target's entity, by name, while it is not found
    }

    // A square of fog over the map (issue #349): its rect, as fractions of the map (it may overhang it).
    public sealed class FogSquare
    {
        public float X { get; internal set; }
        public float Y { get; internal set; }
        public float W { get; internal set; }
        public float H { get; internal set; }

        internal int SquareX, SquareZ;
    }

    // The smallest reach, and the step it grows by, in metres from the player to the map's edge.
    public const float MinimumRange = 20f, RangeStep = 10f;

    // How far it zooms in and out, by halves and doubles.
    public const float MinZoom = 0.25f, MaxZoom = 8f;

    // The most squares of fog across the map: past it, a square of fog stands for several.
    public const int MostFogAcross = 32;

    private readonly List<Marker> _pool = new();
    private World? _world;
    private Query<Transform, MapMarker> _marked;
    private Query<Transform> _players;
    private int _signature = int.MinValue;
    private int _retry;

    public List<Marker> Markers { get; } = new();

    // Every discovered travel point, in any scene, in the order found: rows that travel when chosen (4g-7).
    [System.Diagnostics.CodeAnalysis.Experimental(RpgKitModule.OpenWorld, UrlFormat = RpgKitModule.ExperimentalUrl)]
    public List<Marker> Destinations { get; } = new();

    [System.Diagnostics.CodeAnalysis.Experimental(RpgKitModule.OpenWorld, UrlFormat = RpgKitModule.ExperimentalUrl)]
    public bool HasDestinations => Destinations.Count > 0;

    private readonly List<Marker> _destinationPool = new();

    // Metres from the middle to the edge, as zoomed.
    public int Range { get; private set; } = (int)MinimumRange;

    public int Count => Markers.Count;

    // Whether there is a player to centre on (the map is about them: without one, markers sit round the origin).
    public bool HasPlayer { get; private set; }

    // ---- zoom and pan (issue #349)

    // 1: the reach that holds every marker; 2: half that, closer in.
    public float Zoom { get; private set; } = 1f;
    public bool CanZoomIn => Zoom < MaxZoom;
    public bool CanZoomOut => Zoom > MinZoom;

    // How far the middle is from the player, in metres east and south.
    public Vector2 Pan { get; private set; }
    public bool Panned => Pan != Vector2.Zero;

    // Where the player is on the map, 0..1 (the middle until it is panned).
    public float YouX { get; private set; } = 0.5f;
    public float YouY { get; private set; } = 0.5f;

    // ---- the scene's picture and fog (issue #349, its area_map)

    public bool HasPicture => Picture != null;
    public string? Picture { get; private set; }
    public float PictureX { get; private set; }
    public float PictureY { get; private set; }
    public float PictureW { get; private set; }
    public float PictureH { get; private set; }

    public List<FogSquare> Fog { get; } = new();
    public bool HasFog { get; private set; }

    private readonly List<FogSquare> _fogPool = new();
    private int _fogSignature = int.MinValue;

    public void ZoomIn() => Zoom = MathF.Min(Zoom * 2f, MaxZoom);
    public void ZoomOut() => Zoom = MathF.Max(Zoom * 0.5f, MinZoom);

    // Moves the middle by a share of what the map shows from the middle to its edge (1: to the edge).
    public void PanBy(float east, float south) => Pan += new Vector2(east, south) * _reach;

    public void Recentre()
    {
        Pan = Vector2.Zero;
        Zoom = 1f;
    }

    private float _reach = MinimumRange;

    public void Refresh(in UiBindContext context)
    {
        if (context.World is not { } world) return;
        if (!ReferenceEquals(world, _world))
        {
            _world = world;
            _marked = world.Query<Transform, MapMarker>();
            _players = world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>());
            _signature = _fogSignature = int.MinValue;
        }

        var player = context.Subject;
        if (player.IsNull || !world.IsAlive(player))
        {
            player = default;
            foreach (var entity in _players.Entities) { player = entity; break; }
        }
        HasPlayer = !player.IsNull && world.Has<Transform>(player);
        var you = HasPlayer ? Where(world, player) : Vector3.Zero;

        // The set and what each says: rebuilt when it changes.
        int signature = 17;
        foreach (var entity in _marked.Entities)
        {
            if (entity == player) continue;
            var marker = world.Get<MapMarker>(entity);
            signature = signature * 31 + entity.GetHashCode();
            signature = signature * 31 + (marker.Label?.GetHashCode() ?? 0);
            signature = signature * 31 + (marker.Style?.GetHashCode() ?? 0);
        }
        var scene = SceneOf(world);
        var points = world.Resources.TryGet<TravelLog>(out var log) && log != null ? log.Points : null;
        signature = signature * 31 + scene.GetHashCode();
        signature = signature * 31 + (points?.Count ?? 0);
        signature = signature * 31 + (points == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(points));   // a load's new log
        signature = signature * 31 + QuestSignature(world, player);
        if (_unresolved && ++_retry % 60 == 0) _signature = int.MinValue;   // a quest's target not here yet: look again now and then
        if (signature != _signature)
        {
            _signature = signature;
            Rebuild(world, player, scene, points);
        }

        // A reach that holds everything, in whole steps, with a little room so nothing sits on the edge.
        float farthest = 0f;
        foreach (var marker in Markers)
        {
            if (!TryWhere(world, marker, out var at)) continue;
            at -= you;
            farthest = MathF.Max(farthest, MathF.Max(MathF.Abs(at.X), MathF.Abs(at.Z)));
        }
        float wanted = MathF.Max(MinimumRange, MathF.Ceiling(farthest * 1.1f / RangeStep) * RangeStep);
        float reach = _reach = wanted / Zoom;
        Range = Math.Max(1, (int)MathF.Round(reach));

        // The middle: the player, panned.
        var centre = you + new Vector3(Pan.X, 0f, Pan.Y);
        YouX = Math.Clamp(0.5f + (you.X - centre.X) / (2f * reach), 0f, 1f);
        YouY = Math.Clamp(0.5f + (you.Z - centre.Z) / (2f * reach), 0f, 1f);

        var map = AreaMapRecord.Of(world, scene);
        var discovery = map is { Fog: true } ? MapDiscovery.Of(world) : null;
        float cell = map == null ? 1f : MathF.Max(map.Cell, 1f);
        foreach (var marker in Markers)
        {
            if (!TryWhere(world, marker, out var at)) continue;
            if (discovery != null && !marker.Quest)
                marker.Discovered = discovery.IsDiscovered(scene, cell, world.Origin().ToAbsolute(at));
            at -= centre;
            marker.X = Math.Clamp(0.5f + at.X / (2f * reach), 0f, 1f);
            marker.Y = Math.Clamp(0.5f + at.Z / (2f * reach), 0f, 1f);
            var fromYou = at + centre - you;
            marker.Distance = (int)MathF.Round(new Vector2(fromYou.X, fromYou.Z).Length());
            Cost(world, marker);
        }
        foreach (var destination in Destinations) Cost(world, destination);

        PlacePicture(world, map, centre, reach);
        PlaceFog(world, scene, map, discovery, centre, reach);
    }

    // Choosing a marker or a destination that is a travel point travels there (Travel.ToPoint, 4g-7); the
    // zoom and pan buttons zoom and pan (issue #349).
    public bool Activate(Widget widget, in UiBindContext context)
    {
        switch (widget.Name)
        {
            case "zoom_in": ZoomIn(); return true;
            case "zoom_out": ZoomOut(); return true;
            case "pan_north": PanBy(0f, -0.5f); return true;
            case "pan_south": PanBy(0f, 0.5f); return true;
            case "pan_east": PanBy(0.5f, 0f); return true;
            case "pan_west": PanBy(-0.5f, 0f); return true;
            case "recentre": Recentre(); return true;
        }
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

    // Where a marker is now, origin-relative: its entity, or its place; false for an entity gone.
    private static bool TryWhere(World world, Marker marker, out Vector3 at)
    {
        if (!marker.Entity.IsNull)
        {
            at = world.IsAlive(marker.Entity) ? Where(world, marker.Entity) : default;
            return world.IsAlive(marker.Entity);
        }
        at = world.Origin().ToOrigin(marker.Absolute);
        return true;
    }

    // The scene's picture where its corners fall on the map, as zoomed and panned.
    private void PlacePicture(World world, AreaMapRecord? map, Vector3 centre, float reach)
    {
        if (map == null || map.Picture.IsEmpty)
        {
            Picture = null;
            return;
        }
        Picture = map.Picture.ToString();   // the interned path's own string: the same one every frame
        var from = world.Origin().ToOrigin(new Vector3(map.From.X, 0f, map.From.Y)) - centre;
        PictureX = 0.5f + from.X / (2f * reach);
        PictureY = 0.5f + from.Z / (2f * reach);
        PictureW = (map.To.X - map.From.X) / (2f * reach);
        PictureH = (map.To.Y - map.From.Y) / (2f * reach);
    }

    // A row per square of fog on the map, rebuilt when the squares shown or the fog change; moved every frame.
    private void PlaceFog(World world, RecordId scene, AreaMapRecord? map, MapDiscovery? discovery, Vector3 centre, float reach)
    {
        HasFog = discovery != null && map != null;
        if (!HasFog)
        {
            Fog.Clear();
            _fogSignature = int.MinValue;
            return;
        }
        var origin = world.Origin();
        var middle = origin.ToAbsolute(centre);
        float cell = MathF.Max(map!.Cell, 1f);
        int x0 = MapDiscovery.IndexOf(middle.X - reach, cell), x1 = MapDiscovery.IndexOf(middle.X + reach, cell);
        int z0 = MapDiscovery.IndexOf(middle.Z - reach, cell), z1 = MapDiscovery.IndexOf(middle.Z + reach, cell);
        // Too many across: a square of fog stands for step × step of the map's, fogged unless its middle one is lifted.
        int step = Math.Max(1, (int)MathF.Ceiling((x1 - x0 + 1) / (float)MostFogAcross));
        x0 = FloorTo(x0, step);
        z0 = FloorTo(z0, step);

        int signature = HashCode.Combine(x0, x1, z0, z1, step, discovery!.Version, System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(discovery), scene);
        if (signature != _fogSignature)
        {
            _fogSignature = signature;
            Fog.Clear();
            int used = 0;
            for (int z = z0; z <= z1; z += step)
                for (int x = x0; x <= x1; x += step)
                {
                    if (discovery.IsDiscovered(scene, cell, x + step / 2, z + step / 2)) continue;
                    if (used == _fogPool.Count) _fogPool.Add(new FogSquare());
                    var square = _fogPool[used++];
                    square.SquareX = x;
                    square.SquareZ = z;
                    Fog.Add(square);
                }
        }

        float size = step * cell / (2f * reach);
        foreach (var square in Fog)
        {
            var corner = origin.ToOrigin(new Vector3(square.SquareX * cell, 0f, square.SquareZ * cell)) - centre;
            square.X = 0.5f + corner.X / (2f * reach);
            square.Y = 0.5f + corner.Z / (2f * reach);
            square.W = square.H = size;
        }
    }

    private static int FloorTo(int value, int step) => (int)MathF.Floor(value / (float)step) * step;

    private bool _unresolved;

    // What the tracked quests' targets depend on: which are tracked, their stages, and how far each objective is.
    private static int QuestSignature(World world, Entity player)
    {
        if (Quests.JournalOf(world) is not { } journal) return 0;
        var records = world.Records();
        int hash = journal.Entries.Count * 7 + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(journal);
        foreach (var entry in journal.Entries)
        {
            if (!entry.Tracked || entry.Finished) continue;
            hash = hash * 31 + entry.Quest.GetHashCode();
            hash = hash * 31 + (entry.Stage?.GetHashCode() ?? 0);
            if (!records.TryGet(entry.Quest, out QuestRecord record) || record.Stage(entry.Stage ?? "") is not { } stage) continue;
            for (int i = 0; i < stage.Objectives.Count; i++)
                hash = hash * 31 + Quests.Progress(world, player, entry.Quest, i);
        }
        return hash;
    }

    private void Rebuild(World world, Entity player, RecordId scene, List<TravelDestination>? points)
    {
        Markers.Clear();
        int used = 0;
        foreach (var entity in _marked.Entities)
        {
            if (entity == player) continue;
            var component = world.Get<MapMarker>(entity);
            var marker = Take(ref used);
            marker.Entity = entity;
            marker.Label = component.Label ?? "";
            marker.Style = string.IsNullOrEmpty(component.Style) ? null : component.Style;
            Markers.Add(marker);
        }

        // The travel points: on the entity's own marker when it has one here, else a marker of their own.
        Destinations.Clear();
        if (points != null)
        {
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
                var made = Take(ref used);
                Fill(made, default, point, TravelStyle);
                Markers.Add(made);
            }
        }

        // A tracked quest's targets (issue #349): the stage's, and each objective's that is not met yet.
        _unresolved = false;
        if (Quests.JournalOf(world) is not { } journal) return;
        var records = world.Records();
        foreach (var entry in journal.Entries)
        {
            if (!entry.Tracked || entry.Finished || !records.TryGet(entry.Quest, out QuestRecord record)) continue;
            if (record.Stage(entry.Stage) is not { } stage) continue;
            string label = record.Label.Length > 0 ? record.Label : entry.Quest.Name;
            if (stage.Target.Length > 0) AddTarget(world, ref used, entry.Quest, label, stage.Target, null);
            for (int i = 0; i < stage.Objectives.Count; i++)
            {
                var objective = stage.Objectives[i];
                if (Quests.Progress(world, player, entry.Quest, i) >= Math.Max(objective.Count, 1)) continue;
                if (objective.Target.Length > 0) AddTarget(world, ref used, entry.Quest, label, objective.Target, null);
                else if (objective.TryGetPlace(out var place)) AddTarget(world, ref used, entry.Quest, label, "", place);
            }
        }
    }

    private void AddTarget(World world, ref int used, RecordId quest, string label, string name, Vector3? place)
    {
        Entity entity = default;
        if (place == null)
        {
            entity = world.FindByName(name);
            if (entity.IsNull)
            {
                _unresolved = true;   // asleep, or not spawned yet
                return;
            }
        }
        var marker = Take(ref used);
        marker.Entity = entity;
        marker.Absolute = place ?? default;
        marker.Label = label;
        marker.Style = QuestStyle;
        marker.Quest = true;
        marker.QuestId = quest;
        marker.Target = name;
        Markers.Add(marker);
    }

    // A marker from the pool, as new.
    private Marker Take(ref int used)
    {
        if (used == _pool.Count) _pool.Add(new Marker());
        var marker = _pool[used++];
        marker.Entity = default;
        marker.Label = "";
        marker.Style = null;
        marker.Point = null;
        marker.Tenths = -1;
        marker.TravelHours = "";
        marker.Quest = false;
        marker.QuestId = default;
        marker.Discovered = true;
        marker.Absolute = default;
        marker.Target = "";
        return marker;
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

    // The ui_style a tracked quest's target is drawn in (issue #349): `rpg:map_quest`, restyled with a patch.
    public const string QuestStyle = "rpg:map_quest";
}
