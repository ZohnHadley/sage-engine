#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Editing;

// The undo history of one document (issue #217, docs/design/15 §3).
//
// One list and a position in it: the commands before the position are done, the ones after it were
// undone and can be redone. A new command drops those (the redo stack), as every editor does. "Dirty" is
// not a flag anything sets: it is "the position is not where it was when the document was last saved",
// so undoing back to the save point makes a document clean again, and undoing past it makes it dirty.
//
// **Merging**: a command done straight after another may fold into it (IEditorCommand.TryMerge), which
// is how a drag's sixty frames a second become one "Move crate". `EndMerge` closes the gesture (the mouse
// came up), and nothing merges into the command at the save point, or saving mid-drag would let the rest
// of the drag change the document without making it dirty.
//
// **A cap**: past `Capacity` the oldest command is forgotten. If the save point goes with it, no position
// is the saved one any more, and the document stays dirty until it is saved again.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class CommandLog
{
    public const int DefaultCapacity = 500;

    private readonly List<IEditorCommand> _entries = new();
    private bool _mergeClosed = true;

    public CommandLog(int capacity = DefaultCapacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity), "a log holds at least one command");
        Capacity = capacity;
    }

    public int Capacity { get; }

    // How many of the entries are done; the rest can be redone.
    public int Position { get; private set; }

    // The position the document was saved at; -1 when that state is no longer in the log (it was capped
    // away, or it was in a redo stack a new command dropped).
    public int SavedPosition { get; private set; }

    public bool Dirty => Position != SavedPosition;
    public bool CanUndo => Position > 0;
    public bool CanRedo => Position < _entries.Count;

    // Every command still held, oldest first: `Position` of them are done.
    public IReadOnlyList<IEditorCommand> Entries => _entries;

    public event Action? Changed;

    // Does the command and records it. A command done straight after another may merge into it.
    public void Execute(IEditorCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Do();

        // A new command is a new branch of history: what was undone cannot be redone now.
        if (_entries.Count > Position)
        {
            _entries.RemoveRange(Position, _entries.Count - Position);
            if (SavedPosition > Position) SavedPosition = -1;
        }

        bool merged = !_mergeClosed && Position > 0 && Position != SavedPosition
                      && _entries[Position - 1].TryMerge(command);
        if (!merged)
        {
            _entries.Add(command);
            Position++;
            if (_entries.Count > Capacity)
            {
                _entries.RemoveAt(0);
                Position--;
                SavedPosition = SavedPosition > 0 ? SavedPosition - 1 : -1;
            }
        }
        _mergeClosed = false;
        Changed?.Invoke();
    }

    // The gesture is over: the next command starts a new entry even if it could merge.
    public void EndMerge() => _mergeClosed = true;

    public bool Undo()
    {
        if (!CanUndo) return false;
        _mergeClosed = true;
        _entries[--Position].Undo();
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo) return false;
        _mergeClosed = true;
        _entries[Position++].Do();
        Changed?.Invoke();
        return true;
    }

    // The document was written: this position is the clean one now.
    public void MarkSaved()
    {
        SavedPosition = Position;
        _mergeClosed = true;
        Changed?.Invoke();
    }

    // A document opened or closed: no history, and clean.
    public void Clear()
    {
        _entries.Clear();
        Position = SavedPosition = 0;
        _mergeClosed = true;
        Changed?.Invoke();
    }
}
