#nullable enable
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Editing;

// The editor's status bar, without the drawing (issue #219; moved out of the host's DevTools by #371 so a
// test reads it): the document and whether it is saved, the selection, the world, the free camera and,
// when there is a problems panel, its summary.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public static class EditorStatus
{
    public static string Line(EditDocument? document, World? world, Entity selection, Vector3 camera, string? problems = null)
    {
        string doc = document is { IsOpen: true }
            ? $"{document.Id}  {(document.Dirty ? "modified" : "saved")}"
            : "no document";
        string selected = world != null && !selection.IsNull && world.IsAlive(selection)
            ? World.Describe(selection) : "nothing selected";
        return $"{doc}   |   {selected}   |   {world?.Name} ({world?.EntityCount ?? 0} entities)   |   camera {camera.X:F1} {camera.Y:F1} {camera.Z:F1}"
            + (problems != null ? $"   |   {problems}" : "");
    }
}
