#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Editing;

// What the pointer found: the entity, where the ray met it, and how far along the ray that was.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public readonly record struct PickResult(Entity Entity, Vector3 Point, float Distance);

// Picking in the viewport (docs/design/15 §6): a ray from the camera through a screen point, and what it
// hits first. Colliders are hit by the physics raycast; an entity with no collider is still pickable
// by a small sphere around its position, because headless code has no render mesh bounds (the client
// loads meshes; a model's size is not known here), so a mesh with no collider is picked at its origin.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public static class EditorPicking
{
    // The radius of the stand-in sphere for an entity with no collider.
    public const float FallbackRadius = 0.35f;

    // The ray through `screenPoint` (pixels, origin at the top left) of a viewport `viewportSize` pixels
    // across, for a perspective camera with vertical field of view `fovY` (radians). The direction is
    // unit length; the camera looks down its local -Z (CameraMath). Null for a degenerate viewport.
    public static EditorRay? RayFrom(CameraPose camera, float fovY, Vector2 viewportSize, Vector2 screenPoint)
    {
        if (viewportSize.X <= 0f || viewportSize.Y <= 0f) return null;
        float ndcX = 2f * screenPoint.X / viewportSize.X - 1f, ndcY = 1f - 2f * screenPoint.Y / viewportSize.Y;
        float tan = MathF.Tan(MathF.Min(fovY, CameraMath.MaxFovY) * 0.5f), aspect = viewportSize.X / viewportSize.Y;
        Vector3 local = new(ndcX * tan * aspect, ndcY * tan, -1f);
        return new EditorRay(camera.Position, Vector3.Normalize(Vector3.Transform(local, camera.Rotation)));
    }

    // The same for an orthographic camera showing `orthoHeight` metres top to bottom: parallel rays,
    // each starting on the camera's plane.
    public static EditorRay? RayFromOrthographic(CameraPose camera, float orthoHeight, Vector2 viewportSize, Vector2 screenPoint)
    {
        if (viewportSize.X <= 0f || viewportSize.Y <= 0f) return null;
        float ndcX = 2f * screenPoint.X / viewportSize.X - 1f, ndcY = 1f - 2f * screenPoint.Y / viewportSize.Y;
        float halfHeight = orthoHeight * 0.5f, aspect = viewportSize.X / viewportSize.Y;
        Vector3 offset = Vector3.Transform(new Vector3(ndcX * halfHeight * aspect, ndcY * halfHeight, 0f), camera.Rotation);
        return new EditorRay(camera.Position + offset, CameraMath.Forward(camera.Rotation));
    }

    // The nearest thing along `ray` within `maxDistance`: a collider by the physics raycast (a world
    // with no physics has none), and every entity with a GlobalTransform and no collider by its
    // fallback sphere. `ignore` leaves one entity out (the editor camera). Null when nothing is hit.
    public static PickResult? Pick(World world, in EditorRay ray, float maxDistance = 1000f, Entity ignore = default) =>
        Nearest(world, ray, null, maxDistance, ignore);

    // The same, taking only what `accept` says yes to (issue #221: the viewport leaves out the editor's
    // own cameras, which stand where the ray starts). A collider it refuses still hides what is behind it,
    // since the raycast stops there; a camera has none.
    public static PickResult? PickWhere(World world, in EditorRay ray, Func<Entity, bool> accept, float maxDistance = 1000f) =>
        Nearest(world, ray, accept, maxDistance, default);

    private static PickResult? Nearest(World world, in EditorRay ray, Func<Entity, bool>? accept, float maxDistance, Entity ignore)
    {
        PickResult? best = null;
        if (world.Resources.TryGet<IPhysicsWorld>(out var physics) && physics is not null)
        {
            RayHit hit = physics.Raycast(ray.Origin, ray.Direction, maxDistance, ignore: ignore);
            if (hit.Hit && (accept == null || accept(hit.Entity))) best = new PickResult(hit.Entity, hit.Position, hit.Distance);
        }

        foreach (Entity entity in world.Query<GlobalTransform>().Entities)
        {
            if (entity == ignore || entity.HasComponent<Collider>() || (accept != null && !accept(entity))) continue;
            Vector3 centre = entity.GetComponent<GlobalTransform>().Current.Position;
            float? distance = RaySphere(ray, centre, FallbackRadius);
            if (distance is not { } d || d > maxDistance || (best is { } b && b.Distance <= d)) continue;
            best = new PickResult(entity, ray.At(d), d);
        }
        return best;
    }

    // Distance along the ray to the sphere's near surface (0 when the ray starts inside), or null.
    internal static float? RaySphere(in EditorRay ray, Vector3 centre, float radius)
    {
        Vector3 toCentre = centre - ray.Origin;
        float along = Vector3.Dot(toCentre, ray.Direction);
        float offsetSquared = toCentre.LengthSquared() - along * along, radiusSquared = radius * radius;
        if (offsetSquared > radiusSquared) return null;
        float half = MathF.Sqrt(radiusSquared - offsetSquared), near = along - half, far = along + half;
        if (far < 0f) return null;
        return MathF.Max(near, 0f);
    }
}
