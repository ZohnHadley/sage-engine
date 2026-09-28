#nullable enable
using System.Collections.Generic;
using System.Linq;
using sage_engine;

namespace sage_engine.Tests;

using Assert = Xunit.Assert;

// Content mistakes are loud and say where they are (issue #22): bare ids in a patch mean the patching
// file's namespace (R11), every record load error names file:line:column, AI tasks and colours are
// written the way a person reads them.
public class StrictLoadingTests
{
    public StrictLoadingTests() { _ = TestEnv.UserRoot; }

    // The messages one load logged about one mount's files. The capture sees every test's logging, so
    // each test names its mount after itself.
    private static List<string> MessagesAbout(CaptureSink capture, string mount) =>
        capture.Entries.Select(e => e.Message).Where(m => m.StartsWith(mount + ":")).ToList();

    // ---- R11: a bare id means the namespace of the file it is written in ---------------------------

    [Fact]
    public void Patch_BareIdsMeanThePatchingFilesNamespace()
    {
        var fx = new MountFixture();
        fx.Write("engine", "data/items.json", """
            [{ "type": "test_spell", "id": "zap" },
             { "type": "test_spell", "id": "frost" },
             { "type": "test_item", "id": "wand", "spell": "zap" },
             { "type": "test_item", "id": "staff", "spell": "frost", "spells": ["zap", "frost"] },
             { "type": "test_item", "id": "rod" }]
            """);
        fx.Write("game", "data/patches.json", """
            [{ "type": "test_spell", "id": "zap" },
             { "type": "test_item", "id": "rod_base", "abstract": true, "value": 9 },
             { "type": "test_item", "id": "sage:wand", "patch": true, "spell": "zap" },
             { "type": "test_item", "id": "sage:staff", "patch": true, "spells+": ["zap"], "spells-": ["sage:frost"] },
             { "type": "test_item", "id": "sage:rod", "patch": true, "base": "rod_base" }]
            """);
        fx.Mount("engine", "sage");
        fx.Mount("game", "sandbox");
        var store = fx.Load();

        Assert.Equal(0, store.ErrorCount);
        // The patch's "zap" is the game's zap, not the engine's.
        Assert.Equal(new RecordId("sandbox", "zap"), store.Get<TestItem>(new RecordId("sage", "wand")).Spell);
        // What the patch did not touch still means what the engine meant, and a list operation
        // compares ids in full: the engine's bare "frost" is the sage:frost the patch removes.
        var staff = store.Get<TestItem>(new RecordId("sage", "staff"));
        Assert.Equal(new RecordId("sage", "frost"), staff.Spell);
        Assert.Equal(new[] { new RecordId("sage", "zap"), new RecordId("sandbox", "zap") }, staff.Spells);
        // "base" is an id too.
        Assert.Equal(9, store.Get<TestItem>(new RecordId("sage", "rod")).Value);
    }

    // ---- file:line:column ------------------------------------------------------------------------

    [Fact]
    public void RecordErrors_SayFileLineAndColumn()
    {
        var fx = new MountFixture();
        fx.Write("lines", "data/items.json", """
            [
              { "type": "test_item", "id": "typo", "vaule": 3 },
              { "type": "test_item", "id": "bad",
                "value": "lots" },
              { "type": "test_item", "id": "deep",
                "stats": { "strength": 1, "agility": "quick" } },
              { "type": "test_item", "id": "dangling", "spells": ["zap", "no_such_spell"] },
              { "type": "no_such_type_lines", "id": "x" },
              { "type": "test_item", "id": "Bad Id" },
              { "type": "test_item", "id": "orphan", "base": "nothing" },
              { "type": "test_spell", "id": "zap" }
            ]
            """);
        fx.Mount("lines", "sandbox");
        using var capture = new CaptureSink();
        var store = fx.Load();

        var messages = MessagesAbout(capture, "lines");
        Assert.Contains("lines:data/items.json:2:40: test_item sandbox:typo: unknown field 'vaule'; did you mean 'value'? (record skipped)", messages);
        Assert.Contains("lines:data/items.json:4:5: test_item sandbox:bad: The JSON value could not be converted to System.Int32. (record skipped)", messages);
        Assert.Contains("lines:data/items.json:6:31: test_item sandbox:deep: The JSON value could not be converted to System.Int32. (record skipped)", messages);
        Assert.Contains("lines:data/items.json:7:62: test_item sandbox:dangling: 'spells' refers to sandbox:no_such_spell, which doesn't exist", messages);
        Assert.Contains("lines:data/items.json:8:5: unknown record type 'no_such_type_lines' (no module registered it); skipped", messages);
        Assert.Contains("lines:data/items.json:9:26: Invalid record id 'Bad Id' (expected [namespace:]name, lower case letters, digits, _ and .)", messages);
        Assert.Contains("lines:data/items.json:10:42: test_item sandbox:orphan: base sandbox:nothing not found (or broken)", messages);
        Assert.Equal(6, store.ErrorCount);
    }

    [Fact]
    public void InvalidJson_SaysLineAndColumn()
    {
        var fx = new MountFixture();
        fx.Write("broken", "data/a.json", """
            [
              { "type": "test_item", "id": "a" },
              { "type": "test_item", "id": oops }
            ]
            """);
        fx.Mount("broken", "sandbox");
        using var capture = new CaptureSink();
        var store = fx.Load();

        var message = Assert.Single(MessagesAbout(capture, "broken"));
        Assert.StartsWith("broken:data/a.json:3:32: invalid JSON: ", message);
        Assert.Equal(1, store.ErrorCount);
    }

    // A patch that forgets "patch": true is named where it is written, and so is what it collides with.
    [Fact]
    public void Redefinition_NamesBothPlaces()
    {
        var fx = new MountFixture();
        fx.Write("redef", "data/a.json", """
            [{ "type": "test_item", "id": "sword" }]
            """);
        fx.Write("redef", "data/b.json", """
            // a comment, which is not a record
            [{ "type": "test_item", "id": "axe" },
             { "type": "test_item", "id": "sword", "value": 2 }]
            """);
        fx.Mount("redef", "sandbox");
        using var capture = new CaptureSink();
        var store = fx.Load();

        Assert.Contains("redef:data/b.json:3:2: test_item sandbox:sword is already defined at redef:data/a.json:1:2; " +
                        "use \"patch\": true to change it. Treated as a patch.", MessagesAbout(capture, "redef"));
        Assert.Equal(1, store.ErrorCount);
    }

    // A field a patch set is reported where the patch set it, not where the record was first written.
    [Fact]
    public void AnErrorInAPatchedField_PointsAtThePatch()
    {
        var fx = new MountFixture();
        fx.Write("base_mount", "data/items.json", """
            [{ "type": "test_item", "id": "sword", "value": 1 }]
            """);
        fx.Write("patcher", "data/patch.json", """
            [{ "type": "test_item", "id": "sandbox:sword", "patch": true,
               "value": "many" }]
            """);
        fx.Mount("base_mount", "sandbox");
        fx.Mount("patcher", "mymod");
        using var capture = new CaptureSink();
        var store = fx.Load();

        Assert.Contains("patcher:data/patch.json:2:4: test_item sandbox:sword: The JSON value could not be converted to System.Int32. (record skipped)",
                        MessagesAbout(capture, "patcher"));
        Assert.Equal(1, store.ErrorCount);
    }

    // ---- AI tasks --------------------------------------------------------------------------------

    private static RecordStore LoadSchedules(MountFixture fx)
    {
        var store = new RecordStore();
        store.Register<AIScheduleRecord>();
        store.Load(fx.Vfs);
        return store;
    }

    [Fact]
    public void AITasks_AreObjectsWithANamedArgument_OrABareName()
    {
        var fx = new MountFixture();
        fx.Write("tasks", "data/ai.json", """
            [{ "type": "ai_schedule", "id": "s",
               "tasks": [{ "task": "MoveToTarget", "distance": 1.6 }, "FaceTarget", { "task": "Wait" }] }]
            """);
        fx.Mount("tasks", "sandbox");
        var store = LoadSchedules(fx);

        Assert.Equal(0, store.ErrorCount);
        var steps = store.Get<AIScheduleRecord>(new RecordId("sandbox", "s")).Steps();
        Assert.Equal(new[] { new AITaskStep("MoveToTarget", "distance", 1.6f), new AITaskStep("FaceTarget"), new AITaskStep("Wait") }, steps);
    }

    // The old "Wait:1.5" is refused, at the line it is on, with what to write instead.
    [Fact]
    public void AITasks_TheOldColonSyntaxIsAnErrorThatSaysWhatToWrite()
    {
        var fx = new MountFixture();
        fx.Write("oldtasks", "data/ai.json", """
            [{ "type": "ai_schedule", "id": "s",
               "tasks": ["FaceTarget", "MeleeAttack:1.5"] },
             { "type": "ai_schedule", "id": "t",
               "tasks": [{ "task": "Wait", "seconds": 1, "distance": 2 }] },
             { "type": "ai_schedule", "id": "u",
               "tasks": [{ "seconds": 1 }] }]
            """);
        fx.Mount("oldtasks", "sandbox");
        using var capture = new CaptureSink();
        var store = LoadSchedules(fx);

        var messages = MessagesAbout(capture, "oldtasks");
        Assert.Contains("oldtasks:data/ai.json:2:28: ai_schedule sandbox:s: \"MeleeAttack:1.5\" is the old task syntax; " +
                        "write { \"task\": \"MeleeAttack\", \"giveUpAfter\": 1.5 } (record skipped)", messages);
        Assert.Contains("oldtasks:data/ai.json:4:14: ai_schedule sandbox:t: a task takes one argument; this one has 'seconds' and 'distance' (record skipped)", messages);
        Assert.Contains("oldtasks:data/ai.json:6:14: ai_schedule sandbox:u: a task object needs \"task\": \"<name>\" beside 'seconds' (record skipped)", messages);
        Assert.Equal(3, store.ErrorCount);
    }

    // ---- colours ---------------------------------------------------------------------------------

    private static RecordStore LoadDamageTypes(MountFixture fx)
    {
        var store = new RecordStore();
        store.Register<DamageTypeRecord>();
        store.Register<ParticleRecord>();
        store.Load(fx.Vfs);
        return store;
    }

    [Fact]
    public void Colours_ReadAsHex_Arrays_OrPackedNumbers()
    {
        var fx = new MountFixture();
        fx.Write("colours", "data/colours.json", """
            [{ "type": "damage_type", "id": "hex", "colour": "#FFB0A0" },
             { "type": "damage_type", "id": "hex_alpha", "colour": "#ffb0a080" },
             { "type": "damage_type", "id": "array", "colour": [255, 176, 160] },
             { "type": "damage_type", "id": "array_alpha", "colour": [255, 176, 160, 128] },
             { "type": "damage_type", "id": "packed", "colour": 4288721151 },
             { "type": "particle", "id": "fade", "colourStart": "#FF9800", "colourEnd": "#FF980000" }]
            """);
        fx.Mount("colours", "sandbox");
        var store = LoadDamageTypes(fx);

        Assert.Equal(0, store.ErrorCount);
        uint Colour(string id) => store.Get<DamageTypeRecord>(new RecordId("sandbox", id)).Colour;
        // Packed the way the renderer takes it: red in the low byte, alpha in the high one.
        Assert.Equal(0xFFA0B0FFu, Colour("hex"));
        Assert.Equal(0x80A0B0FFu, Colour("hex_alpha"));
        Assert.Equal(0xFFA0B0FFu, Colour("array"));
        Assert.Equal(0x80A0B0FFu, Colour("array_alpha"));
        Assert.Equal(0xFFA0B0FFu, Colour("packed"));
        var fade = store.Get<ParticleRecord>(new RecordId("sandbox", "fade"));
        Assert.Equal(0xFF0098FFu, fade.ColourStart);
        Assert.Equal(0x000098FFu, fade.ColourEnd);
        Assert.Equal("#FFB0A080", ColourJsonConverter.Format(0x80A0B0FFu));
    }

    [Fact]
    public void Colours_ThatAreNotColoursAreErrorsAtTheirLine()
    {
        var fx = new MountFixture();
        fx.Write("badcolours", "data/colours.json", """
            [{ "type": "damage_type", "id": "no_hash", "colour": "FFB0A0" },
             { "type": "damage_type", "id": "fraction", "colour": [1.0, 0.5, 0.5] },
             { "type": "damage_type", "id": "short", "colour": [255, 0] }]
            """);
        fx.Mount("badcolours", "sandbox");
        using var capture = new CaptureSink();
        var store = LoadDamageTypes(fx);

        const string expected = "a colour is \"#RRGGBB\", \"#RRGGBBAA\" or [r, g, b(, a)] with whole numbers 0-255";
        var messages = MessagesAbout(capture, "badcolours");
        Assert.Contains($"badcolours:data/colours.json:1:44: damage_type sandbox:no_hash: 'FFB0A0': {expected} (record skipped)", messages);
        Assert.Contains($"badcolours:data/colours.json:2:45: damage_type sandbox:fraction: {expected}, not 1.0 (record skipped)", messages);
        Assert.Contains($"badcolours:data/colours.json:3:42: damage_type sandbox:short: {expected} (record skipped)", messages);
        Assert.Equal(3, store.ErrorCount);
    }
}
