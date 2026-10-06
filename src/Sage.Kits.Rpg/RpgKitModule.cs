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
// hold things in, and the bag, spellbook, journal and conversation as panels and screens.
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

    // The rest and wait screen (issue 4g-7): hours, then sleep or wait (RestView, the Rest rule).
    public static readonly RecordId RestScreen = new(ContentNamespace, "rest");

    // The mods screen (issue 4j-6): what was found, on or off, the order, and the conflicts (Sage.UI's ModsView).
    public static readonly RecordId ModsScreen = new(ContentNamespace, "mods");

    // The controls screen (issue #328): rebind by pressing the key (Sage.UI's ControlsView).
    public static readonly RecordId ControlsScreen = new(ContentNamespace, "controls");

    // The options screen (issue #339): graphics, audio, controls and gameplay, its settings ui_option
    // records over cvars (Sage.UI's OptionsView); the controls page opens ControlsScreen.
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
        var actions = _actions;
        ctx.Engine.Records.AddCheck<RpgConventionsRecord>((conventions, check) => RpgConventions.Check(actions, conventions, check));

        var slots = _slots = ctx.Get<EquipSlots>();
        slots.Register(MainHand);
        slots.Register(OffHand);
        ctx.Engine.Records.AddCheck<RpgItemRecord>(RpgItemRecord.Check);

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
        // Using a chest or a body opens its screen (issue #344).
        world.AddSystem(new UseScreenSystem(world));
        // The equipment screen lists the slots there are.
        if (!world.Resources.TryGet<EquipSlots>(out _)) world.Resources.Add(_slots!);
    }
}
