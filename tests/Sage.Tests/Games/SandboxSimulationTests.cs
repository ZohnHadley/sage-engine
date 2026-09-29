#nullable enable
using System.Linq;
using System.Numerics;
using Sandbox;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The *game's* own simulation, ticked headlessly (TODO R15, engine review item 6).
//
// This file could not exist before the split. `games/Sandbox` mixed its rules and spawning with a HUD
// that needs `Color`, `Rectangle` and `Texture2D`, so none of the game's own logic could be tested
// without MonoGame — and the engine was telling games to keep simulation testable while shipping an
// example that did not. The simulation half now references the base engine and nothing else, and the
// compiler enforces it.
//
// What is checked here is the game's, not the engine's: its rules and its respawn, on the scene the
// engine places for it (issue #29; the engine's own scene tests are Content/SceneTests.cs).
public class SandboxSimulationTests
{
    public SandboxSimulationTests() { _ = TestEnv.UserRoot; }

    private const string Scene = """
    [
      { "type": "attribute", "id": "health", "max": 100, "start": 100 },
      { "type": "damage_type", "id": "physical", "resist": "armour" },
      { "type": "attribute", "id": "armour", "max": 95 },
      { "type": "attack", "id": "fists", "damage": 5, "reach": 1.5 },

      { "type": "prefab", "id": "hero", "name": "hero",
        "tags": ["player_controlled"],
        "parts": { "character": { "layer": "player" }, "attributes": {}, "melee": { "attack": "fists" } } },

      { "type": "prefab", "id": "rock", "name": "rock",
        "parts": { "body": { "size": [1, 1, 1] }, "box_mesh": { "size": [1, 1, 1] } } },

      { "type": "scene", "id": "main", "origin": [512, 0, 512], "relativeTo": "Ground",
        "player": { "prefab": "hero", "at": [0, 1, 0] },
        "place": [ { "prefab": "rock", "at": [2, 0, 0] }, { "prefab": "rock", "at": [-2, 0, 0] } ] }
    ]
    """;

    private static (Engine Engine, World World) NewGame()
    {
        var app = HeadlessApp.Simulation()
            .With(new SandboxModule())   // the game, with no client half in sight
            .File("data/scene.json", Scene, ns: "sandbox")
            .StartScene("sandbox:main")      // what the Sandbox's game.json says
            .Boot("sandbox");
        return (app.Engine, app.World);
    }

    // The game's rules spawn the player when the world starts, from its scene record.
    [Xunit.Fact]
    public void TheGamePlacesItsSceneAndItsPlayer()
    {
        var (engine, world) = NewGame();
        using (engine)
        {
            var placed = world.Query<Transform>().AllTags(Tags.Get<FromScene>()).Entities.ToEntityList();
            Assert.Equal(2, placed.Count);   // two rocks; the player is the rules', not the scene's

            var player = world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList();
            Assert.Single(player);
            Assert.True(world.Has<CharacterController>(player[0]));
        }
    }

    // A prefab part that only the client half registers is skipped, not an error: a dedicated server
    // has no renderer, so a rock is a collider with no mesh. This is what `Prefabs.Optional` buys.
    [Xunit.Fact]
    public void AClientOnlyPrefabPartIsSimplySkippedHeadlessly()
    {
        var (engine, world) = NewGame();
        using (engine)
        {
            Assert.True(engine.Prefabs.IsOptional("box_mesh"));

            var rock = world.Query<Transform, Collider>().Entities.ToEntityList()
                .First(e => World.Describe(e).Contains("rock"));
            Assert.True(world.Has<Collider>(rock));     // the body came up
            Assert.False(world.Has<MeshRenderer>(rock)); // the mesh did not, and that is fine
        }
    }

    // The game's own rule: dying puts the player back on their feet, healed (16 §3.1).
    [Xunit.Fact]
    public void TheGamesRulesRespawnAPlayerThatDies()
    {
        var (engine, world) = NewGame();
        using (engine)
        {
            var player = world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList()[0];
            var health = new RecordId("sandbox", "health");
            Assert.Equal(100f, world.Attribute(player, health));

            Combat.ApplyDamage(world, new DamageInfo(default, player, new RecordId("sandbox", "physical"),
                500f, Vector3.Zero, Vector3.UnitY));
            world.RunFixed(1f / 60f);   // EffectSystem notices the death and calls the rules

            Assert.Equal(100f, world.Attribute(player, health));
        }
    }

    // The scene respawns when records reload, which is what makes editing content while the game runs
    // work at all (05 §3.6). Headless, so it is the game's bookkeeping being checked, not the HUD.
    [Xunit.Fact]
    public void ReloadingRecordsReplacesTheScene()
    {
        var (engine, world) = NewGame();
        using (engine)
        {
            var before = world.Query<Transform>().AllTags(Tags.Get<FromScene>()).Entities.ToEntityList().ToList();
            var player = world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList()[0];

            engine.Records.Reload();
            world.RunFixed(1f / 60f);

            var after = world.Query<Transform>().AllTags(Tags.Get<FromScene>()).Entities.ToEntityList();
            Assert.Equal(before.Count, after.Count);

            // The old entities are gone, not kept. Ids are reused by design (03 §3.3 E1), so this
            // asks the handles whether they are still alive rather than comparing ids.
            Assert.True(before.All(e => !world.IsAlive(e)), "the scene should have been rebuilt, not kept");

            // Except the player: it is the rules', not the scene's, so the sweep leaves it alone — the
            // same entity, not a fresh one (review #59 put a new one back; issue #29 keeps it).
            Assert.Single(world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList());
            Assert.True(world.IsAlive(player), "the player should have been kept across the reload");
        }
    }
}
