#nullable enable
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Sage.Editing;

// What `-edit [placements-or-scene]` opens (issue #219): the scene the edit world is placed with, and the
// placements document the editor has open in it.
//
//   -edit                   the game's start scene, and the first placements document it names
//   -edit <scene>           that scene, and the first placements document it names
//   -edit <placements>      that document, in the first scene that names it, else in the start scene
//
// A name without a namespace is looked for in every namespace, the way the console's commands resolve one;
// a placements document wins over a scene of the same name, since the document is what is being edited.
// Either half may be empty: a scene with no placements opens with no document, and the editor's File menu
// (`doc_new`, `doc_open`) works from there.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public readonly record struct EditTarget(RecordId Scene, RecordId Document)
{
    // False, with `error` saying why, when `text` names neither a placements record nor a scene.
    public static bool TryResolve(RecordStore records, RecordId startScene, string? text, out EditTarget target, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            target = new EditTarget(startScene, FirstDocumentOf(records, startScene));
            return true;
        }

        if (Find(records, "placements", text) is { IsEmpty: false } document)
        {
            // Shown where it belongs: a document is usually a scene's (`"placements"`), and one that no scene
            // names yet is most likely meant for the game's own, so it opens there rather than in the void.
            var scene = records.Ids("scene").FirstOrDefault(id =>
                records.TryGet(id, out SceneRecord s) && s.Placements.Any(p => p.Id == document));
            target = new EditTarget(scene.IsEmpty ? startScene : scene, document);
            return true;
        }
        if (Find(records, "scene", text) is { IsEmpty: false } named)
        {
            target = new EditTarget(named, FirstDocumentOf(records, named));
            return true;
        }

        target = default;
        error = $"-edit: there is no placements document or scene called '{text}'";
        return false;
    }

    private static RecordId FirstDocumentOf(RecordStore records, RecordId scene) =>
        !scene.IsEmpty && records.TryGet(scene, out SceneRecord record) && record.Placements.Count > 0
            ? record.Placements[0].Id
            : default;

    // RecordStore.Resolve without its warning: not finding a placements document is how a scene is found.
    private static RecordId Find(RecordStore records, string type, string text)
    {
        if (text.Contains(':'))
        {
            RecordId id;
            try { id = RecordId.Parse(text, "sage"); }
            catch (System.FormatException) { return default; }
            return records.Exists(type, id) ? id : default;
        }
        return records.Ids(type).FirstOrDefault(i => i.Name == text);
    }
}
