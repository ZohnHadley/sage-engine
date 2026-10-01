#nullable enable
using System;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// ---- a game's own words (issue #28) ----------------------------------------------------------------
//
// What these tests declare, the way a game would: each is an entry of one of the engine's open
// vocabularies, owned by a plugin of the "game's", and registered for it by the generator. The content
// in VocabularyTests uses every one of them from JSON alone.

// The test assembly is built without the generator (PrefabPartTests says the same of parts), so this
// Init stands in for the registration a game's build writes from the attributes below — the same calls,
// in the same place, sealed at the same moment.
[Plugin("test.vocabulary", "1.0.0")]
public sealed class VocabularyTestPlugin : IModule
{
    public void Init(ModuleContext ctx)
    {
        var vocabularies = ctx.Engine.Vocabularies;
        vocabularies.Of<IAICondition>().Register<IsNightCondition>("is_night");
        vocabularies.Of<IAIScheduleSelector>().Register<AlwaysProwlSelector>("always_prowl");
        vocabularies.Of<QuestObjective>().Register<PullLeverObjective>("pull_lever");
        vocabularies.Of<ICondition>().Register<NightCondition>("is_night");
        vocabularies.Of<IAction>().Register<HealAction>("heal");
        vocabularies.Of<IItemUse>().Register<HealUse>("heal");
        vocabularies.Of<IAbilityDelivery>().Register<BeneathDelivery>("beneath");
        vocabularies.Of<IEffectExecution>().Register<GrantGoldExecution>("grant_gold");
    }
}

// The game's idea of the time of day, which the engine does not have: perception defined by the game.
public sealed class NightClock
{
    public bool Night;

    public static bool IsNight(World world) => world.Resources.TryGet<NightClock>(out var clock) && clock is { Night: true };
}

[AICondition("is_night", Plugin = "test.vocabulary")]
public sealed class IsNightCondition : IAICondition
{
    public bool Sense(in AIPerception perception) => NightClock.IsNight(perception.World);
}

[AIScheduleSelector("always_prowl", Plugin = "test.vocabulary")]
public sealed class AlwaysProwlSelector : IAIScheduleSelector
{
    public RecordId Choose(in AIScheduleChoice choice) => new("sage", "prowl");
}

[QuestObjective("pull_lever", Plugin = "test.vocabulary")]
public sealed class PullLeverObjective : QuestObjective
{
    public string Lever = "";

    public override int Notice(World world, in QuestHappening happened) =>
        happened.Kind == "test.lever_pulled" && happened.Node == Lever ? 1 : 0;

    public override string Describe(World world, int count) => $"pull the {Lever} lever";
}

[Condition("is_night", Plugin = "test.vocabulary")]
public sealed class NightCondition : ICondition
{
    public bool Test(in ConditionContext context, out string why)
    {
        why = "only after dark";
        return NightClock.IsNight(context.World);
    }
}

[Action("heal", Plugin = "test.vocabulary")]
public sealed class HealAction : IAction
{
    public float Amount;

    public void Run(in ActionContext context) => VocabularyTests.Heal(context.World, context.Subject, Amount);
}

[ItemUse("heal", Plugin = "test.vocabulary")]
public sealed class HealUse : IItemUse
{
    public float Amount;

    public bool CanUse(in ItemUse use, out string why)
    {
        why = "you are not hurt";
        return VocabularyTests.Health(use.World, use.User) < 100f;
    }

    public bool Use(in ItemUse use, out string why)
    {
        why = "";
        VocabularyTests.Heal(use.World, use.User, Amount);
        return true;
    }
}

// Lands on whoever cast it, like `self`, but is not a buff: an AI would throw it at an enemy.
[AbilityDelivery("beneath", Plugin = "test.vocabulary")]
public sealed class BeneathDelivery : IAbilityDelivery
{
    public bool Release(in AbilityRelease cast, out Vector3 point, out Entity struck)
    {
        point = cast.Origin;
        struck = default;
        return true;
    }

    public void Gather(ref AbilityLanding landing) => landing.AddCaster();
}

[EffectExecution("grant_gold", Plugin = "test.vocabulary")]
public sealed class GrantGoldExecution : IEffectExecution
{
    public RecordRef<ItemRecord> Item;
    public int Count = 1;

    public void Execute(in EffectExecution execution) => execution.World.Give(execution.Target, Item, Count);
}

// The open vocabularies (docs/REDESIGN.md §4.3 stage 1, issue #28): each registry takes an entry a game
// declares, and content uses it from JSON alone; the engine's own entries keep today's behaviour.
public class VocabularyTests
{
    public VocabularyTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
        [{ "type": "gameplay_conventions", "id": "default_conventions", "health": "health", "dead": "state.dead" },
         { "type": "attribute", "id": "health", "start": 50, "min": 0, "max": 100 },
         { "type": "tag", "id": "state.dead" },
         { "type": "tag", "id": "state.burning" },
         { "type": "effect", "id": "mend", "modifiers": [ { "attribute": "health", "op": "Add", "value": 10 } ] },
         { "type": "effect", "id": "burning", "duration": "Timed", "time": 30, "grantTags": ["state.burning"] },
         { "type": "effect", "id": "cleanse", "executions": [{ "execution": "dispel", "tags": ["state.burning"] }] },
         { "type": "effect", "id": "gilded", "executions": [{ "execution": "grant_gold", "item": "coin", "count": 3 }] },
         { "type": "effect", "id": "blink", "executions": [{ "execution": "teleport", "offset": [0, 0, 5] }] },
         { "type": "effect", "id": "shove", "executions": [{ "execution": "knockback", "force": 8, "lift": 0 }] },
         { "type": "effect", "id": "call", "executions": [{ "execution": "summon", "prefab": "imp", "count": 2 }] },
         { "type": "prefab", "id": "imp", "name": "imp" },

         { "type": "item", "id": "coin", "maxStack": 99, "weight": 0 },
         { "type": "item", "id": "potion", "uses": [{ "use": "heal", "amount": 25 }, "consume"] },
         { "type": "item", "id": "tome", "uses": [{ "use": "read", "text": "Warm hands, warm heart.", "teach": "warm_hands" }] },
         { "type": "item", "id": "wand", "uses": [{ "use": "cast", "ability": "warm_hands" }] },
         { "type": "ability", "id": "warm_hands", "name": "Warm Hands", "targeting": "Self", "effects": ["mend"] },
         { "type": "ability", "id": "undertow", "name": "Undertow", "delivery": "beneath", "effects": ["mend"] },

         { "type": "ai_profile", "id": "nocturnal", "thinkRate": 30,
           "rules": [ { "when": ["is_night"], "schedule": "prowl" }, { "unless": ["is_night"], "schedule": "rest" } ] },
         { "type": "ai_profile", "id": "restless", "thinkRate": 30, "selector": "always_prowl" },
         { "type": "ai_schedule", "id": "prowl", "tasks": [{ "task": "Wait", "seconds": 5 }] },
         { "type": "ai_schedule", "id": "rest", "tasks": [{ "task": "Wait", "seconds": 5 }], "interrupts": ["is_night", "SeeEnemy"] },

         { "type": "dialogue", "id": "hermit", "label": "the hermit",
           "nodes": [ { "id": "greet", "text": "Well?",
             "options": [ { "text": "Tell me a secret.", "end": true,
               "conditions": [{ "condition": "is_night" }],
               "actions": [{ "action": "heal", "amount": 20 }, { "action": "give_item", "item": "coin", "count": 2 }] } ] } ] },

         { "type": "quest", "id": "errand", "label": "An Errand",
           "stages": [
             { "id": "go", "objectives": [{ "kind": "reach", "at": [10, 0, 0], "radius": 2, "place": "the well" }], "next": "speak" },
             { "id": "speak", "objectives": [{ "kind": "talk", "dialogue": "hermit" }], "next": "pull" },
             { "id": "pull", "objectives": [{ "kind": "pull_lever", "lever": "north" }], "done": true } ] }]
        """;

    private static RecordId Id(string name) => new("sage", name);

    // The content above loads without a word: every entry it names is registered.
    private static HeadlessApp Boot()
    {
        using var log = new CaptureSink();
        var app = HeadlessApp.Gameplay().With(new VocabularyTestPlugin()).File("data/vocabulary.json", Records).Boot("vocabulary");
        var errors = log.Entries.Where(e => e.Level >= LogLevel.Error && e.Message.Contains("vocabulary.json")).Select(e => e.Message).ToList();
        Assert.True(app.Records.ErrorCount == 0 && errors.Count == 0, string.Join("\n", errors));
        return app;
    }

    private static Entity Player(World world, Vector3 position = default)
    {
        var entity = world.Create(Transform.At(position), "player");
        world.AddAttributes(entity);
        world.Add(entity, new Inventory { Capacity = 0f });
        world.Add(entity, new PawnIntent());
        entity.AddTag<PlayerControlled>();
        return entity;
    }

    private static void Tick(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    internal static float Health(World world, Entity entity) => world.Attribute(entity, Id("health"));

    internal static void Heal(World world, Entity entity, float amount)
    {
        int health = world.Resources.Get<GameplayRegistries>().Attribute(Id("health"));
        var values = world.Get<Attributes>(entity).Values;
        values.SetBase(health, Math.Min(values.BaseOf(health) + amount, 100f));
    }

    // ---- AI conditions and selectors ----------------------------------------------------------------

    [Fact]
    public void TheEnginesConditionsKeepTheirBitsAndAGamesTakeTheNextFree()
    {
        using var app = Boot();
        var conditions = AIConditions.Of(app.World);

        Assert.Equal(0, conditions.BitOf("SeeEnemy"));
        Assert.Equal(0, conditions.BitOf("see_enemy"));     // one word however it is spelled
        Assert.Equal(10, conditions.BitOf(nameof(AICondition.RememberEnemy)));
        Assert.Equal(11, conditions.BitOf("in_routine"));    // the engine's own, from 4g-4
        Assert.Equal(12, conditions.BitOf("is_night"));
        Assert.Equal(-1, conditions.BitOf("is_nite"));
        Assert.Equal("sage.gameplay.ai", app.Engine.Registrations.OwnerOf("ai_condition", "SeeEnemy"));
        Assert.Equal("test.vocabulary", app.Engine.Registrations.OwnerOf("ai_condition", "is_night"));
    }

    // Acceptance: an `is_night` condition, declared by the game and sensed by its own perception, picks
    // the schedule through a profile's rules — from JSON alone.
    [Fact]
    public void AGamesConditionPicksASchedulesThroughAProfilesRules()
    {
        using var app = Boot();
        var world = app.World;
        var clock = new NightClock();
        world.Resources.Add(clock);
        var creature = world.Create(Transform.At(Vector3.Zero), "creature");
        world.Add(creature, new AIState { Profile = Id("nocturnal") });
        world.Add(creature, new PawnIntent());

        Tick(world, 6);
        Assert.Equal(Id("rest"), world.Get<AIState>(creature).Schedule);
        Assert.False(AIConditions.Of(world).Has(world.Get<AIState>(creature).Conditions, "is_night"));

        clock.Night = true;
        Tick(world, 6);
        var state = world.Get<AIState>(creature);
        Assert.Equal(Id("prowl"), state.Schedule);
        Assert.True(AIConditions.Of(world).Has(state.Conditions, "is_night"));
        Assert.True(((AICondition)state.Conditions).HasFlag(AICondition.NoEnemy), "the engine's perception still runs");
    }

    [Fact]
    public void AGamesSelectorIsNamedByTheProfile()
    {
        using var app = Boot();
        var world = app.World;
        var creature = world.Create(Transform.At(Vector3.Zero), "creature");
        world.Add(creature, new AIState { Profile = Id("restless") });
        world.Add(creature, new PawnIntent());

        Tick(world, 6);
        Assert.Equal(Id("prowl"), world.Get<AIState>(creature).Schedule);
    }

    // ---- quest objectives ---------------------------------------------------------------------------

    // Acceptance: `reach` and `talk` from JSON, and a game's own objective counting its own happening.
    [Fact]
    public void ReachTalkAndAGamesObjectiveMoveAQuestAlong()
    {
        using var app = Boot();
        var world = app.World;
        var player = Player(world);
        var errand = Id("errand");
        Assert.True(Quests.Start(world, errand));

        var record = app.Records.Get<QuestRecord>(errand);
        Assert.Equal("reach the well", Quests.Describe(world, record.Stages[0].Objectives[0], 0));
        Assert.Equal("talk to the hermit", Quests.Describe(world, record.Stages[1].Objectives[0], 0));
        Assert.Equal("pull the north lever", Quests.Describe(world, record.Stages[2].Objectives[0], 0));

        Tick(world, 3);
        Assert.Equal("go", Quests.StageOf(world, errand));
        world.Teleport(player, Transform.At(new Vector3(10.5f, 0, 1f)));
        Tick(world, 2);
        Assert.Equal("speak", Quests.StageOf(world, errand));

        var hermit = world.Create(Transform.At(new Vector3(11, 0, 0)), "hermit");
        world.Add(hermit, new Dialogue { Record = Id("hermit") });
        Assert.True(DialogueRules.Start(world, hermit, player));
        Tick(world, 2);
        Assert.Equal("pull", Quests.StageOf(world, errand));

        Quests.Notice(world, new QuestHappening("test.lever_pulled", default, player) { Node = "south" });
        Assert.False(Quests.IsFinished(world, errand));
        Quests.Notice(world, new QuestHappening("test.lever_pulled", default, player) { Node = "north" });
        Assert.True(Quests.IsFinished(world, errand));
    }

    // ---- dialogue conditions and actions ------------------------------------------------------------

    [Fact]
    public void ADialogueOptionUsesAGamesConditionAndActionsByName()
    {
        using var app = Boot();
        var world = app.World;
        var player = Player(world);
        var hermit = world.Create(Transform.At(new Vector3(1, 0, 0)), "hermit");
        world.Add(hermit, new Dialogue { Record = Id("hermit") });
        Assert.True(DialogueRules.Start(world, hermit, player));
        var option = DialogueRules.Current(world)!.Options[0];

        Assert.False(DialogueRules.CanPick(world, player, option, out string why));
        Assert.Equal("only after dark", why);

        world.Resources.Add(new NightClock { Night = true });
        Assert.True(DialogueRules.Pick(world, option));
        Assert.Equal(70f, Health(world, player));
        Assert.Equal(2, world.CountOf(player, Id("coin")));
    }

    // ---- item uses -----------------------------------------------------------------------------------

    // Acceptance: a `heal` item use the game declares, then the engine's `consume`, from JSON alone.
    [Fact]
    public void AnItemIsUsedThroughItsUsesInOrder()
    {
        using var app = Boot();
        var world = app.World;
        var player = Player(world);

        Assert.False(world.UseItem(player, Id("potion"), out string why));
        Assert.Equal("you are not carrying it", why);

        world.Give(player, Id("potion"));
        Assert.True(world.UseItem(player, Id("potion"), out why), why);
        Assert.Equal(75f, Health(world, player));
        Assert.Equal(0, world.CountOf(player, Id("potion")));   // consumed

        Heal(world, player, 100f);
        world.Give(player, Id("potion"));
        Assert.False(world.CanUse(player, Id("potion"), out why));
        Assert.Equal("you are not hurt", why);
        Assert.Equal(1, world.CountOf(player, Id("potion")));   // a use that refuses uses nothing up
    }

    [Fact]
    public void ReadingTeachesAndAWandCastsForFree()
    {
        using var app = Boot();
        var world = app.World;
        var player = Player(world);

        world.Give(player, Id("tome"));
        Assert.True(world.UseItem(player, Id("tome")));
        Assert.True(world.Knows(player, Id("warm_hands")));
        Assert.Equal(1, world.CountOf(player, Id("tome")));     // reading is not using up

        world.Give(player, Id("wand"));
        Assert.True(world.UseItem(player, Id("wand")));
        Assert.Equal(60f, Health(world, player));               // warm hands: mend, on the caster, at once
    }

    // ---- ability deliveries --------------------------------------------------------------------------

    [Fact]
    public void AnAbilityNamesAGamesDelivery()
    {
        using var app = Boot();
        var world = app.World;
        var player = Player(world);
        world.Teach(player, Id("undertow"));

        Assert.True(world.Cast(player, Id("undertow")));
        Tick(world, 2);
        Assert.Equal(60f, Health(world, player));
        Assert.False(AbilityDeliveries.OnCaster(world, app.Records.Get<AbilityRecord>(Id("undertow"))));
        Assert.True(AbilityDeliveries.OnCaster(world, app.Records.Get<AbilityRecord>(Id("warm_hands"))));   // `targeting` still means one
    }

    // ---- effect executions ---------------------------------------------------------------------------

    [Fact]
    public void AnEffectRunsAGamesExecutionAndTheEnginesOwn()
    {
        using var app = Boot();
        var world = app.World;
        var player = Player(world);

        Assert.True(Effects.Apply(world, player, Id("gilded")));
        Assert.Equal(3, world.CountOf(player, Id("coin")));

        // dispel: everything that burns goes, after the tick
        Effects.Apply(world, player, Id("burning"));
        Tick(world);
        Assert.True(world.HasTag(player, Id("state.burning")));
        Effects.Apply(world, player, Id("cleanse"));
        Tick(world, 2);
        Assert.False(Effects.IsActive(world, player, Id("burning")));
        Assert.False(world.HasTag(player, Id("state.burning")));

        // teleport: five metres ahead of where it faces (yaw 0 faces -Z)
        Effects.Apply(world, player, Id("blink"));
        var at = world.Get<Transform>(player).LocalPosition;
        Assert.Equal(-5f, at.Z, 3);
        Assert.Equal(0f, at.X, 3);

        // summon: two imps, once the tick is over
        Effects.Apply(world, player, Id("call"));
        Tick(world);
        Assert.Equal(2, world.Query<Sage.Simulation.FromPrefab>().Entities.Count(e => world.Get<Sage.Simulation.FromPrefab>(e).Prefab == Id("imp")));
    }

    [Fact]
    public void KnockbackPushesACharacterAwayFromTheSource()
    {
        using var app = Boot();
        var world = app.World;
        var target = world.Create(Transform.At(Vector3.Zero), "target");
        world.AddCharacter(target, world.Resources.Get<IPhysicsWorld>().Layers.Enemy);
        world.AddAttributes(target);
        var source = world.Create(Transform.At(new Vector3(0, 0, -2)), "source");

        Assert.True(Effects.Apply(world, target, Id("shove"), source));
        var velocity = world.Get<CharacterController>(target).Velocity;
        Assert.Equal(8f, velocity.Z, 3);
        Assert.Equal(0f, velocity.X, 3);
    }

    // ---- the registries themselves -------------------------------------------------------------------

    [Fact]
    public void VocabulariesAreSealedWhenContentLoads()
    {
        using var app = Boot();
        var late = Assert.Throws<InvalidOperationException>(() =>
            app.Engine.Vocabularies.Of<IAICondition>().Register<IsNightCondition>("too_late"));
        Assert.Contains("content was loaded", late.Message);
    }

    [Fact]
    public void AnUnknownEntryIsALoadErrorThatSaysTheNearestName()
    {
        const string bad = """
            [{ "type": "quest", "id": "lost", "stages": [ { "id": "a", "objectives": [{ "kind": "raech", "at": [1, 0, 1] }] } ] },
             { "type": "ability", "id": "misfire", "delivery": "beneth" },
             { "type": "ai_schedule", "id": "dozing", "tasks": ["FaceTarget"], "interrupts": ["is_nigth"] },
             { "type": "ai_profile", "id": "odd", "selector": "alwayz_prowl" },
             { "type": "item", "id": "rock", "uses": [{ "use": "heal", "amuont": 3 }] }]
            """;
        using var log = new CaptureSink();
        using var app = HeadlessApp.Gameplay().With(new VocabularyTestPlugin()).File("data/bad.json", bad).Build();
        var errors = log.Entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Message).ToList();

        Assert.Contains(errors, e => e.Contains("no quest_objective 'raech'; did you mean 'reach'?"));
        Assert.Contains(errors, e => e.Contains("no ability_delivery 'beneth'; did you mean 'beneath'?"));
        Assert.Contains(errors, e => e.Contains("no interrupt condition 'is_nigth'; did you mean 'is_night'?"));
        Assert.Contains(errors, e => e.Contains("no ai_schedule_selector 'alwayz_prowl'; did you mean 'always_prowl'?"));
        Assert.Contains(errors, e => e.Contains("item_use 'heal': unknown field 'amuont'") && e.Contains("did you mean 'amount'?"));
    }

    // The generated schemas offer what is registered: `sage schema` writes the same catalog.
    [Fact]
    public void SchemasAndTheRegistryDumpListTheRegisteredNames()
    {
        using var app = Boot();
        var catalog = new SchemaCatalog();
        catalog.Add(app.Engine);
        var files = RecordSchemas.Write(catalog);

        var vocabularies = JsonNode.Parse(files[RecordSchemas.Vocabularies])!["definitions"]!;
        foreach (var (name, id) in new[]
                 {
                     ("ai_condition", "is_night"), ("ai_condition", "SeeEnemy"), ("ai_schedule_selector", "always_prowl"),
                     ("quest_objective", "pull_lever"), ("quest_objective", "reach"), ("quest_objective", "talk"),
                     ("condition", "is_night"), ("action", "heal"), ("item_use", "heal"), ("item_use", "consume"),
                     ("ability_delivery", "beneath"), ("effect_execution", "grant_gold"), ("effect_execution", "dispel"),
                 })
            Assert.Contains(id, vocabularies[name + ":id"]!["enum"]!.AsArray().Select(n => (string)n!));
        Assert.Contains("vocabularies.schema.json#/definitions/quest_objective", files[RecordSchemas.FileOf("quest")]);
        Assert.Contains("vocabularies.schema.json#/definitions/ai_condition:id", files[RecordSchemas.FileOf("ai_schedule")]);

        var dump = JsonNode.Parse(RegistryDump.Build(app.Engine))!;
        var conditions = dump["vocabularies"]!.AsArray().Single(v => (string)v!["name"]! == "ai_condition")!;
        var night = conditions["entries"]!.AsArray().Single(e => (string)e!["id"]! == "is_night")!;
        Assert.Equal("test.vocabulary", (string)night["owner"]!);
    }

    // The committed schemas (schemas/, from `sage schema`) know the engine's entries: an editor offers
    // them, checks their settings, and underlines a misspelt one before the game runs.
    [Fact]
    public void TheCommittedSchemasCheckEntriesAndTheirSettings()
    {
        var validator = new SchemaValidator(System.IO.Path.Combine(TestEnv.FolderAbove("Sage.sln"), "schemas"));
        var good = JsonNode.Parse("""
            [{ "type": "quest", "id": "q", "stages": [ { "id": "a", "objectives": [
                 { "faction": "beasts", "count": 2 }, { "kind": "Kill", "prefab": "bunny" }, { "kind": "reach", "at": [1, 0, 2], "radius": 3 },
                 { "kind": "talk", "dialogue": "hermit_talk", "node": "greet" }, "have" ] } ] },
             { "type": "effect", "id": "e", "executions": [ "knockback", { "execution": "dispel", "tags": ["state.burning"] } ] },
             { "type": "item", "id": "i", "uses": [ { "use": "read", "text": "hello" }, "consume" ] },
             { "type": "ability", "id": "a", "delivery": "touch_area" },
             { "type": "ai_schedule", "id": "s", "interrupts": ["SeeEnemy", "see_enemy"] },
             { "type": "dialogue", "id": "d", "nodes": [ { "id": "n", "options": [ { "text": "hi",
                 "conditions": [ { "condition": "has_item", "item": "practice_sword", "count": 2 } ], "actions": [ "finish_quest" ] } ] } ] }]
            """);
        Assert.Empty(validator.Validate(good));

        foreach (string bad in new[]
                 {
                     """{ "type": "quest", "id": "q", "stages": [ { "id": "a", "objectives": [ { "kind": "raech" } ] } ] }""",
                     """{ "type": "quest", "id": "q", "stages": [ { "id": "a", "objectives": [ { "kind": "reach", "radious": 3 } ] } ] }""",
                     """{ "type": "effect", "id": "e", "executions": [ { "execution": "dispel", "force": 3 } ] }""",
                     """{ "type": "ability", "id": "a", "delivery": "beneth" }""",
                     """{ "type": "ai_schedule", "id": "s", "interrupts": ["SeeEnemie"] }""",
                 })
            Assert.NotEmpty(validator.Validate(JsonNode.Parse(bad)));
    }

    // Tags were one ulong: 64 for a whole game. 256 now, and a tag past 64 behaves like any other.
    [Fact]
    public void TagsGoPast64()
    {
        var tags = string.Join(",\n", Enumerable.Range(0, 100).Select(i => $"{{ \"type\": \"tag\", \"id\": \"t{i:000}\" }}"));
        string records = $$"""
            [{{tags}},
             { "type": "attribute", "id": "health", "start": 50, "min": 0, "max": 100 },
             { "type": "effect", "id": "mark", "duration": "Infinite", "grantTags": ["t099"] },
             { "type": "effect", "id": "needs_mark", "requireTags": ["t099"], "modifiers": [ { "attribute": "health", "op": "Add", "value": 5 } ] }]
            """;
        using var app = HeadlessApp.Gameplay().File("data/tags.json", records).Boot("tags");
        var world = app.World;
        var entity = world.Create(Transform.At(Vector3.Zero), "marked");
        world.AddAttributes(entity);

        Assert.True(world.Resources.Get<GameplayRegistries>().TagCount >= 100);
        Assert.False(Effects.Apply(world, entity, Id("needs_mark")));
        Effects.Apply(world, entity, Id("mark"));
        Tick(world);
        Assert.True(world.HasTag(entity, Id("t099")));
        Assert.True(Effects.Apply(world, entity, Id("needs_mark")));

        world.AddTag(entity, Id("t080"));
        Tick(world);
        Assert.True(world.HasTag(entity, Id("t080")));             // owned, kept through the tick
        Effects.Remove(world, entity, Id("mark"));
        Tick(world);
        Assert.False(world.HasTag(entity, Id("t099")));            // granted, gone with the effect
        Assert.True(world.HasTag(entity, Id("t080")));
    }
}
