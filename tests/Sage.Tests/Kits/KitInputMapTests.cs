#nullable enable
using System.Linq;
using System.Text.Json.Nodes;
using Sage.Kits.Rpg;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The RPG kit's default input maps (issue #354, docs/design/13 §3): a game that only names the kit (the
// weapons exit game has no C#) gets the keys of its screens from the kit's content, and the registry dump
// carries what a layer above the simulation adds (the client's screens by id).
public class KitInputMapTests
{
    public KitInputMapTests() { _ = TestEnv.UserRoot; }

    private static string Weapons => System.IO.Path.Combine(TestEnv.FolderAbove("Sage.sln"), "tests", "games", "weapons");

    // The client's input_map record type, which a headless host without the client plugin does not have.
    private static HeadlessApp Boot(string? patch = null)
    {
        var builder = HeadlessApp.ForGame(Weapons).WithEngineContent();
        if (patch != null) builder.File("data/input.json", patch, "weapons");
        return builder.OnRegistered(app => app.Records.Register<InputMapRecord>()).Boot();
    }

    private static string[] Fires(HeadlessApp app, InputContext context, string key) =>
        app.Engine.Rebinds.ActionsFor(context, new InputBinding { Key = key }).ToArray();

    [Fact]
    public void ABareKitGameHasTheKitsDefaultKeysInBothContexts()
    {
        using var app = Boot();
        Assert.Equal(0, app.Records.ErrorCount);
        foreach (var context in new[] { InputContext.UI, InputContext.Gameplay })
        {
            Assert.Equal(new[] { "Spellbook" }, Fires(app, context, "B"));
            Assert.Equal(new[] { "Spellmaker" }, Fires(app, context, "M"));
            Assert.Equal(new[] { "Journal" }, Fires(app, context, "J"));
            Assert.Equal(new[] { "Rest" }, Fires(app, context, "T"));
        }
        Assert.Equal(RpgKitModule.Id, app.Engine.Registrations.OwnerOf("input action", "Journal"));
    }

    [Fact]
    public void TheKitsRestKeyOpensItsScreenWithoutAGameBindingIt()
    {
        using var app = Boot();
#pragma warning disable SAGE0125   // widget screens are Phase 4c's experimental UI
        var stack = app.World.Resources.Get<UiScreenStack>();
        Assert.Contains(app.Engine.Actions.Get("Rest"), stack.OpenActions);
#pragma warning restore SAGE0125
    }

    [Fact]
    public void ADefaultMapIsPatchedByAGameLikeAnyRecord()
    {
        using var app = Boot("""[ { "type": "input_map", "id": "rpg:ui", "patch": true, "actions": { "Journal": [ { "key": "K" } ] } } ]""");
        Assert.Equal(0, app.Records.ErrorCount);
        Assert.Contains("Journal", Fires(app, InputContext.UI, "K"));
    }

    [Fact]
    public void ALayerAboveTheSimulationAddsASectionToTheRegistryDump()
    {
        using var app = HeadlessApp.Bare()
            .OnRegistered(a => a.Engine.DumpSections.Add("screens", () => new JsonArray(new JsonObject { ["id"] = "journal" })))
            .Build();
        var dump = JsonNode.Parse(RegistryDump.Build(app.Engine))!.AsObject();
        Assert.Equal("journal", (string?)dump["screens"]![0]!["id"]);
        Assert.Throws<System.InvalidOperationException>(() => app.Engine.DumpSections.Add("screens", () => null));
    }
}
