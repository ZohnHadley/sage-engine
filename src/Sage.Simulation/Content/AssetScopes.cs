#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Simulation;

// Asset scopes, ref-counted release, eviction and the upload budget (issue #308; docs/design/05 §3.3, §3.4).
//
// The client's GPU objects (meshes, textures) live in `AssetTable`s; this file is their bookkeeping, kept
// headless so a test can prove it without a graphics device. Who owns what:
//
//   * **AssetTable<T>**: one kind of loaded thing by stable id. Slot 0 is the placeholder (the error mesh,
//     the checker texture), which is never evicted. A freed slot is reused by the next load and ids never
//     shift, because snapshots, materials and components hold them.
//   * **SectorAssets** (one per streaming world): how many live entities hold each mesh and texture, and
//     which a sector released when it unloaded.
//   * **AssetReleases**: what the sectors of every world released, handed to the client at the frame's
//     safe point (FrameUpdate, before Extract) — and only what no world holds again by then.
//   * **UploadBudget**: how many milliseconds of loading a frame may spend on streamed content.
//
// The scopes (05 §3.3), and when each lets go:
//
//   Engine  the placeholders, engine shaders and anything registered from a stream: never.
//   Game    loaded for the game's life (the viewmodel, a game's own code, a world that does not stream,
//           the post chain): kept until the process ends.
//   Sector  loaded to draw a streaming world's content: evicted when a sector that unloads lets go of it
//           and no live entity in any world still holds it (SectorAssets counts them).
//   Ui      loaded to draw a screen or the HUD: evicted when no screen has drawn it for `UiKeepFrames`.
//
// An asset asked for at two scopes keeps the stronger one (the order of the enum), so a texture both the
// HUD and a sector's sign use stays while either needs it.
internal enum AssetScope : byte
{
    Engine,
    Game,
    Sector,
    Ui,
}

// What SectorAssets counts. Effects and sounds are the game's (a handful each, shared by everything).
internal enum AssetType : byte
{
    Mesh,
    Texture,
}

internal readonly record struct AssetKey(AssetType Type, AssetPath Path)
{
    public static AssetKey Mesh(AssetPath path) => new(AssetType.Mesh, path);
    public static AssetKey Texture(AssetPath path) => new(AssetType.Texture, path);
    public override string ToString() => $"{(Type == AssetType.Mesh ? "mesh" : "texture")} {Path}";
}

internal static class AssetScopes
{
    // A UI-scoped asset no screen has drawn for this many frames is evicted (about five seconds at 60 Hz):
    // long enough that opening and closing the inventory does not reload its pictures.
    public const int UiKeepFrames = 300;

    // The stronger of two scopes: the one that keeps the asset longer.
    public static AssetScope Stronger(AssetScope a, AssetScope b) => a <= b ? a : b;

    public static string Name(AssetScope scope) => scope switch
    {
        AssetScope.Engine => "engine",
        AssetScope.Game => "game",
        AssetScope.Sector => "sector",
        _ => "ui",
    };
}

// One kind of loaded asset, by stable id (see the top of the file). Not thread-safe: the main thread's.
internal sealed class AssetTable<T> where T : class
{
    public struct Entry
    {
        public AssetPath Path;      // empty for a built mesh (a terrain chunk, a box): it has no file
        public T? Value;            // null in a free slot
        public AssetScope Scope;
        public long Bytes;          // what it takes on the GPU, as near as the loader can say
        public long LastUsed;       // the frame it was last asked for
    }

    private Entry[] _entries = new Entry[16];
    private int _length = 1;                      // slot 0 is the placeholder
    private readonly Stack<int> _free = new();
    private readonly Dictionary<AssetPath, int> _ids = new();   // 0 = tried and failed: the placeholder

    public AssetTable(T? placeholder)
    {
        _entries[0] = new Entry { Value = placeholder, Scope = AssetScope.Engine };
    }

    // Loaded now, not counting the placeholder.
    public int Count { get; private set; }

    // Slots ever used (the table's length); never shrinks, and grows only when no slot is free.
    public int Slots => _length;

    public long Bytes { get; private set; }

    public T? Placeholder
    {
        get => _entries[0].Value;
        set => _entries[0].Value = value;
    }

    // The value in a slot; the placeholder for a free or failed one.
    public T? this[int id] => (uint)id < (uint)_length && _entries[id].Value is { } value ? value : _entries[0].Value;

    public ref readonly Entry EntryAt(int id) => ref _entries[id];

    public bool IsLive(int id) => id > 0 && id < _length && _entries[id].Value != null;

    // The id a path was loaded under: 0 when it was tried and failed (draw the placeholder), false when it
    // has not been asked for.
    public bool TryFind(AssetPath path, out int id) => _ids.TryGetValue(path, out id);

    // Asked for again this frame, perhaps at a stronger scope.
    public void Use(int id, AssetScope scope, long frame)
    {
        if (id <= 0 || id >= _length) return;
        ref var entry = ref _entries[id];
        entry.Scope = AssetScopes.Stronger(entry.Scope, scope);
        entry.LastUsed = frame;
    }

    // The id for a path, loading it with `load` on first use (a failure is remembered as 0, the
    // placeholder), and otherwise marking it used this frame at `scope`. With a budget, a Sector-scoped
    // load waits for a frame with budget left (05 §3.4): false means "not loaded yet, draw nothing for it
    // this frame". `load` is timed against the budget.
    public bool TryResolve(AssetPath path, AssetScope scope, long frame, UploadBudget? budget,
                           Func<AssetPath, (T? Value, long Bytes)> load, out int id)
    {
        if (_ids.TryGetValue(path, out id))
        {
            Use(id, scope, frame);
            return true;
        }
        bool budgeted = budget != null && scope == AssetScope.Sector;
        if (budgeted && !budget!.TryStart()) return false;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var (value, bytes) = load(path);
        if (budgeted) budget!.Spend(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        if (value == null)
        {
            AddFailed(path);
            id = 0;
        }
        else id = Add(path, value, scope, bytes, frame);
        return true;
    }

    // A released asset (AssetReleases): freed when it is loaded at a scope a sector lets go of (Sector or
    // Ui), or forgotten when it had failed, so it is tried again. `value` is for the caller to dispose.
    public bool TryEvict(AssetPath path, out T? value)
    {
        value = null;
        if (!_ids.TryGetValue(path, out int id)) return false;
        if (id != 0 && _entries[id].Scope is AssetScope.Engine or AssetScope.Game) return false;
        value = Forget(path);
        return true;
    }

    public int Add(AssetPath path, T value, AssetScope scope, long bytes, long frame)
    {
        int id = Place(new Entry { Path = path, Value = value, Scope = scope, Bytes = bytes, LastUsed = frame });
        if (!path.IsEmpty) _ids[path] = id;
        return id;
    }

    // A load that failed: the path draws the placeholder and is not tried again until it is forgotten.
    public void AddFailed(AssetPath path) => _ids[path] = 0;

    // A hot reload: the new value in the old slot, so every id handed out sees it. The old one is returned
    // for the caller to dispose.
    public T? Replace(int id, T value, long bytes)
    {
        ref var entry = ref _entries[id];
        var old = entry.Value;
        Bytes += bytes - entry.Bytes;
        entry.Value = value;
        entry.Bytes = bytes;
        return old;
    }

    // Frees a slot (never the placeholder's) and forgets its path; the value is returned for the caller to
    // dispose. The slot is reused by a later load.
    public T? Remove(int id)
    {
        if (id <= 0 || id >= _length || _entries[id].Value == null) return null;
        var entry = _entries[id];
        if (!entry.Path.IsEmpty && _ids.TryGetValue(entry.Path, out int mapped) && mapped == id) _ids.Remove(entry.Path);
        _entries[id] = default;
        _free.Push(id);
        Count--;
        Bytes -= entry.Bytes;
        return entry.Value;
    }

    // Forgets a path, loaded or failed: the loaded value is returned (and its slot freed), a failure is just
    // forgotten so the next ask tries again.
    public T? Forget(AssetPath path)
    {
        if (!_ids.Remove(path, out int id)) return null;
        return id == 0 ? null : Remove(id);
    }

    // Live entries, for listing: id and entry.
    public IEnumerable<(int Id, Entry Entry)> Entries
    {
        get
        {
            for (int id = 1; id < _length; id++)
                if (_entries[id].Value != null) yield return (id, _entries[id]);
        }
    }

    // Paths that failed to load (they draw the placeholder).
    public IEnumerable<AssetPath> Failed
    {
        get
        {
            foreach (var (path, id) in _ids)
                if (id == 0) yield return path;
        }
    }

    // UI-scoped entries no screen has asked for since `frame - keep`, freed through `free` (which disposes
    // the value). Returns how many.
    public int CollectUnusedUi(long frame, int keep, Action<AssetPath, T> free)
    {
        int n = 0;
        for (int id = 1; id < _length; id++)
        {
            ref readonly var entry = ref _entries[id];
            if (entry.Value == null || entry.Scope != AssetScope.Ui || frame - entry.LastUsed <= keep) continue;
            var path = entry.Path;
            var value = Remove(id)!;
            free(path, value);
            n++;
        }
        return n;
    }

    private int Place(Entry entry)
    {
        int id;
        if (_free.Count > 0) id = _free.Pop();
        else
        {
            if (_length == _entries.Length) Array.Resize(ref _entries, _entries.Length * 2);
            id = _length++;
        }
        _entries[id] = entry;
        Count++;
        Bytes += entry.Bytes;
        return id;
    }
}

// What the sectors of every world released (SectorAssets.Released), collected engine-wide and handed out
// at the frame's safe point. A key some world has taken up again since (a sector came straight back, or
// another world draws the same model) is dropped from the list, not evicted.
internal sealed class AssetReleases
{
    private readonly List<AssetKey> _pending = new();
    private readonly HashSet<AssetKey> _queued = new();

    public int Pending => _pending.Count;

    // Total evicted so far (`asset_list`'s footer, `stream_status`).
    public int Evicted { get; private set; }

    public void Watch(SectorAssets assets) => assets.Released += Add;

    public void Add(AssetKey key)
    {
        if (_queued.Add(key)) _pending.Add(key);
    }

    // Every pending key no world holds now goes to `evict`, which frees it if it is loaded at a scope a
    // sector may let go of and returns whether it did. Returns how many were freed.
    public int Collect(IReadOnlyList<World> worlds, Func<AssetKey, bool> evict)
    {
        if (_pending.Count == 0) return 0;
        int n = 0;
        for (int i = 0; i < _pending.Count; i++)
        {
            var key = _pending[i];
            if (IsHeld(worlds, key)) continue;
            if (evict(key)) n++;
        }
        _pending.Clear();
        _queued.Clear();
        Evicted += n;
        return n;
    }

    // Held by a live entity in any world.
    public static bool IsHeld(IReadOnlyList<World> worlds, AssetKey key)
    {
        for (int w = 0; w < worlds.Count; w++)
            if (worlds[w].Resources.TryGet<SectorAssets>(out var assets) && assets != null && assets.IsHeld(key)) return true;
        return false;
    }

    // How many live entities in every world hold it (`asset_list`).
    public static int RefCount(IReadOnlyList<World> worlds, AssetKey key)
    {
        int n = 0;
        for (int w = 0; w < worlds.Count; w++)
            if (worlds[w].Resources.TryGet<SectorAssets>(out var assets) && assets != null) n += assets.RefCount(key);
        return n;
    }
}

// The per-frame upload budget (05 §3.4, `asset_upload_ms`): streamed content loads at most this many
// milliseconds a frame, and what does not fit waits for the next frame (it is not drawn until then).
// The first load of a frame always starts, so one large asset still arrives; a budget of 0 is no budget.
// Only Sector-scoped loads are budgeted: the player's own sword, the HUD and a level loaded whole are not.
internal sealed class UploadBudget
{
    private long _frame = long.MinValue;
    private int _started;

    public float MillisecondsPerFrame { get; set; } = 2f;

    // This frame's spending, and how many loads it put off.
    public double Spent { get; private set; }
    public int Deferred { get; private set; }

    // Every load put off so far.
    public long TotalDeferred { get; private set; }

    public void BeginFrame(long frame)
    {
        if (frame == _frame) return;
        _frame = frame;
        _started = 0;
        Spent = 0;
        Deferred = 0;
    }

    // Whether a load may start now. False puts it off to a later frame.
    public bool TryStart()
    {
        if (MillisecondsPerFrame > 0f && _started > 0 && Spent >= MillisecondsPerFrame)
        {
            Deferred++;
            TotalDeferred++;
            return false;
        }
        _started++;
        return true;
    }

    public void Spend(double milliseconds) => Spent += milliseconds;
}

// Which assets a component draws with, as SectorAssets counts them: its mesh, and the textures of its sprite
// sheet, particle effect and named material. A default material (an empty `Material`) is the engine's, and
// its textures are not counted: nothing a sector does releases them.
internal static class AssetUse
{
    public static void Of(RecordStore? records, in MeshRenderer renderer, List<AssetKey> keys)
    {
        if (!renderer.Mesh.IsEmpty) keys.Add(AssetKey.Mesh(renderer.Mesh));
        MaterialTextures(records, renderer.Material, keys);
    }

    public static void Of(RecordStore? records, in SkinnedMeshRenderer renderer, List<AssetKey> keys)
    {
        if (!renderer.Mesh.IsEmpty) keys.Add(AssetKey.Mesh(renderer.Mesh));
        MaterialTextures(records, renderer.Material, keys);
    }

    public static void Of(RecordStore? records, in SpriteRenderer renderer, List<AssetKey> keys)
    {
        if (records != null && Registered<SpriteSheetRecord>(records, "sprite_sheet") && records.TryGet(renderer.Sheet, out SpriteSheetRecord sheet))
        {
            if (!sheet.Texture.IsEmpty) keys.Add(AssetKey.Texture(sheet.Texture));
            if (renderer.Material.IsEmpty) MaterialTextures(records, sheet.Material.Id, keys);
        }
        MaterialTextures(records, renderer.Material, keys);
    }

    public static void Of(RecordStore? records, in ParticleEmitter emitter, List<AssetKey> keys)
    {
        if (records == null || !Registered<ParticleRecord>(records, "particle") || !records.TryGet(emitter.Effect, out ParticleRecord effect)) return;
        if (!effect.Texture.IsEmpty) keys.Add(AssetKey.Texture(effect.Texture));
        MaterialTextures(records, effect.Material.Id, keys);
    }

    private static void MaterialTextures(RecordStore? records, RecordId material, List<AssetKey> keys)
    {
        if (material.IsEmpty || records == null || !Registered<MaterialRecord>(records, "material")) return;
        if (!records.TryGet(material, out MaterialRecord record)) return;
        foreach (var (_, value) in record.Params)
            if (value.IsTexture) keys.Add(AssetKey.Texture(value.Texture));
    }

    // A headless run has no client, so the client's record types may not be registered (TryGet would throw).
    private static bool Registered<T>(RecordStore records, string type) => records.TypeOf(type) == typeof(T);
}
