#nullable enable
using System.IO;

namespace Sage.Simulation;

// What a demo (issue #333) needs of the save system: a world's saved state as one line, which the world
// hash is taken over, and a save written to and read from a folder of its own rather than a slot's, so a
// demo's start never shows in the player's load menu.
public sealed partial class SaveSystem
{
    // A world's file as a save would write it, entities included, as compact JSON. Deterministic for the
    // same state: resources are written by name, entities as the save captures them, numbers round-trip.
    internal string StateOf(World world)
    {
        var (root, captured, json) = WriteWorld(world);
        root["entities"] = BuildEntities(captured, json);
        return root.ToJsonString(Compact);
    }

    // Every world, saved into `directory` (made, or replaced) now: between ticks only. `label` names it in
    // the log. No thumbnail: nobody will see one.
    internal bool SaveTo(string directory, string label)
    {
        if (IsMidTick)
        {
            Log.Error(LogCat.Save, $"'{label}' cannot be saved during a tick");
            return false;
        }
        var snapshot = Snapshot(label, SaveKind.Manual, null, Path.GetFullPath(directory), picture: false);
        if (snapshot == null) return false;
#pragma warning disable SAGE0131 // the save system's own writer
        WaitForWrites();
#pragma warning restore SAGE0131
        return Write(snapshot);
    }
}
