#nullable enable
using System;
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

    // InventoryGrid in whole squares.
    public (int Columns, int Rows) GridSize => (Math.Max((int)MathF.Round(InventoryGrid.X), 1), Math.Max((int)MathF.Round(InventoryGrid.Y), 1));

    // What a game has without a record of its own.
    public static readonly RpgConventionsRecord Default = new();
}

public static class RpgConventions
{
    // The game's rpg_conventions, or the defaults. A lookup per call, so a hot reload is seen at once.
    public static RpgConventionsRecord Of(RecordStore records)
    {
        if (records.TypeNameOf(typeof(RpgConventionsRecord)) == null) return RpgConventionsRecord.Default;
        foreach (var id in records.Ids("rpg_conventions").OrderBy(i => i.ToString(), StringComparer.Ordinal))
            if (records.TryGet(id, out RpgConventionsRecord conventions)) return conventions;
        return RpgConventionsRecord.Default;
    }

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
        try { RecordId.Parse(conventions.SpellNamespace + ":spell", conventions.SpellNamespace); }
        catch (FormatException ex) { check.Error(nameof(RpgConventionsRecord.SpellNamespace), $"'{conventions.SpellNamespace}' is not a record namespace: {ex.Message}"); }
    }
}
