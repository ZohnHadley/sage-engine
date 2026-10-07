#nullable enable
using System;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Equipment slots written in data, and weight with consequences (issue #384).
public class EncumbranceTests
{
    public EncumbranceTests() { _ = TestEnv.UserRoot; }

    private static RecordId Id(string name) => new("sage", name);

    private static EquipSlots SlotsOf(HeadlessApp app) => app.Engine.Modules.Modules.OfType<ItemsModule>().Single().Slots;

    // ---- slots ---------------------------------------------------------------------------------------

    private const string Helmets = """
        [{ "type": "attribute", "id": "armor_head", "start": 0, "min": 0, "max": 95 },
         { "type": "effect", "id": "helmeted", "duration": "Infinite", "modifiers": [ { "attribute": "armor_head", "op": "Add", "value": 50 } ] },
         { "type": "equip_slot", "id": "head", "name": "Head" },
         { "type": "equip_slot", "id": "ring", "order": -1 },
         { "type": "item", "id": "helmet", "label": "a helmet", "slot": "Head", "effects": ["helmeted"] }]
        """;

    // The base has no slots and no kit registers one here: the helmet's slot is an equip_slot record,
    // the item is checked against it as content loads, and wearing it applies its effect.
    [Fact]
    public void AHelmetSlotInDataIsWornWithNoCode()
    {
        using var app = HeadlessApp.Gameplay().File("data/helmets.json", Helmets).Boot("helmets");
        Assert.Equal(0, app.Records.ErrorCount);
        Assert.Equal(new[] { "ring", "Head" }, SlotsOf(app).Names);   // by order, then name; empty name = the id's
        Assert.Equal("Head", SlotsOf(app).Find("HEAD"));

        var world = app.World;
        var knight = world.Create(Transform.At(Vector3.Zero), "knight");
        world.AddInventory(knight);
        world.AddAttributes(knight);
        Assert.True(world.Give(knight, Id("helmet")));
        Assert.True(world.Equip(knight, Id("helmet")));
        Assert.Equal(Id("helmet"), world.Get<Equipment>(knight).In("Head"));
        world.RunFixed(1f / 60f);
        Assert.Equal(50f, world.Attribute(knight, Id("armor_head")));

        world.Unequip(knight, "head");
        world.RunFixed(1f / 60f);
        Assert.Equal(0f, world.Attribute(knight, Id("armor_head")));
    }

    // A slot nobody has, in code or in data, is still a load error, with the nearest spelling.
    [Fact]
    public void AnItemForASlotNobodyWroteIsALoadError()
    {
        using var app = HeadlessApp.Gameplay().File("data/helmets.json", """
            [{ "type": "equip_slot", "id": "head", "name": "Head" },
             { "type": "item", "id": "helmet", "slot": "Haed" }]
            """).Boot("typo");
        Assert.Equal(1, app.Records.ErrorCount);
        Assert.Contains(app.Records.LoadErrors, e => e.Contains("no equipment slot 'Haed'") && e.Contains("Head"));
    }

    // ---- encumbrance ---------------------------------------------------------------------------------

    private const string Porter = """
        [{ "type": "attribute", "id": "speed", "start": 1, "min": 0, "max": 2 },
         { "type": "effect", "id": "laden",      "duration": "Infinite", "modifiers": [ { "attribute": "speed", "op": "Multiply", "value": 0.75 } ] },
         { "type": "effect", "id": "overloaded", "duration": "Infinite", "modifiers": [ { "attribute": "speed", "op": "Multiply", "value": 0.4 } ] },
         { "type": "encumbrance", "id": "burden", "maxLoad": 1.2,
           "levels": [ { "load": 0.75, "effect": "laden" }, { "load": 1.0, "effect": "overloaded" } ] },
         { "type": "item", "id": "rock", "weight": 10 },
         { "type": "prefab", "id": "porter", "name": "porter",
           "parts": { "attributes": {}, "inventory": { "capacity": 50, "encumbrance": "burden", "items": [ { "item": "rock", "count": 3 } ] } } }]
        """;

    private static float Speed(World world, Entity entity) => world.Attribute(entity, Id("speed"));

    // The weight a carrier is under is a level, each level an effect held while it is reached: here a
    // movement attribute multiplied down. Past its capacity it can still carry up to maxLoad — slowed
    // rather than refused — and putting the load down takes the effect off again.
    [Fact]
    public void OverweightReducesAMovementAttribute()
    {
        using var app = HeadlessApp.Gameplay().File("data/porter.json", Porter).Boot("porter");
        Assert.Equal(0, app.Records.ErrorCount);
        var world = app.World;
        var porter = world.Spawn(Id("porter"));
        void Tick() => world.RunFixed(1f / 60f);

        Tick();                                                       // 30 kg of 50: unburdened
        Assert.Equal(0, world.Get<Burden>(porter).Level);
        Assert.Equal(1f, Speed(world, porter));

        Assert.True(world.Give(porter, Id("rock"), 1));               // 40 kg: 80 %, laden
        Tick();
        Assert.Equal(1, world.Get<Burden>(porter).Level);
        Assert.Equal(0.75f, Speed(world, porter), 3);

        Assert.True(world.Give(porter, Id("rock"), 2));               // 60 kg: past capacity, which Give allows up to 120 %
        Tick();
        Assert.Equal(2, world.Get<Burden>(porter).Level);
        Assert.Equal(0.4f, Speed(world, porter), 3);                  // the heaviest level only: laden came off
        Assert.False(Effects.IsActive(world, porter, Id("laden")));
        Assert.Equal(60f, world.CarryLimitOf(porter), 3);
        Assert.False(world.Give(porter, Id("rock")), "past maxLoad is still refused");

        Assert.True(world.Take(porter, Id("rock"), 4));               // 20 kg
        Tick();
        Assert.Equal(0, world.Get<Burden>(porter).Level);
        Assert.Equal(1f, Speed(world, porter));
        Assert.False(Effects.IsActive(world, porter, Id("overloaded")));
    }

    // Without encumbrance rules nothing changes: the capacity is a wall, and a full pack costs nothing.
    [Fact]
    public void WithoutRulesTheCapacityIsStillAWall()
    {
        using var app = HeadlessApp.Gameplay().File("data/porter.json", Porter).Boot("wall");
        var world = app.World;
        var mule = world.Create(Transform.At(Vector3.Zero), "mule");
        world.AddInventory(mule, 50f);
        world.AddAttributes(mule);
        Assert.True(world.Give(mule, Id("rock"), 5));
        Assert.False(world.Give(mule, Id("rock")));
        Assert.Equal(50f, world.CarryLimitOf(mule));
        world.RunFixed(1f / 60f);
        Assert.Equal(1f, Speed(world, mule));
    }

    // The level and its effect are saved together, so a load neither loses the slowness nor doubles it.
    [Fact]
    public void ABurdenSurvivesASave()
    {
        using var app = HeadlessApp.Gameplay().File("data/porter.json", Porter).Boot("saved");
        var world = app.World;
        var porter = world.Spawn(Id("porter"));
        world.MakePersistent(porter);
        Assert.True(world.Give(porter, Id("rock"), 3));               // 60 kg: overloaded
        world.RunFixed(1f / 60f);
        Assert.True(app.Engine.Saves.Save("burdened"));

        Assert.True(world.Take(porter, Id("rock"), 6));
        world.RunFixed(1f / 60f);
        Assert.Equal(1f, Speed(world, porter));

        Assert.True(app.Engine.Saves.Load("burdened"));
        var loaded = world.FindByName("porter");
        world.RunFixed(1f / 60f);
        Assert.Equal(2, world.Get<Burden>(loaded).Level);
        Assert.Equal(0.4f, Speed(world, loaded), 3);
        Assert.Single(world.Get<ActiveEffects>(loaded).Effects, e => e.Record == Id("overloaded"));
    }

    // The engine's own movement feels the burden (issue #384): gameplay_conventions name the attribute
    // that paces a character, the overloaded level halves it, and in the same ticks the overloaded porter
    // walks half as far as the unburdened one beside it.
    [Fact]
    public void AnOverweightCharacterWalksLessFarInTheSameTicks()
    {
        using var app = HeadlessApp.Gameplay().File("data/porter.json", Porter)
            .File("data/conventions.json", """
                [{ "type": "gameplay_conventions", "id": "default_conventions", "speedAttribute": "speed" }]
                """).Boot("walk");
        Assert.Equal(0, app.Records.ErrorCount);
        var world = app.World;
        var ground = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        world.Add(ground, Collider.Box(new Vector3(200, 1, 200)));

        Entity Walker(float x, int rocks)
        {
            var walker = world.Create(Transform.At(new Vector3(x, 0, 0)), $"walker{x}");
            world.AddCharacter(walker, world.Resources.Get<IPhysicsWorld>().Layers.Player);
            world.AddAttributes(walker);
            world.AddInventory(walker, 50f);
            world.AddBurden(walker, new RecordRef<EncumbranceRecord>(Id("burden")));
            if (rocks > 0) Assert.True(world.Give(walker, Id("rock"), rocks));
            return walker;
        }
        var free = Walker(-5f, 0);
        var laden = Walker(5f, 6);                                      // 60 kg of 50: overloaded, speed x0.4

        for (int i = 0; i < 10; i++) world.RunFixed(1f / 60f);        // settle; the level and its pace take hold
        float freeStart = world.Get<Transform>(free).LocalPosition.Z, ladenStart = world.Get<Transform>(laden).LocalPosition.Z;
        for (int i = 0; i < 120; i++)
        {
            world.Get<PawnIntent>(free).Move = new Vector2(0, 1);
            world.Get<PawnIntent>(laden).Move = new Vector2(0, 1);
            world.RunFixed(1f / 60f);
        }
        float freeWalked = MathF.Abs(world.Get<Transform>(free).LocalPosition.Z - freeStart);
        float ladenWalked = MathF.Abs(world.Get<Transform>(laden).LocalPosition.Z - ladenStart);

        Assert.True(freeWalked > 2f, $"the free walker went {freeWalked} m");
        Assert.InRange(ladenWalked / freeWalked, 0.3f, 0.5f);          // about 0.4, less what acceleration takes
        Assert.Equal(0.4f, world.Get<CharacterController>(laden).SpeedScale, 3);
    }
}
