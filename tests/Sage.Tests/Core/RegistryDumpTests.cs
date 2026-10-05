#nullable enable
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The registry dump (docs/REDESIGN.md §4.8, issue #18): everything an app registered, with who
// registered it and the declarations' fields, as JSON. tools/check_docs.py reads it instead of
// scanning the source with regular expressions.
public class RegistryDumpTests
{
    public RegistryDumpTests() { _ = TestEnv.UserRoot; }

    private static string SandboxDirectory => Path.Combine(TestEnv.FolderAbove("Sage.sln"), "games", "Sandbox");

    [Fact]
    public void TheRegistryDumpListsEverythingRegisteredWithItsOwner()
    {
        using var app = HeadlessApp.ForGame(SandboxDirectory, new Sandbox.SandboxModule()).Boot();
        var dump = JsonNode.Parse(RegistryDump.Build(app.Engine))!.AsObject();

        Assert.Equal(RegistryDump.Format, (int)dump["format"]!);
        Assert.Equal("sandbox", (string?)dump["game"]);

        JsonObject Entry(string section, string name, string key = "name") =>
            dump[section]!.AsArray().Select(n => n!.AsObject()).Single(o => (string?)o[key] == name);

        Assert.Equal("sage.gameplay.items", (string?)Entry("commands", "give")["owner"]);
        Assert.Equal("sage.gameplay.items", (string?)Entry("cvars", "g_interact_range")["owner"]);
        Assert.Equal(RegistrationOwners.Core, (string?)Entry("recordTypes", "scene")["owner"]);   // the engine's (#29)
        Assert.Equal("sage.gameplay.movers", (string?)Entry("entityInputs", "Open")["owner"]);
        Assert.Equal("sage.gameplay.items", (string?)Entry("entityOutputs", "OnUse")["owner"]);
        Assert.Equal("sage.gameplay.items", (string?)Entry("inputActions", "Use")["owner"]);
        // Game events are found by their [GameEvent] attribute, a generic one as C# writes it (issue #298):
        // tools/check_docs.py checks "the `Used` event" in a document against these.
        Assert.Equal("Sage.Gameplay", (string?)Entry("gameEvents", "Used")["assembly"]);
        Assert.Equal("Sage.Simulation", (string?)Entry("gameEvents", "TriggerEntered")["assembly"]);
        Assert.Equal("Sage.Simulation.Added`1", (string?)Entry("gameEvents", "Added<T>")["clrType"]);
        Assert.DoesNotContain(dump["gameEvents"]!.AsArray(), e => (string?)e!["name"] == "ClipEvent");   // a struct, not an event
        Assert.Equal("Sage.Simulation", (string?)Entry("components", "sage:point_light", "id")["assembly"]);
        Assert.Contains(dump["tags"]!.AsArray(), t => (string?)t!["id"] == "sandbox:faces_camera");
        Assert.Contains(dump["savedResources"]!.AsArray(), r => (string?)r!["id"] == "journal");

        // A system is declared by an assembly and added per world, by a plugin.
        var hop = Entry("systems", "sandbox.hop", "id");
        Assert.Equal("Gameplay", (string?)hop["phase"]);
        Assert.Equal("main", (string?)hop["added"]![0]!["world"]);

        // Declarations carry their fields from the metadata table.
        var light = Entry("prefabParts", "light", "id");
        Assert.Equal("sage.gameplay.lights", (string?)light["owner"]);
        Assert.Equal("generated", (string?)light["metadata"]);
        var range = light["fields"]!.AsArray().Single(f => (string?)f!["name"] == "range")!;
        Assert.Equal("Number", (string?)range["kind"]);
        Assert.Equal(8.0, (double)range["default"]!);
        Assert.Equal(0.0, (double)range["min"]!);
        Assert.Equal("m", (string?)range["unit"]);
        var stack = Entry("prefabParts", "inventory", "id")["fields"]!.AsArray().Single(f => (string?)f!["name"] == "items")!["item"]!;
        Assert.Equal("item", (string?)stack["fields"]![0]!["recordType"]);
    }

    // What issue #18 is for: a name is in the dump because running code registered it, however that
    // code is spelled. The regexes this replaces missed a registration the moment it was written a new
    // way — `[Record("x", Plugin = …)]` took the record types from 25 to 1 on 2026-09-28.
    [Fact]
    public void ANameRegisteredAnyWayAtAllIsInTheDump()
    {
        string path = Path.Combine(TestEnv.NewTempDir(), "out", "registry.json");
        using var app = HeadlessApp.Bare().OnRegistered(a =>
        {
            // Built at run time and passed through a helper: no call shape a regex could match.
            string Named(string stem) => string.Concat("dump", "_", stem);
            void Command(string name) => a.CVars.RegisterCommand(name, CVarFlags.None, "made up", _ => { });
            Command(Named("probe"));
            a.CVars.Register(Named("volume"), 0.5f, CVarFlags.None, "made up");
            a.Records.Register<DumpProbeRecord>();
            a.Engine.Inputs.Register(Named("Input"), (World w, in IOContext io) => { });
        }).Build();

        RegistryDump.Write(app.Engine, path);
        var dump = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        Assert.Contains(dump["commands"]!.AsArray(), c => (string?)c!["name"] == "dump_probe" && (string?)c["owner"] == "host");
        Assert.Contains(dump["cvars"]!.AsArray(), c => (string?)c!["name"] == "dump_volume");
        Assert.Contains(dump["entityInputs"]!.AsArray(), c => (string?)c!["name"] == "dump_Input");
        var record = dump["recordTypes"]!.AsArray().Single(r => (string?)r!["name"] == "dump_probe_record")!;
        Assert.Equal("reflection", (string?)record["metadata"]);   // this assembly has no generated table
        Assert.Equal("seconds", (string?)record["fields"]![0]!["name"]);
        Assert.Equal("s", (string?)record["fields"]![0]!["unit"]);
    }
}

[Record("dump_probe_record")]
public sealed class DumpProbeRecord
{
    [Property(Unit = "s")] public float Seconds = 2f;
}
