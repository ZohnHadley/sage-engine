#nullable enable
using System.Diagnostics.CodeAnalysis;

namespace Sage.Editing;

// One edit (REDESIGN §4.6, docs/design/15 §3–§4): what it does, what undoes it, and how it reads in the
// undo history ("Move crate_2", "Set door.speed 2 → 3"). Every change an editor makes to a document is
// one of these, so undo, redo and "dirty" come from one log (CommandLog) rather than from each tool.
//
// `Do` is called again by a redo, so it sets the state it describes rather than nudging it ("at = 3", not
// "at += 1"); `Undo` puts back exactly what `Do` found.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public interface IEditorCommand
{
    string Description { get; }
    void Do();
    void Undo();

    // One gesture, one command: a drag sends a command every frame, and undo should take back the drag,
    // not its last frame. Called with the command that was just done after this one; true when this one
    // now stands for both (it keeps its own "before" and takes `next`'s "after"), and `next` is dropped.
    bool TryMerge(IEditorCommand next) => false;
}
