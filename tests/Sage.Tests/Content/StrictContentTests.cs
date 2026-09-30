#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Sage.Tests;

using Assert = Xunit.Assert;

[Record("test_charm")]
public sealed class TestCharm
{
    public RecordRef<TestSpell> Spell;                 // must be a test_spell
    public List<RecordRef<TestSpell>> Spells = new();
    public RecordId Anything;                          // any record at all
    public List<TestCharmStep> Steps = new();
    public AssetPath Icon;
}

public sealed class TestCharmStep
{
    public RecordRef<TestItem> Item;
    public int Count = 1;
}

// The rest of issue #22: references checked by type, every unknown field an error with the nearest
// real one, prefab bodies checked at load, asset paths that must exist, a missing record that stops a
// dev build, and `sage validate`. Each case asserts the exact message and where it points.
public class StrictContentTests
{
    public StrictContentTests() { _ = TestEnv.UserRoot; }

    private static List<string> MessagesAbout(CaptureSink capture, string mount) =>
        capture.Entries.Select(e => e.Message).Where(m => m.StartsWith(mount + ":")).ToList();

    private static RecordStore LoadCharms(MountFixture fx, bool assetsMustExist = false)
    {
        var store = new RecordStore { MissingAssetsAreErrors = assetsMustExist };
        store.Register<TestItem>();
        store.Register<TestSpell>();
        store.Register<TestCharm>();
        store.Load(fx.Vfs);
        return store;
    }

    // ---- RecordRef<T> ----------------------------------------------------------------------------

    [Fact]
    public void RecordRefs_AreCheckedByType()
    {
        var fx = new MountFixture();
        fx.Write("typed", "data/charms.json", """
            [
              { "type": "test_spell", "id": "zap" },
              { "type": "test_item", "id": "sword" },
              { "type": "test_charm", "id": "good", "spell": "zap", "spells": ["zap"], "anything": "sword",
                "steps": [{ "item": "sword" }] },
              { "type": "test_charm", "id": "wrong_type", "spell": "sword" },
              { "type": "test_charm", "id": "typo", "spells": ["zap", "zpa"] },
              { "type": "test_charm", "id": "nested",
                "steps": [{ "item": "sword" }, { "item": "zap", "count": 2 }] }
            ]
            """);
        fx.Mount("typed", "sandbox");
        using var capture = new CaptureSink();
        var store = LoadCharms(fx);

        var messages = MessagesAbout(capture, "typed");
        Assert.Contains("typed:data/charms.json:6:47: test_charm sandbox:wrong_type: 'spell' refers to test_spell sandbox:sword, " +
                        "which doesn't exist (sandbox:sword is a test_item)", messages);
        Assert.Contains("typed:data/charms.json:7:59: test_charm sandbox:typo: 'spells' refers to test_spell sandbox:zpa, " +
                        "which doesn't exist; did you mean 'sandbox:zap'?", messages);
        Assert.Contains("typed:data/charms.json:9:38: test_charm sandbox:nested: 'steps.item' refers to test_item sandbox:zap, " +
                        "which doesn't exist (sandbox:zap is a test_spell)", messages);
        Assert.Equal(3, store.ErrorCount);

        // In code it is an id: it converts both ways and compares with one.
        var good = store.Get<TestCharm>(new RecordId("sandbox", "good"));
        Assert.True(good.Spell == new RecordId("sandbox", "zap"));
        Assert.Equal(typeof(TestSpell), ((IRecordRef)good.Spell).Target);
        Assert.Same(store.Get<TestSpell>(new RecordId("sandbox", "zap")), store.Get(good.Spell));
    }

    // ---- Unknown fields --------------------------------------------------------------------------

    [Fact]
    public void UnknownFields_AtAnyDepth_AreErrorsWithTheNearestName()
    {
        var fx = new MountFixture();
        fx.Write("unknown", "data/items.json", """
            [
              { "type": "test_item", "id": "deep", "stats": { "strength": 1, "agilty": 2 } },
              { "type": "test_charm", "id": "listed", "steps": [{ "item": "", "cuont": 2 }] },
              { "type": "test_item", "id": "far", "wibble": 1 },
              { "type": "test_item", "id": "fine", "$schema": "../schemas/test_item.json" }
            ]
            """);
        fx.Mount("unknown", "sandbox");
        using var capture = new CaptureSink();
        var store = LoadCharms(fx);

        var messages = MessagesAbout(capture, "unknown");
        Assert.Contains("unknown:data/items.json:2:66: test_item sandbox:deep: unknown field 'agilty' in 'stats'; did you mean 'agility'? (record skipped)", messages);
        Assert.Contains("unknown:data/items.json:3:67: test_charm sandbox:listed: unknown field 'cuont' in 'steps'; did you mean 'count'? (record skipped)", messages);
        Assert.Contains("unknown:data/items.json:4:39: test_item sandbox:far: unknown field 'wibble' (record skipped)", messages);
        Assert.Equal(3, store.ErrorCount);
        Assert.True(store.TryGet(new RecordId("sandbox", "fine"), out TestItem _));   // "$schema" is the editor's
        Assert.False(store.TryGet(new RecordId("sandbox", "deep"), out TestItem _));
    }

    // ---- Asset paths -----------------------------------------------------------------------------

    [Fact]
    public void AssetPaths_MustBeInAMount_AWarningInDev_AnErrorWhenValidating()
    {
        var fx = new MountFixture();
        fx.Write("assets", "textures/here.png", "x");
        fx.Write("assets", "shaders/lit.fx", "x");
        fx.Write("assets", "data/charms.json", """
            [
              { "type": "test_charm", "id": "here", "icon": "textures/here.png" },
              { "type": "test_charm", "id": "compiled", "icon": "shaders/lit.mgfxo" },
              { "type": "test_charm", "id": "gone", "icon": "textures/gone.png" }
            ]
            """);
        fx.Mount("assets", "sandbox");

        using (var capture = new CaptureSink())
        {
            var dev = LoadCharms(fx);
            Assert.Equal(new[] { "assets:data/charms.json:4:41: test_charm sandbox:gone: 'icon' names textures/gone.png, which is in no mount" },
                         MessagesAbout(capture, "assets"));
            Assert.Equal(0, dev.ErrorCount);
            Assert.Equal(1, dev.WarningCount);
        }

        var validating = LoadCharms(fx, assetsMustExist: true);
        Assert.Equal(1, validating.ErrorCount);
    }

    // ---- A missing record ------------------------------------------------------------------------

    [Fact]
    public void AMissingRecord_StopsADevBuild_InsteadOfHandingBackABlankOne()
    {
        var fx = new MountFixture();
        fx.Write("missing", "data/items.json", """[{ "type": "test_item", "id": "sword", "value": 7 }]""");
        fx.Mount("missing", "sandbox");
        var store = fx.Load();

        var ex = Assert.Throws<KeyNotFoundException>(() => store.Get<TestItem>(new RecordId("sandbox", "swrod")));
        Assert.Equal("no test_item record sandbox:swrod; did you mean 'sandbox:sword'?", ex.Message);
        Assert.Equal(7, store.Get<TestItem>(new RecordId("sandbox", "sword")).Value);
    }

    // ---- Prefab bodies, at load ------------------------------------------------------------------

    [Fact]
    public void PrefabBodies_AreCheckedAtLoad_AtTheirLines()
    {
        using var capture = new CaptureSink();
        using var app = HeadlessApp.Gameplay().File("data/prefabs.json", """
            [
              { "type": "attack", "id": "claw" },
              { "type": "ability", "id": "zap" },
              { "type": "prefab", "id": "lamp",
                "parts": { "light": { "color": [1, 0.8, 0.6] } } },
              { "type": "prefab", "id": "brute",
                "parts": { "melee": { "attack": "zap" }, "sprit": {}, "effects": ["no_effect"] },
                "components": { "sprite_rendrer": {}, "rigid_body": { "mas": 3 }, "collider": { "isTrigger": "yes" } },
                "tags": ["no_such_tag"] },
              { "type": "prefab", "id": "fine",
                "parts": { "melee": { "attack": "claw" }, "light": { "colour": [1, 1, 1] } } }
            ]
            """, ns: "strictp").Build();

        var messages = MessagesAbout(capture, "strictp");
        Assert.Contains("strictp:data/prefabs.json:5:27: prefab strictp:lamp: unknown field 'color' in 'parts.light'; did you mean 'colour'?", messages);
        Assert.Contains("strictp:data/prefabs.json:7:27: prefab strictp:brute: 'parts.melee.attack' refers to attack strictp:zap, " +
                        "which doesn't exist (strictp:zap is an ability)", messages);
        Assert.Contains(messages, m => m.StartsWith("strictp:data/prefabs.json:7:46: prefab strictp:brute: no prefab part 'sprit'; did you mean 'sprite'? (have: "));
        Assert.Contains("strictp:data/prefabs.json:7:71: prefab strictp:brute: 'parts.effects' refers to effect strictp:no_effect, which doesn't exist", messages);
        Assert.Contains("strictp:data/prefabs.json:8:21: prefab strictp:brute: no component 'sprite_rendrer'; did you mean \"sage:sprite_renderer\"? (see `ent_types`)", messages);
        Assert.Contains("strictp:data/prefabs.json:8:59: prefab strictp:brute: unknown field 'mas' in 'components.rigid_body'; did you mean 'mass'?", messages);
        Assert.Contains("strictp:data/prefabs.json:8:85: prefab strictp:brute: components.collider: The JSON value could not be converted to System.Boolean.", messages);
        Assert.Contains("strictp:data/prefabs.json:9:14: prefab strictp:brute: no tag 'no_such_tag' in 'strictp' or 'sage' (see `ent_types`)", messages);
        Assert.Equal(8, app.Records.ErrorCount);

        // What the load reported is not reported again, as an error, by every spawn.
        var world = app.CreateWorld("spawns");
        using var spawns = new CaptureSink();
        Assert.False(world.Spawn(new RecordId("strictp", "brute")).IsNull);
        Assert.DoesNotContain(spawns.Entries, e => e.Level >= LogLevel.Error && e.Message.StartsWith("strictp:brute"));
    }

    // A patch from another namespace means its own namespace by a bare id inside a part or a component,
    // as in any other field (R11): a carry-over from part 1.
    [Fact]
    public void APatchedPrefabBody_QualifiesBareIdsInThePatchersNamespace()
    {
        var engine = new MountFixture();
        engine.Write("base_prefabs", "data/prefabs.json", """
            [{ "type": "attack", "id": "claw" },
             { "type": "prefab", "id": "wolf", "parts": { "melee": { "attack": "claw" } } }]
            """);
        engine.Mount("base_prefabs", "sage");
        var mod = new MountFixture();
        mod.Write("wolfmod", "data/wolves.json", """
            [{ "type": "attack", "id": "bite" },
             { "type": "prefab", "id": "sage:wolf", "patch": true, "parts": { "melee": { "natural": "bite" } } }]
            """);
        mod.Mount("wolfmod", "wolfmod");

        using var app = HeadlessApp.Gameplay().Mount(engine).Mount(mod).Boot("wolves");
        Assert.Equal(0, app.Records.ErrorCount);
        var wolf = app.World.Spawn(new RecordId("sage", "wolf"));
        Assert.Equal(new RecordId("sage", "claw"), app.World.Get<Melee>(wolf).Attack);
        Assert.Equal(new RecordId("wolfmod", "bite"), app.World.Get<Melee>(wolf).Natural);
    }

    // ---- AI schedules ----------------------------------------------------------------------------

    [Fact]
    public void AISchedules_NameTheirLineForAnUnknownInterruptOrTask()
    {
        using var capture = new CaptureSink();
        using var app = HeadlessApp.Gameplay().File("data/ai.json", """
            [
              { "type": "ai_schedule", "id": "patrol",
                "tasks": ["FaceTarget", "Wiat", { "task": "Wait", "distance": 2 }],
                "interrupts": ["SeeEnemy", "SeeEnemi"] }
            ]
            """, ns: "strictai").Boot("ai");

        var messages = capture.Entries.Select(e => e.Message).Where(m => m.StartsWith("strictai:")).ToList();
        Assert.Contains("strictai:data/ai.json:4:32: ai_schedule strictai:patrol: no interrupt condition 'SeeEnemi'; did you mean 'SeeEnemy'?", messages);
        Assert.Contains("strictai:data/ai.json:3:29: ai_schedule strictai:patrol: no AI task named 'Wiat'; did you mean 'Wait'?", messages);
        Assert.Contains("strictai:data/ai.json:3:37: ai_schedule strictai:patrol: task 'Wait' takes 'seconds', not 'distance'", messages);
    }

    // ---- sage validate ---------------------------------------------------------------------------

    private static string Repo => TestEnv.FolderAbove("Sage.sln");

    [Fact]
    public void Validate_TheSandboxAndHelloHaveNoErrors()
    {
        foreach (var (game, module) in new (string, IGameModule)[] { ("Sandbox", new Sandbox.SandboxModule()), ("Hello", new Hello.HelloModule()) })
        {
            var report = ContentValidation.Run(new ValidateOptions
            {
                GameDirectory = Path.Combine(Repo, "games", game),
                EngineContentDirectory = Path.Combine(Repo, "engine_content"),
                AvailablePlugins = BasePlugins.All(),
                GameModule = module,
            });
            Assert.True(report.Ok, $"{game}: " + string.Join("\n", report.Errors));
            Assert.Empty(report.Warnings);
            Assert.True(report.Records > 30);
        }
    }

    // The log's "(previous message repeated N more times)" note comes from whichever thread drains the
    // queue at the time, so a validation report that counted it depended on timing: the mod test below
    // failed in CI with a third line. The note is never a problem of its own.
    [Fact]
    public void Validate_NeverCountsTheLogsRepeatNoteAsAProblem()
    {
        var sink = new CapturingSink();
        Log.Flush();
        Log.AddSink(sink);
        try
        {
            // Another test's line landing between the two breaks the fold, so try a few times.
            for (int i = 0; i < 20 && !sink.Entries.Any(IsNote); i++)
            {
                string message = "repeat-note probe " + System.Guid.NewGuid().ToString("N");
                Log.Error(LogCat.Records, message);
                Log.Error(LogCat.Records, message);
                Log.Flush();   // emits the pending note on this thread, as ContentValidation.Run's own Flush can
            }
        }
        finally { Log.RemoveSink(sink); }

        var mine = sink.Entries.Where(e => e.ThreadId == System.Environment.CurrentManagedThreadId).ToList();
        Assert.Contains(mine, IsNote);
        Assert.All(mine, e => Assert.Equal(IsNote(e), ContentValidation.IsRepeatNote(e)));

        static bool IsNote(LogEntry e) => e.Message.StartsWith("(previous message repeated 1 more time", System.StringComparison.Ordinal);
    }

    private sealed class CapturingSink : ILogSink
    {
        private readonly List<LogEntry> _entries = new();
        public LogLevel MinLevel => LogLevel.Warn;
        public IReadOnlyList<LogEntry> Entries { get { lock (_entries) return _entries.ToArray(); } }
        public void Write(in LogEntry entry) { lock (_entries) _entries.Add(entry); }
        public void Flush() { }
    }

    [Fact]
    public void Validate_AModMountWithMistakesFails_NamingEachOne()
    {
        var mod = TestEnv.NewTempDir();
        Directory.CreateDirectory(Path.Combine(mod, "data"));
        File.WriteAllText(Path.Combine(mod, "data", "mod.json"), """
            [
              { "type": "item", "id": "rock", "sound": "sandbox:fire_burst", "sheet": "sandbox:sword_flat" },
              { "type": "sprite_sheet", "id": "rock_flat", "texture": "textures/rock.png" }
            ]
            """);

        var report = ContentValidation.Run(new ValidateOptions
        {
            GameDirectory = Path.Combine(Repo, "games", "Sandbox"),
            EngineContentDirectory = Path.Combine(Repo, "engine_content"),
            AvailablePlugins = BasePlugins.All(),
            GameModule = new Sandbox.SandboxModule(),
            Mounts = new[] { (mod, "rockmod") },
        });

        Assert.False(report.Ok);
        string name = Path.GetFileName(mod);
        Assert.Equal(new[]
        {
            $"Records: {name}:data/mod.json:2:35: item rockmod:rock: 'sound' refers to sound sandbox:fire_burst, which doesn't exist (sandbox:fire_burst is a particle)",
            $"Records: {name}:data/mod.json:3:48: sprite_sheet rockmod:rock_flat: 'texture' names textures/rock.png, which is in no mount",
        }, report.Errors);
    }
}
