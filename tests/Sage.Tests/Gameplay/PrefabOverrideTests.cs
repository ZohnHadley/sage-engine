#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Prefab overrides and nested prefabs (F31's remaining half, phase 4i issue 1, REDESIGN §4.5): a
// placement's `overrides` merged into a copy of the prefab, a `.map`'s keys as overrides of the same
// shape, a prefab's `children` spawned parented and destroyed with it, and the load errors for a prefab
// that contains itself or nests too deep.
public class PrefabOverrideTests
{
    public PrefabOverrideTests() { _ = TestEnv.UserRoot; }

    private const string Content = """
    [
      { "type": "prefab", "id": "post", "name": "post",
        "components": { "sprite_renderer": { "size": [1.0, 2.0] } },
        "parts": { "body": { "shape": "Capsule", "radius": 0.5, "height": 2.0 }, "light": { "range": 7, "intensity": 1 } } },
      { "type": "prefab", "id": "lamp", "name": "lamp", "parts": { "light": { "range": 4 } } },
      { "type": "prefab", "id": "cart", "name": "cart",
        "children": [ { "prefab": "lamp", "at": [0, 1, 0], "name": "cart lamp" },
                      { "prefab": "lamp", "at": [1, 1, 0], "yaw": 90, "overrides": { "parts": { "light": { "range": 9 } } } } ] },
      { "type": "prefab", "id": "wagon_train", "name": "train", "children": [ { "prefab": "cart", "at": [0, 0, -5] } ] },
      { "type": "scene", "id": "yard",
        "place": [
          { "prefab": "post", "at": [0, 0, 0], "name": "plain post" },
          { "prefab": "post", "at": [3, 0, 0], "name": "short post",
            "overrides": { "components": { "sage:sprite_renderer": { "size": [1.0, 1.0] } },
                           "parts": { "body": { "radius": 0.25 }, "light": { "range": 12 } } } },
          { "prefab": "wagon_train", "at": [10, 0, 0], "yaw": 180 }
        ] }
    ]
    """;

    private static HeadlessApp NewApp(string content = Content) =>
        HeadlessApp.Gameplay().File("data/yard.json", content).StartScene("sage:yard").Boot("overrides");

    private static Entity Named(World world, string name) =>
        world.QueryAll().Entities.ToEntityList().Single(e => e.Name == name);

    private static RecordId Id(string name) => new("sage", name);

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-3f, $"expected {expected}, got {actual}");

    // The headline: one placement changes a component field and two parts' options — a capsule made
    // thinner through the part that builds it — and nothing else, and the prefab is left as it was.
    [Fact]
    public void APlacementsOverridesMergeIntoACopyOfThePrefab()
    {
        using var app = NewApp();
        var world = app.World;
        var plain = Named(world, "plain post");
        var shortPost = Named(world, "short post");

        Assert.Equal(new Vector2(1f, 2f), world.Get<SpriteRenderer>(plain).Size);
        Assert.Equal(new Vector2(1f, 1f), world.Get<SpriteRenderer>(shortPost).Size);   // component, by its full id

        // A part's options merge field by field: the radius changes, the shape and height stay.
        var thin = world.Get<Collider>(shortPost);
        Assert.Equal(ColliderShape.Capsule, thin.Shape);
        Assert.Equal(0.25f, thin.Size.X, 3);
        Assert.Equal(0.5f, world.Get<Collider>(plain).Size.X, 3);
        Assert.Equal(12f, world.Get<PointLight>(shortPost).Range);
        Assert.Equal(1f, world.Get<PointLight>(shortPost).Intensity);                   // kept from the prefab
        Assert.Equal(7f, world.Get<PointLight>(plain).Range);

        // The record itself is untouched: the next post is a plain one again.
        var another = world.Spawn(Id("post"));
        Assert.Equal(7f, world.Get<PointLight>(another).Range);
        Assert.Equal(0.5f, world.Get<Collider>(another).Size.X, 3);
        Assert.False(world.Has<PrefabOverridden>(another));
        Assert.True(world.Has<PrefabOverridden>(shortPost));
        Assert.Equal(0, app.Records.ErrorCount);
    }

    // A `.map`'s per-entity keys are overrides of the same shape, merged by the same code: the keys
    // `light.range` and `body.radius` build `{ "parts": { "light": { "range": … }, "body": { "radius": … } } }`.
    [Fact]
    public void MapKeysAreOverridesOfTheSameShape()
    {
        using var app = NewApp();
        var world = app.World;
        Assert.True(app.Records.TryGet(Id("post"), out PrefabRecord post));

        var keys = new Dictionary<string, string> { ["light.range"] = "12", ["body.radius"] = "0.25", ["classname"] = "post" };
        var overrides = PrefabKeys.Overrides(app.Engine, Id("post"), post, keys, "test.map:1");

        Assert.NotNull(overrides);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{ "light": { "range": 12 }, "body": { "radius": 0.25 } }"""), overrides!.Parts),
                    overrides.Parts?.ToJsonString());
        Assert.Null(overrides.Components);

        var fromKeys = world.Spawn(Id("post"), Vector3.Zero, 0f, keys, "test.map:1");
        var placed = Named(world, "short post");
        Assert.Equal(world.Get<PointLight>(placed).Range, world.Get<PointLight>(fromKeys).Range);
        Assert.Equal(world.Get<Collider>(placed).Size, world.Get<Collider>(fromKeys).Size);
        Assert.True(world.Has<PrefabOverridden>(fromKeys));
    }

    // Children: spawned with the parent, parented to it at their offset (and starting there, not
    // interpolating in from the parent's origin), nested as deep as the content says, with their own
    // overrides and names.
    [Fact]
    public void APrefabsChildrenSpawnParentedWhereItPutsThem()
    {
        using var app = NewApp();
        var world = app.World;
        var train = Named(world, "train");
        var cart = Assert.Single(train.ChildEntities);
        Assert.Equal("cart", cart.Name);
        Assert.Equal(2, cart.ChildCount);

        var named = Named(world, "cart lamp");
        Assert.Equal(cart, named.Parent);
        Assert.True(named.Tags.Has<FromParentPrefab>());
        Assert.Equal(Id("lamp"), world.Get<Sage.Simulation.FromPrefab>(named).Prefab);
        Assert.Equal(4f, world.Get<PointLight>(named).Range);
        var other = cart.ChildEntities.Single(e => e != named);
        Assert.Equal(9f, world.Get<PointLight>(other).Range);                           // the child's own override

        // Local to the parent: the train at x=10 facing +Z (yaw 180), the cart 5 m ahead of it on its -Z.
        Near(new Vector3(0, 0, -5), world.Get<Transform>(cart).LocalPosition);
        Near(new Vector3(10, 0, 5), world.Get<GlobalTransform>(cart).Current.Position);
        Near(new Vector3(10, 1, 5), world.Get<GlobalTransform>(named).Previous.Position);   // snapped, both halves
        world.RunFixed(1f / 60f);
        Near(new Vector3(10, 1, 5), world.Get<GlobalTransform>(named).Current.Position);   // propagation agrees
    }

    // Destroyed with the parent — directly, from a command buffer, or by a scene reload — and never
    // doubled by one. A child a game parents by hand is not the prefab's, and outlives it as before.
    [Fact]
    public void APrefabsChildrenAreDestroyedWithIt()
    {
        using var app = NewApp();
        var world = app.World;

        var train = world.Spawn(Id("wagon_train"), new Vector3(0, 0, 40));
        var tree = new List<Entity> { train };
        foreach (var cart in train.ChildEntities) { tree.Add(cart); tree.AddRange(cart.ChildEntities); }
        Assert.Equal(4, tree.Count);
        var mine = world.Create(Transform.At(Vector3.Zero), "hand-parented");
        world.SetParent(mine, train);

        world.Destroy(train);
        Assert.All(tree, e => Assert.False(world.IsAlive(e)));
        Assert.True(world.IsAlive(mine));

        var second = world.Spawn(Id("cart"));
        var lamps = second.ChildEntities.ToList();
        world.Commands.Destroy(second);
        world.FlushCommands();
        Assert.All(lamps, e => Assert.False(world.IsAlive(e)));

        // A scene reload sweeps the train with its cart and lamps, and puts back exactly one of each.
        int Lamps() => world.Query<PointLight>().Entities.ToEntityList().Count(e => world.Get<Sage.Simulation.FromPrefab>(e).Prefab == Id("lamp"));
        Assert.Equal(2, Lamps());
        app.Records.Reload();
        Assert.Equal(2, Lamps());
    }

    // A prefab that contains itself would spawn for ever, and one nested past the limit almost
    // certainly is a mistake: both are load errors at the prefab's `children`, and spawning one anyway
    // stops at the limit rather than recursing.
    [Fact]
    public void APrefabThatContainsItselfOrNestsTooDeepIsALoadError()
    {
        using var capture = new CaptureSink();
        using var app = HeadlessApp.Gameplay().File("data/loops.json", """
            [
              { "type": "prefab", "id": "egg", "children": [ { "prefab": "hen" } ] },
              { "type": "prefab", "id": "hen", "children": [ { "prefab": "egg" } ] },
              { "type": "prefab", "id": "d0", "children": [ { "prefab": "d1" } ] },
              { "type": "prefab", "id": "d1", "children": [ { "prefab": "d2" } ] },
              { "type": "prefab", "id": "d2", "children": [ { "prefab": "d3" } ] },
              { "type": "prefab", "id": "d3", "children": [ { "prefab": "d4" } ] },
              { "type": "prefab", "id": "d4", "children": [ { "prefab": "d5" } ] },
              { "type": "prefab", "id": "d5", "children": [ { "prefab": "d6" } ] },
              { "type": "prefab", "id": "d6", "children": [ { "prefab": "d7" } ] },
              { "type": "prefab", "id": "d7", "children": [ { "prefab": "d8" } ] },
              { "type": "prefab", "id": "d8", "children": [ { "prefab": "leaf" } ] },
              { "type": "prefab", "id": "leaf" }
            ]
            """, ns: "loops").Build();

        var messages = capture.Entries.Select(e => e.Message).Where(m => m.StartsWith("loops:")).ToList();
        Assert.Contains("loops:data/loops.json:2:36: prefab loops:egg: contains itself: loops:egg -> loops:hen -> loops:egg", messages);
        Assert.Contains("loops:data/loops.json:3:36: prefab loops:hen: contains itself: loops:hen -> loops:egg -> loops:hen", messages);
        Assert.Contains("loops:data/loops.json:4:35: prefab loops:d0: nests prefabs 10 deep; the limit is 8", messages);
        Assert.Contains("loops:data/loops.json:5:35: prefab loops:d1: nests prefabs 9 deep; the limit is 8", messages);
        Assert.DoesNotContain(messages, m => m.Contains("prefab loops:d2:"));            // 8 deep is allowed
        Assert.Equal(4, app.Records.ErrorCount);

        var world = app.CreateWorld("loops");
        var egg = world.Spawn(new RecordId("loops", "egg"));
        Assert.False(egg.IsNull);
        Assert.Equal(1 + PrefabOverriding.MaxDepth, world.QueryAll().Entities.ToEntityList().Count(e => world.Has<Sage.Simulation.FromPrefab>(e)));
    }

    // An override's bodies are checked at load as a prefab's are, at their own line, in the prefab's
    // namespace; a spawn does not report them again.
    [Fact]
    public void OverridesAreCheckedAtLoad()
    {
        using var capture = new CaptureSink();
        using var app = HeadlessApp.Gameplay().File("data/yard.json", """
            [
              { "type": "prefab", "id": "lamp", "parts": { "light": { "range": 4 } } },
              { "type": "scene", "id": "yard", "place": [
                { "prefab": "lamp", "overrides": { "parts": { "light": { "rnage": 9 } } } },
                { "prefab": "lamp", "overrides": { "components": { "sprite_rendrer": {} } } } ] }
            ]
            """, ns: "ovr").StartScene("ovr:yard").Build();

        var messages = capture.Entries.Select(e => e.Message).Where(m => m.StartsWith("ovr:")).ToList();
        Assert.Contains("ovr:data/yard.json:4:62: scene ovr:yard: unknown field 'rnage' in 'place.overrides.parts.light'; did you mean 'range'?", messages);
        Assert.Contains(messages, m => m.StartsWith("ovr:data/yard.json:5:56: scene ovr:yard: no component 'sprite_rendrer'"));
        Assert.Equal(2, app.Records.ErrorCount);

        using var spawns = new CaptureSink();
        var world = app.CreateWorld("yard");
        Assert.Equal(2, world.Query<PointLight>().Entities.ToEntityList().Count);
        Assert.DoesNotContain(spawns.Entries, e => e.Level >= LogLevel.Error && e.Message.StartsWith("ovr:"));   // the sink is process-wide
    }

    // `ent_dump` marks what a placement overrode: a component's fields with `*` (shown even when the
    // override happens to write the default), and a part's options on a line of their own.
    [Fact]
    public void EntDumpMarksOverriddenFields()
    {
        using var app = NewApp();
        using var capture = new CaptureSink();

        app.CVars.Execute("ent_dump short post");

        var lines = capture.Entries.Select(e => e.Message).ToList();
        Assert.Contains(lines, l => l.Contains("sage:sprite_renderer") && l.Contains("size*=") && !l.Contains("sheet*"));
        Assert.Contains(lines, l => l.Contains("sage:point_light") && l.Contains("range=12.000 m") && !l.Contains("range*"));
        Assert.Contains(lines, l => l.Contains("overridden parts") && l.Contains("body*={\"radius\":0.25}") && l.Contains("light*={\"range\":12}"));

        using var plain = new CaptureSink();
        app.CVars.Execute("ent_dump plain post");
        Assert.Contains(plain.Entries, e => e.Message.Contains("plain post"));
        Assert.DoesNotContain(plain.Entries, e => e.Message.Contains("size*=") || e.Message.Contains("overridden parts"));
    }

    // The editor's way back: a placements document read from the world keeps each placement's overrides
    // as written, so opening and saving it does not flatten or lose them.
    [Fact]
    public void ReadPlacementsKeepsOverrides()
    {
        using var app = HeadlessApp.Gameplay().File("data/doc.json", """
            [
              { "type": "prefab", "id": "lamp", "parts": { "light": { "range": 4 } } },
              { "type": "placements", "id": "doc", "place": [
                { "prefab": "lamp", "at": [1, 0, 0] },
                { "prefab": "lamp", "at": [2, 0, 0], "overrides": { "parts": { "light": { "range": 9 } } } } ] }
            ]
            """).Boot("doc");
        var world = app.World;
        Assert.Equal(2, world.SpawnPlacements(Id("doc")));

        var read = world.ReadPlacements(Id("doc"));

        var byX = read.Place.OrderBy(p => p.At.X).ToList();
        Assert.Null(byX[0].Overrides);
        Assert.Equal("""{"light":{"range":9}}""", byX[1].Overrides?.Parts?.ToJsonString());
    }
}
