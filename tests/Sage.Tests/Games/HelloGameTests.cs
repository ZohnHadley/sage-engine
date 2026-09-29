#nullable enable
using System.Numerics;
using Hello;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The guide's example game (docs/MAKING_A_GAME.md, `games/Hello`).
//
// Two jobs. The first is to keep the example honest: it is the thing a newcomer copies, so it must not
// be allowed to rot quietly when the engine moves under it — which is what happens to every example
// that nothing builds and nothing runs. The second is to *be* the example of the third thing the guide
// tells a game author to do: tick your own game headlessly, with no window and no graphics device.
public class HelloGameTests
{
    public HelloGameTests() { _ = TestEnv.UserRoot; }

    private static Engine NewEngine()
    {
        // The same shape a game's own test would have: the engine modules it needs, then the game's.
        var fixture = new MountFixture();
        fixture.Write("hello", "data/hello.json", Records);
        fixture.Mount("hello", "hello");

        return HeadlessApp.Simulation().With(new HelloModule()).Mount(fixture).Build().Engine;
    }

    // The example's own records, copied here rather than read from `games/Hello/content`, so that the
    // test says what it depends on instead of depending on a path.
    private const string Records = """
        [
          {
            "type": "prefab",
            "id": "player",
            "name": "player",
            "tags": ["player_controlled"],
            "parts": { "character": { "layer": "player" }, "attributes": {} }
          }
        ]
        """;

    [Fact]
    public void TheSmallestGameStandsAPlayerOnItsOwnGround()
    {
        using var engine = NewEngine();

        var world = engine.CreateWorld("hello");

        // Creating a world runs the module's `OnWorldCreated` and then the rules' `OnWorldStarted`, so
        // by this line the player exists — which is the whole of what the guide's §4 promises.
        var player = default(Entity);
        foreach (var entity in world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities)
            player = entity;

        Assert.False(player.IsNull, "the smallest game did not spawn a player");

        // Standing on its own ground rather than at some fixed height: the generator is not flat, so a
        // player whose Y owes nothing to `HeightAt` would be metres out.
        var at = player.GetComponent<Transform>().LocalPosition;
        float ground = world.Resources.Get<Terrain>().HeightAt(at.X, at.Z);
        Assert.InRange(at.Y, ground, ground + 3f);
    }

    [Fact]
    public void ThePlayerHasWhatTheCameraRigLooksFor()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("hello");
        world.RunFixed(1f / 60f);

        // Exactly the query `FirstPersonCameraSystem` runs. If this finds nothing, the game has a player
        // and no view of it — which looks, from inside, like a world made entirely of sky.
        int seen = 0;
        foreach (var _ in world.Query<GlobalTransform, CharacterController, PawnIntent>()
                              .AllTags(Tags.Get<PlayerControlled>()).Entities) seen++;

        Assert.Equal(1, seen);
    }

    [Fact]
    public void ItKeepsRunningWhenNobodyIsWatching()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("hello");

        for (int i = 0; i < 60; i++) world.RunFixed(1f / 60f);

        // A second of simulation with no window, no graphics device and no client half. This is the
        // property the engine/game split exists for, and the reason a game's rules live where they do.
        var player = default(Entity);
        foreach (var entity in world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities)
            player = entity;

        Assert.False(player.IsNull);
        Assert.True(float.IsFinite(player.GetComponent<Transform>().LocalPosition.Y), "the player fell out of the world");
    }
}
