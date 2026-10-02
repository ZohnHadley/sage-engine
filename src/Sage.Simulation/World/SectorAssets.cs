#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Simulation;

// Per-sector asset scopes (issue #277; docs/design/14 §3 "As built (the far ring, asset scopes and jobs)").
//
// A sector brings meshes with it: its terrain chunks (built by the client, owned by a handle) and the
// models of what a streamed scene places in it. Before this, unloading a sector destroyed the entities and
// left their GPU buffers behind (MapMesh's comment said so), so a long walk grew without bound.
//
// The rule is **ref-counted by live users, released by the sector that let go**:
//
//   * every live `MeshRenderer` and `SkinnedMeshRenderer` holds a reference to its mesh path, counted from
//     the world's structural events (an entity counts what it had when the component was added);
//   * when a sector leaves — its terrain unloads (Terrain.Unloaded) or a streamed sector goes dormant
//     (StreamedScene.Unplace) — what it destroys is destroyed inside `Leave()`, and a mesh path whose count
//     reaches zero there is **released** (`Released`); one still drawn by anything anywhere is kept;
//   * a renderer-built mesh (`MeshRenderer.Handle`) on an entity a leaving sector destroys is the sector's
//     alone, and is released with it (`HandleReleased`);
//   * what is not a sector's — the player's sword, a HUD model, a projectile's mesh — is the game's scope:
//     its count may reach zero between uses, and it is not released for that, only when a sector lets go.
//
// The client listens (TerrainMeshSystem) and frees the buffers on its next frame, if nothing has taken the
// path up again in between (`IsHeld`). Headless, so the bookkeeping is tested without a GPU.
internal sealed class SectorAssets
{
    private readonly World _world;
    private readonly Dictionary<AssetPath, int> _refs = new();
    private readonly Dictionary<Entity, Held> _held = new();
    private int _leaving;   // depth of open Leave() windows

    private readonly record struct Held(AssetPath Mesh, MeshHandle Handle, AssetPath Skinned);

    public SectorAssets(World world)
    {
        _world = world;
        world.ComponentAdded += OnAdded;
        world.ComponentRemoved += OnRemoved;
    }

    // Raised for a mesh path no live entity uses any more, released by a sector that left.
    public event Action<AssetPath>? Released;

    // Raised for a renderer-built mesh a leaving sector's entity held.
    public event Action<MeshHandle>? HandleReleased;

    // How many paths and handles have been released so far, and how many paths are held now (`stream_status`).
    public int ReleasedPaths { get; private set; }
    public int ReleasedHandles { get; private set; }
    public int HeldPaths => _refs.Count;

    public int RefCount(AssetPath path) => _refs.TryGetValue(path, out int n) ? n : 0;

    public bool IsHeld(AssetPath path) => _refs.ContainsKey(path);

    // `using (assets.Leave()) { ...destroy... }`: what is destroyed inside is a leaving sector's, and is
    // released as above. Windows nest (a sector's terrain unloading inside a scene's tick boundary).
    public Window Leave() => new(this);

    internal readonly struct Window : IDisposable
    {
        private readonly SectorAssets _assets;
        public Window(SectorAssets assets) { _assets = assets; assets._leaving++; }
        public void Dispose() => _assets._leaving--;
    }

    private void OnAdded(Entity entity, Type type)
    {
        if (type != typeof(MeshRenderer) && type != typeof(SkinnedMeshRenderer)) return;
        _held.TryGetValue(entity, out var before);
        var now = before;
        if (type == typeof(MeshRenderer))
        {
            var renderer = _world.Get<MeshRenderer>(entity);
            now = now with { Mesh = renderer.Mesh, Handle = renderer.Handle };
            Swap(before.Mesh, now.Mesh);
        }
        else
        {
            now = now with { Skinned = _world.Get<SkinnedMeshRenderer>(entity).Mesh };
            Swap(before.Skinned, now.Skinned);
        }
        _held[entity] = now;
    }

    private void OnRemoved(Entity entity, Type type)
    {
        if (type != typeof(MeshRenderer) && type != typeof(SkinnedMeshRenderer)) return;
        if (!_held.TryGetValue(entity, out var held)) return;
        if (type == typeof(MeshRenderer))
        {
            Drop(held.Mesh);
            if (_leaving > 0 && !held.Handle.IsEmpty)
            {
                ReleasedHandles++;
                HandleReleased?.Invoke(held.Handle);
            }
            held = held with { Mesh = default, Handle = default };
        }
        else
        {
            Drop(held.Skinned);
            held = held with { Skinned = default };
        }
        if (held.Mesh.IsEmpty && held.Skinned.IsEmpty && held.Handle.IsEmpty) _held.Remove(entity);
        else _held[entity] = held;
    }

    private void Swap(AssetPath before, AssetPath now)
    {
        if (before == now) return;
        Take(now);
        Drop(before);
    }

    private void Take(AssetPath path)
    {
        if (path.IsEmpty) return;
        _refs[path] = RefCount(path) + 1;
    }

    private void Drop(AssetPath path)
    {
        if (path.IsEmpty || !_refs.TryGetValue(path, out int n)) return;
        if (n > 1)
        {
            _refs[path] = n - 1;
            return;
        }
        _refs.Remove(path);
        if (_leaving == 0) return;   // the game's scope: kept until a sector lets go of it
        ReleasedPaths++;
        Released?.Invoke(path);
    }
}
