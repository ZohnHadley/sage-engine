#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Friflo.Engine.ECS;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The gameplay_conventions record (REDESIGN §3.1, §4.1, issue #26): gameplay code reads which
// attribute is life, which tag is death, the default attack, damage type, profiles, schedules, the
// player's faction, what spells cost and the action names from one record, not from constants.
public class ConventionsTests
{
    public ConventionsTests() { _ = TestEnv.UserRoot; }

    private static string Repository => TestEnv.FolderAbove("engine_content");

    // `new RecordId("sage", …)`, `new("sage", …)` and `RecordId.Parse("sage:…")`: an engine record id
    // written into code. Gameplay (and the character controller it builds on, and the games) read the
    // conventions record instead; the one well-known id is the record's own, DefaultId.
    private static readonly Regex EngineId = new(
        @"new\s*(RecordId\s*)?\(\s*""sage""|new\s*(RecordId\s*)?\(\s*ComponentSchema\.EngineNamespace|RecordId\.Parse\(\s*""sage:",
        RegexOptions.Compiled);

    [Fact]
    public void NoGameplayCodeNamesAnEngineRecordId()
    {
        var folders = new[] { "src/Sage.Gameplay", "src/Sage.Physics3D", "games" }.Select(f => Path.Combine(Repository, f));
        var found = folders
            .SelectMany(f => Directory.EnumerateFiles(f, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: f, Line: i + 1, Text: line)))
            .Where(l => !l.Text.TrimStart().StartsWith("//", StringComparison.Ordinal) && EngineId.IsMatch(l.Text))
            .Select(l => $"{Path.GetRelativePath(Repository, l.File)}:{l.Line}: {l.Text.Trim()}")
            .ToList();

        // The one exception, by name: the conventions record's own well-known id.
        var allowed = found.Where(l => l.Contains("GameplayConventions.cs") && l.Contains("DefaultId")).ToList();
        Assert.Single(allowed);
        Assert.Empty(found.Except(allowed));
    }

    // The engine ships its conventions, and they name the engine's own records: loading engine content
    // is error-free, and every field says what the constants it replaced used to.
    [Fact]
    public void TheEngineShipsItsConventionsAsOneRecord()
    {
        using var app = HeadlessApp.Gameplay().WithEngineContent().Boot("conventions");
        Assert.Equal(0, app.Records.ErrorCount);

        var conventions = app.World.Conventions();
        Assert.Same(app.Records.Get<GameplayConventionsRecord>(GameplayConventionsRecord.DefaultId), conventions);
        Assert.Equal(new RecordId("sage", "health"), conventions.Health.Id);
        Assert.Equal(new RecordId("sage", "state.dead"), conventions.Dead.Id);
        Assert.Equal(new RecordId("sage", "state.invulnerable"), conventions.Invulnerable.Id);
        Assert.Equal(new RecordId("sage", "physical"), conventions.DamageType.Id);
        Assert.Equal(new RecordId("sage", "default_attack"), conventions.Attack.Id);
        Assert.Equal(new RecordId("sage", "default_movement"), conventions.Movement.Id);
        Assert.Equal(new RecordId("sage", "default_ai"), conventions.AiProfile.Id);
        Assert.Equal(new RecordId("sage", "player"), conventions.PlayerFaction.Id);
        Assert.Equal(new RecordId("sage", "mana"), conventions.CostAttribute.Id);
        Assert.Equal(new RecordId("sage", "idle"), conventions.Schedules.Idle.Id);
        Assert.Equal(new RecordId("sage", "hold_ground"), conventions.Schedules.HoldGround.Id);
        Assert.Equal("Attack", conventions.Actions.Attack);
        Assert.Equal("Jump", conventions.Actions.Jump);

        // The character controller, below gameplay, reads the same record through its own view.
        Assert.Equal(conventions.Movement.Id, CharacterConventions.Of(app.World).DefaultProfile);
    }

    private const string Hp = """
    [
      { "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
      { "type": "attribute", "id": "hp",     "start": 30,  "min": 0, "max": 30 },
      { "type": "tag", "id": "state.dead" },
      { "type": "effect", "id": "wound", "modifiers": [ { "attribute": "hp", "op": "Add", "value": -1 } ] },
      { "type": "damage_type", "id": "cut", "effect": "wound" },
    """;

    private static HeadlessApp Game(string conventions) =>
        HeadlessApp.Gameplay().File("data/game.json", Hp + conventions + "\n]").Boot("hp");

    // Issue #26's acceptance: a game that calls its life "hp" says so in one record — the same content,
    // the same code, and what kills a creature is now hp running out, not health.
    [Fact]
    public void AGameRenamesHealthToHpByChangingOneRecord()
    {
        foreach (string life in new[] { "health", "hp" })
        {
            using var app = Game($$"""{ "type": "gameplay_conventions", "id": "default_conventions", "health": "{{life}}", "dead": "state.dead", "damageType": "cut" }""");
            Assert.Equal(0, app.Records.ErrorCount);
            var world = app.World;
            var died = new EventProbe<Died>(world);

            var creature = world.Create(Transform.At(Vector3.Zero), "creature");
            world.AddAttributes(creature);
            // No type: the conventions' default, `cut`, which wounds hp.
            float applied = Combat.ApplyDamage(world, new DamageInfo(default, creature, default, 50f, Vector3.Zero, Vector3.UnitZ));
            world.RunFixed(1f / 60f);

            Assert.Equal(0f, world.Attribute(creature, new RecordId("sage", "hp")));
            Assert.Equal(100f, world.Attribute(creature, new RecordId("sage", "health")));
            bool hpIsLife = life == "hp";
            // `applied` is the life a hit cost, so it is measured in whatever the game calls life.
            Assert.Equal(hpIsLife ? 30f : 0f, applied);
            Assert.Equal(hpIsLife, world.HasTag(creature, new RecordId("sage", "state.dead")));
            Assert.Equal(hpIsLife ? 1 : 0, died.All.Count);
        }
    }

    // No conventions record at all (a game with no engine content): nothing is life, so nothing dies,
    // and nothing breaks either.
    [Fact]
    public void WithoutConventionsNothingIsLifeAndNothingBreaks()
    {
        using var app = Game("""{ "type": "tag", "id": "unused" }""");
        var world = app.World;
        Assert.Same(GameplayConventionsRecord.None, world.Conventions());
        var died = new EventProbe<Died>(world);

        var creature = world.Create(Transform.At(Vector3.Zero), "creature");
        world.AddAttributes(creature);
        Assert.Equal(0f, Combat.ApplyDamage(world, new DamageInfo(default, creature, default, 50f, Vector3.Zero, Vector3.UnitZ)));
        Assert.Equal(0f, Combat.ApplyDamage(world, new DamageInfo(default, creature, new RecordId("sage", "cut"), 50f, Vector3.Zero, Vector3.UnitZ)));
        world.RunFixed(1f / 60f);

        Assert.Equal(0f, world.Attribute(creature, new RecordId("sage", "hp")));   // the cut landed…
        Assert.Empty(died.All);                                                      // …and killed nothing
    }

    // Action names are the one part a game cannot invent in data: actions are registered in Init. A
    // name nobody registered is a load error at its line, not a button that silently does nothing.
    [Fact]
    public void AConventionNamingAnActionNobodyRegisteredIsALoadError()
    {
        using var app = Game("""{ "type": "gameplay_conventions", "id": "default_conventions", "actions": { "attack": "Swing" } }""");
        Assert.Equal(1, app.Records.ErrorCount);

        using var registered = HeadlessApp.Gameplay()
            .OnRegistered(a => a.Engine.Actions.Register("Swing", ActionKind.Button))
            .File("data/game.json", Hp + """{ "type": "gameplay_conventions", "id": "default_conventions", "actions": { "attack": "Swing" } }""" + "\n]")
            .Boot("swing");
        Assert.Equal(0, registered.Records.ErrorCount);
    }
}
