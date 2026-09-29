#nullable enable
using System;
using System.Numerics;
using Friflo.Engine.ECS;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Items, inventory and interaction (docs/design/16 §3.2, TODO F19). Headless: an inventory is a list
// of record ids, and picking something up is a physics query — neither needs a window.
public class ItemTests
{
    public ItemTests() { _ = TestEnv.UserRoot; }

    private static readonly RecordId Sword = new("sage", "sword");
    private static readonly RecordId Shield = new("sage", "shield");
    private static readonly RecordId Arrow = new("sage", "arrow");
    private static readonly RecordId Anvil = new("sage", "anvil");
    private static readonly RecordId Armor = new("sage", "armor");
    private static readonly RecordId Health = new("sage", "health");

    private const string Records = """
        [{ "type": "gameplay_conventions", "id": "default_conventions", "health": "health", "dead": "state.dead", "invulnerable": "state.invulnerable", "damageType": "physical" },
         { "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
         { "type": "attribute", "id": "armor",  "start": 0,   "min": 0, "max": 95 },
         { "type": "tag", "id": "state.dead" },
         { "type": "tag", "id": "state.invulnerable" },

         { "type": "effect", "id": "damage", "duration": "Instant", "blockTags": ["state.invulnerable"],
           "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
         { "type": "effect", "id": "warded", "duration": "Infinite",
           "modifiers": [ { "attribute": "armor", "op": "Add", "value": 25 } ] },
         { "type": "damage_type", "id": "physical", "resist": "armor", "effect": "damage" },

         { "type": "attack", "id": "fists",       "damage": 6,  "damageType": "physical", "reach": 1.8, "windupTime": 0.1, "recoverTime": 0.1, "cooldown": 0.2 },
         { "type": "attack", "id": "sword_swing", "damage": 24, "damageType": "physical", "reach": 2.4, "windupTime": 0.1, "recoverTime": 0.1, "cooldown": 0.2 },

         { "type": "item", "id": "sword",  "label": "iron sword", "slot": "MainHand", "attack": "sword_swing", "weight": 4 },
         { "type": "item", "id": "shield", "label": "wooden shield", "slot": "OffHand", "effects": ["warded"], "weight": 6 },
         { "type": "item", "id": "arrow",  "label": "arrow", "weight": 0.1, "maxStack": 20 },
         { "type": "item", "id": "anvil",  "label": "anvil", "weight": 90 }]
        """;

    private static (Engine Engine, World World) NewWorld()
    {
        var engine = HeadlessApp.Gameplay().File("data/items.json", Records).Build().Engine;

        var world = engine.CreateWorld("items");
        var ground = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        world.Add(ground, Collider.Box(new Vector3(100, 1, 100)));
        return (engine, world);
    }

    private static Entity Carrier(World world, Vector3 feet, string name, Vector3 lookAt, float capacity = 50f)
    {
        var entity = world.Create(Transform.At(feet), name);
        world.AddCharacter(entity, world.Resources.Get<PhysicsSpace>().Layers.Player);
        world.Add(entity, Melee.With(new RecordId("sage", "fists")));
        world.AddInventory(entity, capacity);
        world.AddAttributes(entity);
        world.Get<PawnIntent>(entity).Yaw = SageMath.YawTo(feet, lookAt);
        return entity;
    }

    private static void Tick(World world, int ticks)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    [Fact]
    public void ItemsStackToTheirLimitAndComeBackOut()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var carrier = Carrier(world, Vector3.Zero, "carrier", Vector3.UnitZ);

            Assert.True(world.Give(carrier, Arrow, 25));      // maxStack 20: one full stack and a part
            Assert.Equal(25, world.CountOf(carrier, Arrow));
            Assert.Equal(2, world.Get<Inventory>(carrier).Items.Count);

            Assert.False(world.Take(carrier, Arrow, 30), "taking more than it has must take nothing");
            Assert.Equal(25, world.CountOf(carrier, Arrow));

            Assert.True(world.Take(carrier, Arrow, 21));
            Assert.Equal(4, world.CountOf(carrier, Arrow));
            Assert.Single(world.Get<Inventory>(carrier).Items);
        }
    }

    [Fact]
    public void ACarrierWillNotTakeMoreThanItCanLift()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var carrier = Carrier(world, Vector3.Zero, "carrier", Vector3.UnitZ, capacity: 20f);

            Assert.True(world.Give(carrier, Sword));            // 4 kg
            Assert.Equal(4f, world.WeightOf(carrier), 3);
            Assert.False(world.Give(carrier, Anvil), "90 kg does not fit in a 20 kg pack");
            Assert.Equal(0, world.CountOf(carrier, Anvil));
        }
    }

    // The point of the whole feature: a weapon hands its attack record to the wielder, and combat
    // never learns that swords exist (16 §3.2).
    [Fact]
    public void EquippingAWeaponChangesWhatASwingDoes()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var attacker = Carrier(world, Vector3.Zero, "attacker", new Vector3(0, 0, -2));
            var target = Carrier(world, new Vector3(0, 0, -1.5f), "target", Vector3.Zero);
            var attack = engine.Actions.Get("Attack");
            var damage = new EventProbe<Damaged>(world);

            float Swing()
            {
                float dealt = 0f;
                world.Get<PawnIntent>(attacker).Pressed = new ActionMask().With(attack);
                for (int i = 0; i < 40; i++)
                {
                    world.RunFixed(1f / 60f);
                    foreach (var ev in damage.Since()) if (ev.Hit.Target == target) dealt += ev.Applied;
                    world.Get<PawnIntent>(attacker).Pressed = default;
                }
                return dealt;
            }

            Assert.Equal(6f, Swing(), 2);                       // bare hands

            world.Give(attacker, Sword);
            Assert.True(world.Equip(attacker, Sword));
            Assert.Equal(24f, Swing(), 2);                      // the sword's own attack record

            world.Unequip(attacker, EquipSlot.MainHand);
            Assert.Equal(6f, Swing(), 2);                       // and back to what it was born with
        }
    }

    [Fact]
    public void WornItemsApplyTheirEffectsAndTakeThemBack()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var carrier = Carrier(world, Vector3.Zero, "carrier", Vector3.UnitZ);
            world.Give(carrier, Shield);

            Assert.True(world.Equip(carrier, Shield));
            Tick(world, 2);
            Assert.Equal(25f, world.Attribute(carrier, Armor), 3);

            // Dropping what you are wearing takes it off on the way out.
            Assert.False(world.Drop(carrier, Shield).IsNull);
            Tick(world, 2);
            Assert.Equal(0f, world.Attribute(carrier, Armor), 3);
            Assert.True(world.Get<Equipment>(carrier).OffHand.IsEmpty);
        }
    }

    [Fact]
    public void TheUseActionPicksUpWhatIsInFrontOfYou()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var player = Carrier(world, Vector3.Zero, "player", new Vector3(0, 0, -2));
            var wanted = world.SpawnPickup(Sword, 1, new Vector3(0, 0, -1.4f));
            var behind = world.SpawnPickup(Shield, 1, new Vector3(0, 0, 1.4f));
            var use = engine.Actions.Get("Use");
            Tick(world, 2);                                     // the pickups get their bodies

            world.Get<PawnIntent>(player).Pressed = new ActionMask().With(use);
            Tick(world, 1);

            Assert.Equal(1, world.CountOf(player, Sword));
            Assert.False(world.IsAlive(wanted), "what you picked up is gone from the world");
            Assert.Equal(0, world.CountOf(player, Shield));
            Assert.True(world.IsAlive(behind), "and what is behind you stays where it is");
        }
    }

    // The event has to stand on its own, because the thing it describes is destroyed in the same tick:
    // a reader a frame later (the audio system, 11 §3) can only read the event, and asking the world for
    // the sword's `Pickup` finds a dead entity. So `Used` carries what was taken and where.
    [Fact]
    public void WhatWasPickedUpOutlivesTheThingItWasPickedUpFrom()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var player = Carrier(world, Vector3.Zero, "player", new Vector3(0, 0, -2));
            var sword = world.SpawnPickup(Sword, 1, new Vector3(0, 0, -1.4f));
            var use = engine.Actions.Get("Use");
            var used = new EventProbe<Used>(world);
            Tick(world, 2);

            world.Get<PawnIntent>(player).Pressed = new ActionMask().With(use);
            Tick(world, 1);

            Assert.Single(used.All);
            Assert.Equal(Sword, used.All[0].Item);
            Assert.Equal(new Vector3(0, 0, -1.4f), used.All[0].Point);
            Assert.False(world.IsAlive(used.All[0].Target), "and the event still says what it said");
        }
    }

    // Using something that is not a pickup is still an interaction, and it says so — but it says nothing
    // was taken, which is how a presentation system knows not to play "picked up".
    [Fact]
    public void UsingSomethingThatIsNotAPickupTakesNothing()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var player = Carrier(world, Vector3.Zero, "player", new Vector3(0, 0, -2));
            var lever = world.Create(Transform.At(new Vector3(0, 0, -1.4f)), "lever");
            world.Add(lever, Collider.Box(new Vector3(0.4f, 1f, 0.4f)));
            lever.AddTag<Interactable>();
            var use = engine.Actions.Get("Use");
            var used = new EventProbe<Used>(world);
            Tick(world, 2);

            world.Get<PawnIntent>(player).Pressed = new ActionMask().With(use);
            Tick(world, 1);

            Assert.Single(used.All);
            Assert.Equal(lever, used.All[0].Target);
            Assert.True(used.All[0].Item.IsEmpty);
            Assert.True(world.IsAlive(lever), "and the lever is still there to be used again");
        }
    }

    // A HUD can only offer "press E" if something says what is in reach, so the system publishes that
    // every tick for the player, pressed or not (13 §3).
    [Fact]
    public void WhatThePlayerIsLookingAtIsPublishedForTheHud()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var player = Carrier(world, Vector3.Zero, "player", new Vector3(0, 0, -2));
            player.AddTag<PlayerControlled>();
            var sword = world.SpawnPickup(Sword, 1, new Vector3(0, 0, -1.4f));
            var state = world.Resources.Get<InteractionState>();
            var used = new EventProbe<Used>(world);
            Tick(world, 2);

            Assert.Equal(sword, state.Hovered);               // without pressing anything
            Assert.Empty(used.All);
            Assert.Equal(0, world.CountOf(player, Sword));    // and without taking it

            world.Destroy(sword);
            Tick(world, 2);
            Assert.True(state.Hovered.IsNull);
        }
    }

    [Fact]
    public void WhatIsOutOfReachStaysOnTheGround()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var player = Carrier(world, Vector3.Zero, "player", new Vector3(0, 0, -5));
            var far = world.SpawnPickup(Sword, 1, new Vector3(0, 0, -4.5f));   // g_interact_range is 2.5
            var use = engine.Actions.Get("Use");
            Tick(world, 2);

            world.Get<PawnIntent>(player).Pressed = new ActionMask().With(use);
            Tick(world, 1);

            Assert.Equal(0, world.CountOf(player, Sword));
            Assert.True(world.IsAlive(far));
        }
    }

    [Fact]
    public void WhatYouDropCanBePickedUpAgain()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var player = Carrier(world, Vector3.Zero, "player", new Vector3(0, 0, -2));
            world.Give(player, Sword);

            var dropped = world.Drop(player, Sword);
            Assert.False(dropped.IsNull);
            Assert.Equal(0, world.CountOf(player, Sword));
            Assert.True(world.Has<Pickup>(dropped));

            var use = engine.Actions.Get("Use");
            Tick(world, 2);
            world.Get<PawnIntent>(player).Pressed = new ActionMask().With(use);
            Tick(world, 1);

            Assert.Equal(1, world.CountOf(player, Sword));
        }
    }

    [Fact]
    public void AnUnknownItemIsReportedRatherThanCrashing()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var carrier = Carrier(world, Vector3.Zero, "carrier", Vector3.UnitZ);
            using var capture = new CaptureSink();

            Assert.False(world.Give(carrier, new RecordId("sage", "no_such_item")));
            Assert.Contains(capture.Entries, e => e.Message.Contains("No item record"));
        }
    }
}
