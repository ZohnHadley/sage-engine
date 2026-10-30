#nullable enable
using System.Linq;
using System.Text.Json.Nodes;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// A game overriding another namespace's prefab (#649): its bare ids are its own where it has that record, and
// the prefab's otherwise. Only a field that is a record id is touched.
public class PrefabOverrideNamespaceTests
{
    private const string Engine = """
        [{ "type": "state_machine", "id": "shared", "initial": "a", "states": { "a": {} } },
         { "type": "state_machine", "id": "engine_only", "initial": "a", "states": { "a": {} } },
         { "type": "prefab", "id": "walker", "name": "walker", "parts": { "state_machine": { "machine": "engine_only" } } }]
        """;

    private const string Game = """
        [{ "type": "state_machine", "id": "shared", "initial": "a", "states": { "a": {} } },
         { "type": "prefab", "id": "own", "name": "own", "parts": { "state_machine": { "machine": "shared" } } },
         { "type": "scene", "id": "yard", "place": [
             { "prefab": "sage:walker", "overrides": { "parts": { "state_machine": { "machine": "shared" } } } },
             { "prefab": "sage:walker", "overrides": { "parts": { "state_machine": { "machine": "engine_only" } } } },
             { "prefab": "sage:walker", "overrides": { "parts": { "state_machine": { "machine": "sage:shared" } } } },
             { "prefab": "own", "overrides": { "parts": { "state_machine": { "machine": "shared" } } } }
         ] }]
        """;

    [Xunit.Fact]
    public void ABareIdInAnOverrideIsTheWritersWhereItHasThatRecord()
    {
        var fixture = new MountFixture();
        fixture.Write("game", "data/game.json", Game);
        fixture.Mount("game", "game");
        using var app = HeadlessApp.Gameplay().File("data/engine.json", Engine).Mount(fixture).Build();
        Assert.Equal(0, app.Records.ErrorCount);

        var place = app.Records.Get<SceneRecord>(new RecordId("game", "yard")).Place;
        string Machine(int i) => place[i].Overrides!.Parts!["state_machine"]!["machine"]!.GetValue<string>();

        Assert.Equal("game:shared", Machine(0));      // the writer has one, so it is that one
        Assert.Equal("engine_only", Machine(1));      // it has not, so it is still the prefab's
        Assert.Equal("sage:shared", Machine(2));      // said in full, it stays
        Assert.Equal("shared", Machine(3));           // its own prefab: one namespace, nothing to decide
    }
}
