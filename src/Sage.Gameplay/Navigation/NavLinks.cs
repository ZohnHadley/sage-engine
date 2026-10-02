#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Gameplay;

// What a path has to *do*, not just walk (docs/design/16 §3.4, issue #265): a door to open, a ledge to
// drop from, a gap to jump, a ladder to climb, a teleporter to step into.
//
// **Doors are keyed off `Mover`.** Every mover tall enough to stand in a body's way is a door to the
// planner, wherever it came from — a brush entity in a map or a prefab with a `mover` part — and whether
// physics has it as a static or a kinematic body. Its *closed* footprint is what the planner knows about:
// the mesh is baked as if it were open (it is left out of the bake), the spans under it are marked as a
// crossing, and a creature whose path goes through it opens it (fires `Open`) and waits until it is fully
// open before it walks on. A door creatures may not open (`nav_door` with `locked`) is baked as a wall
// while it is not fully open, so the planner finds another way or says there is none.
//
// **Off-mesh links are data**: an entity with the `nav_link` part (on a game's prefab, which a map places
// with `"nav_link.end" "2 -3 0"` and `"nav_link.kind" "Drop"`), from where it stands to `end`. There is no
// engine prefab for it: engine content may only use the core's parts, and this one is the AI plugin's. The planner treats one as an edge between
// the two spans it joins, and the creature crosses it by its kind (MoveToTargetTask.Cross).
//
// **The planner re-plans when the world changes under a path.** A static collider added or moved, a
// dynamic body coming to rest (a crate pushed into a doorway), a locked door shutting, a link switched
// on or off: each bumps `Navigation.Version`, and a creature following a path planned before it plans
// again (budgeted like any plan).

// How a creature crosses an off-mesh link.
public enum NavLinkKind : byte
{
    Walk,       // walks it: a plank, a gap the bake closed, a way it could not see
    Jump,       // jumps it: a ballistic leap from where it stands to the far end
    Drop,       // walks off the edge and falls: one way, unless TwoWay says otherwise
    Ladder,     // climbs straight up (or down) to the far end's height, then steps off
    Teleport,   // is put at the far end at once
}

// An off-mesh link (issue #265): from where the entity stands to `End`, crossed as `Kind` says.
[Component("sage:nav_link")]
public struct NavLink : IComponent
{
    [Property(Unit = "m", Tooltip = "Where the link lands, from where the entity stands, in world axes (not turned with the entity)")]
    public Vector3 End;
    [Property(Tooltip = "How a creature crosses it: Walk, Jump, Drop, Ladder or Teleport")]
    public NavLinkKind Kind;
    [Property(Tooltip = "Crossed from either end; off, only from the entity to End (a drop)")]
    public bool TwoWay;
    [Property(Min = 0, Unit = "m", Tooltip = "What using it costs over its length, in metres of walking")]
    public float Cost;
    [Property(Tooltip = "Not used by the planner until Enable")]
    public bool Disabled;
}

// What creatures may do about a door (issue #265). A mover with no `nav_door` is a door creatures open.
[Component("sage:nav_door")]
public struct NavDoor : IComponent
{
    [Property(Tooltip = "Creatures do not open it: while not fully open it is a wall to the planner")]
    public bool Locked;
}

// `"nav_link": { "end": [0, -3, 2], "kind": "Drop" }` on a prefab: an off-mesh link from where the
// entity is placed.
[PrefabPart("nav_link", Plugin = "sage.gameplay.ai")]
public sealed class NavLinkPart : IPrefabPart
{
    public Vector3 End;                // where it lands, from the entity, in world axes
    public NavLinkKind Kind;           // "Walk" (default), "Jump", "Drop", "Ladder" or "Teleport"
    public bool TwoWay;                // crossed from either end
    public float Cost;                 // extra cost of using it, in metres
    public bool StartDisabled;         // off until Enable

    public void Apply(in PrefabPartContext ctx) => ctx.World.Add(ctx.Entity, new NavLink
    {
        End = End,
        Kind = Kind,
        TwoWay = TwoWay,
        Cost = MathF.Max(Cost, 0f),
        Disabled = StartDisabled,
    });
}

// `"nav_door": { "locked": true }` beside a `mover`: a door creatures leave alone.
[PrefabPart("nav_door", Plugin = "sage.gameplay.ai")]
public sealed class NavDoorPart : IPrefabPart
{
    public bool Locked;

    public void Apply(in PrefabPartContext ctx) => ctx.World.Add(ctx.Entity, new NavDoor { Locked = Locked });
}

// ---- what the planner reads (absolute metres, like the mesh) ------------------------------------------

// What a path does at one of its corners: nothing, open a door, or cross a link of some kind.
internal enum NavCrossing : byte { None, Door, Walk, Jump, Drop, Ladder, Teleport }

// A door's closed footprint and state, absolute.
internal readonly record struct NavDoorSpan(Entity Entity, Vector3 Min, Vector3 Max, bool Open, bool Locked);

// A link's two ends, absolute.
internal readonly record struct NavLinkSpan(Entity Entity, Vector3 Start, Vector3 End, NavLinkKind Kind, bool TwoWay, float Cost);

// The doors and links a plan may cross, gathered once a tick that plans (NavWorldGeometry.Sync).
internal sealed class NavCrossings
{
    public readonly List<NavDoorSpan> Doors = new();
    public readonly List<NavLinkSpan> Links = new();

    public static NavCrossing Of(NavLinkKind kind) => kind switch
    {
        NavLinkKind.Jump => NavCrossing.Jump,
        NavLinkKind.Drop => NavCrossing.Drop,
        NavLinkKind.Ladder => NavCrossing.Ladder,
        NavLinkKind.Teleport => NavCrossing.Teleport,
        _ => NavCrossing.Walk,
    };
}

// The crossing a plan found: at corner `Corner` the creature acts on door or link `Index` (of
// NavCrossings), and corner `Corner + 1` is the far side.
internal struct NavMeshCrossing
{
    public NavCrossing Kind;
    public int Corner;
    public int Index;
}
