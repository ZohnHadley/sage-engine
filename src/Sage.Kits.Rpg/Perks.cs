#nullable enable
using System;
using System.Collections.Generic;
using Sage.UI;

namespace Sage.Kits.Rpg;

// Perks and traits as effects with prerequisites (issue #381, REDESIGN §5 4f). A perk is effects granted for
// good, the conditions it asks first (the base's ICondition vocabulary, the same words a dialogue option or a
// wire asks), and what it costs in perk points: an attribute, rpg_conventions `perkPoints`, that content fills
// (a levelling's effect adds a point a level). A trait is a perk that is never bought: a prefab's `perks` part
// gives it at character creation, a `grant_perk` action gives it later (a quest's reward, a chargen choice).
//
//   { "type": "perk", "id": "strong_arm", "cost": 1, "effects": ["strong_arm"],
//     "requires": [ { "skill": "blade", "atLeast": 4 }, { "level": "character", "atLeast": 2 } ] }
//   { "type": "perk", "id": "hardy", "trait": true, "effects": ["hardy"] }
//
// What a perk did is in its effects: an instant one changes base values (saved as attributes), a lasting one
// runs on the entity (saved as its active effects). The perks it has are saved too (`rpg:perks`), for the
// `has_perk` condition and the screen; granting one does not apply its effects again on a load.

[Record("perk", Plugin = RpgKitModule.Id)]
public sealed class PerkRecord
{
    [Property(Tooltip = "What a screen calls it; empty: its id")]
    public string Name = "";

    [Property(Tooltip = "What it does, in words a screen shows")]
    public string Description = "";

    [Property(Tooltip = "Applied when it is granted, with the one who has it as their source: a lasting effect (Infinite) for a bonus that stays, an instant one for a base value raised for good")]
    public List<RecordRef<EffectRecord>> Effects = new();

    [Property(Tooltip = "Must all hold for the one who picks it: { \"skill\": \"blade\", \"atLeast\": 4 }, { \"level\": \"character\", \"atLeast\": 2 }, { \"has_perk\": \"x\" }")]
    public List<ICondition> Requires = new();

    [Property(Min = 0, Tooltip = "Perk points it costs (rpg_conventions perkPoints); a trait costs nothing")]
    public float Cost = 1f;

    [Property(Tooltip = "A trait: chosen at character creation (a prefab's perks part) or granted (grant_perk), never picked with points")]
    public bool Trait;

    public string Label(RecordId id) => Name.Length > 0 ? Name : id.Name;

    internal static void Check(PerkRecord record, RecordCheck check)
    {
        if (record.Cost < 0f) check.Error(nameof(Cost), $"must not be below 0 (it is {record.Cost})");
        for (int i = 0; i < record.Effects.Count; i++)
            if (record.Effects[i].IsEmpty) check.Error($"{nameof(Effects)}[{i}]", "names no effect");
    }
}

// The perks and traits an entity has, in the order it got them. Saved; added with the first.
[Component("rpg:perks")]
public struct GrantedPerks : IComponent
{
    [Property(Tooltip = "The perks and traits it has")]
    public List<RecordId>? Perks;

    public readonly bool Has(RecordId perk) => Perks != null && Perks.Contains(perk);
}

// A perk or trait was granted: picked with points, or given.
[GameEvent]
public readonly record struct PerkGranted(Entity Entity, RecordId Perk);

public static class Perks
{
    public static bool Has(World world, Entity entity, RecordId perk) =>
        world.TryGet<GrantedPerks>(entity, out var granted) && granted.Has(perk);

    // The perk points an entity has to spend (rpg_conventions perkPoints' base value), or 0 when the game has none.
    public static float PointsOf(World world, Entity entity)
    {
        var points = RpgConventions.Of(world).PerkPoints;
        return points.IsEmpty ? 0f : Skills.Base(world, entity, points.Id);
    }

    // Whether `entity` may pick `perk` now, and when not, why: an unknown perk, one it has, a trait, a
    // requirement that does not hold (its own words), or too few points.
    public static bool CanPick(World world, Entity entity, RecordId perk, out string why)
    {
        if (!world.Records().TryGet(perk, out PerkRecord record)) { why = "there is no such perk"; return false; }
        if (Has(world, entity, perk)) { why = "you have it already"; return false; }
        if (record.Trait) { why = "a trait is chosen when the character is made"; return false; }
        if (!world.Has<Attributes>(entity)) { why = "it has no attributes"; return false; }
        if (!Conditions.TestAll(record.Requires, new ConditionContext(world, entity), out why))
        {
            if (why.Length == 0) why = "you do not meet its requirements";
            return false;
        }
        if (record.Cost > 0f)
        {
            if (RpgConventions.Of(world).PerkPoints.IsEmpty) { why = "this game has no perk points (rpg_conventions perkPoints)"; return false; }
            if (PointsOf(world, entity) < record.Cost) { why = "not enough perk points"; return false; }
        }
        why = "";
        return true;
    }

    // Picks a perk as a level-up's choice: CanPick, then its cost from the perk points, then Grant.
    public static bool Pick(World world, Entity entity, RecordId perk, out string why)
    {
        if (!CanPick(world, entity, perk, out why)) return false;
        var record = world.Records().Get<PerkRecord>(perk);
        if (record.Cost > 0f) Skills.AddBase(world, entity, RpgConventions.Of(world).PerkPoints.Id, -record.Cost);
        return Grant(world, entity, perk);
    }

    // Gives a perk or trait for nothing and applies its effects, asking nothing: character creation, a reward.
    // False when there is no such perk, it has it already, or it has no attributes for the effects to change.
    public static bool Grant(World world, Entity entity, RecordId perk)
    {
        if (!world.IsAlive(entity) || !world.Records().TryGet(perk, out PerkRecord record) || Has(world, entity, perk)) return false;
        if (record.Effects.Count > 0 && !world.Has<Attributes>(entity)) return false;
        if (!world.Has<GrantedPerks>(entity)) world.Add(entity, new GrantedPerks { Perks = new List<RecordId>() });
        ref var granted = ref world.Get<GrantedPerks>(entity);
        (granted.Perks ??= new List<RecordId>()).Add(perk);
        foreach (var effect in record.Effects)
            if (!effect.IsEmpty) Sage.Gameplay.Effects.Apply(world, entity, effect.Id, entity);
        world.Events.Send(new PerkGranted(entity, perk));
        return true;
    }

    // The perks screen's rows and the `perks` command's: every perk it has (ticked) and every perk it could
    // buy, greyed with the reason it cannot yet. Traits it does not have are not listed.
    public static Panel Panel(World world, Entity who, Panel? into = null)
    {
        var panel = into ?? new Panel();
        var records = world.Records();
        if (!world.Has<Attributes>(who))
        {
            panel.Begin("Perks", who, $"{World.Describe(who)} can have no perks (it has no attributes)");
            return panel;
        }
        var conventions = RpgConventions.Of(world);
        panel.Begin(conventions.PerkPoints.IsEmpty ? "Perks" : $"Perks — {PointsOf(world, who):0.##} points", who);
        if (records.TypeNameOf(typeof(PerkRecord)) == null) return panel;
        foreach (var id in records.Ids("perk"))
        {
            if (!records.TryGet(id, out PerkRecord record)) continue;
            bool has = Has(world, who, id);
            if (record.Trait && !has) continue;
            string why = "";
            bool can = has || CanPick(world, who, id, out why);
            string detail = record.Trait ? "trait" : record.Cost > 0f ? $"{record.Cost:0.##} {(record.Cost == 1f ? "point" : "points")}" : "free";
            panel.Add(PanelRow.Of(id, record.Label(id), detail, 1, has, can, can ? "" : why));
        }
        return panel;
    }

    // `perks`: the local player's perks and what it could pick; `perk <perk>`: pick one with perk points;
    // `grant_perk <perk>`: give one for nothing, as a cheat.
    internal static void RegisterCommands(Engine engine)
    {
        engine.CVars.RegisterCommand("perks", CVarFlags.None, "The local player's perks and traits, and the perks it could pick.", _ =>
            engine.ForEachPlayer((world, entity) => Panel(world, entity).Log(LogCat.Console)));

        engine.CVars.RegisterCommand("perk", CVarFlags.None, "perk <perk>: pick a perk for the local player, paying its cost in perk points.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "perk <perk>"); return; }
            var perk = engine.Records.Resolve("perk", a[0]);
            if (perk.IsEmpty) return;
            engine.ForEachPlayer((world, entity) =>
            {
                if (Pick(world, entity, perk, out string why)) Log.Info(LogCat.Console, $"{World.Describe(entity)} takes {perk.Name}");
                else Log.Warn(LogCat.Console, $"{World.Describe(entity)} cannot take {perk.Name}: {why}");
            });
        });

        engine.CVars.RegisterCommand("grant_perk", CVarFlags.Cheat, "grant_perk <perk>: give the local player a perk or trait for nothing.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "grant_perk <perk>"); return; }
            var perk = engine.Records.Resolve("perk", a[0]);
            if (perk.IsEmpty) return;
            engine.ForEachPlayer((world, entity) =>
                Log.Info(LogCat.Console, Grant(world, entity, perk)
                    ? $"{World.Describe(entity)} has {perk.Name}"
                    : $"{World.Describe(entity)} already has {perk.Name}, or cannot have it"));
        });
    }
}

// "perks": ["hardy"] — the perks and traits it starts with, for nothing: character creation's choice. After
// `attributes`, because their effects change attributes and never add the components that hold them.
[PrefabPart("perks", Plugin = RpgKitModule.Id, After = new[] { "attributes" }, Shorthand = nameof(Ids))]
public sealed class PerksPart : IPrefabPart
{
    [Property(Tooltip = "Perks and traits it starts with, granted for nothing")]
    public List<RecordRef<PerkRecord>> Ids = new();

    public void Apply(in PrefabPartContext ctx)
    {
        foreach (var perk in Ids)
            if (!perk.IsEmpty) Perks.Grant(ctx.World, ctx.Entity, perk.Id);
    }
}

// ---- the conditions a perk asks, and the action that grants one --------------------------------------

// Their fields are set from content by the vocabulary reader, never by this assembly's code.
#pragma warning disable CS0649   // never assigned: the vocabulary reader fills them from content

// `{ "has_perk": "strong_arm" }`: the subject has that perk or trait.
[Condition("has_perk", Plugin = RpgKitModule.Id)]
internal sealed class HasPerkCondition : ICondition
{
    [EntryValue, Property(Tooltip = "The perk or trait it must have")]
    public RecordRef<PerkRecord> Perk;

    public bool Test(in ConditionContext c, out string why)
    {
        why = "you need another perk first";
        return Perk.IsEmpty || Perks.Has(c.World, c.Subject, Perk.Id);
    }
}

// `{ "skill": "blade", "atLeast": 4 }`: the subject's rank in a skill (its base, as Skills.RankOf) is at least that.
[Condition("skill", Plugin = RpgKitModule.Id)]
internal sealed class SkillCondition : ICondition
{
    [EntryValue, Property(Tooltip = "The skill whose rank is asked")]
    public RecordRef<SkillRecord> Skill;
    [Property(Tooltip = "The least rank that holds")]
    public float AtLeast;

    public bool Test(in ConditionContext c, out string why)
    {
        why = "your skill is too low";
        return Skill.IsEmpty || Skills.RankOf(c.World, c.Subject, Skill.Id) >= AtLeast;
    }
}

// `{ "level": "character", "atLeast": 2 }`: the subject's level in a levelling is at least that.
[Condition("level", Plugin = RpgKitModule.Id)]
internal sealed class LevelCondition : ICondition
{
    [EntryValue, Property(Tooltip = "The levelling whose level is asked")]
    public RecordRef<LevellingRecord> Levelling;
    [Property(Tooltip = "The least level that holds")]
    public float AtLeast;

    public bool Test(in ConditionContext c, out string why)
    {
        why = "your level is too low";
        if (Levelling.IsEmpty) return true;
        return c.Records.TryGet(Levelling.Id, out LevellingRecord record) && Skills.Base(c.World, c.Subject, record.Level.Id) >= AtLeast;
    }
}

// `{ "grant_perk": "hardy" }`: gives the subject a perk or trait for nothing (a reward, a chargen choice).
[Action("grant_perk", Plugin = RpgKitModule.Id)]
internal sealed class GrantPerkAction : IAction
{
    [EntryValue, Property(Tooltip = "The perk or trait it gives")]
    public RecordRef<PerkRecord> Perk;

    public void Run(in ActionContext c)
    {
        if (!Perk.IsEmpty) Perks.Grant(c.World, c.Subject, Perk.Id);
    }
}

#pragma warning restore CS0649

// The perks screen (issue #381): what the player has (ticked), what it could pick and what each costs, greyed
// with why not. Confirm picks one, paying its perk points: the level-up's choice. The rows are the `perks`
// command's (Perks.Panel), so the console and the screen agree.
//   screen rpg:perks — layout rpg:list
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = RpgKitModule.ExperimentalUrl)]
[ViewModel("rpg_perks")]
public sealed class PerksView : PanelListView
{
    public override string Hint => "@rpg.perks.hint";

    protected override int Signature(World world, Entity subject)
    {
        int hash = Fold(13, (int)(Perks.PointsOf(world, subject) * 100f));
        if (world.TryGet<GrantedPerks>(subject, out var granted) && granted.Perks != null)
            foreach (var perk in granted.Perks) hash = Fold(hash, perk.GetHashCode());
        var records = world.Records();
        if (records.TypeNameOf(typeof(PerkRecord)) == null) return hash;
        foreach (var id in records.Ids("perk"))
            hash = Fold(hash, Perks.CanPick(world, subject, id, out _) ? 1 : 2);
        return hash;
    }

    protected override void Build(World world, Entity subject, Panel panel) => Perks.Panel(world, subject, panel);

    protected override bool Activate(World world, Entity subject, ListRow row)
    {
        if (row.Selected) return false;
        if (Perks.Pick(world, subject, row.Id, out string why)) return true;
        Message = why;
        return true;
    }
}
