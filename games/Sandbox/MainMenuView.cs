using Sage.UI;

namespace Sandbox;

// The Sandbox's main menu (docs/design/13 "As built (the HUD, journal, map and menus)", issue #99): the
// saves there are to load, from SaveSystem.Slots, and three buttons — Resume, Save and Quit. Saving and
// loading were console commands only (`save`, `load`, `saves`); this is the same SaveSystem behind a
// screen, `sandbox:main_menu` in content/data/ui.json, which F10 (the MainMenu action) opens.
//
// Activate is where it acts (IViewModel.Activate): a slot's row loads that slot and closes the menu, so
// the world under it is the restored one; `save` writes a new slot, which appears at the top of the
// list; `resume` closes the menu; `quit` runs the host's `quit` (a headless app has none: nothing
// happens). A slot this build cannot read is listed, greyed, and says why when chosen.
[ViewModel("sandbox_main_menu")]
public sealed class MainMenuView : IViewModel
{
    public sealed class Slot
    {
        public string Name { get; internal set; } = "";
        public string When { get; internal set; } = "";
        public bool CanLoad { get; internal set; }
        public bool CannotLoad => !CanLoad;
        public SaveSlot? Save { get; internal set; }
    }

    private readonly List<Slot> _pool = new();
    private SaveSystem? _saves;
    private int _version = -1;

    public List<Slot> Slots { get; } = new();
    public int SlotCount => Slots.Count;
    public bool HasSaves => Slots.Count > 0;
    public bool NoSaves => Slots.Count == 0;

    // What the last button did, as a key (@sandbox.menu.saved…) with the slot it did it to; empty at first.
    public string Message { get; private set; } = "";
    public string MessageSlot { get; private set; } = "";

    public void Refresh(in UiBindContext context)
    {
        if (context.World?.Engine is not { } engine) return;
        if (_saves == null)
        {
            _saves = engine.Saves;
            _saves.Rescan();   // opening the menu: another process may have written a save meanwhile
        }
        int version = _saves.SlotsVersion;
        if (version == _version) return;
        _version = version;

        Slots.Clear();
        int used = 0;
        foreach (var save in _saves.Slots)
        {
            if (used == _pool.Count) _pool.Add(new Slot());
            var slot = _pool[used++];
            slot.Save = save;
            slot.Name = save.Name;
            slot.When = save.SavedUtc == default ? "" : save.SavedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            slot.CanLoad = save.CanLoad;
            Slots.Add(slot);
        }
    }

    public bool Activate(Widget widget, in UiBindContext context)
    {
        if (context.World is not { Engine: { } engine } world) return false;
        if (UiScreen.RowOf(widget) is Slot slot) return Load(world, engine, slot);

        switch (widget.Name)
        {
            case "resume":
                Close(world);
                return true;
            case "save":
                string name = NextSlotName(engine.Saves);
                Say(engine.Saves.Save(name) ? "@sandbox.menu.saved" : "@sandbox.menu.save_failed", name);
                return true;
            case "quit":
                if (engine.CVars.FindCommand("quit") != null) engine.CVars.Execute("quit");
                return true;
        }
        return false;
    }

    // Loads the slot and closes the menu over the world it restored. SaveSystem.Load rebuilds every
    // persistent entity from the file, so the player the HUD and this screen were about is a new entity:
    // what reads it looks it up again (HudView does, every frame).
    private bool Load(World world, Engine engine, Slot slot)
    {
        if (!slot.CanLoad) { Say("@sandbox.menu.cannot_load", slot.Name); return true; }
        if (!engine.Saves.Load(slot.Name)) { Say("@sandbox.menu.load_failed", slot.Name); return true; }
        Say("@sandbox.menu.loaded", slot.Name);
        Close(world);
        return true;
    }

    private static void Close(World world)
    {
        if (world.Resources.TryGet<UiScreenStack>(out var stack)) stack!.CloseTop();
    }

    private void Say(string key, string slot)
    {
        Message = key;
        MessageSlot = slot;
    }

    // "save1", "save2"…: the first number no slot has.
    private static string NextSlotName(SaveSystem saves)
    {
        for (int n = 1; ; n++)
        {
            string name = $"save{n}";
            if (!saves.Exists(name)) return name;
        }
    }
}
