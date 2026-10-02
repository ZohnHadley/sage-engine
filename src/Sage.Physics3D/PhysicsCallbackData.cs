#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;

namespace Sage.Physics3D;

// What Bepu's callbacks are allowed to touch (docs/design/10 §3). Reads (layers, entity mapping,
// materials) happen on worker threads during the step and are only written between steps from the
// main thread. Trigger overlaps and reported contacts are written into per-worker buffers and merged
// afterwards.
internal sealed class PhysicsCallbackData
{
    private const int Grow = 64;

    private Entry[] _bodies = new Entry[Grow];
    private Entry[] _statics = new Entry[Grow];
    private readonly List<(uint A, uint B)>[] _workerTriggers;
    private readonly List<ContactReport>[] _workerContacts;

    private readonly HashSet<(uint A, uint B)> _overlapping = new();   // pairs overlapping last step
    private readonly HashSet<(uint A, uint B)> _current = new();
    private readonly List<TriggerOverlap> _entered = new();
    private readonly List<TriggerOverlap> _exited = new();

    private readonly HashSet<(uint A, uint B)> _touching = new();      // reported contacts touching last step
    private readonly HashSet<(uint A, uint B)> _touchingNow = new();
    private readonly List<ContactEvent> _contactBegin = new();
    private readonly List<ContactEvent> _contactEnd = new();

    private readonly record struct ContactReport(uint A, uint B, Vector3 Point, Vector3 Normal, float Impulse, float Speed, bool Touching);

    // Pairs that asked for contacts and are about to touch (a speculative contact): the hardest each was
    // closing, carried from step to step until it touches, which is when Collided reports it (issue #269).
    private Dictionary<(uint A, uint B), (float Speed, float Impulse)> _approach = new();
    private Dictionary<(uint A, uint B), (float Speed, float Impulse)> _approachNow = new();

    public PhysicsCallbackData(int workerCount)
    {
        _workerTriggers = new List<(uint, uint)>[workerCount];
        _workerContacts = new List<ContactReport>[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            _workerTriggers[i] = new List<(uint, uint)>();
            _workerContacts[i] = new List<ContactReport>();
        }
    }

    // The simulation these callbacks belong to, for the step boundaries: a pair whose bodies are all
    // asleep (or static) is not tested at all, and must not read as having come apart.
    public BepuPhysics.Simulation? Simulation { get; set; }

    public LayerMatrix Layers { get; } = new();

    // Spans, not interfaces: systems read these every tick and foreach over an interface would box
    // an enumerator each time.
    public ReadOnlySpan<TriggerOverlap> Entered => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_entered);
    public ReadOnlySpan<TriggerOverlap> Exited => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_exited);
    public ReadOnlySpan<ContactEvent> ContactBegin => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_contactBegin);
    public ReadOnlySpan<ContactEvent> ContactEnd => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_contactEnd);

    private struct Entry
    {
        public Entity Entity;
        public byte Layer;
        public bool Trigger;
        public bool Contacts;   // reports contact begin/end (Collider.ReportContacts)
        public bool Used;
        public float Friction;
        public float Restitution;
        public int Group;       // bodies sharing a nonzero group never collide (issue #242)
        public RecordId Surface;   // what it is made of (issue #270)
        public SurfaceMap? Map;    // per face or per triangle, when they differ
    }

    // A collider whose surface differs across it (issue #270): by face normal for a hull, by triangle
    // for a mesh. Read by queries on the main thread; written only between steps.
    internal sealed class SurfaceMap
    {
        public Vector3[]? Normals;      // a hull's faces, one per entry of Surfaces
        public byte[]? Triangles;       // a mesh's triangles, each an index into Surfaces
        public RecordId[] Surfaces = Array.Empty<RecordId>();
    }

    // ---- Registration (main thread, between steps) ----

    public void RegisterBody(int handle, Entity entity, byte layer, bool trigger, float friction, float restitution, bool contacts = false, int group = 0) =>
        Register(ref _bodies, handle, entity, layer, trigger, friction, restitution, contacts, group);

    public void RegisterStatic(int handle, Entity entity, byte layer, bool trigger, float friction, float restitution, bool contacts = false, int group = 0) =>
        Register(ref _statics, handle, entity, layer, trigger, friction, restitution, contacts, group);

    // A body's or a static's collision group (0 = none). Unknown handles are ignored.
    public void SetGroup(int handle, bool isStatic, int group)
    {
        var entries = isStatic ? _statics : _bodies;
        if ((uint)handle < (uint)entries.Length && entries[handle].Used) entries[handle].Group = group;
    }

    public int GroupOf(int handle, bool isStatic)
    {
        var entries = isStatic ? _statics : _bodies;
        return (uint)handle < (uint)entries.Length ? entries[handle].Group : 0;
    }

    public Entity BodyEntity(int handle, bool isStatic)
    {
        var entries = isStatic ? _statics : _bodies;
        return (uint)handle < (uint)entries.Length ? entries[handle].Entity : default;
    }

    // A static that has become a body (issue #261: a mover's brush made kinematic) keeps what it was
    // registered with: its entity, layer, trigger flag, material and group.
    public void StaticBecameBody(int staticHandle, int bodyHandle)
    {
        var entry = (uint)staticHandle < (uint)_statics.Length ? _statics[staticHandle] : default;
        UnregisterStatic(staticHandle);
        if (!entry.Used) return;
        if (bodyHandle >= _bodies.Length) Array.Resize(ref _bodies, Math.Max(bodyHandle + 1, _bodies.Length * 2));
        _bodies[bodyHandle] = entry;
    }

    // ---- Surfaces (issue #270; main thread) ----

    public void SetSurface(int handle, bool isStatic, RecordId surface, SurfaceMap? map, float? friction, float? restitution)
    {
        var entries = isStatic ? _statics : _bodies;
        if ((uint)handle >= (uint)entries.Length || !entries[handle].Used) return;
        ref var entry = ref entries[handle];
        entry.Surface = surface;
        entry.Map = map;
        if (friction is { } f) entry.Friction = f;
        if (restitution is { } r) entry.Restitution = r;
    }

    public RecordId SurfaceOf(int handle, bool isStatic)
    {
        var entries = isStatic ? _statics : _bodies;
        return (uint)handle < (uint)entries.Length ? entries[handle].Surface : default;
    }

    // What a query hit is made of, where it hit: the triangle (`child`) of a mesh, the face nearest
    // `normal` of a hull, or the collider's own. Allocation-free.
    public RecordId SurfaceAt(CollidableReference collidable, int child, Vector3 normal)
    {
        ref var entry = ref EntryOf(collidable);
        var map = entry.Map;
        if (map == null) return entry.Surface;
        if (map.Triangles != null)
            return (uint)child < (uint)map.Triangles.Length && map.Triangles[child] < map.Surfaces.Length
                ? map.Surfaces[map.Triangles[child]] : entry.Surface;
        if (map.Normals != null)
        {
            int best = -1;
            float closest = float.NegativeInfinity;
            for (int i = 0; i < map.Normals.Length; i++)
            {
                float d = Vector3.Dot(map.Normals[i], normal);
                if (d > closest) { closest = d; best = i; }
            }
            if (best >= 0) return map.Surfaces[best];
        }
        return entry.Surface;
    }

    // Whether a hit on this collidable needs its triangle to say what it hit (a mesh with a map).
    public bool HasTriangleSurfaces(CollidableReference collidable) => EntryOf(collidable).Map?.Triangles != null;

    public void UnregisterBody(int handle) { if (handle < _bodies.Length) _bodies[handle] = default; }
    public void UnregisterStatic(int handle) { if (handle < _statics.Length) _statics[handle] = default; }

    private static void Register(ref Entry[] entries, int handle, Entity entity, byte layer, bool trigger, float friction, float restitution, bool contacts, int group)
    {
        if (handle >= entries.Length) Array.Resize(ref entries, Math.Max(handle + 1, entries.Length * 2));
        entries[handle] = new Entry { Entity = entity, Layer = layer, Trigger = trigger, Contacts = contacts, Used = true, Friction = friction, Restitution = restitution, Group = group };
    }

    // ---- Callbacks (worker threads, during the step) ----

    private ref Entry EntryOf(CollidableReference collidable)
    {
        if (collidable.Mobility == CollidableMobility.Static)
        {
            int handle = collidable.StaticHandle.Value;
            if (handle < _statics.Length) return ref _statics[handle];
        }
        else
        {
            int handle = collidable.BodyHandle.Value;
            if (handle < _bodies.Length) return ref _bodies[handle];
        }
        return ref _missing;
    }

    private Entry _missing;

    public bool ShouldCollide(CollidableReference a, CollidableReference b)
    {
        ref var ea = ref EntryOf(a);
        ref var eb = ref EntryOf(b);
        if (!ea.Used || !eb.Used) return true;              // unknown collidables collide by default
        if (ea.Trigger && eb.Trigger) return false;         // two triggers ignore each other
        if (ea.Group != 0 && ea.Group == eb.Group) return false;   // one collision group (issue #242)
        return Layers.Collide(ea.Layer, eb.Layer);
    }

    public bool IsTrigger(CollidableReference collidable) => EntryOf(collidable).Trigger;

    public bool WantsContacts(CollidablePair pair) => EntryOf(pair.A).Contacts || EntryOf(pair.B).Contacts;

    public void MaterialFor(CollidablePair pair, out float friction, out float restitution)
    {
        ref var a = ref EntryOf(pair.A);
        ref var b = ref EntryOf(pair.B);
        friction = MathF.Sqrt(MathF.Max(a.Friction, 0.01f) * MathF.Max(b.Friction, 0.01f));
        restitution = MathF.Max(a.Restitution, b.Restitution);
    }

    public void ReportTrigger(int workerIndex, CollidablePair pair)
    {
        if ((uint)workerIndex >= (uint)_workerTriggers.Length) workerIndex = 0;
        _workerTriggers[workerIndex].Add((pair.A.Packed, pair.B.Packed));
    }

    // A solid contact on a pair that asked for them: `point` in origin space, `normal` from B to A, and
    // how hard they were closing (Collided, issue #269); not `touching` yet is an approach.
    public void ReportContact(int workerIndex, CollidablePair pair, Vector3 point, Vector3 normal, float impulse, float speed, bool touching)
    {
        if ((uint)workerIndex >= (uint)_workerContacts.Length) workerIndex = 0;
        _workerContacts[workerIndex].Add(new ContactReport(pair.A.Packed, pair.B.Packed, point, normal, impulse, speed, touching));
    }

    public Entity EntityOf(CollidableReference collidable) => EntryOf(collidable).Entity;

    // Query filter: is this collidable on a layer the query asked for?
    // Queries ask about *solid* things by default: a trigger has no surface to stop a ray, a sweep or
    // a sword, and something that wants overlaps reads the trigger lists (10 §3). Before this, a
    // trigger volume blocked line of sight and a swing could "hit" thin air (review #53).
    // `ignore` leaves out one entity, usually the one asking (IPhysicsWorld's queries).
    public bool Allows(CollidableReference collidable, LayerMask mask, bool includeTriggers, Entity ignore = default)
    {
        ref var entry = ref EntryOf(collidable);
        if (!entry.Used) return true;
        if (!ignore.IsNull && entry.Entity == ignore) return false;
#pragma warning disable SAGE0127   // query-only layers (issue #137): the backend is what honours them
        if (!Layers.Sees(mask, entry.Layer)) return false;
        // A query-only collider (a hitbox) that is a child of the one asking is its own: a shot from the
        // eye starts inside the shooter's head.
        if (!ignore.IsNull && Layers.QueryOnly.Has(entry.Layer) && entry.Entity.Parent == ignore) return false;
#pragma warning restore SAGE0127
        return includeTriggers || !entry.Trigger;
    }

    // ---- Step boundaries (main thread) ----

    public void BeginStep()
    {
        foreach (var list in _workerTriggers) list.Clear();
        foreach (var list in _workerContacts) list.Clear();
        _entered.Clear();
        _exited.Clear();
        _contactBegin.Clear();
        _contactEnd.Clear();
    }

    // Turns this step's overlapping pairs into enter/exit lists (10 §3).
    public void EndStep()
    {
        _current.Clear();
        foreach (var list in _workerTriggers)
            foreach (var pair in list)
            {
                var key = pair.A <= pair.B ? pair : (pair.B, pair.A);
                if (!_current.Add(key)) continue;
                if (!_overlapping.Contains(key) && Resolve(key, out var overlap)) _entered.Add(overlap);
            }

        foreach (var key in _overlapping)
        {
            if (_current.Contains(key)) continue;
            if (Untested(key)) { _current.Add(key); continue; }   // asleep, not gone: still inside
            if (Resolve(key, out var overlap)) _exited.Add(overlap);
        }

        _overlapping.Clear();
        foreach (var key in _current) _overlapping.Add(key);

        EndContacts();
    }

    // The same for reported contacts, which carry where they touch.
    private void EndContacts()
    {
        _touchingNow.Clear();
        _approachNow.Clear();
        foreach (var list in _workerContacts)
            foreach (var report in list)
            {
                bool swap = report.A > report.B;
                var key = swap ? (report.B, report.A) : (report.A, report.B);
                float speed = report.Speed, impulse = report.Impulse;
                if (_approach.TryGetValue(key, out var before))
                {
                    speed = MathF.Max(speed, before.Speed);
                    impulse = MathF.Max(impulse, before.Impulse);
                }
                if (!report.Touching)
                {
                    if (!_touching.Contains(key)) _approachNow[key] = (speed, impulse);   // still on its way
                    continue;
                }
                if (!_touchingNow.Add(key) || _touching.Contains(key)) continue;
                var a = EntityOf(new CollidableReference { Packed = report.A });
                var b = EntityOf(new CollidableReference { Packed = report.B });
                if (!a.IsNull && !b.IsNull)
                    _contactBegin.Add(new ContactEvent(a, b, report.Point, report.Normal) { Impulse = impulse, Speed = speed });
            }
        (_approach, _approachNow) = (_approachNow, _approach);

        foreach (var key in _touching)
        {
            if (_touchingNow.Contains(key)) continue;
            if (Untested(key)) { _touchingNow.Add(key); continue; }
            ref var ea = ref EntryOf(new CollidableReference { Packed = key.A });
            ref var eb = ref EntryOf(new CollidableReference { Packed = key.B });
            if (ea.Used && eb.Used && !ea.Entity.IsNull && !eb.Entity.IsNull)
                _contactEnd.Add(new ContactEvent(ea.Entity, eb.Entity, default, default));
        }

        _touching.Clear();
        foreach (var key in _touchingNow) _touching.Add(key);
    }

    // Bepu tests a pair only while one side is an awake body. A pair with nothing awake in it (a crate
    // that fell asleep in a trigger, or on the floor it reported touching) is untested rather than
    // apart, and keeps its state until something wakes it. Anything removed since is not kept.
    private bool Untested((uint A, uint B) key)
    {
        if (Simulation == null) return false;
        if (!Exists(new CollidableReference { Packed = key.A }, out bool awakeA)) return false;
        if (!Exists(new CollidableReference { Packed = key.B }, out bool awakeB)) return false;
        return !awakeA && !awakeB;
    }

    // False if the collidable no longer exists; `awake` says whether it is a body Bepu is simulating.
    private bool Exists(CollidableReference collidable, out bool awake)
    {
        awake = false;
        if (collidable.Mobility == CollidableMobility.Static)
            return Simulation!.Statics.StaticExists(collidable.StaticHandle) && EntryOf(collidable).Used;
        var handle = collidable.BodyHandle;
        if (!Simulation!.Bodies.BodyExists(handle) || !EntryOf(collidable).Used) return false;
        awake = Simulation.Bodies[handle].Awake;
        return true;
    }

    // The trigger side first, then what entered it. Pairs whose entities are gone are dropped.
    private bool Resolve((uint A, uint B) key, out TriggerOverlap overlap)
    {
        var a = new CollidableReference { Packed = key.A };
        var b = new CollidableReference { Packed = key.B };
        ref var ea = ref EntryOf(a);
        ref var eb = ref EntryOf(b);
        overlap = ea.Trigger ? new TriggerOverlap(ea.Entity, eb.Entity) : new TriggerOverlap(eb.Entity, ea.Entity);
        return ea.Used && eb.Used && !overlap.Trigger.IsNull && !overlap.Other.IsNull;
    }
}
