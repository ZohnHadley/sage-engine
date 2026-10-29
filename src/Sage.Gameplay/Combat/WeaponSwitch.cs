#nullable enable
using System.Collections.Generic;

namespace Sage.Gameplay;

// Gameplay phase, before reloading: switching between the weapons carried (issue #599). A weapon is a
// carried item with an `attack` and a `slot`; the weapons are those, once each, in inventory order (the
// order they were picked up). NextWeapon and PreviousWeapon wield the one after or before the one in hand,
// wrapping round; Weapon1 to Weapon9 wield the 1st to 9th. Wielding goes through Items.Equip, so a broken
// weapon is skipped and a reload in progress is abandoned as for any swap. Not mid-swing, not dead.
[System("sage.combat.weapon_switch", Phase.Gameplay, Before = new[] { "sage.combat.reload" })]
internal sealed class WeaponSwitchSystem : ISystem
{
    private readonly Query<PawnIntent, Equipment> _wielders;
    private readonly RecordStore _records;
    private readonly ActionId _next, _previous;
    private readonly ActionId[] _slots = new ActionId[ActionConventions.WeaponSlots];
    private readonly List<(Entity Entity, int Step, int Slot)> _wanted = new();   // equipped after the loop (R14)
    private readonly List<RecordId> _weapons = new();

    public WeaponSwitchSystem(World world, RecordStore records, ActionRegistry actions)
    {
        _wielders = world.Query<PawnIntent, Equipment>();
        _records = records;
        var names = world.Conventions().Actions;
        _next = actions.Get(names.NextWeapon);
        _previous = actions.Get(names.PreviousWeapon);
        for (int i = 0; i < _slots.Length; i++) _slots[i] = actions.Get($"{names.WeaponSlot}{i + 1}");
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        var dead = world.Conventions().Dead;
        _wanted.Clear();
        foreach (var (intents, _, entities) in _wielders.Chunks)
        {
            var intent = intents.Span;
            for (int n = 0; n < intent.Length; n++)
            {
                var pressed = intent[n].Pressed;
                int step = _next.IsValid && pressed.Has(_next) ? 1 : _previous.IsValid && pressed.Has(_previous) ? -1 : 0;
                int slot = 0;
                for (int i = 0; i < _slots.Length && step == 0 && slot == 0; i++)
                    if (_slots[i].IsValid && pressed.Has(_slots[i])) slot = i + 1;
                if (step == 0 && slot == 0) continue;
                var entity = entities.EntityAt(n);
                if (world.HasTag(entity, dead)) continue;
                if (world.TryGet<Melee>(entity, out var melee) && melee.Phase != MeleePhase.Ready) continue;
                _wanted.Add((entity, step, slot));
            }
        }
        foreach (var (entity, step, slot) in _wanted) Switch(world, entity, step, slot);
    }

    private void Switch(World world, Entity entity, int step, int slot)
    {
        if (!world.TryGet<Inventory>(entity, out var inventory) || inventory.Items == null) return;
        var equipment = world.Get<Equipment>(entity);
        _weapons.Clear();
        int current = -1;
        foreach (var stack in inventory.Items)
        {
            if (_weapons.Contains(stack.Item) || !_records.TryGet(stack.Item, out ItemRecord record)) continue;
            if (record.Attack.IsEmpty || record.Slot.Length == 0 || !world.CanEquip(entity, stack.Item, out _)) continue;
            if (current < 0 && equipment.In(record.Slot) == stack.Item) current = _weapons.Count;
            _weapons.Add(stack.Item);
        }
        if (_weapons.Count == 0) return;
        int target = slot > 0 ? slot - 1
                   : current < 0 ? (step > 0 ? 0 : _weapons.Count - 1)
                   : (current + step + _weapons.Count) % _weapons.Count;
        if (target >= _weapons.Count || target == current) return;
        world.Equip(entity, _weapons[target]);
    }
}
