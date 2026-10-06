#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Sage.UI;

namespace Sage.Kits.Rpg;

// The menus round a playthrough (issue #342): the title a game shows before its world starts, the pause
// menu, and the save and load slot screens. Each is a `screen` in the kit's content over one of these
// view-models; a game patches the layouts, or makes its own screens over the same view-models.
//
//   screen rpg:title — layout rpg:title over `rpg_title`: New game, Continue (the newest save this build
//                      reads), Load, Options, Mods, Quit. game.json's `"title": "rpg:title"` opens it over
//                      the world, which waits (Scenes.Title) until a new game begins or a save loads.
//   screen rpg:pause — layout rpg:pause over `rpg_pause`, `"pauses": true`: the world stands still while it
//                      is open. Resume, Save, Load, Options, Quit (which asks first: what is not saved is lost).
//   screen rpg:save  — layout rpg:saves over `rpg_save`: a new slot, or a slot saved over (asked first).
//   screen rpg:load  — layout rpg:saves over `rpg_load`: a slot loaded (asked first, in a game).
//                      Both list SaveSystem.Slots, newest first, with when, where, the mods and the kind,
//                      and delete a slot (asked first).
//
// The questions are UiScreenStack.Confirm's (issue #343). The words are `@rpg.menu.*` and `@rpg.saves.*`
// keys in the kit's string table.

// What the three share: opening another of the kit's screens on top, and closing every window once the
// game is under way (a new game, a load).
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the RPG screens: SAGE0125, as Sage.UI
public static class RpgMenus
{
    public const string NewGameButton = "new_game", ContinueButton = "continue", LoadButton = "load", SaveButton = "save",
                        OptionsButton = "options", ModsButton = "mods", QuitButton = "quit", ResumeButton = "resume";

    // `screen` on top of the world's stack; false when the world has no stack or there is no such screen.
    public static bool Open(World world, RecordId screen)
    {
        if (!world.Resources.TryGet<UiScreenStack>(out var stack) || stack == null) return false;
        if (world.Engine is { } engine && !engine.Records.Exists("screen", screen))
        {
            Log.Warn(LogCat.UI, $"No screen '{screen}' to open");
            return false;
        }
        stack.Open(screen, new UiBindContext(world, Scenes.Player(world)));
        return true;
    }

    // Every window closes (fading), the HUD stays: what a new game or a load ends with.
    public static void CloseMenus(World world)
    {
        if (!world.Resources.TryGet<UiScreenStack>(out var stack) || stack == null) return;
        foreach (var layer in stack.Layers.ToList())
            if (layer.Modal && !layer.IsClosing) stack.Close(layer);
    }

    // The host's `quit`, when it has one (a headless app has none: nothing happens).
    public static void Quit(Engine engine)
    {
        if (engine.CVars.FindCommand("quit") != null) engine.CVars.Execute("quit");
    }

    internal static bool HasScreen(in UiBindContext context, RecordId screen) =>
        context.World?.Engine is { } engine && engine.Records.Exists("screen", screen);

    internal static UiScreenStack? Stack(World world) =>
        world.Resources.TryGet<UiScreenStack>(out var stack) ? stack : null;
}

// The title screen's view-model: what to do before the world starts.
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[ViewModel("rpg_title")]
public sealed class TitleView : IViewModel
{
    private SaveSystem? _saves;
    private int _version = -1;

    // The newest save this build can read: what Continue loads; empty when there is none.
    public string ContinueSlot { get; private set; } = "";
    public bool CanContinue => ContinueSlot.Length > 0;

    // The options screen (issue #339) and the mods screen exist in this game's content.
    public bool HasOptions { get; private set; }
    public bool HasMods { get; private set; }

    // What the last button did, as a key (@rpg.menu.…) with the slot it was about; empty at first.
    public string Message { get; private set; } = "";
    public string MessageSlot { get; private set; } = "";

    public void Refresh(in UiBindContext context)
    {
        if (context.World?.Engine is not { } engine) return;
        if (_saves == null)
        {
            _saves = engine.Saves;
            _saves.Rescan();   // opening: another run may have saved since this one started
            HasOptions = RpgMenus.HasScreen(context, RpgKitModule.OptionsScreen);
            HasMods = RpgMenus.HasScreen(context, RpgKitModule.ModsScreen);
        }
        int version = _saves.SlotsVersion;
        if (version == _version) return;
        _version = version;
        ContinueSlot = "";
        foreach (var slot in _saves.Slots)   // newest first
            if (slot.CanLoad) { ContinueSlot = slot.Name; break; }
    }

    public bool Activate(Widget widget, in UiBindContext context)
    {
        if (context.World is not { Engine: { } engine } world) return false;
        switch (widget.Name)
        {
            case RpgMenus.NewGameButton:
                engine.BeginGame(world);
                RpgMenus.CloseMenus(world);
                return true;
            case RpgMenus.ContinueButton:
                if (!CanContinue) return false;
                if (engine.Saves.Load(ContinueSlot)) RpgMenus.CloseMenus(world);
                else Say("@rpg.saves.load_failed", ContinueSlot);
                return true;
            case RpgMenus.LoadButton: return RpgMenus.Open(world, RpgKitModule.LoadScreen);
            case RpgMenus.OptionsButton: return HasOptions && RpgMenus.Open(world, RpgKitModule.OptionsScreen);
            case RpgMenus.ModsButton: return HasMods && RpgMenus.Open(world, RpgKitModule.ModsScreen);
            case RpgMenus.QuitButton:
                RpgMenus.Quit(engine);   // nothing is lost at the title: no question
                return true;
        }
        return false;
    }

    // Back at the title goes nowhere: the screen stays (it is the game's way in).
    public bool Back(in UiBindContext context) => true;

    private void Say(string key, string slot)
    {
        Message = key;
        MessageSlot = slot;
    }
}

// The pause menu's view-model. Its screen `pauses`: the world's time stands still while it is open.
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[ViewModel("rpg_pause")]
public sealed class PauseView : IViewModel
{
    private bool _looked;

    public bool HasOptions { get; private set; }
    public bool HasMods { get; private set; }

    // The quit question is up (UiScreenStack.Confirm), and what it was answered, once it has been.
    public UiDialog? QuitQuestion { get; private set; }

    public void Refresh(in UiBindContext context)
    {
        if (_looked) return;
        _looked = true;
        HasOptions = RpgMenus.HasScreen(context, RpgKitModule.OptionsScreen);
        HasMods = RpgMenus.HasScreen(context, RpgKitModule.ModsScreen);
    }

    public bool Activate(Widget widget, in UiBindContext context)
    {
        if (context.World is not { Engine: { } engine } world) return false;
        switch (widget.Name)
        {
            case RpgMenus.ResumeButton:
                if (RpgMenus.Stack(world) is { } stack) stack.CloseTop();
                return true;
            case RpgMenus.SaveButton: return RpgMenus.Open(world, RpgKitModule.SaveScreen);
            case RpgMenus.LoadButton: return RpgMenus.Open(world, RpgKitModule.LoadScreen);
            case RpgMenus.OptionsButton: return HasOptions && RpgMenus.Open(world, RpgKitModule.OptionsScreen);
            case RpgMenus.ModsButton: return HasMods && RpgMenus.Open(world, RpgKitModule.ModsScreen);
            case RpgMenus.QuitButton:
                if (RpgMenus.Stack(world) is not { } question) { RpgMenus.Quit(engine); return true; }
                QuitQuestion = question.Confirm("@rpg.menu.quit_title", "@rpg.menu.quit_unsaved", yes =>
                {
                    if (yes) RpgMenus.Quit(engine);
                }, "@rpg.menu.quit_yes", "@rpg.menu.no");
                return true;
        }
        return false;
    }
}

// The save and load slot screens' rows and lists (rpg:save over SaveGameView, rpg:load over LoadGameView,
// one layout): every slot on disk, newest first, read from the headers alone and again only when they
// change (SaveSystem.SlotsVersion), so Refresh allocates nothing once warm.
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public abstract class SaveSlotsView : IViewModel
{
    public const string UseButton = "use", DeleteButton = "delete", NewButton = "new";

    public sealed class Slot
    {
        public string Name { get; internal set; } = "";

        // The title it was saved with (`save <slot> <title>`), else its name.
        public string Title { get; internal set; } = "";

        // When it was saved, in local time; where (the scene the player was in); which kind (a string key,
        // or empty for a save the player made); the mods that were on.
        public string When { get; internal set; } = "";
        public string Location { get; internal set; } = "";
        public string Kind { get; internal set; } = "";
        public string Mods { get; internal set; } = "";
        public bool HasMods => Mods.Length > 0;

        // How what wrote it differs from what is loaded now (the first line of SaveSlot.Mismatches).
        public string Mismatch { get; internal set; } = "";
        public bool HasMismatch => Mismatch.Length > 0;

        // The picture taken when it was saved, as a full path; empty without one.
        public string Thumbnail { get; internal set; } = "";
        public bool HasThumbnail => Thumbnail.Length > 0;

        public bool CanLoad { get; internal set; }
        public bool CannotLoad => !CanLoad;

        // The row's button: @rpg.saves.load or @rpg.saves.overwrite, and whether it does anything.
        public string Use { get; internal set; } = "";
        public bool CanUse { get; internal set; }

        public SaveSlot? Save { get; internal set; }
    }

    private readonly List<Slot> _pool = new();
    private SaveSystem? _saves;
    private int _version = -1;

    public List<Slot> Slots { get; } = new();
    public int SlotCount => Slots.Count;
    public bool HasSaves => Slots.Count > 0;
    public bool NoSaves => Slots.Count == 0;

    // Which screen this is: saving (a new slot, or over one) or loading.
    public abstract bool Saving { get; }
    public bool Loading => !Saving;

    // The title of the screen, a key.
    public string Heading => Saving ? "@rpg.saves.save_title" : "@rpg.saves.load_title";

    // What the last button did, as a key (@rpg.saves.…) with the slot it was about; empty at first.
    public string Message { get; private set; } = "";
    public string MessageSlot { get; private set; } = "";

    // The question up now (overwrite, delete, load over a game), until it is answered.
    public UiDialog? Question { get; private set; }

    public void Refresh(in UiBindContext context)
    {
        if (context.World?.Engine is not { } engine) return;
        if (_saves == null)
        {
            _saves = engine.Saves;
            _saves.Rescan();
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
            slot.Title = save.Title;
            slot.When = save.SavedUtc == default ? "" : save.SavedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            slot.Location = save.Scene.IsEmpty ? "" : save.Scene.Name;
            slot.Kind = save.Kind switch
            {
                SaveKind.Quick => "@rpg.saves.kind_quick",
                SaveKind.Auto => "@rpg.saves.kind_auto",
                _ => "",
            };
#pragma warning disable SAGE0132   // SaveSlot.Mods: the data mods API (phase 4j)
            slot.Mods = save.Mods.Count == 0 ? "" : string.Join(", ", save.Mods.Select(m => m.Id));
#pragma warning restore SAGE0132
            slot.Mismatch = save.Mismatches.Count > 0 ? save.Mismatches[0] : "";
            slot.Thumbnail = save.ThumbnailPath ?? "";
            slot.CanLoad = save.CanLoad;
            // A quick-save or an autosave is the engine's to write: the save screen does not save over it.
            slot.CanUse = Saving ? save.Kind == SaveKind.Manual : save.CanLoad;
            slot.Use = Saving ? "@rpg.saves.overwrite" : "@rpg.saves.load";
            Slots.Add(slot);
        }
    }

    public bool Activate(Widget widget, in UiBindContext context)
    {
        if (context.World is not { Engine: { } engine } world) return false;
        var stack = RpgMenus.Stack(world);
        if (widget.Name == NewButton)
        {
            if (!Saving) return false;
            string name = NextSlotName(engine.Saves);
            Say(SaveNow(world, stack, engine.Saves, name) ? "@rpg.saves.saved" : "@rpg.saves.save_failed", name);
            return true;
        }
        if (UiScreen.RowOf(widget) is not Slot slot) return false;
        if (widget.Name == DeleteButton)
        {
            Ask(world, stack, "@rpg.saves.delete_title", "@rpg.saves.delete_question", "@rpg.saves.delete", slot,
                yes => { if (yes) Delete(engine.Saves, slot); });
            return true;
        }
        // The row itself, or its button: load it, or save over it.
        return Saving ? Overwrite(world, stack, engine.Saves, slot) : Load(world, stack, engine.Saves, slot);
    }

    private bool Overwrite(World world, UiScreenStack? stack, SaveSystem saves, Slot slot)
    {
        if (!slot.CanUse) { Say("@rpg.saves.cannot_overwrite", slot.Name); return true; }
        Ask(world, stack, "@rpg.saves.overwrite_title", "@rpg.saves.overwrite_question", "@rpg.saves.overwrite", slot, yes =>
        {
            if (yes) Say(SaveNow(world, stack, saves, slot.Name) ? "@rpg.saves.overwritten" : "@rpg.saves.save_failed", slot.Name);
        });
        return true;
    }

    private bool Load(World world, UiScreenStack? stack, SaveSystem saves, Slot slot)
    {
        if (!slot.CanLoad) { Say("@rpg.saves.cannot_load", slot.Name); return true; }
        void Go()
        {
            if (!saves.Load(slot.Name)) { Say("@rpg.saves.load_failed", slot.Name); return; }
            Say("@rpg.saves.loaded", slot.Name);
            RpgMenus.CloseMenus(world);   // the game goes on from the save
        }
        // At the title there is nothing to lose; in a game, what was not saved is.
        if (Scenes.AtTitle(world) || stack == null) { Go(); return true; }
        Ask(world, stack, "@rpg.saves.load_title_question", "@rpg.saves.load_question", "@rpg.saves.load", slot, yes => { if (yes) Go(); });
        return true;
    }

    private void Delete(SaveSystem saves, Slot slot) =>
        Say(saves.Delete(slot.Name) ? "@rpg.saves.deleted" : "@rpg.saves.delete_failed", slot.Name);

    // A save from a pause menu: the stack paused the world, and a save taken so would load paused.
    private static bool SaveNow(World world, UiScreenStack? stack, SaveSystem saves, string name)
    {
        bool held = stack is { HoldsPause: true };
        if (held) world.Paused = false;
        try { return saves.Save(name); }
        finally { if (held) world.Paused = true; }
    }

    // Confirm localises a key itself; the question names the slot, so it is formatted here first.
    private void Ask(World world, UiScreenStack? stack, string title, string question, string confirm, Slot slot, Action<bool> answered)
    {
        if (stack == null) { answered(true); return; }
        string text = world.Resources.TryGet<Localisation>(out var words) && words != null
            ? words.Format(question, ("slot", slot.Title)) : question;
        Question = stack.Confirm(title, text, answered, confirm, "@rpg.menu.no");
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

// The save screen: a new slot, or a slot of the player's saved over.
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[ViewModel("rpg_save")]
public sealed class SaveGameView : SaveSlotsView
{
    public override bool Saving => true;
}

// The load screen: any slot this build reads.
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[ViewModel("rpg_load")]
public sealed class LoadGameView : SaveSlotsView
{
    public override bool Saving => false;
}
