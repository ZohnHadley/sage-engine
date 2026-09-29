#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Gameplay;

// What using an item does (docs/REDESIGN.md §4.3, issue #28). Items could be carried and worn and
// nothing else; an item now lists `uses`, entries of an open vocabulary, run in order when it is used:
//
//   { "type": "item", "id": "potion_red", "uses": [{ "use": "heal", "amount": 25 }, "consume"] }
//
// The engine's are `consume` (apply its effects, then one is gone), `read` (words, and perhaps a spell
// learned) and `cast` (a spell goes off at once, free: the item paid for it). A game declares more:
//
//   [ItemUse("heal", Plugin = "mygame")] public sealed class Heal : IItemUse { public float Amount; … }
//
// `world.UseItem(user, item)` does it (the `use_item` command, and later an inventory screen); a use
// that refuses stops the rest, so a potion is not used up by a use that could not happen.
[Vocabulary("item_use", Key = "use")]
[Experimental("SAGE0120", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // open vocabulary (#28): may change before 1.0
public interface IItemUse
{
    // Whether it could happen now, and why not (R17): asked of every use before any runs.
    bool CanUse(in ItemUse use, out string why)
    {
        why = "";
        return true;
    }

    // Does it. False, with why, when it could not.
    bool Use(in ItemUse use, out string why);
}

[Experimental("SAGE0120", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // open vocabulary (#28): may change before 1.0
public sealed class ItemUseAttribute : VocabularyEntryAttribute<IItemUse>
{
    public ItemUseAttribute(string id) : base(id) { }
}

// One using: who, which item, and its record.
[Experimental("SAGE0120", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // open vocabulary (#28): may change before 1.0
public readonly ref struct ItemUse
{
    public World World { get; init; }
    public Entity User { get; init; }
    public RecordId Item { get; init; }
    public ItemRecord Record { get; init; }
}

public static class ItemUses
{
    // Whether `user` could use `item` now, and why not: carrying it, it has a use, and every use agrees.
    public static bool CanUse(this World world, Entity user, RecordId item, out string why)
    {
        if (!world.Records().TryGet(item, out ItemRecord record)) { why = "there is no such thing"; return false; }
        if (world.CountOf(user, item) == 0) { why = "you are not carrying it"; return false; }
        if (record.Uses.Count == 0) { why = "nothing happens"; return false; }
        var use = new ItemUse { World = world, User = user, Item = item, Record = record };
        foreach (var entry in record.Uses)
            if (!entry.CanUse(in use, out why)) return false;
        why = "";
        return true;
    }

    // Uses it: every use in order, stopping at one that refuses. False (and why) when nothing could.
    public static bool UseItem(this World world, Entity user, RecordId item, out string why)
    {
        if (!world.CanUse(user, item, out why)) return false;
        var record = world.Records().Get<ItemRecord>(item);
        var use = new ItemUse { World = world, User = user, Item = item, Record = record };
        foreach (var entry in record.Uses)
            if (!entry.Use(in use, out why)) return false;
        why = "";
        return true;
    }

    public static bool UseItem(this World world, Entity user, RecordId item) => world.UseItem(user, item, out _);

    // `use_item <item>`: what an inventory screen's "use" will do.
    internal static void RegisterCommands(Engine engine) =>
        engine.CVars.RegisterCommand("use_item", CVarFlags.None, "use_item <item>: use something the local player carries.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "use_item <item>"); return; }
            var item = engine.Records.Resolve("item", a[0]);
            if (item.IsEmpty) return;
            engine.ForEachPlayer((world, entity) =>
                Log.Info(LogCat.Console, world.UseItem(entity, item, out string why)
                    ? $"{World.Describe(entity)} uses {item.Name}"
                    : $"{World.Describe(entity)} cannot use {item.Name}: {why}"));
        });
}

// ---- the engine's uses ----------------------------------------------------------------------------

// Used up: its `effects` are applied to the user, and one is gone from the stack.
[ItemUse("consume", Plugin = "sage.gameplay.items")]
internal sealed class ConsumeUse : IItemUse
{
    [Property(Tooltip = "Effects applied to whoever uses it up")]
    public List<RecordRef<EffectRecord>> Effects = new();

    public bool Use(in ItemUse use, out string why)
    {
        foreach (var effect in Effects) global::Sage.Gameplay.Effects.Apply(use.World, use.User, effect, use.User);
        why = use.World.Take(use.User, use.Item, 1) ? "" : "it is gone";
        return why.Length == 0;
    }
}

// Words: said to the user, and an ability learned if it names one (a spellbook, a scroll of learning).
[ItemUse("read", Plugin = "sage.gameplay.items")]
internal sealed class ReadUse : IItemUse
{
    [Property(Tooltip = "What it says")]
    public string Text = "";
    [Property(Tooltip = "An ability reading it teaches")]
    public RecordRef<AbilityRecord> Teach;

    public bool Use(in ItemUse use, out string why)
    {
        why = "";
        if (Text.Length > 0)
        {
            use.World.Say(Text, MessageKind.Info, 6f);
            Log.Info(LogCat.Gameplay, $"{use.Record.Describe(use.Item)}: {Text}");
        }
        if (!Teach.IsEmpty) use.World.Teach(use.User, Teach);
        return true;
    }
}

// A spell that goes off at once from where the user looks, whether or not they know it, at no cost and
// with no cooldown: the item is what paid (a wand, a scroll). The same delivery and payload as the
// spell cast by hand (AbilityCasting.CastNow), so it lands the same way.
[ItemUse("cast", Plugin = "sage.gameplay.items")]
internal sealed class CastUse : IItemUse
{
    [Property(Tooltip = "The ability it casts")]
    public RecordRef<AbilityRecord> Ability;

    public bool CanUse(in ItemUse use, out string why)
    {
        why = Ability.IsEmpty || !use.World.Records().TryGet(Ability, out AbilityRecord _) ? "it holds no spell" : "";
        return why.Length == 0;
    }

    public bool Use(in ItemUse use, out string why)
    {
        why = AbilityCasting.CastNow(use.World, use.User, Ability) ? "" : "the spell fizzles";
        return why.Length == 0;
    }
}

internal static class AbilityCasting
{
    // Casts an ability now, from `caster`'s eye along where it looks: no wind-up, no cost, no cooldown
    // and no need to know it — a spell something else paid for (an item, a trap). Through the ability's
    // own delivery and payload, so it lands exactly as the same spell cast by hand would.
    public static bool CastNow(World world, Entity caster, RecordId ability)
    {
        var records = world.Records();
        if (!world.IsAlive(caster) || !records.TryGet(ability, out AbilityRecord record)) return false;
        if (!world.TryGet<Transform>(caster, out var transform)) return false;
        if (AbilityDeliveries.Of(world, record) is not { } delivery) return false;

        var origin = transform.LocalPosition;
        if (world.TryGet<CharacterController>(caster, out var character))
            origin = CharacterController.EyeOf(origin, in character, CharacterConventions.Of(world).ProfileOf(records, character.Profile));
        var aim = world.TryGet<PawnIntent>(caster, out var intent)
            ? Vector3.Transform(TransformMath.Forward, Quaternion.CreateFromYawPitchRoll(intent.Yaw, intent.Pitch, 0))
            : SageMath.ForwardFromYaw(SageMath.YawOf(transform.LocalRotation));

        foreach (var cue in record.CastCues) world.Events.Send(new CueTriggered(cue, caster, origin));
        var release = new AbilityRelease
        {
            World = world, Caster = caster, Ability = ability, Record = record,
            Origin = origin, Aim = aim, Space = world.Resources.Get<IPhysicsWorld>(),
        };
        if (!delivery.Release(in release, out var point, out var struck)) return true;   // on its way
        new AbilityPayload(world).Deliver(world, caster, ability, record, point, struck);
        return true;
    }
}
