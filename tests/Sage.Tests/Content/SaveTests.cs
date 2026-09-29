#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Friflo.Engine.ECS;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Save and load (docs/design/09, TODO F27). What the slice promised: fight something, pick something
// up, learn a spell, save, come back to it.
//
// The things worth pinning are the translations, because every one of them is a value that means
// something different next run: an entity handle, an attribute index, a tag bit. A save that round
// trips in the same process proves almost nothing about those — so several of these change the
// *content* between save and load, which is what a real playthrough does.
public class SaveTests
{
    public SaveTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
    [
      { "type": "attribute", "id": "health", "max": 100, "start": 100 },
      { "type": "attribute", "id": "mana",   "max": 50,  "start": 50, "spendEffect": "spend_mana" },
      { "type": "attribute", "id": "armour", "max": 95 },
      { "type": "damage_type", "id": "physical", "resist": "armour" },
      { "type": "attack", "id": "fists", "damage": 5, "reach": 1.5 },
      { "type": "item", "id": "sword", "label": "a sword", "weight": 3, "slot": "MainHand", "attack": "fists" },
      { "type": "item", "id": "bread", "label": "bread", "weight": 1 },

      { "type": "tag", "id": "state.cursed" },
      { "type": "effect", "id": "spend_mana", "modifiers": [ { "attribute": "mana", "op": "Add", "value": -1 } ] },
      { "type": "effect", "id": "hurt",       "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
      { "type": "effect", "id": "curse", "duration": "Infinite", "grantTags": ["state.cursed"],
        "modifiers": [ { "attribute": "armour", "op": "Add", "value": -10 } ] },

      { "type": "ability", "id": "heal", "targeting": "Self", "effects": ["hurt"], "magnitude": 1 },

      { "type": "prefab", "id": "hero", "name": "hero",
        "tags": ["player_controlled"],
        "parts": { "character": { "layer": "player" }, "attributes": {},
                   "melee": { "attack": "fists" }, "inventory": { "capacity": 40 } } },

      { "type": "prefab", "id": "goblin", "name": "goblin",
        "components": { "ai_state": { "schedule": "sage:idle" } },
        "parts": { "character": { "layer": "enemy" }, "attributes": {} } }
    ]
    """;

    private sealed class Fixture : IDisposable
    {
        public readonly Engine Engine;
        public readonly World World;
        private readonly string _userRoot;

        private readonly bool _keepRoot;

        // `userRoot` shares a folder with an earlier fixture, which is how a test spans two "runs of
        // the game" — the save has to outlive the engine that wrote it.
        public Fixture(string? extraRecords = null, string? userRoot = null)
        {
            // Its own saves folder, so a test never collides with another or with the game's. Pointed
            // at through SaveSystem rather than UserPaths: that one is process-global, and moving it
            // here pulled it out from under whatever else xUnit was running in parallel — a flake in
            // the crash-report test, which writes to the user folder, that took a re-run to see.
            _userRoot = userRoot ?? TestEnv.NewTempDir();
            _keepRoot = userRoot != null;

            var builder = HeadlessApp.Gameplay().File("data/save_test.json", Records);
            if (extraRecords != null) builder.File("data/extra.json", extraRecords);
            Engine = builder.Build().Engine;

            Engine.Saves.Root = Path.Combine(_userRoot, "saves");

            World = Engine.CreateWorld("main");
            var ground = World.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
            World.Add(ground, Collider.Box(new Vector3(200, 1, 200)));
        }

        public void Dispose()
        {
            Engine.Dispose();
            if (_keepRoot) return;
            try { Directory.Delete(_userRoot, recursive: true); } catch { /* a leftover temp dir is not a failure */ }
        }
    }

    private static RecordId Id(string name) => new("sage", name);

    // Everything the save system can see has to be persistent, and with a *stable* id: that is what a
    // scene placement or a map file supplies in a real game (09 §3.5).
    private static Entity Place(World world, string prefab, string id, Vector3 at)
    {
        var entity = world.Spawn(Id(prefab), at);
        world.Add(entity, new Persistent { Id = PersistentId.FromName(id) });
        return entity;
    }

    private static void Tick(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    [Xunit.Fact]
    public void APlayerComesBackWhereItWasWithWhatItHad()
    {
        using var fx = new Fixture();
        var hero = Place(fx.World, "hero", "hero", new Vector3(3, 0.1f, -7));
        fx.World.Give(hero, Id("sword"));
        fx.World.Give(hero, Id("bread"), 4);
        fx.World.Equip(hero, Id("sword"));
        fx.World.Teach(hero, Id("heal"));
        Tick(fx.World, 2);

        // Where it actually is, not where it was put: two ticks of gravity have already moved it.
        var where = fx.World.Get<Transform>(hero).LocalPosition;
        Assert.True(fx.Engine.Saves.Save("slot"));

        // Ruin all of it, the way playing on would.
        fx.World.Take(hero, Id("bread"), 4);
        fx.World.Unequip(hero, EquipSlot.MainHand);
        fx.World.Get<Transform>(hero).LocalPosition = new Vector3(90, 0, 90);
        Tick(fx.World, 2);

        Assert.True(fx.Engine.Saves.Load("slot"));

        var loaded = fx.World.Resolve(PersistentId.FromName("hero"));
        Assert.False(loaded.IsNull);
        Assert.Equal(where, fx.World.Get<Transform>(loaded).LocalPosition);
        Assert.Equal(4, fx.World.CountOf(loaded, Id("bread")));
        Assert.Equal(Id("sword"), fx.World.Get<Equipment>(loaded).MainHand);
        Assert.True(fx.World.Knows(loaded, Id("heal")));

        // The prefab's own work came back without being saved: a character's capsule and controller are
        // applied by the `character` part, not written into the file (F31).
        Assert.True(fx.World.Has<CharacterController>(loaded));
        Assert.True(fx.World.Has<Collider>(loaded));
    }

    // A save holds **origin-space** positions (R6), so it holds the origin as well. Without that, a
    // save taken a hundred kilometres out would load its entities into the starting sector: the player
    // would come back standing on somebody else's ground, and only the numbers would look right.
    [Xunit.Fact]
    public void ASaveFarFromHomeComesBackInTheRightPlace()
    {
        string shared = TestEnv.NewTempDir();
        var far = new SectorCoord(117, -79);
        Vector3 absolute;

        using (var fx = new Fixture(userRoot: shared))
        {
            fx.World.Rebase(far);
            var hero = Place(fx.World, "hero", "hero", new Vector3(40, 0.1f, -25));
            Tick(fx.World, 2);

            absolute = fx.World.Origin().ToAbsolute(fx.World.Get<Transform>(hero).LocalPosition);
            Assert.True(MathF.Abs(absolute.X) > 100_000f, "the hero really is a long way out");
            Assert.True(fx.Engine.Saves.Save("far"));
        }

        // A second run of the game, which starts at the origin like every run does.
        using (var fx = new Fixture(userRoot: shared))
        {
            Assert.Equal(SectorCoord.Zero, fx.World.Origin().Sector);
            Assert.True(fx.Engine.Saves.Load("far"));

            Assert.Equal(far, fx.World.Origin().Sector);
            var loaded = fx.World.Resolve(PersistentId.FromName("hero"));
            Assert.False(loaded.IsNull);

            var back = fx.World.Origin().ToAbsolute(fx.World.Get<Transform>(loaded).LocalPosition);
            Assert.Equal(absolute.X, back.X, 2);
            Assert.Equal(absolute.Z, back.Z, 2);
        }

        try { System.IO.Directory.Delete(shared, recursive: true); } catch { /* best effort */ }
    }

    // Attribute values are saved by name, because the integer index is assigned in record load order.
    // This test *adds an attribute record* between save and load, which shifts every index — the exact
    // thing a content update or a mod does, and the reason the converter exists.
    [Xunit.Fact]
    public void AttributesSurviveTheirIndexesChanging()
    {
        string shared = TestEnv.NewTempDir();
        using (var fx = new Fixture(userRoot: shared))
        {
            var hero = Place(fx.World, "hero", "hero", Vector3.Zero);
            Effects.Apply(fx.World, hero, Id("hurt"), hero, 35f);
            Tick(fx.World, 2);
            Assert.Equal(65f, fx.World.Attribute(hero, Id("health")));
            Assert.True(fx.Engine.Saves.Save("slot"));
        }

        // A second run of the "game", with an extra attribute record that sorts ahead of the others and
        // therefore shifts every attribute index by one.
        using (var fx = new Fixture("""[ { "type": "attribute", "id": "aaa_luck", "max": 10, "start": 5 } ]""", shared))
        {
            Place(fx.World, "hero", "hero", Vector3.Zero);
            Tick(fx.World);

            Assert.True(fx.Engine.Saves.Load("slot"));
            var loaded = fx.World.Resolve(PersistentId.FromName("hero"));
            Assert.Equal(65f, fx.World.Attribute(loaded, Id("health")));   // not luck's 5, nor 100
        }
        try { Directory.Delete(shared, recursive: true); } catch { /* best effort */ }
    }

    // An effect that never expires has `Remaining = +∞`, which plain JSON refuses to write. It is real
    // data — "this curse has no end" — so the dialect has to be able to say it.
    [Xunit.Fact]
    public void AnEverlastingEffectSurvives()
    {
        using var fx = new Fixture();
        var hero = Place(fx.World, "hero", "hero", Vector3.Zero);
        Effects.Apply(fx.World, hero, Id("curse"), hero);
        Tick(fx.World, 2);
        Assert.True(fx.World.HasTag(hero, Id("state.cursed")));

        Assert.True(fx.Engine.Saves.Save("slot"));
        Assert.True(fx.Engine.Saves.Load("slot"));
        Tick(fx.World, 2);

        var loaded = fx.World.Resolve(PersistentId.FromName("hero"));
        Assert.Contains(Id("curse"), fx.World.Get<ActiveEffects>(loaded).Effects.Select(e => e.Record));
        Assert.True(fx.World.HasTag(loaded, Id("state.cursed")), "the tag the effect grants should be back");
    }

    // An `Entity` inside a `List<ActiveEffect>` inside a component: the reference the first design of
    // this silently wrote as null. It is what gives a kill its killer.
    [Xunit.Fact]
    public void AnEntityReferenceNestedInAListIsKept()
    {
        using var fx = new Fixture();
        var hero = Place(fx.World, "hero", "hero", Vector3.Zero);
        var goblin = Place(fx.World, "goblin", "goblin", new Vector3(0, 0, -3));

        Effects.Apply(fx.World, hero, Id("curse"), goblin);   // the goblin cursed the hero
        Tick(fx.World, 2);

        Assert.True(fx.Engine.Saves.Save("slot"));
        Assert.True(fx.Engine.Saves.Load("slot"));

        var loadedHero = fx.World.Resolve(PersistentId.FromName("hero"));
        var loadedGoblin = fx.World.Resolve(PersistentId.FromName("goblin"));
        var curse = fx.World.Get<ActiveEffects>(loadedHero).Effects.Single(e => e.Record == Id("curse"));

        Assert.False(loadedGoblin.IsNull);
        Assert.Equal(loadedGoblin, curse.Source);
    }

    // Something killed between save and load is not in the save, so it does not come back: an absence
    // is the tombstone (09 §3.5, and why v1 needs no separate tombstone list).
    [Xunit.Fact]
    public void SomethingDestroyedBeforeTheSaveDoesNotComeBack()
    {
        using var fx = new Fixture();
        Place(fx.World, "hero", "hero", Vector3.Zero);
        var goblin = Place(fx.World, "goblin", "goblin", new Vector3(0, 0, -3));
        Tick(fx.World);

        fx.World.Destroy(goblin);
        fx.World.FlushCommands();
        Assert.True(fx.Engine.Saves.Save("slot"));

        // It is back in the world before the load, so the load has to remove it again.
        Place(fx.World, "goblin", "goblin", new Vector3(0, 0, -3));
        Tick(fx.World);
        Assert.False(fx.World.Resolve(PersistentId.FromName("goblin")).IsNull);

        Assert.True(fx.Engine.Saves.Load("slot"));
        Assert.True(fx.World.Resolve(PersistentId.FromName("goblin")).IsNull);
    }

    // Derived state is not written, so it cannot come back wrong. `Grounded` and the ground normal are
    // recomputed every tick; a save that restored them would assert a stale answer for one tick.
    [Xunit.Fact]
    public void DerivedStateIsNotInTheFile()
    {
        using var fx = new Fixture();
        var hero = Place(fx.World, "hero", "hero", Vector3.Zero);
        Tick(fx.World, 10);
        Assert.True(fx.Engine.Saves.Save("slot"));

        string json = File.ReadAllText(Path.Combine(fx.Engine.Saves.Root, "slot", "world_main.json"));
        Assert.DoesNotContain("GroundNormal", json);
        Assert.DoesNotContain("Grounded", json);
        Assert.DoesNotContain("LocalMatrix", json);
        Assert.DoesNotContain("Pressed", json);
        Assert.Contains("LocalPosition", json);      // but the pose itself is there
        Assert.Contains("sage:health", json);        // and attributes, by name
    }

    [Xunit.Fact]
    public void AMissingSaveIsRefusedRatherThanThrown()
    {
        using var fx = new Fixture();
        Assert.False(fx.Engine.Saves.Exists("nope"));
        Assert.False(fx.Engine.Saves.Load("nope"));
    }

    [Xunit.Fact]
    public void SavesCanBeListed()
    {
        using var fx = new Fixture();
        Place(fx.World, "hero", "hero", Vector3.Zero);
        Assert.True(fx.Engine.Saves.Save("one"));
        Assert.True(fx.Engine.Saves.Save("two"));

        var slots = fx.Engine.Saves.List().Select(s => s.Slot).ToList();
        Assert.Contains("one", slots);
        Assert.Contains("two", slots);
        Assert.All(fx.Engine.Saves.List(), s => Assert.True(s.Entities > 0));
    }

    // A storm you walked into is part of the world you left (06 §3.13, F40). The blend is saved too, so
    // loading half way through a change carries on rather than snapping.
    [Fact]
    public void TheWeatherSurvivesASave()
    {
        using var fx = new Fixture();
        var weather = fx.World.Resources.Get<Weather>();
        weather.Set(new RecordId("sandbox", "storm"), seconds: 4f);
        weather.Advance(1f);

        Assert.True(fx.Engine.Saves.Save("weather"));

        // Let it clear up, the way playing on would.
        weather.Set(new RecordId("sage", "clear"), seconds: 0f);
        weather.Advance(1f);
        Assert.True(weather.Settled);

        Assert.True(fx.Engine.Saves.Load("weather"));

        var loaded = fx.World.Resources.Get<Weather>();
        Assert.Equal(new RecordId("sandbox", "storm"), loaded.Target);
        Assert.Equal(0.25f, loaded.Blend, 2);
        Assert.False(loaded.Settled);
    }

    // What a world thinks of you, and what you are half way through, are the two things a save is most
    // obviously *for* (F24). Both were attributed `[SavedResource]` and neither was registered, so
    // neither was written — which no test noticed until one asked.
    [Fact]
    public void ReputationAndTheJournalSurviveASave()
    {
        using var fx = new Fixture();
        var faction = new RecordId("sandbox", "townsfolk");
        var quest = new RecordId("sandbox", "errand");

        fx.World.Resources.Get<Reputation>().Set(faction, -40f);
        fx.World.Resources.Get<Journal>().Entries.Add(new Journal.Entry
        {
            Quest = quest,
            Stage = "hunt",
            Progress = { 1 },
        });

        Assert.True(fx.Engine.Saves.Save("story"));

        // Play on, badly.
        fx.World.Resources.Get<Reputation>().Set(faction, 80f);
        fx.World.Resources.Get<Journal>().Entries.Clear();

        Assert.True(fx.Engine.Saves.Load("story"));

        Assert.Equal(-40f, fx.World.Resources.Get<Reputation>().Of(faction), 1);
        var entry = fx.World.Resources.Get<Journal>().Of(quest);
        Assert.NotNull(entry);
        Assert.Equal("hunt", entry!.Stage);
        Assert.Equal(1, entry.Progress[0]);
    }
}
