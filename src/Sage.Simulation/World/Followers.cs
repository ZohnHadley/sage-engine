#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// Followers: companions who go where the player goes (issue #290; design 14 "Only the player travels" was the
// limit this lifts).
//
//   { "type": "prefab", "id": "hound", "parts": { "follower": { "distance": 2 } } }
//
// A journey (`Travel.To`, `Travel.ToPoint`, a load door) puts the player at its entry and then every live
// follower behind them (`Followers.Bring`), facing the same way, on the ground when there is ground. From
// there streaming does the rest: a follower that is a streamed scene's content belongs to the sector it now
// stands in on the next Late (SectorOwnersSystem), so the ring it left does not put it to sleep. A follower is
// always the player's; what makes it walk after them between journeys is its AI's, not this.
internal static class Followers
{
    // How far behind the player a follower arrives when its `distance` is not set, and how far apart two
    // followers stand, in metres.
    public const float DefaultDistance = 2f;
    private const float Spacing = 1.5f;

    // Puts every live follower (a root with `sage:follower`, not the player) behind `player` where it stands
    // now: in rows of three (the first straight behind, then to either side), `distance` back, the same yaw. How many were moved. Run by Travel at the tick
    // boundary, after the player is placed; a follower that went dormant with the scene it was in is not
    // there to bring.
    internal static int Bring(World world, Entity player)
    {
        if (player.IsNull || !world.TryGet<Transform>(player, out var leader)) return 0;

        List<Entity>? followers = null;
        foreach (var entity in world.Query<Transform, Follower>().Entities)
        {
            if (entity == player || !entity.Parent.IsNull) continue;
            (followers ??= new()).Add(entity);
        }
        if (followers == null) return 0;
        followers.Sort(static (a, b) => a.Id.CompareTo(b.Id));   // the same places every run

        var rotation = leader.LocalRotation;
        var back = Vector3.Transform(Vector3.UnitZ, rotation);    // yaw 0 faces -Z, so +Z is behind
        var side = Vector3.Transform(Vector3.UnitX, rotation);
        back.Y = side.Y = 0f;
        back = back.LengthSquared() > 1e-6f ? Vector3.Normalize(back) : Vector3.UnitZ;
        side = side.LengthSquared() > 1e-6f ? Vector3.Normalize(side) : Vector3.UnitX;

        // Inside an interior the ground is not the floor, even when an exterior held live beside it keeps its own (4m-17).
        var terrain = !Interiors.Active(world) && world.Resources.TryGet<Terrain>(out var t) && t is { Generator: not null } && t.Sectors.Count > 0 ? t : null;
        float above = terrain != null ? leader.LocalPosition.Y - terrain.HeightAt(leader.LocalPosition.X, leader.LocalPosition.Z) : 0f;

        for (int i = 0; i < followers.Count; i++)
        {
            var entity = followers[i];
            float distance = world.Get<Follower>(entity).Distance;
            if (!(distance > 0f)) distance = DefaultDistance;
            int row = i / 3, column = (i % 3) switch { 0 => 0, 1 => 1, _ => -1 };   // the first straight behind
            var at = leader.LocalPosition + back * (distance + row * Spacing) + side * (column * Spacing);
            at.Y = terrain != null ? terrain.HeightAt(at.X, at.Z) + above : leader.LocalPosition.Y;

            var transform = world.Get<Transform>(entity);
            transform.LocalPosition = at;
            transform.LocalRotation = rotation;
            world.Teleport(entity, transform);
        }
        Log.Info(LogCat.World, $"'{world.Name}': {followers.Count} follower(s) came along");
        return followers.Count;
    }
}

// A companion who travels with the player: put behind them, `distance` back, when they arrive anywhere.
[Component("sage:follower")]
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
public struct Follower : IComponent
{
    [Property(Min = 0, Unit = "m", Tooltip = "How far behind the player it arrives after a journey (0: 2 m)")]
    public float Distance;
}

// "follower": { "distance": 2 }
[PrefabPart("follower", Plugin = RegistrationOwners.Core)]
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
public sealed class FollowerPart : IPrefabPart
{
    [Property(Min = 0, Unit = "m", Tooltip = "How far behind the player it arrives after a journey")]
    public float Distance = Followers.DefaultDistance;

    public void Apply(in PrefabPartContext ctx) =>
        ctx.World.Add(ctx.Entity, new Follower { Distance = MathF.Max(0f, Distance) });
}
