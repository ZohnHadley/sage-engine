#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// Interiors as spaces of their own, live beside each other and beside their exterior (phase 4m issue 4m-17,
// #291; design 14 §3 "Spaces", "As built (doors, interiors and travel)").
//
//   { "type": "scene", "id": "crypt", "space": "interior", "live": true, "environment": { "ambient": [0.1, 0.1, 0.12] }, ... }
//   { "type": "scene", "id": "village", "streamed": true, "live": true, ... }
//
// Before this, the world was in one scene at a time: going through a door put the scene the player left to
// sleep (4g-1's cells) and placed the other, so a dungeon could not go on while the player was out of it,
// and its exterior stopped while the player was in. A `live` scene is **held** instead when the player
// leaves: what it placed stays in the world and goes on being simulated — its AI, its physics, its logic —
// and going back takes it up as it is, without placing it again. Two interiors (or ten, up to
// `space_live_max`) are live at once, each with its own light (the player's space lights the world, 4g-5's
// `ApplySpace`), at its own coordinates.
//
// - **One ground.** The world has one terrain, so an exterior is held only while the player is inside an
//   interior: its ground and the sectors it placed stay (the ring stops following the player, it does not
//   drop them), and going to another exterior puts every held exterior to sleep first.
// - **How many**: `space_live_max` (default 4). The space left longest ago goes dormant when one more would
//   be held; 0 holds none, which is 4g-5's behaviour.
// - **Runtime spawns** made among a held space's things (a skeleton's dropped sword while the player is
//   outside) belong to that space's cell, by where they are: within the box what it placed stands in.
// - **Saves** keep the held spaces (the `spaces` resource): a load places each again beside the player's
//   scene, with the state the save had for it. A held streamed exterior is the exception: after a load it is
//   dormant until the player goes back, as a streamed scene places its sectors around the player.
// - **Hot reload** places held spaces again from the new records, as it does the player's scene.
public sealed partial class Scenes
{
    // How many scenes are held live beside the player's (`space_live_max`); 4 until the commands are registered.
    internal int LiveMax => _liveMax?.Value ?? 4;
    private CVar<int>? _liveMax;

    // The scenes held live beside the one the player is in, the one left longest ago first (4m-17).
    [Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
    public static IReadOnlyList<RecordId> LiveBeside(World world) =>
        world.Resources.TryGet<LiveSpaces>(out var live) && live != null ? live.Ids() : Array.Empty<RecordId>();

    // The player leaves the world's scene for `to` (Load): it goes dormant, or is held live (`live`). Going to
    // an exterior, every held exterior but `to` goes dormant first: the ground is about to be `to`'s.
    private void Leave(World world, RecordId to, SceneRecord toScene)
    {
        var live = Sage.Simulation.LiveSpaces.Of(world);
        if (toScene.Space == SceneSpace.Exterior)
            for (int i = live.Held.Count - 1; i >= 0; i--)
                if (live.Held[i].Exterior && live.Held[i].Id != to) Close(world, live, i);

        var state = world.Resources.Get<ActiveScene>();
        if (state.Id.IsEmpty) return;
        if (state.Id == to || LiveMax <= 0 || !_engine.Records.TryGet(state.Id, out SceneRecord from) || !from.Live
            || (from.Space == SceneSpace.Exterior && toScene.Space == SceneSpace.Exterior))
        {
            Clear(world);
            return;
        }

        var slot = new ActiveScene();
        state.MoveContentTo(slot);
        slot.Exterior = from.Space == SceneSpace.Exterior;
        var player = Player(world);
        foreach (var entity in world.Query<Transform>().AllTags(Tags.Get<FromScene>()).Entities.ToEntityList())
        {
            if (entity == player) continue;
            entity.RemoveTag<FromScene>();
            slot.Held.Add(entity);
        }
        Hold(world, live, slot);
        Log.Info(LogCat.World, $"'{world.Name}': scene '{slot.Id}' stays live beside '{to}' ({slot.Held.Count} placed, " +
                               $"{live.Held.Count} live)");
    }

    // `id` is held live: it becomes the world's scene again as it is (its ground and light applied, as Place
    // would). False when it is not held.
    private bool Resume(World world, RecordId id, SceneRecord scene)
    {
        if (!world.Resources.TryGet<LiveSpaces>(out var live) || live == null) return false;
        int index = live.IndexOf(id);
        if (index < 0) return false;
        var slot = live.Held[index];
        live.Held.RemoveAt(index);

        var state = world.Resources.Get<ActiveScene>();
        if (!state.Id.IsEmpty) Clear(world);   // never: Leave cleared or held it
        slot.MoveContentTo(state);
        foreach (var entity in state.Held)
            if (world.IsAlive(entity)) entity.AddTag<FromScene>();
        state.Held.Clear();
        ApplyTerrain(world, scene);
        ApplySpace(world, scene);
        UpdateGravity(world);
        Log.Info(LogCat.World, $"'{world.Name}': back in '{id}', live all along");
        return true;
    }

    // Places `id` beside the world's scene and holds it live (a load's `spaces`, a hot reload). A streamed
    // exterior is not placed in the background: its runtime spawns go to sleep in their sectors, and it is
    // placed when the player goes back. Nor is a scene that no longer says `live`.
    internal void OpenLive(World world, RecordId id)
    {
        if (!_engine.Records.TryGet(id, out SceneRecord scene))
        {
            Log.Warn(LogCat.World, $"'{world.Name}': scene '{id}' was live, and is no longer in this game");
            return;
        }
        var state = world.Resources.Get<ActiveScene>();
        var live = Sage.Simulation.LiveSpaces.Of(world);
        if (state.Id == id || live.IndexOf(id) >= 0) return;
        if (!scene.Live || LiveMax <= 0 || (scene.Streamed && scene.Space == SceneSpace.Exterior && world.Resources.TryGet<SectorRing>(out _))
            || (scene.Space == SceneSpace.Exterior && Scene(world) is { Space: SceneSpace.Exterior }))
        {
            Doze(world, id);
            return;
        }

        var before = new HashSet<Entity>(world.Query<Transform>().AllTags(Tags.Get<FromScene>()).Entities.ToEntityList());
        var aside = new ActiveScene();
        state.MoveContentTo(aside);
        try { Place(world, id, scene, background: true); }
        finally
        {
            var slot = new ActiveScene();
            state.MoveContentTo(slot);
            aside.MoveContentTo(state);
            foreach (var entity in world.Query<Transform>().AllTags(Tags.Get<FromScene>()).Entities.ToEntityList())
            {
                if (before.Contains(entity)) continue;
                entity.RemoveTag<FromScene>();
                slot.Held.Add(entity);
            }
            Hold(world, live, slot);
        }
    }

    // Every scene held live goes, its state not kept: a load or a hot reload is replacing the world's content.
    private static void CloseLive(World world)
    {
        if (!world.Resources.TryGet<LiveSpaces>(out var live) || live == null) return;
        for (int i = live.Held.Count - 1; i >= 0; i--) Close(world, live, i);
    }

    // A held scene goes dormant with its state (or is discarded, under ContentIds.Discarding).
    private static void Close(World world, LiveSpaces live, int index)
    {
        var slot = live.Held[index];
        live.Held.RemoveAt(index);
        var id = slot.Id;
        Clear(world, slot, new List<Entity>(slot.Held));
        UpdateGravity(world);
        Log.Info(LogCat.World, $"'{world.Name}': scene '{id}' is live no longer");
    }

    private void Hold(World world, LiveSpaces live, ActiveScene slot)
    {
        Bound(world, slot);
        live.Held.Add(slot);
        while (live.Held.Count > Math.Max(LiveMax, 0)) Close(world, live, 0);
        UpdateGravity(world);
    }

    // Each space's gravity (SceneEnvironment.GravityScale): the player's scene's everywhere, and a held
    // scene's within the box its things stand in, interiors first.
    private static void UpdateGravity(World world)
    {
        if (world.Engine is not { } engine) return;
        var records = engine.Records;
        var state = world.Resources.Get<ActiveScene>();
        float scale = !state.Id.IsEmpty && records.TryGet(state.Id, out SceneRecord active) ? active.Environment.GravityScale : 1f;
        var regions = new List<GravityRegion>();
        if (world.Resources.TryGet<LiveSpaces>(out var live) && live != null)
            for (int pass = 0; pass < 2; pass++)
                foreach (var slot in live.Held)
                    if (slot.Exterior == (pass == 1) && records.TryGet(slot.Id, out SceneRecord held)
                        && held.Environment.GravityScale != scale && slot.Min.X <= slot.Max.X)
                        regions.Add(new GravityRegion(slot.Min, slot.Max, held.Environment.GravityScale));
        SpaceGravity.Of(world).Set(scale, regions);
    }

    // The box what a held scene placed stands in, absolute, with a margin: a runtime spawn made in it is the
    // scene's (Cells.Current).
    private static void Bound(World world, ActiveScene slot)
    {
        const float margin = 32f;
        var origin = world.Origin();
        bool any = false;
        Vector3 min = default, max = default;
        void Take(Vector3 local)
        {
            var at = origin.ToAbsolute(local);
            min = any ? Vector3.Min(min, at) : at;
            max = any ? Vector3.Max(max, at) : at;
            any = true;
        }
        foreach (var entity in slot.Held)
            if (world.IsAlive(entity) && world.TryGet<Transform>(entity, out var transform)) Take(transform.LocalPosition);
        foreach (var level in slot.Levels)
            if (level.Placed) Take(level.Position);
        slot.Min = any ? min - new Vector3(margin) : new Vector3(float.MaxValue);
        slot.Max = any ? max + new Vector3(margin) : new Vector3(float.MinValue);
    }

    // A scene that is not placed: what it spawned at run time sleeps in its cells (the scene's, or a streamed
    // scene's sectors), as if it had gone dormant, and comes back when it is placed.
    private static void Doze(World world, RecordId id)
    {
        string whole = ContentIds.SceneSource(id);
        string sectors = ContentIds.SectorPrefix + id + ":";
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entity in world.PersistentIncludingDisabled())
            if (entity.Parent.IsNull && entity.TryGetComponent<InCell>(out var cell) && cell.Source is { } source
                && (source == whole || source.StartsWith(sectors, StringComparison.Ordinal)))
                sources.Add(source);
        foreach (var source in sources) ContentIds.Doze(world, source);
    }

    // The console's half: `space_live_max` and `spaces`.
    private void RegisterSpaceCommands(CVarRegistry cvars)
    {
        _liveMax = cvars.Register("space_live_max", 4, CVarFlags.Archive,
            "Scenes held live beside the player's: a `live` scene the player leaves stays placed and simulated, up to this many; 0 holds none (4m-17).",
            0, 32);
        cvars.RegisterCommand("spaces", CVarFlags.None, "spaces: the scene each world is in and the scenes held live beside it (4m-17).", _ =>
        {
            foreach (var world in _engine.Worlds)
            {
                Log.Info(LogCat.Console, $"'{world.Name}': in '{Current(world)}'");
                if (!world.Resources.TryGet<LiveSpaces>(out var live) || live == null) continue;
                foreach (var slot in live.Held)
                    Log.Info(LogCat.Console, $"  live: '{slot.Id}' ({(slot.Exterior ? "exterior" : "interior")}, {slot.Held.Count} placed, " +
                                             $"{slot.Levels.Count} level(s))");
            }
        });
    }
}

// The scenes held live beside the player's (4m-17), the one left longest ago first. Saved (`spaces`): a load
// places them again beside the save's scene (AfterLoad).
[SavedResource("spaces", Plugin = RegistrationOwners.Core)]
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
internal sealed class LiveSpaces : ISavedResource
{
    internal readonly List<ActiveScene> Held = new();
    private List<RecordId> _toOpen = new();

    // What a save writes and a load reads: the held scenes' ids.
    public List<RecordId> Scenes
    {
        get => Ids();
        set => _toOpen = value ?? new();
    }

    public static LiveSpaces Of(World world) => world.Resources.GetOrAdd(static () => new LiveSpaces());

    internal List<RecordId> Ids()
    {
        var ids = new List<RecordId>(Held.Count);
        foreach (var slot in Held) ids.Add(slot.Id);
        return ids;
    }

    internal int IndexOf(RecordId id)
    {
        for (int i = 0; i < Held.Count; i++)
            if (Held[i].Id == id) return i;
        return -1;
    }

    // An exterior is held: its ground stays while the player is inside.
    internal bool HoldsExterior
    {
        get
        {
            foreach (var slot in Held)
                if (slot.Exterior) return true;
            return false;
        }
    }

    // The cell of the held scene whose things stand around `absolute`, if any (Cells.Current). A streamed
    // exterior's runtime spawns belong to its sectors, so it is not asked here.
    internal bool TryCellAt(Vector3 absolute, [NotNullWhen(true)] out string? cell)
    {
        foreach (var slot in Held)
        {
            if (slot.Streamed != null) continue;
            if (absolute.X >= slot.Min.X && absolute.Y >= slot.Min.Y && absolute.Z >= slot.Min.Z
                && absolute.X <= slot.Max.X && absolute.Y <= slot.Max.Y && absolute.Z <= slot.Max.Z)
            {
                cell = ContentIds.SceneSource(slot.Id);
                return true;
            }
        }
        cell = null;
        return false;
    }

    public void AfterLoad(World world)
    {
        var open = _toOpen;
        _toOpen = new();
        if (world.Engine is not { } engine) return;
        foreach (var id in open) engine.Scenes.OpenLive(world, id);
    }
}

// Gravity by space (4m-17): the player's scene's scale everywhere but in the boxes of the scenes held live
// beside it, each its own. Physics multiplies the world's gravity by it for each body, and a character
// controller its profile's gravity. A world resource every world has once a scene is placed.
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
public sealed class SpaceGravity
{
    private GravityRegion[] _regions = Array.Empty<GravityRegion>();

    // The player's space's scale: everywhere no region covers.
    public float Scale { get; private set; } = 1f;

    // The boxes with a gravity of their own (absolute metres), the first that holds a point wins.
    public int RegionCount { get; private set; }
    public GravityRegion Region(int index) => _regions[index];

    // Changes each time the scales or the boxes do: for a cache of them.
    public int Version { get; private set; }

    public static SpaceGravity Of(World world) => world.Resources.GetOrAdd(static () => new SpaceGravity());

    // The scale at a position in the world's frame (origin space).
    public float ScaleAt(World world, Vector3 position)
    {
        if (RegionCount == 0) return Scale;
        var at = world.Origin().ToAbsolute(position);
        for (int i = 0; i < RegionCount; i++)
            if (_regions[i].Contains(at)) return _regions[i].Scale;
        return Scale;
    }

    internal void Set(float scale, List<GravityRegion> regions)
    {
        if (_regions.Length < regions.Count) _regions = new GravityRegion[regions.Count];
        regions.CopyTo(_regions);
        RegionCount = regions.Count;
        Scale = scale;
        Version++;
    }
}

// A box (absolute metres) whose gravity is `Scale` times the world's.
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
public readonly struct GravityRegion
{
    public readonly Vector3 Min, Max;
    public readonly float Scale;

    public GravityRegion(Vector3 min, Vector3 max, float scale)
    {
        Min = min;
        Max = max;
        Scale = scale;
    }

    public bool Contains(Vector3 at) =>
        at.X >= Min.X && at.Y >= Min.Y && at.Z >= Min.Z && at.X <= Max.X && at.Y <= Max.Y && at.Z <= Max.Z;
}
