#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// First-person arms and what they hold (issue #121, docs/design/12 "As built (first-person arms)" and 06
// "As built (the viewmodel pass)"): the HL1 / S.T.A.L.K.E.R. kind, a skinned model animated by an
// anim_graph, drawn over the world from the player's eyes only.
//
//   { "type": "viewmodel", "id": "sword_arms", "model": "models/arms.glb", "graph": "arms",
//     "material": "skin", "weapon": "models/viewmodel_sword.glb", "weaponMaterial": "steel", "socket": "hand_r",
//     "offset": [0, -0.1, 0] }
//
// **Where it lives.** A `Viewmodel` component on the *camera* entity, beside its FirstPersonRig (the
// player's camera prefab has one, `"viewmodel": {}`): what it shows is a `viewmodel` record, and its own
// field of view and depth range, because arms drawn with the world's 90° and 0.1 m near plane stretch and
// clip. ViewmodelSystem spawns what the record names as two entities — the arms (a `skinned_mesh` with an
// `animator`) and the weapon (a mesh `BoneAttachments.Attach`ed to a socket of the arms, #120) — and
// keeps them matching the record. They are never saved: the camera's component is, and they are made again.
//
// **In view space.** The arms entity is a root whose transform is in the camera's own space (x right,
// y up, looking down -Z): the record's `offset` and `angles` put it below and in front of the eye. The
// weapon is its child, so transform propagation puts it in view space too. Both carry the
// `sage:viewmodel_layer` tag, which every world extract leaves out; the viewmodel pass (ViewmodelPass,
// and the client's ViewmodelExtract) draws them, rotated by the view, in a pass after the world that
// clears depth first — so the arms never clip into a wall the player stands against.
//
// **When it is drawn.** Only while the screen's view is this camera looking out of its first-person rig
// (world.MainViewRig() is FirstPerson): not in third person, not from the editor's free camera, not from
// a scripted cut. The arms still animate then, so a reload finishes behind a cut.

// Entities drawn only by the viewmodel pass, in the camera's view space (see above). ViewmodelSystem tags
// the arms and the weapon it spawns; a game's code may tag more (a watch on the wrist), as long as it
// parents them to the arms.
[Experimental("SAGE0126", UrlFormat = AnimationApi.Url)]
[Tag("sage:viewmodel_layer")]
public struct ViewmodelLayer : ITag { }

// What a viewmodel shows (see above): the arms, their graph, and a weapon on a socket. Engine-owned, so
// the `viewmodel` part and gameplay's attack records can name one in any game.
[Experimental("SAGE0126", UrlFormat = AnimationApi.Url)]
[Record("viewmodel", Plugin = RegistrationOwners.Core)]
public sealed class ViewmodelRecord
{
    [AssetKind("mesh")]
    [Property(Tooltip = "The arms: a skinned .glb (JOINTS_0, WEIGHTS_0) in view space, looking down -Z")]
    public AssetPath Model;
    [RecordRef("anim_graph"), Property(Tooltip = "The anim_graph that animates the arms (idle, attack, reload…); empty = they stand in their rest pose")]
    public RecordId Graph;
    [Property(Tooltip = "The arms' material; empty = sage:lit_default (its effect has the Skinned technique)")]
    public RecordRef<MaterialRecord> Material;
    [AssetKind("mesh")]
    [Property(Category = "Weapon", Tooltip = "What the arms hold: a .glb on `socket`; empty = bare hands")]
    public AssetPath Weapon;
    [Property(Category = "Weapon", Tooltip = "The weapon's material; empty = sage:lit_default")]
    public RecordRef<MaterialRecord> WeaponMaterial;
    [Property(Category = "Weapon", Tooltip = "A socket of the arms' model (skeleton_sockets) the weapon hangs from")]
    public string Socket = "";
    [Property(Unit = "m", Tooltip = "Where the arms' origin sits from the eye: x right, y up, z back")]
    public Vector3 Offset;
    [Property(Unit = "deg", Tooltip = "How the arms are turned in view space: pitch, yaw, roll")]
    public Vector3 Angles;

    // Load: arms to draw, and a socket for a weapon to hang from.
    internal static void Check(ViewmodelRecord record, RecordCheck check)
    {
        if (record.Model.IsEmpty) check.Error("model", "names no arms: a viewmodel is a skinned .glb (\"model\")");
        if (!record.Weapon.IsEmpty && string.IsNullOrWhiteSpace(record.Socket))
            check.Error("socket", "a weapon needs a socket of the arms' model to hang from (skeleton_sockets)");
    }
}

// The camera's viewmodel (see above). Saved: which record it shows and its projection. The arms and the
// weapon are not; ViewmodelSystem makes them again from the record after a load.
[Experimental("SAGE0126", UrlFormat = AnimationApi.Url)]
[Component("sage:viewmodel")]
public struct Viewmodel : IComponent
{
    public const float DefaultFovY = 54f;
    public const float DefaultNear = 0.01f;
    public const float DefaultFar = 10f;

    [RecordRef("viewmodel"), Property(Tooltip = "What it shows (a viewmodel record); empty = nothing (the HUD may draw sprite hands)")]
    public RecordId Record;
    [Property(Tooltip = "Off: nothing is drawn, though the arms stay and animate")]
    public bool Enabled;
    [Property(Min = 0, Max = 179, Unit = "deg", Tooltip = "Its own vertical field of view; 0 = the camera's")]
    public float FovY;
    [Property(Min = 0, Unit = "m", Tooltip = "Its own near plane: close, so the arms are never clipped")]
    public float Near;
    [Property(Min = 0, Unit = "m", Tooltip = "Its own far plane: only the arms and the weapon are drawn")]
    public float Far;

    // What ViewmodelSystem spawned for `Shown`; found again (made again) after a load.
    [Transient] internal Entity Arms;
    [Transient] internal Entity Weapon;
    [Transient] internal RecordId Shown;

    public static Viewmodel Create(RecordId record = default) => new()
    {
        Record = record, Enabled = true, FovY = DefaultFovY, Near = DefaultNear, Far = DefaultFar,
    };
}

// "viewmodel": { "record": "sword_arms", "fovY": 54, "near": 0.01, "far": 10 }, or `{}` on the player's
// camera: gameplay (the attack in the player's hands) chooses the record from then on. The engine's, like
// the rigs it goes with.
[Experimental("SAGE0126", UrlFormat = AnimationApi.Url)]
[PrefabPart("viewmodel", Plugin = RegistrationOwners.Core)]
public sealed class ViewmodelPart : IPrefabPart
{
    [RecordRef("viewmodel"), Property(Tooltip = "What it shows to start with; empty = nothing until gameplay picks one")]
    public RecordId Record;
    [Property(Tooltip = "Off: nothing is drawn")]
    public bool Enabled = true;
    [Property(Min = 0, Max = 179, Unit = "deg", Tooltip = "Its own vertical field of view; 0 = the camera's")]
    public float FovY = Viewmodel.DefaultFovY;
    [Property(Min = 0, Unit = "m", Tooltip = "Its own near plane")]
    public float Near = Viewmodel.DefaultNear;
    [Property(Min = 0, Unit = "m", Tooltip = "Its own far plane")]
    public float Far = Viewmodel.DefaultFar;

    public void Apply(in PrefabPartContext ctx)
    {
        if (!(FovY >= 0f && FovY < 180f)) ctx.Warn($"fovY {FovY} is not a field of view in degrees; the camera's is used");
        if (!(Near > 0f) || !(Far > Near)) ctx.Warn($"near {Near} / far {Far} is not a depth range; {Viewmodel.DefaultNear} / {Viewmodel.DefaultFar} is used");
        AddTo(ctx.World, ctx.Entity);
    }

    internal void AddTo(World world, Entity camera)
    {
        var viewmodel = Viewmodel.Create(Record);
        viewmodel.Enabled = Enabled;
        viewmodel.FovY = FovY >= 0f && FovY < 180f ? FovY : 0f;
        if (Near > 0f && Far > Near)
        {
            viewmodel.Near = Near;
            viewmodel.Far = Far;
        }
        world.Add(camera, viewmodel);
    }
}

// Driving a viewmodel from code: which record it shows, its arms (to set animator triggers on), and the
// question the HUD asks before drawing sprite hands.
[Experimental("SAGE0126", UrlFormat = AnimationApi.Url)]
public static class Viewmodels
{
    public const string SystemId = "sage.camera.viewmodel";

    // Shows `record` on `camera` (empty: nothing). The arms change on the next tick's Animation phase.
    public static bool Show(World world, Entity camera, RecordId record)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!world.Has<Viewmodel>(camera)) return false;
        world.Get<Viewmodel>(camera).Record = record;
        return true;
    }

    // The arms `camera`'s viewmodel spawned, or null (none shown yet, or none to show).
    public static Entity ArmsOf(World world, Entity camera)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.TryGet<Viewmodel>(camera, out var v) && world.IsAlive(v.Arms) ? v.Arms : default;
    }

    // The weapon on the arms, or null (bare hands, or nothing shown).
    public static Entity WeaponOf(World world, Entity camera)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.TryGet<Viewmodel>(camera, out var v) && world.IsAlive(v.Weapon) ? v.Weapon : default;
    }

    // The camera with a viewmodel whose rigs follow `pawn` (the player's camera), or null. Allocates
    // nothing when `query` is the caller's cached `world.Query<Viewmodel>()`.
    public static Entity CameraOf(World world, Query<Viewmodel> query, Entity pawn)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (pawn.IsNull) return default;
        foreach (var (_, cameras) in query.Chunks)
            for (int i = 0; i < cameras.Length; i++)
            {
                var camera = cameras.EntityAt(i);
                if (world.TryGet<FirstPersonRig>(camera, out var first) && first.Follow == pawn) return camera;
                if (world.TryGet<ThirdPersonRig>(camera, out var third) && third.Follow == pawn) return camera;
            }
        return default;
    }

    // Whether the screen's view draws a viewmodel this frame (first person, enabled, arms spawned). What
    // the HUD asks before drawing sprite hands in their place. From late FrameUpdate on, like MainViewRig.
    public static bool IsDrawn(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return ViewmodelPass.TryGet(world, out _);
    }
}

// Phase.Animation, before the animator: every camera's viewmodel has the arms and the weapon its record
// names, and no others. Spawns when the record changes (a weapon drawn, a load), which allocates; the
// steady state is a loop over the cameras and allocates nothing. Arms whose camera is gone (a load
// rebuilt it) are destroyed. The engine installs it in every world, beside the animator.
[Experimental("SAGE0126", UrlFormat = AnimationApi.Url)]
[System(Viewmodels.SystemId, Phase.Animation, Before = new[] { Animators.SystemId })]
internal sealed class ViewmodelSystem : ISystem
{
    private readonly World _world;
    private readonly Query<Viewmodel> _cameras;
    private readonly Query<Transform> _pieces;
    private Entity[] _orphans = new Entity[4];

    public ViewmodelSystem(World world)
    {
        _world = world;
        _cameras = world.Query<Viewmodel>();
        _pieces = world.Query<Transform>().AllTags(Tags.Get<ViewmodelLayer>());
    }

    public void Run(in SystemContext ctx)
    {
        var records = _world.Engine?.Records ?? (_world.Resources.TryGet<RecordStore>(out var r) ? r : null);
        bool pending = false;
        foreach (var (viewmodels, _) in _cameras.Chunks)
        {
            var v = viewmodels.Span;
            for (int i = 0; i < v.Length; i++)
            {
                if (Stale(in v[i])) pending = true;
            }
        }
        if (pending) Respawn(records);   // outside the query: it creates and destroys entities
        Sweep();
    }

    // A record other than the one shown (a weapon drawn, a load: Shown is transient), or arms handed out
    // and no longer alive. A record that could not be shown (no such record) is not tried again until the
    // record changes.
    private bool Stale(in Viewmodel viewmodel) =>
        viewmodel.Record != viewmodel.Shown || (!viewmodel.Arms.IsNull && !_world.IsAlive(viewmodel.Arms));

    private void Respawn(RecordStore? records)
    {
        foreach (var camera in _cameras.Entities.ToEntityList())
        {
            ref var viewmodel = ref _world.Get<Viewmodel>(camera);
            if (!Stale(in viewmodel)) continue;
            Despawn(ref viewmodel);
            viewmodel.Shown = viewmodel.Record;
            if (viewmodel.Record.IsEmpty) continue;
            if (records == null || !records.TryGet(viewmodel.Record, out ViewmodelRecord record))
            {
                Log.Once(LogCat.Animation, LogLevel.Warn, $"viewmodel:{viewmodel.Record}",
                    $"{World.Describe(camera)}: no viewmodel record {viewmodel.Record}; nothing is shown");
                continue;
            }
            var (arms, weapon) = Spawn(_world, record, camera);
            ref var after = ref _world.Get<Viewmodel>(camera);   // read again: creating entities may have grown the stores
            after.Arms = arms;
            after.Weapon = weapon;
        }
    }

    // The arms (and the weapon on them) for `record`, in view space. Loads the model now (a spawn is
    // content time, as the animator part's is).
    internal static (Entity Arms, Entity Weapon) Spawn(World world, ViewmodelRecord record, Entity camera)
    {
        const float ToRadians = MathF.PI / 180f;
        var transform = new Transform
        {
            LocalPosition = record.Offset,
            LocalRotation = Quaternion.CreateFromYawPitchRoll(record.Angles.Y * ToRadians, record.Angles.X * ToRadians, record.Angles.Z * ToRadians),
            LocalScale = Vector3.One,
        };
        var arms = world.Create(transform, "viewmodel arms");
        arms.AddTag<ViewmodelLayer>();
        world.Add(arms, new SkinnedMeshRenderer { Mesh = record.Model, Material = record.Material.Id });
        if (!record.Graph.IsEmpty)
        {
            world.Add(arms, new Animator { Graph = record.Graph, Model = record.Model });
            if (world.Resources.TryGet<GltfAnimationReader>(out var reader) && reader != null) reader.Load(record.Model);
        }

        Entity weapon = default;
        if (!record.Weapon.IsEmpty)
        {
            weapon = world.Create(Transform.Identity, "viewmodel weapon");
            weapon.AddTag<ViewmodelLayer>();
            world.Add(weapon, new MeshRenderer { Mesh = record.Weapon, Material = record.WeaponMaterial.Id });
            BoneAttachments.Attach(world, weapon, arms, record.Socket);
        }
        Log.Info(LogCat.Animation, $"{World.Describe(camera)}: viewmodel {record.Model}" + (weapon.IsNull ? "" : $" holding {record.Weapon}"));
        return (arms, weapon);
    }

    private void Despawn(ref Viewmodel viewmodel)
    {
        if (_world.IsAlive(viewmodel.Weapon)) _world.Destroy(viewmodel.Weapon);
        if (_world.IsAlive(viewmodel.Arms)) _world.Destroy(viewmodel.Arms);
        viewmodel.Weapon = default;
        viewmodel.Arms = default;
    }

    // Viewmodel pieces no camera owns any more (its camera destroyed, or rebuilt by a load): destroyed.
    // A piece parented to live arms belongs to them.
    private void Sweep()
    {
        int count = 0;
        foreach (var (_, pieces) in _pieces.Chunks)
            for (int i = 0; i < pieces.Length; i++)
            {
                var piece = pieces.EntityAt(i);
                if (Owned(piece)) continue;
                if (count == _orphans.Length) Array.Resize(ref _orphans, count * 2);
                _orphans[count++] = piece;
            }
        for (int i = 0; i < count; i++)
        {
            if (_world.IsAlive(_orphans[i])) _world.Destroy(_orphans[i]);
            _orphans[i] = default;
        }
    }

    private bool Owned(Entity piece)
    {
        var root = piece;
        while (!root.Parent.IsNull) root = root.Parent;
        foreach (var (viewmodels, _) in _cameras.Chunks)
        {
            var v = viewmodels.Span;
            for (int i = 0; i < v.Length; i++)
                if (v[i].Arms == root || v[i].Weapon == piece) return true;
        }
        return false;
    }
}
