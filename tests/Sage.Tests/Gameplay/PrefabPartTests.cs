#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Declared prefab parts (REDESIGN §3.4, issue #17): public option types a tool can list, registered
// for the plugin that declares them, applied in one documented order, and an error when two share an
// id — where the old delegates were private, ran in module order and silently replaced each other.
public class PrefabPartTests
{
    public PrefabPartTests() { _ = TestEnv.UserRoot; }

    // What each test part did, in order: a world resource, so parallel tests keep their own.
    private sealed class PartLog { public readonly List<string> Applied = new(); }

    private static void Note(in PrefabPartContext ctx, string what) =>
        ctx.World.Resources.GetOrAdd(() => new PartLog()).Applied.Add(what);

    [PrefabPart("test_alpha")]
    private sealed class AlphaPart : IPrefabPart { public void Apply(in PrefabPartContext ctx) => Note(ctx, "alpha"); }

    [PrefabPart("test_beta", After = new[] { "test_gamma" })]
    private sealed class BetaPart : IPrefabPart { public void Apply(in PrefabPartContext ctx) => Note(ctx, "beta"); }

    [PrefabPart("test_gamma")]
    private sealed class GammaPart : IPrefabPart { public void Apply(in PrefabPartContext ctx) => Note(ctx, "gamma"); }

    // One value is shorthand for the object: "test_label": "hello" is { "text": "hello" }.
    [PrefabPart("test_label", Shorthand = nameof(Text))]
    private sealed class LabelPart : IPrefabPart
    {
        public string Text = "";
        public int Times = 1;
        public void Apply(in PrefabPartContext ctx) => Note(ctx, $"label {Text} x{Times}");
    }

    // Uses a service rather than a module's field, the way the Sandbox's box_mesh uses the renderer.
    public sealed class Greeter { public string Greeting = "hello"; }

    [PrefabPart("test_greeting")]
    private sealed class GreetingPart : IPrefabPart
    {
        public void Apply(in PrefabPartContext ctx) => Note(ctx, ctx.Get<Greeter>().Greeting);
    }

    [PrefabPart("test_alpha")]
    private sealed class SecondAlphaPart : IPrefabPart { public void Apply(in PrefabPartContext ctx) { } }

    [PrefabPart("test_loop_a", After = new[] { "test_loop_b" })]
    private sealed class LoopA : IPrefabPart { public void Apply(in PrefabPartContext ctx) { } }

    [PrefabPart("test_loop_b", After = new[] { "test_loop_a" })]
    private sealed class LoopB : IPrefabPart { public void Apply(in PrefabPartContext ctx) { } }

    // A plugin that declares its parts by hand: the test assembly is built without the generator, so
    // this Init stands in for the generated registration.
    [Plugin("test.parts", "1.0.0")]
    private sealed class PartsModule : IModule
    {
        public void Init(ModuleContext ctx)
        {
            ctx.Engine.Prefabs.Register<AlphaPart>();
            ctx.Engine.Prefabs.Register<BetaPart>();
            ctx.Engine.Prefabs.Register<GammaPart>();
            ctx.Engine.Prefabs.Register<LabelPart>();
            ctx.Engine.Prefabs.Register<GreetingPart>();
            ctx.Provide(new Greeter { Greeting = "good evening" });
        }
    }

    private const string Prefabs = """
    [
      { "type": "prefab", "id": "ordered",
        "parts": { "test_beta": {}, "test_alpha": {}, "test_gamma": {} } },
      { "type": "prefab", "id": "labelled", "parts": { "test_label": "hello" } },
      { "type": "prefab", "id": "labelled_long", "parts": { "test_label": { "text": "hi", "times": 3 } } },
      { "type": "prefab", "id": "greeted", "parts": { "test_greeting": {} } }
    ]
    """;

    private static HeadlessApp Boot() =>
        HeadlessApp.Bare().With(new PartsModule()).File("data/parts.json", Prefabs).Boot();

    private static List<string> Applied(World world) => world.Resources.Get<PartLog>().Applied;

    // The order is the registry's — `After` first, then by id — not the order the prefab wrote them,
    // which a `base` prefab's merge would otherwise decide.
    [Fact]
    public void PartsRunInTheirDeclaredOrderNotTheOrderTheyAreWritten()
    {
        using var app = Boot();
        app.World.Spawn(new RecordId("sage", "ordered"));
        Assert.Equal(new[] { "alpha", "gamma", "beta" }, Applied(app.World));
        Assert.Equal("test.parts", app.Engine.Registrations.OwnerOf("prefab part", "test_beta"));
    }

    [Fact]
    public void ABareValueFillsTheShorthandField()
    {
        using var app = Boot();
        app.World.Spawn(new RecordId("sage", "labelled"));
        app.World.Spawn(new RecordId("sage", "labelled_long"));
        Assert.Equal(new[] { "label hello x1", "label hi x3" }, Applied(app.World));
    }

    [Fact]
    public void APartGetsServicesFromItsContextUnderItsPluginsRules()
    {
        using var app = Boot();
        app.World.Spawn(new RecordId("sage", "greeted"));
        Assert.Equal(new[] { "good evening" }, Applied(app.World));
    }

    // What the old private option classes hid: `ent_types` lists every part with its options, its
    // plugin and what it waits for.
    [Fact]
    public void EntTypesListsEachPartWithItsOptions()
    {
        using var capture = new CaptureSink();
        using var app = Boot();
        app.CVars.Execute("ent_types test_");
        var lines = capture.Entries.Select(e => e.Message).ToList();
        Assert.Contains(lines, l => l.Contains("test_label") && l.Contains("{ text: string (shorthand), times: int }  [test.parts]"));
        Assert.Contains(lines, l => l.Contains("test_beta") && l.Contains("[test.parts] after test_gamma"));
    }

    // Two plugins declaring one id used to be a warning and a replacement, decided by load order.
    [Fact]
    public void TwoPartsWithOneIdAreAnError()
    {
        using var app = HeadlessApp.Bare().Create();
        app.Engine.Prefabs.Register<AlphaPart>();
        var ex = Assert.Throws<InvalidOperationException>(() => app.Engine.Prefabs.Register<SecondAlphaPart>());
        Assert.Contains("Prefab part 'test_alpha' is declared twice: AlphaPart (from host) and SecondAlphaPart", ex.Message);
    }

    [Fact]
    public void PartsThatWaitForEachOtherAreAnError()
    {
        using var app = HeadlessApp.Bare().Create();
        app.Engine.Prefabs.Register<LoopA>();
        app.Engine.Prefabs.Register<LoopB>();
        var ex = Assert.Throws<InvalidOperationException>(() => app.Engine.Prefabs.Parts);
        Assert.Contains("test_loop_a, test_loop_b", ex.Message);
    }

    // Issue #17's acceptance for parts: every one the engine and the Sandbox's simulation ship is a
    // declared type, registered for the plugin it belongs to, and the order is the documented one.
    [Fact]
    public void EveryPartInTheSandboxIsDeclaredByItsPlugin()
    {
        using var app = HeadlessApp.ForGame(SandboxDirectory, new Sandbox.SandboxModule()).Build();
        var expected = new Dictionary<string, string>
        {
            ["abilities"] = "sage.gameplay.abilities", ["aim_ik"] = "sage.core", ["animator"] = "sage.core", ["attributes"] = "sage.gameplay.attributes",
            ["bone_attachment"] = "sage.core", ["foot_ik"] = "sage.gameplay.animation", ["footsteps"] = "sage.gameplay.animation", ["hand_ik"] = "sage.core", ["look_at_ik"] = "sage.core", ["loot"] = "sage.gameplay.items", ["merchant"] = "sage.gameplay.items",
            ["body"] = "sage.physics3d", ["brush"] = "sage.core", ["camera"] = "sage.core", ["character"] = "sage.gameplay.character",
            ["dialogue"] = "sage.gameplay.dialogue", ["effects"] = "sage.gameplay.attributes",
            ["faction"] = "sage.gameplay.factions", ["first_person_rig"] = "sage.core", ["hitboxes"] = "sage.gameplay.combat", ["hop"] = "sandbox", ["orbit"] = "sandbox", ["inventory"] = "sage.gameplay.items", ["container"] = "sage.gameplay.items",
            ["joint"] = "sage.physics3d", ["ladder"] = "sage.gameplay.character", ["light"] = "sage.gameplay.lights", ["melee"] = "sage.gameplay.combat", ["mover"] = "sage.gameplay.movers",
            ["logic_branch"] = "sage.core", ["logic_compare"] = "sage.core", ["logic_counter"] = "sage.core",
            ["logic_auto"] = "sage.core", ["logic_case"] = "sage.core", ["logic_multisource"] = "sage.core", ["spawner"] = "sage.core", ["trigger"] = "sage.core",
            ["load_door"] = "sage.core", ["logic_relay"] = "sage.core", ["math_remap"] = "sage.core", ["quest_watch"] = "sage.gameplay.quests", ["routine"] = "sage.gameplay.ai", ["offscreen"] = "sage.gameplay.ai", ["nav_link"] = "sage.gameplay.ai", ["nav_door"] = "sage.gameplay.ai", ["nav_area"] = "sage.gameplay.ai",
            ["pickup"] = "sage.gameplay.items", ["ragdoll"] = "sage.core", ["reverb_zone"] = "sage.core", ["scripted_camera"] = "sage.core", ["skinned_mesh"] = "sage.core", ["sprite"] = "sage.gameplay.animation", ["third_person_rig"] = "sage.core",
            ["state_machine"] = "sage.core", ["timer"] = "sage.core", ["travel_point"] = "sage.core", ["tween"] = "sage.core", ["viewmodel"] = "sage.core",
            ["calendar_event"] = "sage.core", ["water"] = "sage.physics3d", ["water_surface"] = "sage.core",
            ["follower"] = "sage.core", ["streaming_ring"] = "sage.core", ["use_screen"] = "sage.kits.rpg", ["perks"] = "sage.kits.rpg",
        };
        var parts = app.Engine.Prefabs.Parts;
        Assert.Equal(expected.Keys.OrderBy(k => k, StringComparer.Ordinal), parts.Select(p => p.Id).OrderBy(k => k, StringComparer.Ordinal));
        foreach (var part in parts)
        {
            Assert.Equal(expected[part.Id], part.Owner);
            Assert.True(part.Type.IsPublic, $"{part.Type.Name} should be public, so tools can list its options");
        }

        // The constraints the engine's parts declare, and id order for everything else.
        var order = parts.Select(p => p.Id).ToList();
        Assert.True(order.IndexOf("attributes") < order.IndexOf("effects"));
        Assert.True(order.IndexOf("sprite") < order.IndexOf("pickup"));
        Assert.True(order.IndexOf("body") < order.IndexOf("pickup"));
        Assert.True(order.IndexOf("inventory") < order.IndexOf("container"));   // a chest's contents first (issue #378)
        Assert.True(order.IndexOf("skinned_mesh") < order.IndexOf("animator"));
        Assert.Equal(new[] { "abilities", "aim_ik", "attributes", "body", "bone_attachment", "brush" }, order.Take(6));
    }

    private static string SandboxDirectory => Path.Combine(TestEnv.FolderAbove("Sage.sln"), "games", "Sandbox");
}
