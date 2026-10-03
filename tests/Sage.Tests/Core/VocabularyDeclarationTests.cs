#nullable enable
using System.Linq;
using Sage.Generators;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Vocabulary entries are declared, not registered (issue #28): any attribute deriving from
// VocabularyEntryAttribute<T> declares an entry of T's vocabulary, and VocabularyGenerator writes the
// registration its plugin runs before Init, with SAGE0100–0104 for what cannot work.
public class VocabularyDeclarationTests
{
    private const string Vocabulary = """
        using Sage.Core;
        using Sage.Simulation;
        [Vocabulary("widget_kind")] public interface IWidget { }
        public sealed class WidgetAttribute : VocabularyEntryAttribute<IWidget> { public WidgetAttribute(string id) : base(id) { } }
        """;

    private const string OnePlugin = """
        [Plugin("mygame", "1.0.0")] public sealed class MyGame : IModule { public void Init(ModuleContext ctx) { } }
        """;

    [Fact]
    public void TheGeneratorRegistersEveryIdAClassDeclaresForItsPlugin()
    {
        var (output, diagnostics) = DeclarationTests.Generate(Vocabulary + OnePlugin + """
            [Widget("round")] [Widget("square")] public sealed class Shape : IWidget { }
            """, new VocabularyGenerator());

        Assert.Empty(diagnostics);
        Assert.Contains("case \"mygame\":", output);
        Assert.Contains("builder.Vocabulary<global::IWidget, global::Shape>(\"round\");", output);
        Assert.Contains("builder.Vocabulary<global::IWidget, global::Shape>(\"square\");", output);
    }

    [Theory]
    [InlineData("""
        [Plugin("a", "1.0.0")] public sealed class A : IModule { public void Init(ModuleContext ctx) { } }
        [Plugin("b", "1.0.0")] public sealed class B : IModule { public void Init(ModuleContext ctx) { } }
        [Widget("round")] public sealed class Shape : IWidget { }
        """, "SAGE0100", "Shape is declared with [Widget] but names no plugin, and this assembly has 2 plugins (a, b)")]
    [InlineData(OnePlugin + """
        [Widget("round")] public sealed class Shape { }
        """, "SAGE0101", "Shape is declared with [Widget] but it is not a IWidget")]
    [InlineData(OnePlugin + """
        [Widget("round")] public sealed class Shape : IWidget { public Shape(int sides) { } }
        """, "SAGE0101", "it has no public parameterless constructor")]
    [InlineData(OnePlugin + """
        [Widget("is_round")] public sealed class Shape : IWidget { }
        [Widget("IsRound")] public sealed class Ball : IWidget { }
        """, "SAGE0102", "[Widget(\"is_round\")] is declared by both Ball and Shape")]
    [InlineData(OnePlugin + """
        [Widget(" ")] public sealed class Shape : IWidget { }
        """, "SAGE0103", "Shape is declared with [Widget] and an empty id")]
    [InlineData(OnePlugin + """
        public interface IGadget { }
        public sealed class GadgetAttribute : VocabularyEntryAttribute<IGadget> { public GadgetAttribute(string id) : base(id) { } }
        [Gadget("lever")] public sealed class Lever : IGadget { }
        """, "SAGE0104", "[Gadget] declares entries of IGadget, which is not marked [Vocabulary(\"name\")]")]
    public void WhatCannotBeAnEntryIsABuildError(string source, string id, string message)
    {
        var (_, diagnostics) = DeclarationTests.Generate(Vocabulary + source, new VocabularyGenerator());
        var d = Assert.Single(diagnostics);
        Assert.Equal(id, d.Id);
        Assert.Contains(message, d.GetMessage());
    }

    // SAGE0020 covers the registries by hand too: a vocabulary is sealed when content loads.
    [Fact]
    public void RegisteringAnEntryInStartIsABuildError()
    {
        var d = Assert.Single(AnalyzerTests.Analyze(Vocabulary + """
            public sealed class Shape : IWidget { }
            public sealed class M : IModule
            {
                public void Init(ModuleContext ctx) => ctx.Engine.Vocabularies.Of<IWidget>().Register<Shape>("fine");
                public void Start(ModuleContext ctx) => ctx.Engine.Vocabularies.Of<IWidget>().Register<Shape>("late");
            }
            """, new RegistrationStageAnalyzer()));
        Assert.Equal("SAGE0020", d.Id);
        Assert.Contains("Vocabulary.Register registers a vocabulary entry in M.Start", d.GetMessage());
    }

    // Every entry the engine declares belongs to a plugin it ships (the generator infers nothing: the
    // gameplay assembly has many plugins, so each says which).
    [Fact]
    public void TheEnginesEntriesAreRegisteredByTheirPlugins()
    {
        using var app = HeadlessApp.Gameplay().Build();
        var vocabularies = app.Engine.Vocabularies;
        var owners = vocabularies.All.SelectMany(v => v.Entries.Select(e => (v.Name, e.Id, e.Owner))).ToList();

        Assert.Contains(("quest_objective", "kill", "sage.gameplay.quests"), owners);
        Assert.Contains(("quest_objective", "reach", "sage.gameplay.quests"), owners);
        Assert.Contains(("condition", "has_item", "sage.gameplay.items"), owners);
        Assert.Contains(("condition", "all", "sage.core"), owners);            // the language is the base's (#89)
        Assert.Contains(("action", "fire", "sage.core"), owners);
        Assert.Contains(("action", "start_quest", "sage.gameplay.quests"), owners);
        Assert.Contains(("ability_delivery", "touch_area", "sage.gameplay.abilities"), owners);
        Assert.Contains(("effect_execution", "dispel", "sage.gameplay.attributes"), owners);
        Assert.Contains(("item_use", "cast", "sage.gameplay.items"), owners);
        Assert.Contains(("ai_schedule_selector", "default", "sage.gameplay.ai"), owners);
        Assert.Contains(("ai_condition", "CanCastAtEnemy", "sage.gameplay.ai"), owners);
        Assert.Contains(("ai_condition", "in_routine", "sage.gameplay.ai"), owners);   // issue 4g-4
        Assert.Equal(12, vocabularies.Of<IAICondition>().Entries.Count);
    }

    // A plugin switched off takes its entries with it: no quests, no `reach`. What is left is the base's
    // own condition and action language (issue #89), which every game has.
    [Fact]
    public void APluginThatIsNotLoadedRegistersNoEntries()
    {
        using var app = HeadlessApp.Bare().Build();
        var entries = app.Engine.Vocabularies.All.SelectMany(v => v.Entries.Select(e => (v.Name, e.Id, e.Owner))).ToList();
        Assert.All(entries, e => Assert.Equal("sage.core", e.Owner));
        Assert.Equal(new[] { "action:add_var", "action:destroy", "action:fire", "action:hit_stop", "action:load_scene", "action:log", "action:message",
                             "action:pass_time", "action:play_sound", "action:save_game", "action:set_var", "action:spawn_prefab",
                             "action:teleport", "action:wait", "action:world_speed",
                             "condition:all", "condition:anim_finished", "condition:anim_param", "condition:any", "condition:date_between",
                             "condition:distance_to", "condition:entity_exists", "condition:in_scene", "condition:not", "condition:random",
                             "condition:time_between", "condition:var", "condition:weekday" },
                     entries.Select(e => $"{e.Name}:{e.Id}").OrderBy(e => e, System.StringComparer.Ordinal));
    }
}
