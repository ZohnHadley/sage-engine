#nullable enable
using System;
using System.Linq;
using System.Numerics;
using Friflo.Engine.ECS;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// A tag with no meaning beyond "a prefab put it here", so the test can name one.
[Tag("test:from_prefab")] public struct FromPrefab : ITag { }

// Prefabs (docs/design/05 §3.5, TODO F31). The point of the feature is that placing a thing is one
// call against data, instead of eight calls in a particular order across five static classes
// (engine review 2026-09-23, items 3 and 8) — and that a game gets `base` inheritance, patching and
// hot reload for it without the engine doing anything, because a prefab is a record.
public class PrefabTests
{
    public PrefabTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
    [
      { "type": "gameplay_conventions", "id": "default_conventions", "health": "health", "damageType": "physical" },
      { "type": "attribute", "id": "health", "max": 100, "start": 100 },
      { "type": "damage_type", "id": "physical", "resist": "armour" },
      { "type": "attribute", "id": "armour", "max": 95 },
      { "type": "effect", "id": "tough_hide", "duration": "Infinite",
        "modifiers": [ { "attribute": "armour", "op": "Add", "value": 30 } ] },
      { "type": "attack", "id": "claw", "damage": 7, "reach": 1.2, "windupTime": 0.2, "recoverTime": 0.1 },
      { "type": "attack", "id": "bite", "damage": 12, "reach": 1.0 },
      { "type": "item", "id": "sword", "label": "a sword", "weight": 3, "attack": "claw" },

      { "type": "prefab", "id": "creature", "abstract": true,
        "name": "creature",
        "components": { "sprite_renderer": { "size": [1.6, 1.9] } },
        "parts": { "character": { "layer": "enemy" }, "attributes": {}, "melee": { "attack": "claw" } } },

      { "type": "prefab", "id": "goblin", "base": "creature",
        "name": "goblin",
        "components": { "ai_state": { "schedule": "chase" } },
        "parts": { "effects": ["tough_hide"] } },

      { "type": "prefab", "id": "goblin_chief", "base": "goblin",
        "name": "goblin chief",
        "components": { "sage:sprite_renderer": { "size": [2.0, 2.4] } },
        "parts": { "melee": { "attack": "bite" } } },

      { "type": "prefab", "id": "dropped_sword", "name": "a sword",
        "parts": { "pickup": { "item": "sword" } } },

      { "type": "prefab", "id": "rock",
        "components": { "collider": { "shape": "Box", "size": [1, 1, 1] } },
        "tags": ["test:from_prefab"] }
    ]
    """;

    private static (Engine Engine, World World) NewWorld(string? extra = null)
    {
        var builder = HeadlessApp.Gameplay().File("data/prefabs.json", Records);
        if (extra != null) builder.File("data/extra.json", extra);
        var app = builder.Boot("prefabs");
        return (app.Engine, app.World);
    }

    private static RecordId Id(string name) => new("sage", name);

    // The headline: one call, and the thing that comes out is complete.
    [Fact]
    public void SpawningAPrefabBuildsTheWholeThing()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var goblin = world.Spawn(Id("goblin"), new Vector3(2, 0, -3), 90f);

            Assert.False(goblin.IsNull);
            Assert.Equal(new Vector3(2, 0, -3), world.Get<Transform>(goblin).LocalPosition);
            Assert.Equal(90f, SageMath.YawOf(world.Get<Transform>(goblin).LocalRotation) * 180f / MathF.PI, 2);

            // the component half
            Assert.Equal(new Vector2(1.6f, 1.9f), world.Get<SpriteRenderer>(goblin).Size);

            // the parts half: a character is a controller, an intent and a capsule that agree
            Assert.True(world.Has<CharacterController>(goblin));
            Assert.True(world.Has<PawnIntent>(goblin));
            // and the intent faces the way it was placed, not -Z on the first tick (review #43)
            Assert.Equal(90f, world.Get<PawnIntent>(goblin).Yaw * 180f / MathF.PI, 2);
            Assert.True(world.Has<Collider>(goblin));
            Assert.Equal(world.Resources.Get<IPhysicsWorld>().Layers.Enemy, world.Get<Collider>(goblin).Layer);

            // attributes and the attack it swings
            Assert.Equal(100f, world.Attribute(goblin, new RecordId("sage", "health")));
            Assert.Equal(Id("claw"), world.Get<Melee>(goblin).Attack);

            // The effect it starts with is running, and lands the tick after it is applied: a
            // lasting effect is a modifier EffectSystem folds in, not a value written at spawn.
            Assert.Contains(Id("tough_hide"), world.Get<ActiveEffects>(goblin).Effects.Select(e => e.Record));
            world.RunFixed(1f / 60f);
            Assert.Equal(30f, world.Attribute(goblin, new RecordId("sage", "armour")));
        }
    }

    // Inheritance is the record pipeline's, not the prefab code's: the merge is per field, inside
    // the component and part bodies, so a variant overrides one thing and keeps the rest.
    [Fact]
    public void APrefabInheritsAndOverridesFieldByField()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var chief = world.Spawn(Id("goblin_chief"));

            Assert.Equal(Id("bite"), world.Get<Melee>(chief).Attack);            // overridden
            Assert.Equal(new Vector2(2f, 2.4f), world.Get<SpriteRenderer>(chief).Size);
            Assert.True(world.Has<CharacterController>(chief));                  // from the abstract base
            world.RunFixed(1f / 60f);
            Assert.Equal(30f, world.Attribute(chief, new RecordId("sage", "armour")));   // from goblin
            Assert.Contains("goblin chief", World.Describe(chief));
        }
    }

    // An abstract prefab is a template. Placing one is a mistake worth catching, not a silent entity.
    [Fact]
    public void AnAbstractPrefabIsNotPlaceable()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            Assert.True(world.Spawn(Id("creature")).IsNull);
        }
    }

    [Fact]
    public void AMissingPrefabCostsTheSpawnAndNothingElse()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            Assert.True(world.Spawn(Id("no_such_thing")).IsNull);
            Assert.False(world.Spawn(Id("goblin")).IsNull);   // and the next one still works
        }
    }

    // A bare id inside a component or part body means one in the prefab's own namespace, the same
    // rule record fields follow. The record pipeline cannot qualify these, so Populate does.
    [Fact]
    public void BareRecordIdsResolveInThePrefabsNamespace()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var goblin = world.Spawn(Id("goblin"));
            Assert.Equal("sage", world.Get<Melee>(goblin).Attack.Namespace);   // written as "claw"
        }
    }

    // A component holding an Entity has to be readable even though a prefab can never fill one in:
    // `Entity` exposes a ref struct, which System.Text.Json refuses outright, so without a converter
    // AIState, ActiveEffect and anything else with a handle in it would crash the spawn.
    [Fact]
    public void AComponentHoldingAnEntityHandleStillReads()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var goblin = world.Spawn(Id("goblin"));
            Assert.Equal(Id("chase"), world.Get<AIState>(goblin).Schedule);
            Assert.True(world.Get<AIState>(goblin).Target.IsNull);   // nothing to point at yet
        }
    }

    [Fact]
    public void APartCanBuildAThingOnTheGroundFromAnItemRecord()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var sword = world.Spawn(Id("dropped_sword"), new Vector3(0, 0.2f, -1));
            Assert.True(world.Has<Pickup>(sword));
            Assert.Equal(Id("sword"), world.Get<Pickup>(sword).Item);
        }
    }

    [Fact]
    public void ComponentsAndTagsAreWrittenByName()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var rock = world.Spawn(Id("rock"));
            Assert.Equal(ColliderShape.Box, world.Get<Collider>(rock).Shape);
            Assert.Equal(new Vector3(1, 1, 1), world.Get<Collider>(rock).Size);
            Assert.Contains("test:from_prefab", engine.Components.TagsOf(rock));
        }
    }

    // A typo costs the thing it names, not the entity: the rest still comes up, and the log says what.
    [Fact]
    public void AnUnknownComponentOrPartDoesNotSinkTheSpawn()
    {
        const string bad = """
        [ { "type": "prefab", "id": "wonky",
            "components": { "no_such_component": { "x": 1 }, "collider": { "shape": "Box", "size": [2,2,2] } },
            "tags": ["no_such_tag"],
            "parts": { "no_such_part": {}, "attributes": {} } } ]
        """;
        var (engine, world) = NewWorld(bad);
        using (engine)
        {
            var wonky = world.Spawn(Id("wonky"));
            Assert.False(wonky.IsNull);
            Assert.Equal(new Vector3(2, 2, 2), world.Get<Collider>(wonky).Size);   // the good half landed
            Assert.Equal(100f, world.Attribute(wonky, new RecordId("sage", "health")));
        }
    }

    // The schema is read on first use, never when the Engine is built: the host constructs the Engine
    // and only then loads the game assembly, so a game's own components have to be findable too.
    // `FromPrefab` and `Health` live in this assembly, which is a "game assembly" as far as the
    // engine is concerned — one built without Sage.Generators, so read by reflection (issue #16).
    [Fact]
    public void ComponentTypesFromOtherAssembliesAreFoundById()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            Assert.True(engine.Components.TryComponent("test:health", out var health));
            Assert.Equal(typeof(Health), health.Type);
            Assert.True(engine.Components.TryTag("test:from_prefab", out _));
            Assert.True(engine.Components.TryComponent("sage:transform", out var transform));
            Assert.Equal(typeof(Transform), transform.Type);   // Sage's, not Friflo's own Transform
            Assert.Equal("sage:transform", engine.Components.IdOf(typeof(Transform)));
            Assert.Null(engine.Components.IdOf(typeof(Friflo.Engine.ECS.Transform)));   // Friflo's: no id

            // Ids are exact: the case matters, and a bare type name is not an id.
            Assert.False(engine.Components.TryComponent("sage:Transform", out _));
            Assert.False(engine.Components.TryComponent("Transform", out _));
        }
    }

    // What `ent_dump` prints, and what the editor's inspector (15) shows.
    [Fact]
    public void EveryComponentOnAnEntityCanBeFoundById()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var goblin = world.Spawn(Id("goblin"));
            var ids = engine.Components.ComponentsOf(goblin).Select(c => c.Id).ToList();

            Assert.Contains("sage:transform", ids);
            Assert.Contains("sage:collider", ids);
            Assert.Contains("sage:melee", ids);
            Assert.DoesNotContain("EntityName", ids);   // Friflo's own components have no id
            Assert.Equal((object)world.Get<Melee>(goblin), engine.Components.ComponentsOf(goblin).First(c => c.Id == "sage:melee").Value);
        }
    }
}
