#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Simulation;

// Per-sector asset scopes (issue #277; docs/design/14 §3 "As built (the far ring, asset scopes and jobs)").
//
// A sector brings meshes and textures with it: its terrain chunks (built by the client, owned by a handle),
// the models of what a streamed scene places in it, and their textures (a named material's, a sprite
// sheet's, a particle effect's; issue #308). Before this, unloading a sector destroyed the entities and
// left their GPU buffers behind (MapMesh's comment said so), so a long walk grew without bound.
//
// The rule is **ref-counted by live users, released by the sector that let go**:
//
//   * every live `MeshRenderer` and `SkinnedMeshRenderer` holds a reference to its mesh path, and every
//     drawing component (those two, `SpriteRenderer`, `ParticleEmitter`) to the textures it draws with
//     (`AssetUse`), counted from the world's structural events (an entity counts what it had when the
//     component was added, so a record reloaded since cannot unbalance the count);
//   * when a sector leaves — its terrain unloads (Terrain.Unloaded) or a streamed sector goes dormant
//     (StreamedScene.Unplace) — what it destroys is destroyed inside `Leave()`, and an asset whose count
//     reaches zero there is **released** (`Released`); one still drawn by anything anywhere is kept;
//   * a renderer-built mesh (`MeshRenderer.Handle`) on an entity a leaving sector destroys is the sector's
//     alone, and is released with it (`HandleReleased`);
//   * what is not a sector's — the player's sword, a HUD model, a projectile's mesh — is the game's scope:
//     its count may reach zero between uses, and it is not released for that, only when a sector lets go.
//
// The client collects what was released (`AssetReleases`, docs in Content/AssetScopes.cs) and frees it at
// its next frame's safe point, if no world has taken it up again in between (`IsHeld`). Headless, so the
// bookkeeping is tested without a GPU.
internal sealed class SectorAssets
{
    private readonly World _world;
    private readonly RecordStore? _records;
    private readonly Dictionary<AssetKey, int> _refs = new();
    private readonly Dictionary<(Entity Entity, Type Component), Holding> _held = new();
    private readonly List<AssetKey> _scratch = new();
    private int _leaving;   // depth of open Leave() windows

    private readonly record struct Holding(AssetKey[] Keys, MeshHandle Handle);

    public SectorAssets(World world)
    {
        _world = world;
        world.Resources.TryGet(out _records);
        world.ComponentAdded += OnAdded;
        world.ComponentRemoved += OnRemoved;
    }

    // Raised for an asset no live entity uses any more, released by a sector that left.
    public event Action<AssetKey>? Released;

    // Raised for a renderer-built mesh a leaving sector's entity held.
    public event Action<MeshHandle>? HandleReleased;

    // How many assets and handles have been released so far, and how many are held now (`stream_status`).
    public int ReleasedPaths { get; private set; }
    public int ReleasedHandles { get; private set; }
    public int HeldPaths => _refs.Count;

    public int RefCount(AssetKey key) => _refs.TryGetValue(key, out int n) ? n : 0;
    public bool IsHeld(AssetKey key) => _refs.ContainsKey(key);

    // A mesh path's count (the common question).
    public int RefCount(AssetPath mesh) => RefCount(AssetKey.Mesh(mesh));
    public bool IsHeld(AssetPath mesh) => IsHeld(AssetKey.Mesh(mesh));

    // Everything held now, with its count (`asset_list`, tests).
    public IEnumerable<KeyValuePair<AssetKey, int>> Held => _refs;

    // `using (assets.Leave()) { ...destroy... }`: what is destroyed inside is a leaving sector's, and is
    // released as above. Windows nest (a sector's terrain unloading inside a scene's tick boundary).
    public Window Leave() => new(this);

    internal readonly struct Window : IDisposable
    {
        private readonly SectorAssets _assets;
        public Window(SectorAssets assets) { _assets = assets; assets._leaving++; }
        public void Dispose() => _assets._leaving--;
    }

    private static bool Tracked(Type type) =>
        type == typeof(MeshRenderer) || type == typeof(SkinnedMeshRenderer) || type == typeof(SpriteRenderer) || type == typeof(ParticleEmitter);

    private void OnAdded(Entity entity, Type type)
    {
        if (!Tracked(type)) return;
        _scratch.Clear();
        var handle = default(MeshHandle);
        if (type == typeof(MeshRenderer))
        {
            var renderer = _world.Get<MeshRenderer>(entity);
            AssetUse.Of(_records, renderer, _scratch);
            handle = renderer.Handle;
        }
        else if (type == typeof(SkinnedMeshRenderer)) AssetUse.Of(_records, _world.Get<SkinnedMeshRenderer>(entity), _scratch);
        else if (type == typeof(SpriteRenderer)) AssetUse.Of(_records, _world.Get<SpriteRenderer>(entity), _scratch);
        else AssetUse.Of(_records, _world.Get<ParticleEmitter>(entity), _scratch);

        // Taken before the old ones are dropped, so a component set again with the same assets never
        // passes through zero.
        foreach (var key in _scratch) Take(key);
        if (_held.TryGetValue((entity, type), out var before))
            foreach (var key in before.Keys) Drop(key);
        if (_scratch.Count == 0 && handle.IsEmpty) _held.Remove((entity, type));
        else _held[(entity, type)] = new Holding(_scratch.ToArray(), handle);
    }

    private void OnRemoved(Entity entity, Type type)
    {
        if (!Tracked(type) || !_held.Remove((entity, type), out var held)) return;
        foreach (var key in held.Keys) Drop(key);
        if (_leaving > 0 && !held.Handle.IsEmpty)
        {
            ReleasedHandles++;
            HandleReleased?.Invoke(held.Handle);
        }
    }

    private void Take(AssetKey key) => _refs[key] = RefCount(key) + 1;

    private void Drop(AssetKey key)
    {
        if (!_refs.TryGetValue(key, out int n)) return;
        if (n > 1)
        {
            _refs[key] = n - 1;
            return;
        }
        _refs.Remove(key);
        if (_leaving == 0) return;   // the game's scope: kept until a sector lets go of it
        ReleasedPaths++;
        Released?.Invoke(key);
    }
}
