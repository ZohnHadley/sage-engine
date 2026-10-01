#nullable enable
using System.Collections.Generic;
using Sage.Editing;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The undo history on its own (issue #217): no document, no world, a list of numbers and commands that
// set them. What the editor's undo means — a redo stack a new edit drops, a drag that is one edit, a
// "dirty" that comes back when an undo goes past the save — is decided here.
public class CommandLogTests
{
    // Sets one slot of a list; merges with a later set of the same slot, as a drag does.
    private sealed class SetSlot : IEditorCommand
    {
        private readonly List<int> _values;
        private readonly int _slot;
        private readonly int _before;
        private int _after;

        public SetSlot(List<int> values, int slot, int value)
        {
            _values = values;
            _slot = slot;
            _before = values[slot];
            _after = value;
        }

        public string Description => $"Set {_slot} to {_after}";
        public void Do() => _values[_slot] = _after;
        public void Undo() => _values[_slot] = _before;

        public bool TryMerge(IEditorCommand next)
        {
            if (next is not SetSlot later || later._slot != _slot) return false;
            _after = later._after;
            return true;
        }
    }

    [Fact]
    public void UndoAndRedoWalkTheLogAndANewCommandDropsTheRedoStack()
    {
        var values = new List<int> { 0, 0 };
        var log = new CommandLog();
        int changes = 0;
        log.Changed += () => changes++;

        log.Execute(new SetSlot(values, 0, 1));
        log.EndMerge();
        log.Execute(new SetSlot(values, 0, 2));
        Assert.Equal(2, log.Entries.Count);
        Assert.Equal(2, values[0]);

        Assert.True(log.Undo());
        Assert.Equal(1, values[0]);
        Assert.True(log.Redo());
        Assert.Equal(2, values[0]);
        Assert.True(log.Undo());
        Assert.True(log.CanRedo);

        // A new edit after an undo is a new branch: the undone one cannot come back.
        log.Execute(new SetSlot(values, 1, 5));
        Assert.False(log.CanRedo);
        Assert.False(log.Redo());
        Assert.Equal(new[] { 1, 5 }, values);
        Assert.Equal(new[] { "Set 0 to 1", "Set 1 to 5" }, Descriptions(log));

        Assert.True(log.Undo());
        Assert.True(log.Undo());
        Assert.False(log.Undo());
        Assert.Equal(new[] { 0, 0 }, values);
        Assert.Equal(8, changes);   // 3 executes, 4 undos, 1 redo: a failed step says nothing
    }

    [Fact]
    public void ADragsFramesAreOneCommandUntilTheGestureEnds()
    {
        var values = new List<int> { 0, 0 };
        var log = new CommandLog();

        for (int frame = 1; frame <= 60; frame++) log.Execute(new SetSlot(values, 0, frame));
        Assert.Single(log.Entries);
        Assert.Equal("Set 0 to 60", log.Entries[0].Description);

        // The mouse came up: the next drag is an edit of its own.
        log.EndMerge();
        log.Execute(new SetSlot(values, 0, 70));
        // And a command of another kind (another slot) never merges.
        log.Execute(new SetSlot(values, 1, 3));
        Assert.Equal(3, log.Entries.Count);

        log.Undo();
        log.Undo();
        Assert.Equal(60, values[0]);
        log.Undo();
        Assert.Equal(0, values[0]);   // the whole first drag, in one undo
    }

    [Fact]
    public void ADocumentIsDirtyAgainAfterAnUndoPastTheSavePoint()
    {
        var values = new List<int> { 0 };
        var log = new CommandLog();
        Assert.False(log.Dirty);

        log.Execute(new SetSlot(values, 0, 1));
        Assert.True(log.Dirty);
        log.MarkSaved();
        Assert.False(log.Dirty);
        Assert.Equal(1, log.SavedPosition);

        log.Undo();
        Assert.True(log.Dirty);     // the file says 1, the document 0
        log.Redo();
        Assert.False(log.Dirty);    // back where it was saved

        // Nothing merges into the saved command: the rest of a drag after a save is unsaved work.
        log.Execute(new SetSlot(values, 0, 2));
        Assert.Equal(2, log.Entries.Count);
        Assert.True(log.Dirty);

        // A save point in a dropped redo stack can never be reached again.
        log.Undo();
        log.Undo();
        log.Execute(new SetSlot(values, 0, 9));
        Assert.Equal(-1, log.SavedPosition);
        Assert.True(log.Dirty);
        log.Undo();
        Assert.True(log.Dirty);     // even with nothing done: the file holds a state the log no longer has
    }

    [Fact]
    public void TheCapForgetsTheOldestCommandsAndASavePointWithThem()
    {
        var values = new List<int> { 0, 0 };
        var log = new CommandLog(capacity: 3);
        log.MarkSaved();

        for (int i = 1; i <= 5; i++)
        {
            log.Execute(new SetSlot(values, i % 2, i));
            log.EndMerge();
        }

        Assert.Equal(3, log.Entries.Count);
        Assert.Equal(3, log.Position);
        Assert.Equal(-1, log.SavedPosition);
        Assert.Equal(new[] { "Set 1 to 3", "Set 0 to 4", "Set 1 to 5" }, Descriptions(log));

        while (log.Undo()) { }
        Assert.True(log.Dirty);
        Assert.Equal(new[] { 2, 1 }, values);   // as far back as the log reaches
    }

    private static string[] Descriptions(CommandLog log)
    {
        var list = new string[log.Entries.Count];
        for (int i = 0; i < list.Length; i++) list[i] = log.Entries[i].Description;
        return list;
    }
}
