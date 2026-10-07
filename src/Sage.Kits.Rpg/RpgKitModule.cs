#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sage.UI;

namespace Sage.Kits.Rpg;

// The RPG kit (REDESIGN §0.5, issue #27): the rules the action-RPG family shares, on top of the base's
// generic models. The base has abilities, items, quests and dialogue; this adds what makes them a
// Daggerfall — a readied spell the Cast button fires, a spellmaker and the book it writes, two hands to
// hold things in, and the bag, spellbook, journal and conversation as panels (printed at the console) and
// screens (widget screens, the only kind since issue #350).
//
// Loaded only for a game that names it (game.json `"kits": ["sage.kits.rpg"]`); never part of
// BasePlugins.All(). Its client half, Sage.Kits.Rpg.Client (`sage.kits.rpg.client`), comes with it in
// a host with a window.
//
// Its content (issue #98) is carried in the assembly and mounted under the record namespace `rpg`:
// the inventory, equipment, loot and topics `screen`s, their `ui_layout`s and `ui_style`s, and the
// `rpg` string table (`src/Sage.Kits.Rpg/content`). A game patches any of it from its own mounts.
[Plugin(Id, "0.1.0")]
[PluginContent(ContentNamespace)]
[RequiresPlugin("sage", ">=0.1")]   // the engine versions it is built for (issue #31)
public sealed class RpgKitModule : IModule
{
    public const string Id = "sage.kits.rpg";

    // The record namespace of the kit's own content: `rpg:inventory`, `@rpg.inventory.title`.
    public const string ContentNamespace = "rpg";

    // The kit's screens (screen records in its content), for UiScreens.OpenScreen.
    public static readonly RecordId InventoryScreen = new(ContentNamespace, "inventory");
    public static readonly RecordId EquipmentScreen = new(ContentNamespace, "equipment");
    public static readonly RecordId LootScreen = new(ContentNamespace, "loot");
    public static readonly RecordId TopicsScreen = new(ContentNamespace, "topics");
    public static readonly RecordId ShopScreen = new(ContentNamespace, "shop");

    // The journal and the map (issue #349): the story so far and the quests tracked, and the scene's picture
    // under its fog with the markers on it (JournalView, MapView). Every game with the kit has them.
    public static readonly RecordId JournalScreen = new(ContentNamespace, "journal");
    public static readonly RecordId MapScreen = new(ContentNamespace, "map");

    // The screens that were panels until issue #350 (ListViews.cs): the spellbook (B), the bag as a list with
    // what is in your hands (a game binds its key: the Sandbox's I), a conversation (opened by using somebody
    // with a `dialogue`) and the spellmaker (M).
    public static readonly RecordId SpellbookScreen = new(ContentNamespace, "spellbook");
    public static readonly RecordId BagScreen = new(ContentNamespace, "bag");
    public static readonly RecordId DialogueScreen = new(ContentNamespace, "dialogue");
    public static readonly RecordId SpellmakerScreen = new(ContentNamespace, "spellmaker");

    // The rest and wait screen (issue 4g-7): hours, then sleep or wait (RestView, the Rest rule).
    public static readonly RecordId RestScreen = new(ContentNamespace, "rest");

    // The perks screen (issue #381): pick a perk with the points a level gave (PerksView, Perks.Pick).
    public static readonly RecordId PerksScreen = new(ContentNamespace, "perks");

    // The mods screen (issue 4j-6): what was found, on or off, the order, and the conflicts (Sage.UI's ModsView).
    public static readonly RecordId ModsScreen = new(ContentNamespace, "mods");

    // The controls screen (issue #328): rebind by pressing the key (Sage.UI's ControlsView).
    public static readonly RecordId ControlsScreen = new(ContentNamespace, "controls");

    // The menus round a playthrough (issue #342): the title before the world starts (game.json's
    // `"title": "rpg:title"`), the pause menu, which stands the world still, and the save and load slot
    // screens (TitleView, PauseView, SaveGameView, LoadGameView).
    public static readonly RecordId TitleScreen = new(ContentNamespace, "title");
    public static readonly RecordId PauseScreen = new(ContentNamespace, "pause");
    public static readonly RecordId SaveScreen = new(ContentNamespace, "save");
    public static readonly RecordId LoadScreen = new(ContentNamespace, "load");

    // The options screen (issue #339): graphics, audio, controls and gameplay, its settings ui_option
    // records over cvars (Sage.UI's OptionsView); the controls page opens ControlsScreen. The title and pause menus
    // open it (#342).
    public static readonly RecordId OptionsScreen = new(ContentNamespace, "options");

    // The kit's experimental id for what it adds to phase 4g's open world (MAKING_A_GAME §10b): the base's own.
    internal const string OpenWorld = "SAGE0129";
    internal const string ExperimentalUrl = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api";

    // The kit's equipment slots: a weapon and a shield, Daggerfall's two hands (issue #27). The base
    // has no slots of its own; a game adds more with EquipSlots.Register in its Init.
    public const string MainHand = "MainHand";
    public const string OffHand = "OffHand";

    private ActionRegistry? _actions;
    private EquipSlots? _slots;
    private ProgressionRules? _progression;

    // Spells need abilities, the bag needs items; both bring attributes and combat with them. The
    // screens need sage.ui (a kit brings the base plugins it needs, even past game.json's `plugins`).
    // Topics are the dialogue plugin's; without it the topics screen lists nothing.
    public IReadOnlyList<Type> Dependencies => new[] { typeof(AbilitiesModule), typeof(ItemsModule), typeof(UiModule) };

    public void Init(ModuleContext ctx)
    {
        // The rpg_conventions record and the saved Spellbook are this plugin's by their attributes;
        // generated code registers them (#16). The button that fires the readied spell is registered
        // here, and rpg_conventions `castAction` picks it (or a game's own).
        _actions = ctx.Engine.Actions;
        _actions.Register("Cast", ActionKind.Button);
        // The buttons that open the kit's screens (08 §3.2, issue #354), with their default keys in the kit's
        // content (`rpg:ui`, `rpg:gameplay`): a game that wants other keys patches those maps. Registered here,
        // not in the client half, so a headless run validates the kit's input map against them.
        _actions.Register("Spellbook", ActionKind.Button);
        _actions.Register("Spellmaker", ActionKind.Button);
        _actions.Register("Journal", ActionKind.Button);
        _actions.Register("Rest", ActionKind.Button);
        var actions = _actions;
        ctx.Engine.Records.AddCheck<RpgConventionsRecord>((conventions, check) => RpgConventions.Check(actions, conventions, check));

        var slots = _slots = ctx.Get<EquipSlots>();
        slots.Register(MainHand);
        slots.Register(OffHand);
        ctx.Engine.Records.AddCheck<RpgItemRecord>(RpgItemRecord.Check);
        ctx.Engine.Records.AddCheck<AreaMapRecord>(AreaMapRecord.Check);

        // Skills and levelling (issue #377): the skill and levelling records over the base's attribute_gain.
        ctx.Engine.Records.AddCheck<SkillRecord>(SkillRecord.Check);
        ctx.Engine.Records.AddCheck<LevellingRecord>(LevellingRecord.Check);
        _progression = new ProgressionRules(ctx.Engine.Records);
        Skills.RegisterCommands(ctx.Engine);

        // Perks and traits (issue #381): effects with prerequisites, picked with perk points or granted.
        ctx.Engine.Records.AddCheck<PerkRecord>(PerkRecord.Check);
        Perks.RegisterCommands(ctx.Engine);

        // Composing spells at the console (F21), the same rules a spellmaker screen calls.
        Spellmaker.RegisterCommands(ctx.Engine);

        // The spellbook panel, printed (13 §3). A `*` is the readied spell and a greyed row says why it
        // cannot be cast right now — the same words a screen shows, from the same rules the cast
        // system applies (R17).
        ctx.Engine.CVars.RegisterCommand("spells", CVarFlags.None, "What the local player can cast, and what is ready.", _ =>
            ctx.Engine.ForEachPlayer((world, entity) =>
                GameplayPanels.Spellbook(world, entity).Log(LogCat.Console)));

        ctx.Engine.CVars.RegisterCommand("ready", CVarFlags.None, "ready <ability>: make it the spell the Cast button fires.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "ready <ability>"); return; }
            var ability = ctx.Engine.Records.Resolve("ability", a[0]);
            if (ability.IsEmpty) return;
            ctx.Engine.ForEachPlayer((world, entity) =>
                Log.Info(LogCat.Console, world.Ready(entity, ability)
                    ? $"{World.Describe(entity)} readies {ability.Name}"
                    : $"{World.Describe(entity)} does not know {ability.Name}"));
        });

        // Resting and waiting (4g-7): the rest screen's rule, at the console.
        Rest.RegisterCommand(ctx.Engine);

        // Prints the inventory *panel* (13 §3): the same rows the bag screen draws, so the console and
        // the screen cannot disagree about what you are carrying, and a `*` is what is in your hands.
        ctx.Engine.CVars.RegisterCommand("inv", CVarFlags.None, "What the local player is carrying and wearing.", _ =>
            ctx.Engine.ForEachPlayer((world, entity) =>
                GameplayPanels.Inventory(world, entity).Log(LogCat.Console)));
    }

    // One rpg_conventions at most: with two, which one counts would depend on the order of the mounts.
    public void Start(ModuleContext ctx)
    {
        var ids = ctx.Engine.Records.Ids("rpg_conventions").Select(i => i.ToString()).OrderBy(i => i, StringComparer.Ordinal).ToList();
        if (ids.Count > 1)
            throw new InvalidDataException($"There are {ids.Count} rpg_conventions records ({string.Join(", ", ids)}); a game has one, or none for the kit's defaults.");
    }

    public void OnWorldCreated(World world)
    {
        world.AddSystem(new ReadiedSpellSystem(world, _actions!));
        // Using a chest or a body opens its screen (issue #344), and somebody with something to say a
        // conversation (issue #350).
        world.AddSystem(new UseScreenSystem(world));
        // Walking lifts the map's fog (issue #349).
        world.AddSystem(new MapDiscoverySystem());
        // Use-XP becomes a skill's rank, and rises a level (issue #377).
        world.Resources.Add(_progression!);
        world.AddSystem(new ProgressionSystem(world, _progression!));
        // The kit's screens open with its buttons (the keys are the kit's default maps', issue #354): the
        // spellbook, the spellmaker, the journal and the rest screen. A game that binds one of these actions
        // to a screen of its own after this replaces it.
#pragma warning disable SAGE0125   // widget screens are Phase 4c's experimental UI (MAKING_A_GAME §10b)
        if (world.Resources.TryGet<UiScreenStack>(out var widgets) && widgets != null)
        {
            widgets.Bind(_actions!.Get("Spellbook"), SpellbookScreen);
            widgets.Bind(_actions!.Get("Spellmaker"), SpellmakerScreen);
            widgets.Bind(_actions!.Get("Journal"), JournalScreen);
            widgets.Bind(_actions!.Get("Rest"), RestScreen);
        }
#pragma warning restore SAGE0125
        // The equipment screen lists the slots there are.
        if (!world.Resources.TryGet<EquipSlots>(out _)) world.Resources.Add(_slots!);
    }
}
