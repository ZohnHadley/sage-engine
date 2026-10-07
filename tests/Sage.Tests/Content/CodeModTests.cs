#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Code mods (phase 9, issue #396): a mod whose mod.json names "assemblies" has them loaded into a collectible
// load context of its own at Create, sharing the engine's assemblies, before any Init and any world; its
// modules are plugins named for the mod, added after the game's; a mod whose code cannot be used is refused
// and the game still boots. The example is tests/games/code-mod (smiths_guild, built by the solution),
// beside tests/games/mods and its data mod better_blades.
public class CodeModTests : IDisposable
{
    public CodeModTests() { _ = TestEnv.UserRoot; }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sage-codemod-" + Guid.NewGuid().ToString("N"));

    // On Windows a loaded dll can't be deleted until it is unloaded, so what's left stays in temp.
    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static string Games => Path.Combine(TestEnv.FolderAbove("tests"), "tests", "games");
    private static string ModsGame => Path.Combine(Games, "mods");
    private static string BetterBlades => Path.Combine(ModsGame, "mods", "better_blades");
    private static string SmithsGuild => Path.Combine(Games, "code-mod");

    private static readonly RecordId Trader = new("village", "trader");

    private static HeadlessApp BootVillage(string saves, params string[] mods)
    {
        var app = HeadlessApp.ForGame(ModsGame).WithEngineContent().WithMods(mods).Boot();
        Assert.Equal(0, app.Records.ErrorCount);
        app.Engine.Saves.Root = saves;
        NpcLocomotionTests.Step(app.World, 3);
        return app;
    }

    private static List<string> Run(HeadlessApp app, string command)
    {
        using var log = new CaptureSink();
        Assert.True(app.CVars.Execute(command));
        return log.Entries.Where(e => e.Category == LogCat.Console).Select(e => e.Message).ToList();
    }

    private static string TraderName(HeadlessApp app) => app.Records.Get<PrefabRecord>(Trader).Name;

    // The issue's "done": the example code mod and a data mod load together; the code mod's module runs after
    // the game's plugins and the kit, registers under its own plugin id, declares a record type its data uses;
    // both mods rename the trader, and `mod_conflicts` reports it per key. Each is listed, the code mod flagged.
    [Fact]
    public void TheExampleCodeModAndADataModLoad_AndModConflictsReportsTheirConflict()
    {
        using var app = BootVillage(TestEnv.NewTempDir(), BetterBlades, SmithsGuild);

        Assert.Equal(new[] { "better_blades", "smiths_guild" }, app.Engine.Mods.Active.Select(m => m.Id));
        Assert.Empty(app.Engine.Mods.Refused);

        // Its module: the plugin `smiths_guild`, after every other module (the kit and the items plugin it
        // requires among them), and what it registered is its own.
        var plugins = app.Engine.Modules.Modules.Select(m => app.Engine.Modules.Plugin(m).Id).ToList();
        Assert.Equal("smiths_guild", plugins[^1]);
        Assert.True(plugins.IndexOf("sage.kits.rpg") < plugins.IndexOf("smiths_guild"));
        Assert.True(plugins.IndexOf("sage.gameplay.items") < plugins.IndexOf("smiths_guild"));
        Assert.Contains(app.Engine.Registrations.By("smiths_guild"), r => r.Name == "guild_commission");
        Assert.Equal("smiths_guild", app.Engine.Registrations.OwnerOf("command", "guild_commission"));

        // In a collectible context of its own, sharing the engine: its IModule is the engine's.
        var assembly = Assert.Single(app.Engine.ModManager.CodeOf("smiths_guild"));
        var context = AssemblyLoadContext.GetLoadContext(assembly)!;
        Assert.True(context.IsCollectible);
        Assert.NotSame(AssemblyLoadContext.Default, context);
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(typeof(IModule).Assembly));
        Assert.Empty(app.Engine.ModManager.CodeOf("better_blades"));

        // Its own record type, and its data in it.
        Assert.True(app.Records.Exists("guild_order", new RecordId("smiths_guild", "falchion_order")));
        Assert.Contains("smiths_guild:lantern_order", Assert.Single(Run(app, "guild_commission 0")));

        // The conflict: both renamed the trader, and the later one won.
        Assert.Equal("The Smiths' Guild forge", TraderName(app));
        var conflicts = Run(app, "mod_conflicts");
        Assert.Contains(conflicts, l => l.Contains("prefab village:trader name: better_blades, smiths_guild; smiths_guild won"));

        // Flagged wherever mods are listed.
        var list = Run(app, "mod_list");
        Assert.Contains(list, l => l.Contains("smiths_guild 1.0.0") && l.Contains(ModManager.ContainsCodeFlag));
        Assert.DoesNotContain(list, l => l.Contains("better_blades 1.0.0") && l.Contains(ModManager.ContainsCodeFlag));
        Assert.True(ModManager.ContainsCode(app.Engine.Mods.Active[1]));
    }

    // A save made with the code mod loads without it (its ledger kept as it was, an unknown resource, and the
    // trader as the data mod names him), and a save made then loads with the mod back: the ledger is the mod's
    // again, as it was.
    [Fact]
    public void SavesSurviveRemovingTheCodeModAndPuttingItBack()
    {
        string saves = TestEnv.NewTempDir();
        using (var with = BootVillage(saves, BetterBlades, SmithsGuild))
        {
            Assert.Contains("The guild has 3 commission(s)", Assert.Single(Run(with, "guild_commission 3")));
            Assert.True(with.Engine.Saves.Save("guild"));
        }

        using (var without = BootVillage(saves, BetterBlades))
        {
            Assert.Equal(new[] { "better_blades" }, without.Engine.Mods.Active.Select(m => m.Id));
            var slot = Assert.Single(without.Engine.Saves.Slots);
            Assert.Contains("mod 'smiths_guild' 1.0.0 is not active", slot.Mismatches);
            Assert.True(without.Engine.Saves.Load("guild"));
            NpcLocomotionTests.Step(without.World, 2);
            Assert.Equal("Hilde the bladesmith", TraderName(without));
            Assert.False(without.CVars.Execute("guild_commission"));       // no such command without the mod
            Assert.True(without.Engine.Saves.Save("guild_again"));
        }

        using var back = BootVillage(saves, BetterBlades, SmithsGuild);
        Assert.True(back.Engine.Saves.Load("guild_again"));
        NpcLocomotionTests.Step(back.World, 2);
        Assert.Contains("The guild has 3 commission(s)", Assert.Single(Run(back, "guild_commission 0")));
    }

    // The mod's saved resource at version 1 (`Orders`) is brought to version 2 (`Commissions`) by the mod's own
    // [Upgrade] method, found in its collectible assembly.
    [Fact]
    public void ACodeModsOldSaveIsUpgradedByItsOwnUpgrader()
    {
        string saves = TestEnv.NewTempDir();
        using var app = BootVillage(saves, BetterBlades, SmithsGuild);
        Assert.True(app.CVars.Execute("save_compress 0"));
        Assert.True(app.Engine.Saves.Save("old"));
        string file = Directory.GetFiles(saves, "world_*.json", SearchOption.AllDirectories).Single();
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        root["resources"]!["guild_ledger"] = System.Text.Json.Nodes.JsonNode.Parse("""{ "version": 1, "data": { "Orders": 7 } }""");
        File.WriteAllText(file, root.ToJsonString());

        Assert.True(app.Engine.Saves.Load("old"));
        Assert.Contains("The guild has 7 commission(s)", Assert.Single(Run(app, "guild_commission 0")));
    }

    // `sage validate` boots the game with the code mod as the host would: its Init and its world run, and its
    // data is checked; `sage mods` lists it flagged.
    [Fact]
    public void ValidateRunsTheCodeMod()
    {
        var report = ContentValidation.Run(new ValidateOptions
        {
            GameDirectory = ModsGame,
            EngineContentDirectory = Path.Combine(TestEnv.FolderAbove("engine_content"), "engine_content"),
            AvailablePlugins = BasePlugins.All(),
            GameMods = true,
            Mods = new[] { SmithsGuild },
        });
        Assert.True(report.Ok, string.Join("\n", report.Errors));
        Assert.Equal(new[] { "better_blades", "rival_trade", "smiths_guild" }, report.Mods.Active.Select(m => m.Id));
        Assert.Empty(report.Mods.Refused);
        Assert.Equal(2, report.Conflicts);                                   // the trader's name; the falchion's texture
        Assert.Contains(report.ReportLines, l => l.Contains("prefab village:trader name: better_blades, rival_trade, smiths_guild; smiths_guild won"));
        Assert.Contains(report.ReportLines, l => l.Contains("added guild_order: smiths_guild:falchion_order"));
        Assert.Contains(report.ModLines, l => l.Contains("smiths_guild") && l.Contains(ModManager.ContainsCodeFlag));
    }

    // ---- compiled code mods: what is refused, and why ----

    // A data-only game, `modtest`, with one prefab.
    private string NewGame()
    {
        string dir = Path.Combine(_root, "game");
        Directory.CreateDirectory(Path.Combine(dir, "content", "data"));
        File.WriteAllText(Path.Combine(dir, "game.json"), """{ "name": "Code mod test", "id": "modtest", "mounts": ["content"], "version": "1.0.0" }""");
        File.WriteAllText(Path.Combine(dir, "content", "data", "rock.json"), """[ { "type": "prefab", "id": "rock", "name": "rock" } ]""");
        return dir;
    }

    private static readonly Lazy<MetadataReference[]> References = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .Concat(new[] { EngineAssemblies.Core, EngineAssemblies.Simulation }.Select(a => MetadataReference.CreateFromFile(a.Location)))
            .Append(MetadataReference.CreateFromFile(typeof(IComponent).GetInterfaces().Single().Assembly.Location))
            .GroupBy(r => r.Display).Select(g => g.First()).ToArray());

    // A code mod `id` in `<root>/mods/<id>`: `source` compiled to bin/<Name>.dll (a name of its own per test),
    // named in mod.json, and a data file that renames the game's rock.
    private string CodeMod(string id, string source, string extra = "", bool build = true)
    {
        string dir = Path.Combine(_root, "mods", id);
        Directory.CreateDirectory(Path.Combine(dir, "data"));
        string name = "Mod" + Guid.NewGuid().ToString("N")[..10];
        File.WriteAllText(Path.Combine(dir, "mod.json"),
            $$"""{ "id": "{{id}}", "version": "1.0.0", "game": "modtest", "assemblies": ["bin/{{name}}.dll"]{{extra}} }""");
        File.WriteAllText(Path.Combine(dir, "data", "rock.json"), $$"""[ { "type": "prefab", "id": "modtest:rock", "patch": true, "name": "{{id}} rock" } ]""");
        if (!build) return dir;
        var compilation = CSharpCompilation.Create(name, new[] { CSharpSyntaxTree.ParseText("using Sage.Core; using Sage.Simulation;\n" + source) },
            References.Value, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        var result = compilation.Emit(Path.Combine(dir, "bin", name + ".dll"));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return dir;
    }

    private static string Module(string plugin, string attributes = "", string body = "") =>
        $$"""
        {{(plugin.Length > 0 ? $"[Plugin(\"{plugin}\", \"1.0.0\")]" : "")}}
        {{attributes}}
        public sealed class TheModule : IModule
        {
            public static int Inits;
            public void Init(ModuleContext ctx) { Inits++; {{body}} }
        }
        """;

    private static string Why(HeadlessApp app, string id) => Assert.Single(app.Engine.Mods.Refused, r => r.Id == id).Reason;

    [Fact]
    public void ACompiledCodeModRunsItsInitUnderItsOwnPluginId_AfterTheGame()
    {
        string mod = CodeMod("smith", Module("smith.forge", body: """ctx.Engine.CVars.RegisterCommand("forge", CVarFlags.None, "", _ => { });"""));
        using var app = HeadlessApp.ForGame(NewGame()).WithMods(mod).Boot();

        Assert.Equal(new[] { "smith" }, app.Engine.Mods.Active.Select(m => m.Id));
        Assert.Equal("smith rock", app.Records.Get<PrefabRecord>(new RecordId("modtest", "rock")).Name);
        Assert.Equal("smith.forge", app.Engine.Registrations.OwnerOf("command", "forge"));
        var type = Assert.Single(app.Engine.ModManager.CodeOf("smith")).GetType("TheModule")!;
        Assert.Equal(1, (int)type.GetField("Inits")!.GetValue(null)!);
    }

    [Theory]
    [InlineData("", "", "has no [Plugin(\"bad\", version)]")]
    [InlineData("other", "", "is the plugin 'other', and a code mod's plugins are named 'bad' or 'bad.<name>'")]
    [InlineData("bad", "[RequiresPlugin(\"sage.nothing\", \">=1.0\")]", "requires the plugin sage.nothing >=1.0, which this game does not load")]
    [InlineData("bad", "[RequiresPlugin(\"sage\", \">=999.0\")]", "needs Sage >=999.0")]
    public void ACodeModWhoseModulesDoNotFitIsRefused_AndTheGameBoots(string plugin, string attributes, string why)
    {
        string bad = CodeMod("bad", Module(plugin, attributes));
        string good = Path.Combine(_root, "mods", "good");
        Directory.CreateDirectory(Path.Combine(good, "data"));
        File.WriteAllText(Path.Combine(good, "mod.json"), """{ "id": "good", "version": "1.0.0", "game": "modtest", "dependencies": { "bad": "*" } }""");
        string other = CodeMod("fine", Module("fine"));

        using var app = HeadlessApp.ForGame(NewGame()).WithMods(bad, good, other).Boot();

        Assert.Contains(why, Why(app, "bad"));
        Assert.Contains("it needs 'bad'", Why(app, "good"));                 // what needs it goes too
        Assert.Equal(new[] { "fine" }, app.Engine.Mods.Active.Select(m => m.Id));
        Assert.Equal("fine rock", app.Records.Get<PrefabRecord>(new RecordId("modtest", "rock")).Name);
        Assert.DoesNotContain(app.Engine.Modules.Modules, m => app.Engine.Modules.Plugin(m).Id is "bad" or "other");
        Assert.Empty(app.Engine.ModManager.CodeOf("bad"));
        Assert.Contains(Run(app, "mod_list"), l => l.Contains("bad:") && l.Contains(why));
    }

    [Fact]
    public void ACodeModWhoseAssemblyIsMissingOrHasNoModuleIsRefused()
    {
        string missing = CodeMod("unbuilt", "", build: false);
        string empty = CodeMod("empty", "public static class Nothing { }");
        using var app = HeadlessApp.ForGame(NewGame()).WithMods(missing, empty).Boot();

        Assert.Empty(app.Engine.Mods.Active);
        Assert.Contains("is not there (build the mod", Why(app, "unbuilt"));
        Assert.Contains("have no public IModule class", Why(app, "empty"));
    }

    // A component type exists only in a schema built after its assembly loaded: in a process whose schema is
    // already built (a second app), such a mod is refused rather than half working.
    [Fact]
    public void ACodeModWithComponentsIsRefusedOnceTheProcessHasBuiltItsSchema()
    {
        _ = EcsSchema.ComponentTypes();
        string mod = CodeMod("parts", Module("parts") + "\npublic struct Heat : IComponent { public float Value; }");
        using var app = HeadlessApp.ForGame(NewGame()).WithMods(mod).Boot();
        Assert.Contains("declares ECS components or tags (Heat)", Why(app, "parts"));
    }

    [Theory]
    [InlineData("\"kind\": \"code\"", "names no \"assemblies\"")]
    public void KindCodeWithNoAssembliesIsRefused(string json, string why)
    {
        string dir = Path.Combine(_root, "mods", "talk");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "mod.json"), $$"""{ "id": "talk", "version": "1.0.0", {{json}} }""");
        using var app = HeadlessApp.ForGame(NewGame()).WithMods(dir).Boot();
        Assert.Contains(why, Why(app, "talk"));
    }

    [Theory]
    [InlineData("\"assemblies\": [\"../elsewhere/X.dll\"]", "inside the mod's folder")]
    [InlineData("\"assemblies\": [\"/abs/X.dll\"]", "inside the mod's folder")]
    [InlineData("\"assemblies\": [\"bin/X.txt\"]", "is not a .dll")]
    [InlineData("\"kind\": \"data\", \"assemblies\": [\"X.dll\"]", "\"kind\" is 'data'")]
    public void AnAssemblyOutsideTheModOrMislabelledIsAManifestError(string json, string why)
    {
        string dir = Path.Combine(_root, "mods", "m");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "mod.json"), $$"""{ "id": "m", "version": "1.0.0", {{json}} }""");
        var ex = Assert.Throws<InvalidDataException>(() => ModManifest.Load(dir));
        Assert.Contains(why, ex.Message);
    }
}

// The mod's load context is collectible and goes when the app is disposed (phase 9, issue #396): nothing the
// engine keeps in a static holds the mod's types — not the save upgraders' cache, the metadata tables or the
// system declarations, which keep a collectible type weakly or not at all. System.Text.Json keeps the member
// accessors it emitted for a type in a cache of its own that lets go of an entry a second after its last use,
// at its next use, so the test waits for that and uses the serializer meanwhile. Forced collections are
// process-wide: run alone.
[Collection(ProcessWideStateCollection.Name)]
public class CodeModUnloadTests
{
    public CodeModUnloadTests() { _ = TestEnv.UserRoot; }

    public sealed class Nudge { public int Value { get; set; } }

    [Fact]
    public void TheCodeModsContextUnloadsWhenTheAppIsDisposed()
    {
        var context = BootUseAndDispose();
        for (int i = 0; i < 40 && context.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            System.Threading.Thread.Sleep(250);
            // A resolver of its own: a cache miss, which is when the serializer drops what it no longer uses.
            _ = System.Text.Json.JsonSerializer.Deserialize<Nudge>("{}", new System.Text.Json.JsonSerializerOptions
            {
                TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
            });
        }
        Assert.False(context.IsAlive, "the mod's load context was not collected after the app was disposed");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference BootUseAndDispose()
    {
        string games = Path.Combine(TestEnv.FolderAbove("tests"), "tests", "games");
        var app = HeadlessApp.ForGame(Path.Combine(games, "mods")).WithEngineContent()
            .WithMods(Path.Combine(games, "mods", "mods", "better_blades"), Path.Combine(games, "code-mod")).Boot();
        var assembly = Assert.Single(app.Engine.ModManager.CodeOf("smiths_guild"));
        Assert.Single(Upgraders.Of(assembly.GetType("SmithsGuild.GuildLedger")!));   // cached, weakly
        Assert.True(app.CVars.Execute("guild_commission 2"));
        app.Engine.Saves.Root = TestEnv.NewTempDir();
        Assert.True(app.Engine.Saves.Save("ledger"));                                  // the ledger serialized
        Assert.True(app.Engine.Saves.Load("ledger"));                                  // and read back, upgraders and all
        var weak = new WeakReference(AssemblyLoadContext.GetLoadContext(assembly));
        app.Dispose();
        return weak;
    }
}
