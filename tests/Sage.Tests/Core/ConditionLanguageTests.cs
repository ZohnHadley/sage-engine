#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// ---- a game's own record that decides and does (issue #89) -----------------------------------------
//
// What a data-only game (or #91's wires, #92's state machines, #93's topics) reads: a `requires` and a
// `then` in the base's language. Registered by hand, because the test assembly is built without the
// generator (VocabularyTests says the same).

[Record("gate_rule", Plugin = "test.logic")]
public sealed class GateRule
{
    public ICondition? Requires;
    public List<IAction> Then = new();
}

[Plugin("test.logic", "1.0.0")]
public sealed class LogicTestPlugin : IModule
{
    public static readonly List<(Entity Self, Entity Activator, Entity Caller, string Parameter)> Poked = new();

    public void Init(ModuleContext ctx)
    {
        ctx.Engine.Records.Register<GateRule>();
        if (!ctx.Engine.Inputs.Has("Poke"))
            ctx.Engine.Inputs.Register("Poke", (World world, in IOContext io) =>
            {
                lock (Poked) Poked.Add((io.Self, io.Activator, io.Caller, io.Parameter));
            });
    }
}

// One condition and action language in the base (docs/REDESIGN.md §4.3 stage 2, issue #89).
public class ConditionLanguageTests
{
    public ConditionLanguageTests() { _ = TestEnv.UserRoot; }

    private const string Rules = """
        [{ "type": "gate_rule", "id": "gate",
           "requires": { "all": [ { "var": "levers", "min": 2 },
                                  { "not": { "var": "alarm", "eq": 1 } },
                                  { "any": [ { "var": "key", "eq": 1 }, { "condition": "var", "name": "guard_bribed", "min": 1 } ] } ] },
           "then": [ { "add_var": "opened" }, { "set_var": "alarm", "value": 1 }, { "action": "add_var", "name": "levers", "amount": -2 } ] },
         { "type": "gate_rule", "id": "bare", "requires": "all", "then": [] },
         { "type": "gate_rule", "id": "lamp", "then": [ { "fire": "lamp", "input": "Poke", "parameter": "3" },
                                                         { "fire": "!other", "input": "Poke", "parameter": "other" },
                                                         { "action": "fire", "target": "later", "input": "Poke", "delay": 0.05 } ] }]
        """;

    private static RecordId Id(string name) => new("sage", name);

    // A game with no plugins but its own: no dialogue, no gameplay, not even entity I/O.
    private static HeadlessApp Bare()
    {
        using var log = new CaptureSink();
        var app = HeadlessApp.Bare().With(new LogicTestPlugin()).File("data/rules.json", Rules).Boot("logic");
        var errors = log.Entries.Where(e => e.Level >= LogLevel.Error && e.Message.Contains("rules.json")).Select(e => e.Message).ToList();
        Assert.True(app.Records.ErrorCount == 0 && errors.Count == 0, string.Join("\n", errors));
        return app;
    }

    private static void Tick(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    // Acceptance: `requires: { all: [ …, { not: … } ] }` loads and decides in a game without the
    // dialogue plugin, and `then` does what it says.
    [Fact]
    public void AGameWithoutDialogueReadsAndEvaluatesNestedRequires()
    {
        using var app = Bare();
        Assert.DoesNotContain("sage.gameplay.dialogue", app.PluginIds);
        var world = app.World;
        var gate = app.Records.Get<GateRule>(Id("gate"));
        var player = world.Create(Transform.At(Vector3.Zero), "player");
        var vars = Vars.Of(world);

        Assert.False(Conditions.Evaluate(world, player, gate.Requires));
        vars.Set("levers", 2);
        Assert.False(Conditions.Test(gate.Requires, new ConditionContext(world, player), out string why));
        Assert.Equal("not yet", why);                              // `any`: neither the key nor the bribe
        vars.Set("guard_bribed", 1);
        Assert.True(Conditions.Evaluate(world, player, gate.Requires));

        Conditions.Run(world, player, gate.Then);
        Assert.Equal(1, vars.Get("opened"));
        Assert.Equal(0, vars.Get("levers"));
        Assert.False(Conditions.Evaluate(world, player, gate.Requires));   // the alarm went off: `not`

        // Nothing required is allowed; an `all` of nothing holds.
        Assert.True(Conditions.Evaluate(world, player, (ICondition?)null));
        Assert.True(Conditions.Evaluate(world, player, app.Records.Get<GateRule>(Id("bare")).Requires));

        // The words are the base's own.
        var owners = app.Engine.Vocabularies.All.SelectMany(v => v.Entries.Select(e => (v.Name, e.Id, e.Owner))).ToList();
        foreach (var (vocabulary, id) in new[] { ("condition", "all"), ("condition", "any"), ("condition", "not"), ("condition", "var"),
                                                 ("action", "fire"), ("action", "set_var"), ("action", "add_var") })
            Assert.Contains((vocabulary, id, "sage.core"), owners);
    }

    // Acceptance: an id nobody registered is a load error that says the nearest one, in either form.
    [Fact]
    public void AnUnknownIdSuggestsTheNearestOne()
    {
        const string bad = """
            [{ "type": "gate_rule", "id": "a", "requires": { "all": [ { "vra": "levers", "min": 1 } ] } },
             { "type": "gate_rule", "id": "b", "requires": { "condition": "nto", "of": "all" } },
             { "type": "gate_rule", "id": "c", "then": [ { "set_vra": "x" } ] },
             { "type": "gate_rule", "id": "d", "requires": { "var": "levers", "eqq": 1 } }]
            """;
        using var log = new CaptureSink();
        using var app = HeadlessApp.Bare().With(new LogicTestPlugin()).File("data/bad.json", bad).Build();
        var errors = log.Entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Message).ToList();

        Assert.Contains(errors, e => e.Contains("no condition 'vra'; did you mean 'var'?"));
        Assert.Contains(errors, e => e.Contains("no condition 'nto'; did you mean 'not'?"));
        Assert.Contains(errors, e => e.Contains("no action 'set_vra'; did you mean 'set_var'?"));
        Assert.Contains(errors, e => e.Contains("condition 'var': unknown field 'eqq'") && e.Contains("did you mean 'eq'?"));
    }

    // Acceptance: a var survives save and load, and a load puts back what the save had, not what the
    // world did since.
    [Fact]
    public void AVarSurvivesSaveAndLoad()
    {
        using var app = Bare();
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        var world = app.World;
        Vars.Of(world).Set("alarm", 1);
        Vars.Of(world).Add("levers", 2.5);

        Assert.True(app.Engine.Saves.Save("vars"));
        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(app.Engine.Saves.Root, "vars", "world_logic.json")))!;
        Assert.Equal(2.5, (double)saved["resources"]!["vars"]!["data"]!["Values"]!["levers"]!);

        Vars.Of(world).Set("alarm", 0);
        Vars.Of(world).Set("stranger", 7);
        Assert.True(app.Engine.Saves.Load("vars"));
        Assert.Equal(1, Vars.ValueOf(world, "alarm"));
        Assert.Equal(2.5, Vars.ValueOf(world, "levers"));
        Assert.False(Vars.Of(world).Has("stranger"));
        var alarm = System.Text.Json.JsonSerializer.Deserialize<ICondition>("""{ "var": "alarm", "eq": 1 }""", app.Records.Json);
        Assert.True(Conditions.Evaluate(world, default, alarm));
    }

    // `fire` sends an input through entity I/O, as a wire would: by name (and by name again if the
    // target arrives later), or to whoever the action is about or done by.
    [Fact]
    public void FireSendsAnInputThroughEntityIO()
    {
        using var log = new CaptureSink();
        using var app = HeadlessApp.Simulation().With(new LogicTestPlugin()).File("data/rules.json", Rules).Boot("fire");
        var world = app.World;
        var lamp = world.Create(Transform.At(Vector3.Zero), "lamp");
        var speaker = world.Create(Transform.At(Vector3.One), "speaker");
        var player = world.Create(Transform.At(Vector3.UnitX), "player");
        lock (LogicTestPlugin.Poked) LogicTestPlugin.Poked.RemoveAll(p => p.Self == lamp || p.Self == speaker || p.Activator == player);

        var later = world.Create(Transform.At(Vector3.UnitZ), "later");
        Conditions.Run(world, player, app.Records.Get<GateRule>(Id("lamp")).Then, speaker);
        Tick(world, 2);
        List<(Entity Self, Entity Activator, Entity Caller, string Parameter)> poked;
        lock (LogicTestPlugin.Poked) poked = LogicTestPlugin.Poked.Where(p => p.Activator == player).ToList();
        Assert.Contains((lamp, player, speaker, "3"), poked);
        Assert.Contains((speaker, player, speaker, "other"), poked);
        Assert.DoesNotContain(poked, p => p.Self == later);                // 0.05 s: not yet

        Tick(world, 4);
        lock (LogicTestPlugin.Poked) poked = LogicTestPlugin.Poked.Where(p => p.Activator == player).ToList();
        Assert.Contains((later, player, speaker, ""), poked);

        // Without entity I/O it says so, once, and does nothing.
        using var bare = Bare();
        Conditions.Run(bare.World, default, bare.Records.Get<GateRule>(Id("lamp")).Then);
        Assert.Contains(log.Entries, e => e.Message.Contains("this game has no entity I/O"));
    }

    // `quest` with `atLeast`: that stage, any after it, or finished.
    [Fact]
    public void AQuestConditionAsksHowFarAlongItIs()
    {
        const string quest = """
            [{ "type": "quest", "id": "wood", "stages": [ { "id": "a", "next": "b" }, { "id": "b", "next": "c" }, { "id": "c" } ] },
             { "type": "gate_rule", "id": "far", "requires": { "quest": "wood", "atLeast": "b" } },
             { "type": "gate_rule", "id": "long", "requires": { "condition": "quest", "quest": "wood", "atLeast": "nowhere" } }]
            """;
        using var app = HeadlessApp.Gameplay().With(new LogicTestPlugin()).File("data/quest.json", quest).Boot("quest");
        Assert.Equal(0, app.Records.ErrorCount);
        var world = app.World;
        var far = app.Records.Get<GateRule>(Id("far")).Requires;
        var nowhere = app.Records.Get<GateRule>(Id("long")).Requires;

        Assert.False(Conditions.Evaluate(world, default, far));        // not started
        Assert.True(Quests.Start(world, Id("wood")));
        Assert.False(Conditions.Evaluate(world, default, far));        // at a
        Assert.True(Quests.SetStage(world, Id("wood"), "b"));
        Assert.True(Conditions.Evaluate(world, default, far));
        Assert.True(Quests.SetStage(world, Id("wood"), "c"));
        Assert.True(Conditions.Evaluate(world, default, far));
        Assert.False(Conditions.Evaluate(world, default, nowhere));    // a stage it does not have
        Assert.True(Quests.Finish(world, Id("wood")));
        Assert.True(Conditions.Evaluate(world, default, far));
        Assert.True(Conditions.Evaluate(world, default, nowhere));     // finished is past every stage
    }

    // The gameplay entries belong to the plugins that own what they ask about, so switching dialogue
    // off keeps them; the shorthand reads them too.
    [Fact]
    public void GameplayEntriesComeWithTheirPluginsNotWithDialogue()
    {
        const string rules = """
            [{ "type": "item", "id": "key_iron" },
             { "type": "gate_rule", "id": "door", "requires": { "all": [ { "has_item": "key_iron" }, { "not": { "has_item": "key_iron", "count": 2 } } ] },
               "then": [ { "give_item": "key_iron" } ] }]
            """;
        using var app = HeadlessApp.Gameplay().With(new LogicTestPlugin()).File("data/door.json", rules).Build();
        Assert.Equal(0, app.Records.ErrorCount);
        var owners = app.Engine.Vocabularies.All.SelectMany(v => v.Entries.Select(e => (v.Name, e.Id, e.Owner))).ToList();
        Assert.Contains(("condition", "has_item", "sage.gameplay.items"), owners);
        Assert.Contains(("condition", "standing", "sage.gameplay.factions"), owners);
        Assert.Contains(("action", "apply_effect", "sage.gameplay.attributes"), owners);
        // Dialogue owns only its own words, the topic ones (#93) and `bark` (#392): nothing the rest of gameplay asks.
        Assert.Equal(new[] { "add_topic", "bark", "speaker" }, owners.Where(o => o.Owner == "sage.gameplay.dialogue").Select(o => o.Id).OrderBy(i => i, System.StringComparer.Ordinal));

        var world = app.CreateWorld("door");
        var player = world.Create(Transform.At(Vector3.Zero), "player");
        world.Add(player, new Inventory { Capacity = 0f });
        var door = app.Records.Get<GateRule>(Id("door"));
        Assert.False(Conditions.Evaluate(world, player, door.Requires));
        Conditions.Run(world, player, door.Then);
        Assert.True(Conditions.Evaluate(world, player, door.Requires));
        Conditions.Run(world, player, door.Then);
        Assert.False(Conditions.Evaluate(world, player, door.Requires));   // two: `not` refuses
    }

    // The committed schemas (`sage schema`) know the shorthand: an editor checks it and underlines a
    // misspelt id or setting. (Ids are the Sandbox's: the committed schemas list the ids games load.)
    [Fact]
    public void TheCommittedSchemasReadTheShorthand()
    {
        var validator = new SchemaValidator(Path.Combine(TestEnv.FolderAbove("Sage.sln"), "schemas"));
        var good = JsonNode.Parse("""
            { "type": "dialogue", "id": "d", "nodes": [ { "id": "n", "options": [ { "text": "hi",
                "conditions": [ { "all": [ { "has_item": "practice_sword", "count": 2 }, { "not": { "var": "alarm", "eq": 1 } },
                                           { "any": [ "all", { "quest": "thin_the_wood", "atLeast": "b" } ] } ] },
                                { "condition": "var", "name": "levers", "min": 2 } ],
                "actions": [ { "fire": "hut_door", "input": "Open", "delay": 1 }, { "set_stage": { "quest": "thin_the_wood", "stage": "b" } },
                             { "add_var": "doors" }, { "action": "set_var", "name": "alarm", "value": 0 } ] } ] } ] }
            """);
        Assert.Empty(validator.Validate(good));

        foreach (string bad in new[] { """{ "has_itme": "practice_sword" }""", """{ "var": "alarm", "eqq": 1 }""", """{ "not": { "vra": "x" } }""" })
        {
            var record = JsonNode.Parse("""{ "type": "dialogue", "id": "d", "nodes": [ { "id": "n", "options": [ { "text": "hi", "conditions": [] } ] } ] }""")!;
            record["nodes"]![0]!["options"]![0]!["conditions"]!.AsArray().Add(JsonNode.Parse(bad));
            Assert.NotEmpty(validator.Validate(record));
        }
    }
}

// Asking allocates nothing (02 §4.6): the combinators walk their lists by index and every reason is a
// constant. Measured per thread, alone.
[Xunit.Collection(MeasurementsCollection.Name)]
public class ConditionAllocationTests
{
    public ConditionAllocationTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void EvaluatingConditionsAllocatesNothing()
    {
        const string rules = """
            [{ "type": "gate_rule", "id": "gate",
               "requires": { "all": [ { "var": "levers", "min": 2 }, { "not": { "var": "alarm", "eq": 1 } },
                                      { "any": [ { "var": "key", "eq": 1 }, { "var": "bribe", "min": 1 }, "all" ] } ] } }]
            """;
        using var app = HeadlessApp.Bare().With(new LogicTestPlugin()).File("data/gate.json", rules).Boot("measure");
        var world = app.World;
        var requires = app.Records.Get<GateRule>(new RecordId("sage", "gate")).Requires;
        var list = new List<ICondition> { requires!, requires! };
        Vars.Of(world).Set("levers", 3);
        var context = new ConditionContext(world, default);

        int held = 0;
        for (int i = 0; i < 10; i++) held += Conditions.TestAll(list, in context, out _) ? 1 : 0;   // warm up
        AllocationProbe.AssertNone(10_000, () =>
        {
            if (Conditions.TestAll(list, in context, out _)) held++;
            if (Conditions.Evaluate(world, default, requires)) held++;
        });
        Assert.Equal(20_010, held);
    }
}
