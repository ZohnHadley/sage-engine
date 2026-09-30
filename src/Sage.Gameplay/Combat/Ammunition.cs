#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Gameplay;

// Ammunition, magazines and reload (issue #135, phase 4e; docs/design/16 "As built (ammunition)").
//
// An `attack` that names an `ammo` item spends it: from the wielder's `Inventory` directly when its
// `magazine` is 0 (a bow and its arrows), or from a magazine that is filled from the inventory by the
// Reload button. An attack with no `ammo` is infinite — a sword, a wand, a pistol in a game with no
// bookkeeping — and never touches any of this. The rounds loaded are a saved `sage:magazine` on the
// wielder, keyed by attack id, so they outlive a swap of `Melee.Attack` and a save.

// Rounds loaded for one attack.
[Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4e: may change before 1.0
public struct MagazineSlot
{
    [RecordRef("attack"), Property(Tooltip = "The attack these rounds are loaded for")]
    public RecordId Attack;
    [Property(Min = 0, Tooltip = "Rounds loaded")]
    public int Rounds;
}

// What a wielder has loaded, per attack. Added by the reload system the first time a wielder holds an
// attack with a magazine; the loaded rounds are saved, the reload in progress is not (a load starts
// you ready, like a swing).
[Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4e: may change before 1.0
[Component("sage:magazine")]
public struct Magazine : IComponent
{
    [Property(Tooltip = "Rounds loaded, by attack")]
    public List<MagazineSlot>? Loaded;
    [Transient] public bool Reloading;      // the Reload button was pressed and the magazine has not gone in yet
    [Transient] public float Timer;         // seconds into the reload
    [Transient] public RecordId Reloads;    // the attack being reloaded: swapping weapons abandons it

    public readonly int RoundsFor(RecordId attack)
    {
        if (Loaded != null)
            foreach (var slot in Loaded)
                if (slot.Attack == attack) return slot.Rounds;
        return 0;
    }

    internal void Set(RecordId attack, int rounds)
    {
        Loaded ??= new List<MagazineSlot>();
        for (int i = 0; i < Loaded.Count; i++)
        {
            if (Loaded[i].Attack != attack) continue;
            if (rounds > 0) Loaded[i] = new MagazineSlot { Attack = attack, Rounds = rounds };
            else Loaded.RemoveAt(i);
            return;
        }
        if (rounds > 0) Loaded.Add(new MagazineSlot { Attack = attack, Rounds = rounds });
    }
}

// A shot left the weapon (every attack the system starts: a pistol's round, a bow's arrow, a swing).
[Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4e: may change before 1.0
[GameEvent]
public readonly record struct WeaponFired(Entity Shooter, RecordId Attack);

// The trigger was pulled on an empty magazine (or an empty inventory, for an attack with no magazine).
[Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4e: may change before 1.0
[GameEvent]
public readonly record struct DryFire(Entity Shooter, RecordId Attack);

[Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4e: may change before 1.0
public static class Ammunition
{
    // Rounds the wielder has loaded for `attack`.
    public static int Loaded(World world, Entity entity, RecordId attack) =>
        world.TryGet<Magazine>(entity, out var magazine) ? magazine.RoundsFor(attack) : 0;

    // Takes one shot's ammunition. Ok; Dry (nothing to fire: DryFire is the caller's to raise); or
    // Busy (a reload is under way, so the trigger does nothing). An attack with no `ammo` is always Ok.
    internal enum Spent { Ok, Dry, Busy }

    internal static Spent Spend(World world, Entity entity, RecordId attackId, AttackRecord attack)
    {
        if (attack.Ammo.IsEmpty) return Spent.Ok;
        int cost = Math.Max(1, attack.AmmoPerShot);
        if (attack.Magazine <= 0) return world.Take(entity, attack.Ammo.Id, cost) ? Spent.Ok : Spent.Dry;

        if (!world.Has<Magazine>(entity)) return Spent.Dry;
        ref var magazine = ref world.Get<Magazine>(entity);
        if (magazine.Reloading && magazine.Reloads == attackId) return Spent.Busy;
        int rounds = magazine.RoundsFor(attackId);
        if (rounds < cost) return Spent.Dry;
        magazine.Set(attackId, rounds - cost);
        return Spent.Ok;
    }

    // Puts whatever is loaded for `attack` back in the inventory (unequipping or dropping the weapon).
    // What does not fit stays loaded rather than vanishing.
    internal static void Unload(World world, Entity entity, RecordId attackId)
    {
        if (attackId.IsEmpty || !world.Has<Magazine>(entity)) return;
        ref var magazine = ref world.Get<Magazine>(entity);
        if (magazine.Reloading && magazine.Reloads == attackId) magazine.Reloading = false;
        int rounds = magazine.RoundsFor(attackId);
        if (rounds <= 0) return;
        if (!world.Resources.Get<RecordStore>().TryGet(attackId, out AttackRecord attack) || attack.Ammo.IsEmpty) return;
        if (world.Give(entity, attack.Ammo.Id, rounds)) magazine.Set(attackId, 0);
    }
}

// Gameplay phase, before the attack system: the Reload button. Pressing it (ready, not mid-swing, the
// magazine not full, ammunition in the inventory) starts a reload; it completes on the `mag_in` event
// (`animations.magIn`) from the wielder's animator or its first-person arms — the previous tick's, like
// MeleeCombatSystem.Lands — or, with no event, after the attack's `reloadTime`. Completion moves rounds
// from the inventory into the magazine. Swapping weapons, dying or dropping the weapon abandons it.
[System("sage.combat.reload", Phase.Gameplay, Before = new[] { "sage.combat.melee" })]
internal sealed class ReloadSystem : ISystem
{
    private readonly Query<PawnIntent, Melee> _fighters;
    private readonly Query<Viewmodel> _cameras;
    private readonly RecordStore _records;
    private readonly EventReader<AnimationEvent> _animation;
    private readonly List<AnimationEvent> _fired = new();
    private readonly List<(Entity Entity, RecordId Attack, bool Reload)> _needMagazine = new();   // wielders to give a Magazine, after the loop (R14)
    private readonly List<(Entity Entity, string Trigger)> _started = new();
    private readonly ActionId _reload;

    public ReloadSystem(World world, RecordStore records, ActionRegistry actions)
    {
        _fighters = world.Query<PawnIntent, Melee>();
        _cameras = world.Query<Viewmodel>();
        _records = records;
        _animation = world.Events.Reader<AnimationEvent>(this);
        _reload = actions.Get(world.Conventions().Actions.Reload);
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        float dt = ctx.Tick.Dt;
        var conventions = world.Conventions();

        _fired.Clear();
        foreach (ref readonly var e in _animation.Read()) _fired.Add(e);

        foreach (var (intents, melees, entities) in _fighters.Chunks)
        {
            var intent = intents.Span;
            var melee = melees.Span;
            for (int n = 0; n < melee.Length; n++)
            {
                var entity = entities.EntityAt(n);
                var id = melee[n].Attack.IsEmpty ? conventions.Attack.Id : melee[n].Attack;
                AttackRecord? attack = null;
                if (!id.IsEmpty && _records.TryGet(id, out AttackRecord found)) attack = found;
                bool magazine = attack != null && !attack.Ammo.IsEmpty && attack.Magazine > 0;
                bool has = world.Has<Magazine>(entity);

                if (has)
                {
                    ref var mag = ref world.Get<Magazine>(entity);
                    if (mag.Reloading)
                    {
                        if (!magazine || mag.Reloads != id || world.HasTag(entity, conventions.Dead)) mag.Reloading = false;
                        else
                        {
                            mag.Timer += dt;
                            if (mag.Timer >= attack!.ReloadTime || MagIn(world, entity, conventions.Animations.MagIn))
                            {
                                mag.Reloading = false;
                                Fill(world, entity, ref mag, id, attack);
                            }
                        }
                        continue;
                    }
                }
                if (!magazine) continue;

                bool pressed = _reload.IsValid && intent[n].Pressed.Has(_reload) && melee[n].Phase == MeleePhase.Ready
                               && !world.HasTag(entity, conventions.Dead);
                if (!has)
                {
                    // Nothing is loaded yet, so the press is a reload if any ammunition is carried.
                    _needMagazine.Add((entity, id, pressed && world.CountOf(entity, attack!.Ammo.Id) > 0));
                    continue;
                }
                if (!pressed) continue;
                ref var held = ref world.Get<Magazine>(entity);
                if (held.RoundsFor(id) < attack!.Magazine && world.CountOf(entity, attack.Ammo.Id) > 0)
                {
                    held.Reloading = true;
                    held.Timer = 0f;
                    held.Reloads = id;
                    _started.Add((entity, conventions.Animations.Reload));
                }
            }
        }

        foreach (var (entity, id, reload) in _needMagazine)
        {
            if (!world.IsAlive(entity) || world.Has<Magazine>(entity)) continue;
            world.Add(entity, new Magazine { Reloading = reload, Reloads = reload ? id : default });
            if (reload) _started.Add((entity, conventions.Animations.Reload));
        }
        _needMagazine.Clear();

        // The wielder's own animator plays its reload too (the arms get theirs from ViewmodelCombatSystem).
        foreach (var (entity, trigger) in _started)
            if (world.IsAlive(entity) && !string.IsNullOrEmpty(trigger)) Animators.SetTrigger(world, entity, trigger);
        _started.Clear();
    }

    // Moves rounds from the inventory up to a full magazine.
    private static void Fill(World world, Entity entity, ref Magazine mag, RecordId id, AttackRecord attack)
    {
        int room = attack.Magazine - mag.RoundsFor(id);
        int take = Math.Min(room, world.CountOf(entity, attack.Ammo.Id));
        if (take <= 0 || !world.Take(entity, attack.Ammo.Id, take)) return;
        mag.Set(id, mag.RoundsFor(id) + take);
    }

    private bool MagIn(World world, Entity entity, string name)
    {
        if (name.Length == 0 || _fired.Count == 0) return false;
        if (Fired(entity, name)) return true;
        var camera = Viewmodels.CameraOf(world, _cameras, entity);
        if (camera.IsNull) return false;
        var arms = Viewmodels.ArmsOf(world, camera);
        return !arms.IsNull && Fired(arms, name);
    }

    private bool Fired(Entity entity, string name)
    {
        for (int i = 0; i < _fired.Count; i++)
            if (_fired[i].Entity == entity && string.Equals(_fired[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
