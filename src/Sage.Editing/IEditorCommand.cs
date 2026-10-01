#nullable enable
using System.Diagnostics.CodeAnalysis;

namespace Sage.Editing;

// One edit (REDESIGN §4.6, docs/design/15 §3–§4): what it does, what undoes it, and how it reads in the
// undo history ("Move crate_2", "Set door.speed 2 → 3"). Every change an editor makes to a document is
// one of these, so undo, redo and "dirty" come from one log rather than from each tool (issue #217
// builds the log and the document's commands on it).
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public interface IEditorCommand
{
    string Description { get; }
    void Do();
    void Undo();
}
