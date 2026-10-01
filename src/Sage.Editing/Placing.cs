#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;

namespace Sage.Editing;

// Placing a prefab from the palette (issue #222): where the pointer's ray meets the world, and the
// AddPlacement that puts the prefab there under a name no other placement has.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public static class Placing
{
    // Where a click's ray lands: the first collider the physics raycast finds (terrain included: the ground
    // is mirrored into the edit world), else the plane y = 0. Not the fallback spheres of EditorPicking.Pick:
    // a prefab put down by clicking on another's marker would sit on a sphere nothing draws. Null when the ray
    // meets neither (it points up, or runs parallel to the ground).
    public static Vector3? Surface(World world, in EditorRay ray, float maxDistance = 1000f, Entity ignore = default)
    {
        if (world.Resources.TryGet<IPhysicsWorld>(out var physics) && physics is not null)
        {
            RayHit hit = physics.Raycast(ray.Origin, ray.Direction, maxDistance, ignore: ignore);
            if (hit.Hit) return hit.Position;
        }
        return ray.IntersectPlane(Vector3.Zero, Vector3.UnitY) is { } t && t <= maxDistance ? ray.At(t) : null;
    }

    // A name for a new placement of `prefab`: the wanted one (else the prefab's name), then name_2, name_3,
    // ... until neither a placement's name nor its id has it.
    public static string UniqueName(EditDocument document, RecordId prefab, string wanted = "")
    {
        string stem = wanted.Length > 0 ? wanted : prefab.Name;
        bool Taken(string name) => document.Placements.Any(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(p.Id, name, StringComparison.OrdinalIgnoreCase));
        string candidate = stem;
        for (int n = 2; Taken(candidate); n++) candidate = $"{stem}_{n}";
        return candidate;
    }

    // Places `prefab` at `at` (the document's frame, absolute metres unless it says otherwise) facing `yaw`
    // degrees, as one AddPlacement on the document's log. `name` is made unique when it is taken; left empty
    // it is the prefab's. Null when no document is open or the prefab is not a record.
    public static Placement? Place(EditDocument document, RecordId prefab, Vector3 at, float yaw = 0f, string name = "")
    {
        if (!document.IsOpen || !document.Engine.Records.Exists("prefab", prefab)) return null;
        var placement = new Placement { Prefab = prefab, At = at, Yaw = yaw, Name = UniqueName(document, prefab, name) };
        return document.Execute(new AddPlacement(document, placement)) ? placement : null;
    }

    // A click: the prefab on whatever the ray meets. Null when it meets nothing or the placement fails.
    public static Placement? PlaceAt(EditDocument document, RecordId prefab, in EditorRay ray, float yaw = 0f, string name = "") =>
        Surface(document.World, ray) is { } point ? Place(document, prefab, point, yaw, name) : null;
}
