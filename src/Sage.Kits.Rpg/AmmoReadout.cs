#nullable enable
namespace Sage.Kits.Rpg;

// The rounds of the weapon in hand (issue #138), for a view-model to bind: what is loaded and what is
// carried, as one line — "7 / 24" for an attack with a magazine (a pistol), "24" for one that spends
// straight from the bag (a bow and its arrows) — and nothing for an attack with no `ammo` (a sword).
// EquipmentView shows it, and a game's HUD view-model holds one (the Sandbox's HudView).
//
// Read it every frame: the line is rebuilt only when a number or the attack changes, so a HUD frame
// allocates nothing.
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4e: may change before 1.0
public sealed class AmmoReadout
{
    // The attack in hand, when it spends ammunition; empty otherwise.
    public RecordId Attack { get; private set; }
    public bool Has => !Attack.IsEmpty;
    public int Loaded { get; private set; }
    public int Carried { get; private set; }
    // Rounds a magazine holds (0: the attack takes its ammunition straight from the inventory).
    public int Magazine { get; private set; }
    public string Text { get; private set; } = "";

    // Reads `wielder`'s; true when anything shown changed.
    public bool Read(World world, Entity wielder)
    {
        var attack = AttackOf(world, wielder, out var record);
        int loaded = 0, carried = 0, magazine = 0;
        if (record != null)
        {
            magazine = record.Magazine;
            loaded = magazine > 0 ? Ammunition.Loaded(world, wielder, attack) : 0;
            carried = world.CountOf(wielder, record.Ammo.Id);
        }
        if (attack == Attack && loaded == Loaded && carried == Carried && magazine == Magazine && (Text.Length > 0 || !Has)) return false;

        Attack = attack;
        Loaded = loaded;
        Carried = carried;
        Magazine = magazine;
        var text = RpgText.Of(world);
        Text = !Has ? ""
             : magazine > 0 ? text.Format("@rpg.ammo.magazine", ("loaded", loaded), ("carried", carried))
             : text.Format("@rpg.ammo.loose", ("carried", carried));
        return true;
    }

    // What `wielder` attacks with now, and its record, when that attack spends ammunition.
    private static RecordId AttackOf(World world, Entity wielder, out AttackRecord? record)
    {
        record = null;
        if (!world.TryGet<Melee>(wielder, out var melee)) return default;
        var id = melee.Attack.IsEmpty ? world.Conventions().Attack.Id : melee.Attack;
        if (id.IsEmpty || !world.Records().TryGet(id, out AttackRecord found) || found.Ammo.IsEmpty) return default;
        record = found;
        return id;
    }
}
