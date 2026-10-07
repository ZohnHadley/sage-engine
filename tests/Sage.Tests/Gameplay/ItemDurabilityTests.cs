#nullable enable
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Item condition, wear and repair (issue #382): a weapon loses condition on each blow that lands, deals
// less as it wears and breaks at 0 as its record says; armour wears when its wearer is hurt; a repair kit's
// use puts condition back; all of it on the instance (#383), through equipment and a save.
public class ItemDurabilityTests
{
    public ItemDurabilityTests() { _ = TestEnv.UserRoot; }

    private static readonly RecordId Sword = new("sage", "sword");
    private static readonly RecordId Glass = new("sage", "glass_sword");
    private static readonly RecordId Stick = new("sage", "stick");
    private static readonly RecordId Knife = new("sage", "knife");
    private static readonly RecordId Shield = new("sage", "shield");
    private static readonly RecordId Kit = new("sage", "repair_kit");
    private static readonly RecordId Whetstone = new("sage", "whetstone");
    private static readonly RecordId Armor = new("sage", "armor");
    private static readonly RecordId SwordSwing = new("sage", "sword_swing");
    private static readonly RecordId Fists = new("sage", "fists");

    private const string Records = """
        [{ "type": "gameplay_conventions", "id": "default_conventions", "health": "health", "dead": "state.dead", "invulnerable": "state.invulnerable", "damageType": "physical" },
         { "type": "attribute", "id": "health", "start": 1000, "min": 0, "max": 1000 },
         { "type": "attribute", "id": "armor",  "start": 0,   "min": 0, "max": 95 },
         { "type": "tag", "id": "state.dead" },
         { "type": "tag", "id": "state.invulnerable" },
         { "type": "effect", "id": "damage", "duration": "Instant", "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
         { "type": "effect", "id": "warded", "duration": "Infinite", "modifiers": [ { "attribute": "armor", "op": "Add", "value": 20 } ] },
         { "type": "damage_type", "id": "physical", "resist": "armor", "effect": "damage" },
         { "type": "attack", "id": "fists",       "damage": 6,  "damageType": "physical", "reach": 1.8, "windupTime": 0.1, "recoverTime": 0.1, "cooldown": 0.2 },
         { "type": "attack", "id": "sword_swing", "damage": 24, "damageType": "physical", "reach": 2.4, "windupTime": 0.1, "recoverTime": 0.1, "cooldown": 0.2 },
         { "type": "attack", "id": "glass_swing", "damage": 40, "damageType": "physical", "reach": 2.4, "windupTime": 0.1, "recoverTime": 0.1, "cooldown": 0.2 },
         { "type": "attack", "id": "stick_swing", "damage": 10, "damageType": "physical", "reach": 2.4, "windupTime": 0.1, "recoverTime": 0.1, "cooldown": 0.2 },
         { "type": "attack", "id": "knife_swing", "damage": 8,  "damageType": "physical", "reach": 2.4, "windupTime": 0.1, "recoverTime": 0.1, "cooldown": 0.2 },
         { "type": "item", "id": "sword", "label": "iron sword", "slot": "MainHand", "attack": "sword_swing", "weight": 4,
           "durability": { "wearPerHit": 0.25, "wornDamage": 0.5 } },
         { "type": "item", "id": "glass_sword", "label": "glass sword", "slot": "MainHand", "attack": "glass_swing", "weight": 2,
           "durability": { "wearPerHit": 0.5, "breaks": "Remove" } },
         { "type": "item", "id": "stick", "label": "stick", "slot": "MainHand", "attack": "stick_swing", "weight": 1,
           "durability": { "wearPerHit": 0.5, "wornDamage": 0, "breaks": "Keep" } },
         { "type": "item", "id": "knife", "label": "throwing knife", "slot": "MainHand", "attack": "knife_swing", "weight": 0.2, "maxStack": 5,
           "durability": { "wearPerHit": 0.1 } },
         { "type": "item", "id": "shield", "label": "wooden shield", "slot": "OffHand", "effects": ["warded"], "weight": 6,
           "durability": { "wearWhenStruck": 0.5 } },
         { "type": "item", "id": "repair_kit", "label": "repair kit", "weight": 0.5, "maxStack": 10,
           "uses": [{ "use": "repair", "amount": 0.5 }, "consume"] },
         { "type": "item", "id": "whetstone", "label": "whetstone", "weight": 0.5, "maxStack": 10,
           "uses": [{ "use": "repair", "amount": 1, "upTo": 0.8, "slot": "MainHand" }, "consume"] }]
        """;

    private static HeadlessApp NewApp(string? savesRoot = null)
    {
        var app = HeadlessApp.Gameplay().WithHands().File("data/durability.json", Records).Build();
        if (savesRoot != null) app.Engine.Saves.Root = savesRoot;
        return app;
    }

    private static World NewWorld(HeadlessApp app)
    {
        var world = app.Engine.CreateWorld("main");
        var ground = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        world.Add(ground, Collider.Box(new Vector3(100, 1, 100)));
        return world;
    }

    private static Entity Carrier(World world, Vector3 feet, string name, Vector3 lookAt)
    {
        var entity = world.Create(Transform.At(feet), name);
        world.AddCharacter(entity, world.Resources.Get<IPhysicsWorld>().Layers.Player);
        world.Add(entity, Melee.With(Fists));
        world.AddInventory(entity, 100f);
        world.AddAttributes(entity);
        world.Get<PawnIntent>(entity).Yaw = SageMath.YawTo(feet, lookAt);
        return entity;
    }

    private sealed class Duel
    {
        public required HeadlessApp App;
        public required World World;
        public required Entity Attacker;
        public required Entity Target;
        public required EventProbe<Damaged> Damage;

        // One press of Attack, run to the end of the swing: what it dealt the target.
        public float Swing()
        {
            var attack = App.Engine.Actions.Get("Attack");
            float dealt = 0f;
            World.Get<PawnIntent>(Attacker).Pressed = new ActionMask().With(attack);
            for (int i = 0; i < 40; i++)
            {
                World.RunFixed(1f / 60f);
                foreach (var ev in Damage.Since()) if (ev.Hit.Target == Target) dealt += ev.Applied;
                World.Get<PawnIntent>(Attacker).Pressed = default;
            }
            return dealt;
        }
    }

    private static Duel NewDuel(HeadlessApp app)
    {
        var world = NewWorld(app);
        return new Duel
        {
            App = app, World = world,
            Attacker = Carrier(world, Vector3.Zero, "attacker", new Vector3(0, 0, -2)),
            Target = Carrier(world, new Vector3(0, 0, -1.5f), "target", Vector3.Zero),
            Damage = new EventProbe<Damaged>(world),
        };
    }

    private static System.Collections.Generic.List<ItemStack> Stacks(World world, Entity e) => world.Get<Inventory>(e).Items;

    // The issue's acceptance: each blow that lands wears the sword, each one deals less as it wears, and at
    // 0 it breaks — off it comes, back to bare hands, still in the bag and not equippable until repaired.
    [Fact]
    public void AWeaponLosesConditionOnHitDealsLessAndBreaks()
    {
        using var app = NewApp();
        var duel = NewDuel(app);
        var world = duel.World;
        var broke = new EventProbe<ItemBroke>(world);
        world.Give(duel.Attacker, Sword);
        Assert.True(world.Equip(duel.Attacker, Sword));
        Assert.Null(world.WornInstance(duel.Attacker, Hands.Main));   // as made: plain

        Assert.Equal(24f, duel.Swing(), 2);                            // condition 1: full damage
        var worn = world.WornInstance(duel.Attacker, Hands.Main);
        Assert.NotNull(worn);                                          // wear made it an instance
        Assert.Equal(0.75f, worn!.Condition, 3);
        Assert.Equal(24f * (0.5f + 0.5f * 0.75f), duel.Swing(), 2);    // 21: linear down to wornDamage at 0
        Assert.Equal(18f, duel.Swing(), 2);                            // condition 0.5
        Assert.Equal(0f, broke.Since().Count());
        Assert.Equal(15f, duel.Swing(), 2);                            // condition 0.25, and that blow broke it

        var gone = Assert.Single(broke.Since());
        Assert.Equal(Sword, gone.Item);
        Assert.False(gone.Removed);
        Assert.True(world.Get<Equipment>(duel.Attacker).In(Hands.Main).IsEmpty);
        Assert.Equal(Fists, world.Get<Melee>(duel.Attacker).Attack);
        Assert.Equal(1, world.CountOf(duel.Attacker, Sword));
        Assert.Equal(0f, Stacks(world, duel.Attacker).Single(s => s.Item == Sword).Instance!.Condition);
        Assert.False(world.CanEquip(duel.Attacker, Sword, out string why));
        Assert.Equal("it is broken", why);
        Assert.False(world.EquipAt(duel.Attacker, Stacks(world, duel.Attacker).FindIndex(s => s.Item == Sword)));
        Assert.Equal(6f, duel.Swing(), 2);                             // bare hands again
    }

    [Fact]
    public void AWeaponWhoseRecordSaysRemoveIsDestroyedWhenItBreaks()
    {
        using var app = NewApp();
        var duel = NewDuel(app);
        var world = duel.World;
        var broke = new EventProbe<ItemBroke>(world);
        world.Give(duel.Attacker, Glass);
        Assert.True(world.Equip(duel.Attacker, Glass));

        Assert.Equal(40f, duel.Swing(), 2);
        Assert.Equal(40f * (0.5f + 0.5f * 0.5f), duel.Swing(), 2);     // the default curve: 0.5x at 0
        Assert.True(Assert.Single(broke.Since()).Removed);
        Assert.Equal(0, world.CountOf(duel.Attacker, Glass));
        Assert.True(world.Get<Equipment>(duel.Attacker).In(Hands.Main).IsEmpty);
        Assert.Equal(Fists, world.Get<Melee>(duel.Attacker).Attack);
    }

    [Fact]
    public void AWeaponWhoseRecordSaysKeepStaysInHandAndUseless()
    {
        using var app = NewApp();
        var duel = NewDuel(app);
        var world = duel.World;
        world.Give(duel.Attacker, Stick);
        Assert.True(world.Equip(duel.Attacker, Stick));

        Assert.Equal(10f, duel.Swing(), 2);
        Assert.Equal(5f, duel.Swing(), 2);                             // 0.5: halfway to wornDamage 0
        Assert.Equal(0f, world.ConditionIn(duel.Attacker, Hands.Main));
        Assert.Equal(Stick, world.Get<Equipment>(duel.Attacker).In(Hands.Main));
        Assert.Equal(0f, duel.Swing(), 2);                             // in hand, and does nothing
        Assert.True(world.CanEquip(duel.Attacker, Stick, out _));
    }

    // "Only what differs is an instance" (#383): one knife of a plain stack of five is in hand; it is the one
    // that wears, split off as an instance, and the four left stay a plain stack.
    [Fact]
    public void APlainStackThatTakesWearSplitsTheWornUnitOffAsAnInstance()
    {
        using var app = NewApp();
        var world = NewWorld(app);
        var hero = Carrier(world, Vector3.Zero, "hero", new Vector3(0, 0, -2));
        world.Give(hero, Knife, 5);
        Assert.True(world.Equip(hero, Knife));

        Assert.True(world.Wear(hero, Hands.Main, 0.1f));
        var stacks = Stacks(world, hero);
        Assert.Equal(4, stacks.Single(s => s.Item == Knife && s.Instance == null).Count);
        var worn = stacks.Single(s => s.Item == Knife && s.Instance != null);
        Assert.Equal(1, worn.Count);
        Assert.Equal(0.9f, worn.Instance!.Condition, 3);
        Assert.Same(worn.Instance, world.WornInstance(hero, Hands.Main));
        Assert.Equal(5, world.CountOf(hero, Knife));

        // A plain item nobody wears does not wear; an empty slot has nothing to wear.
        Assert.False(world.Wear(hero, Hands.Off, 0.5f));
    }

    // Armour (a shield) wears when its wearer is hurt; at 0 it comes off and its effect with it.
    [Fact]
    public void WornArmourWearsWhenItsWearerIsHurt()
    {
        using var app = NewApp();
        var world = NewWorld(app);
        var hero = Carrier(world, Vector3.Zero, "hero", new Vector3(0, 0, -2));
        world.Give(hero, Shield);
        Assert.True(world.Equip(hero, Shield));
        world.RunFixed(1f / 60f);
        Assert.Equal(20f, world.Attribute(hero, Armor));

        Combat.ApplyDamage(world, new DamageInfo(default, hero, default, 10f, Vector3.Zero, Vector3.UnitZ));
        Assert.Equal(0.5f, world.ConditionIn(hero, Hands.Off), 3);
        Combat.ApplyDamage(world, new DamageInfo(default, hero, default, 10f, Vector3.Zero, Vector3.UnitZ));
        Assert.True(world.Get<Equipment>(hero).In(Hands.Off).IsEmpty);
        world.RunFixed(1f / 60f);
        Assert.Equal(0f, world.Attribute(hero, Armor));
        Assert.Equal(1, world.CountOf(hero, Shield));
    }

    // A repair kit's use mends what is in hand and is used up; mended back to as made it is plain again;
    // with nothing to mend it refuses and is not used up.
    [Fact]
    public void ARepairKitRestoresTheEquippedWeaponAndIsUsedUp()
    {
        using var app = NewApp();
        var duel = NewDuel(app);
        var world = duel.World;
        var hero = duel.Attacker;
        world.Give(hero, Sword);
        world.Give(hero, Kit, 3);
        Assert.True(world.Equip(hero, Sword));
        duel.Swing();
        duel.Swing();
        duel.Swing();
        Assert.Equal(0.25f, world.ConditionIn(hero, Hands.Main), 3);

        Assert.True(world.UseItem(hero, Kit, out string why), why);
        Assert.Equal(0.75f, world.ConditionIn(hero, Hands.Main), 3);
        Assert.Equal(2, world.CountOf(hero, Kit));
        Assert.Equal(24f * (0.5f + 0.5f * 0.75f), duel.Swing(), 2);   // it hits harder for it

        Assert.True(world.UseItem(hero, Kit));
        Assert.Null(world.WornInstance(hero, Hands.Main));            // as made again: a plain sword
        Assert.Null(Stacks(world, hero).Single(s => s.Item == Sword).Instance);
        Assert.Equal(Sword, world.Get<Equipment>(hero).In(Hands.Main));
        Assert.Equal(1, world.CountOf(hero, Kit));

        Assert.False(world.UseItem(hero, Kit, out why));
        Assert.Equal("nothing needs repair", why);
        Assert.Equal(1, world.CountOf(hero, Kit));
    }

    // A broken sword in the bag is what a kit mends when nothing worn needs it, and then it can be wielded;
    // a whetstone only reaches 0.8 and only the main hand.
    [Fact]
    public void ARepairMendsABrokenItemInTheBagAndAFieldKitStopsShortOfAsMade()
    {
        using var app = NewApp();
        var world = NewWorld(app);
        var hero = Carrier(world, Vector3.Zero, "hero", new Vector3(0, 0, -2));
        world.Give(hero, Sword, new ItemInstance { Condition = 0f });
        world.Give(hero, Kit);
        world.Give(hero, Whetstone, 2);
        Assert.False(world.CanEquip(hero, Sword, out _));
        Assert.False(world.UseItem(hero, Whetstone, out _));         // its slot is empty

        Assert.True(world.UseItem(hero, Kit));
        Assert.Equal(0.5f, Stacks(world, hero).Single(s => s.Item == Sword).Instance!.Condition, 3);
        Assert.True(world.Equip(hero, Sword));

        Assert.True(world.UseItem(hero, Whetstone));
        Assert.Equal(0.8f, world.ConditionIn(hero, Hands.Main), 3);
        Assert.False(world.UseItem(hero, Whetstone, out string why));   // already as good as it can make it
        Assert.Equal("nothing needs repair", why);
        Assert.Equal(1, world.CountOf(hero, Whetstone));

        // A screen repairs the stack the player picked.
        int index = Stacks(world, hero).FindIndex(s => s.Item == Sword);
        Assert.True(world.CanRepair(hero, index));
        Assert.True(world.Repair(hero, index, 0.5f));
        Assert.Null(world.WornInstance(hero, Hands.Main));
        Assert.False(world.CanRepair(hero, index));
    }

    // Wear survives a save: the worn sword is still worn, still the same condition, still dealing less.
    [Fact]
    public void ConditionAndTheWornInstanceSurviveASave()
    {
        string root = TestEnv.NewTempDir();
        using var app = NewApp(root);
        var duel = NewDuel(app);
        var world = duel.World;
        world.Add(duel.Attacker, new Persistent { Id = PersistentId.FromName("attacker") });
        world.Add(duel.Target, new Persistent { Id = PersistentId.FromName("target") });
        world.Give(duel.Attacker, Sword);
        world.Give(duel.Attacker, Sword);
        Assert.True(world.Equip(duel.Attacker, Sword));
        duel.Swing();
        duel.Swing();
        world.FlushCommands();
        Assert.True(app.Engine.Saves.Save("worn"));
        Assert.True(app.Engine.Saves.Load("worn"));

        var hero = world.Resolve(PersistentId.FromName("attacker"));
        var target = world.Resolve(PersistentId.FromName("target"));
        Assert.Equal(0.5f, world.ConditionIn(hero, Hands.Main), 3);
        Assert.Single(Stacks(world, hero), s => s.Item == Sword && s.Instance == null);   // the spare is still plain
        duel.Attacker = hero;
        duel.Target = target;
        Assert.Equal(18f, duel.Swing(), 2);
        Assert.Equal(0.25f, world.ConditionIn(hero, Hands.Main), 3);
    }

    [Fact]
    public void DurabilityOutOfRangeIsALoadError()
    {
        const string bad = """
            [{ "type": "item", "id": "bad", "durability": { "wearPerHit": 2, "wornDamage": -1, "fullDamageAt": 0 } }]
            """;
        using var app = HeadlessApp.Gameplay().WithHands().File("data/bad.json", bad).Build();
        Assert.Equal(3, app.Engine.Records.ErrorCount);
    }
}
