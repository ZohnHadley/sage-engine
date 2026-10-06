#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Sage.Kits.Rpg;

// What the RPG kit means by its words (issue #27), as gameplay_conventions is for the base (#26):
// the namespace the spellmaker writes its spells under and the button that fires the readied spell.
//
// A game that wants other words adds **one** record of this type, in its own namespace:
//
//   { "type": "rpg_conventions", "id": "rpg", "spellNamespace": "spells", "castAction": "Cast" }
//
// With none, every field is its default. The kit's content (issue #98) has none of these either, so
// there is no well-known id to patch; two of them is a load error, since which one won would depend on
// the order of the mounts.
[Record("rpg_conventions", Plugin = RpgKitModule.Id)]
public sealed class RpgConventionsRecord
{
    [Property(Tooltip = "The record namespace a spell made in the spellmaker is given (\"custom\": custom:firebolt). " +
                        "Its own, so nothing a player made can shadow content")]
    public string SpellNamespace = "custom";

    [Property(Tooltip = "The button that casts the readied spell; the kit registers \"Cast\", a game may register its own")]
    public string CastAction = "Cast";

    [Property(Min = 1, Max = 64, Tooltip = "An inventory grid's size in squares, [columns, rows]: the rows are the least it shows, and it grows " +
                                            "downwards to fit what is carried (issue #98)")]
    public System.Numerics.Vector2 InventoryGrid = new(8, 6);

    // Resting (issue 4g-7, the kit's rest rule over Time.Pass): sleeping is refused with a hostile creature
    // this near, and the rest screen passes at most RestMaxHours at once. RestEffect, when given, is
    // applied on waking with the hours slept as its magnitude: "health +5" heals five an hour.
    [Experimental(RpgKitModule.OpenWorld, UrlFormat = RpgKitModule.ExperimentalUrl)]
    [Property(Min = 0, Unit = "m", Category = "Rest", Tooltip = "Sleeping is refused while a hostile creature is this near; 0: never refused")]
    public float RestEnemyRange = 25f;

    [Experimental(RpgKitModule.OpenWorld, UrlFormat = RpgKitModule.ExperimentalUrl)]
    [Property(Min = 1, Max = 168, Unit = "h", Category = "Rest", Tooltip = "The most the rest screen passes at once")]
    public int RestMaxHours = 24;

    [Experimental(RpgKitModule.OpenWorld, UrlFormat = RpgKitModule.ExperimentalUrl)]
    [Property(Category = "Rest", Tooltip = "Applied on waking from a sleep, its magnitude the hours slept (\"health +5\" heals five an hour); empty: none")]
    public RecordRef<EffectRecord> RestEffect;

    [Property(Tooltip = "The screen using a body with an inventory opens, and a use_screen part that names none (issue #344); empty: the kit's rpg:loot")]
    public RecordRef<Sage.UI.ScreenRecord> LootScreen;

    // InventoryGrid in whole squares.
    public (int Columns, int Rows) GridSize => (Math.Max((int)MathF.Round(InventoryGrid.X), 1), Math.Max((int)MathF.Round(InventoryGrid.Y), 1));

    // What a game has without a record of its own.
    public static readonly RpgConventionsRecord Default = new();
}

public static class RpgConventions
{
    // The game's rpg_conventions, or the defaults. Found once per content load (a hot reload is seen at
    // the next call), so a system or a screen can ask every frame without allocating (issue #98).
    public static RpgConventionsRecord Of(RecordStore records)
    {
        if (records.TypeNameOf(typeof(RpgConventionsRecord)) == null) return RpgConventionsRecord.Default;
        var cache = Cached.GetValue(records, static store =>
        {
            var made = new Cache();
            store.Reloaded += () => made.Stale = true;
            return made;
        });
        if (cache.Stale || cache.Count != records.Count)
        {
            cache.Found = RpgConventionsRecord.Default;
            foreach (var id in records.Ids("rpg_conventions").OrderBy(i => i.ToString(), StringComparer.Ordinal))
                if (records.TryGet(id, out RpgConventionsRecord conventions)) { cache.Found = conventions; break; }
            cache.Count = records.Count;
            cache.Stale = false;
        }
        return cache.Found;
    }

    private sealed class Cache
    {
        public RpgConventionsRecord Found = RpgConventionsRecord.Default;
        public int Count = -1;
        public bool Stale = true;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RecordStore, Cache> Cached = new();

    public static RpgConventionsRecord Of(World world) => Of(world.Records());

    // Each field against what it names (content loads after every module's Init, so the actions are
    // all registered by then): a misspelt cast button would otherwise be a button that does nothing.
    internal static void Check(ActionRegistry actions, RpgConventionsRecord conventions, RecordCheck check)
    {
        if (!string.IsNullOrEmpty(conventions.CastAction))
        {
            if (!actions.TryGet(conventions.CastAction, out var info))
                check.Error(nameof(RpgConventionsRecord.CastAction), $"no input action '{conventions.CastAction}' is registered; " +
                    $"a module registers it in Init (actions.Register(\"{conventions.CastAction}\", ActionKind.Button))");
            else if (info.Kind != ActionKind.Button)
                check.Error(nameof(RpgConventionsRecord.CastAction), $"input action '{conventions.CastAction}' is {info.Kind}, not a button");
        }
        if (conventions.InventoryGrid.X < 1 || conventions.InventoryGrid.Y < 1)
            check.Error(nameof(RpgConventionsRecord.InventoryGrid), $"a grid is at least [1, 1] squares; it is [{conventions.InventoryGrid.X}, {conventions.InventoryGrid.Y}]");
        if (conventions.RestMaxHours < 1)
            check.Error(nameof(RpgConventionsRecord.RestMaxHours), $"a rest is at least an hour; it is {conventions.RestMaxHours}");
        if (conventions.RestEnemyRange < 0f || float.IsNaN(conventions.RestEnemyRange))
            check.Error(nameof(RpgConventionsRecord.RestEnemyRange), $"{conventions.RestEnemyRange} is not a distance (0: never refused)");
        try { RecordId.Parse(conventions.SpellNamespace + ":spell", conventions.SpellNamespace); }
        catch (FormatException ex) { check.Error(nameof(RpgConventionsRecord.SpellNamespace), $"'{conventions.SpellNamespace}' is not a record namespace: {ex.Message}"); }
    }
}
