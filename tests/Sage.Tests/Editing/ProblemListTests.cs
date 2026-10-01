#nullable enable
using System.Collections.Generic;
using System.Linq;
using Sage.Editing;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The problems panel (issue #227, docs/design/15 §10c): what the loader and the content report found, and
// what is wrong with the open document, by file; a row selects its placement or hands its record on.
public class ProblemListTests
{
    public ProblemListTests() { _ = TestEnv.UserRoot; }

    private const string Yard = """
        [
          { "type": "prefab", "id": "post", "components": { "transform": {} } },
          { "type": "placements", "id": "yard", "place": [
            { "prefab": "post", "at": [0, 0, 0], "name": "post", "id": "post_1",
              "outputs": [ { "output": "OnUse", "target": "gate", "input": "Open" } ] },
            { "prefab": "ghost", "at": [2, 0, 0], "name": "stray", "id": "stray_1" }
          ] }
        ]
        """;

    private static (Engine Engine, EditDocument Document, MountFixture Fixture) Open(string records = Yard, bool mods = false)
    {
        var fixture = new MountFixture();
        fixture.Write("game", "data/yard.json", records);
        fixture.Mount("game", "sandbox");
        if (mods)
        {
            fixture.Write("mods/one", "data/patch.json", """[{ "type": "prefab", "id": "sandbox:post", "patch": true, "name": "One post" }]""");
            fixture.Write("mods/two", "data/patch.json", """[{ "type": "prefab", "id": "sandbox:post", "patch": true, "name": "Two post" }]""");
            fixture.Mount("mods/one", "one");
            fixture.Mount("mods/two", "two");
        }
        var engine = HeadlessApp.Bare().With(new PhysicsModule()).Mount(fixture).Build().Engine;
        var document = new EditDocument(engine.CreateWorld("edit"));
        EditorCommands.Register(engine.CVars, () => document);
        ProblemCommands.Register(engine.CVars, engine, () => document);
        document.Open(new RecordId("sandbox", "yard"));
        return (engine, document, fixture);
    }

    [Fact]
    public void AMissingPrefabAndADanglingWireAreListedWithThePlacements_AndFixingThemClears()
    {
        var (engine, document, _) = Open();
        using (engine)
        using (var list = new ProblemList(engine, document))
        {
            var stray = document.Find("stray")!;
            var post = document.Find("post")!;
            var prefab = Assert.Single(list.Problems, p => p.Message.Contains("prefab sandbox:ghost"));
            Assert.Equal(ProblemSeverity.Error, prefab.Severity);
            Assert.Equal("stray_1", prefab.PlacementId);
            Assert.Same(stray, prefab.Placement);
            var wire = Assert.Single(list.Problems, p => p.Message.Contains("no placement called 'gate'"));
            Assert.Equal(ProblemSeverity.Warning, wire.Severity);
            Assert.Equal("post_1", wire.PlacementId);
            Assert.Same(post, wire.Placement);
            Assert.Equal((1, 1), (list.Errors, list.Warnings));
            Assert.Equal("1 error, 1 warning", list.Summary);
            Assert.Equal("game:data/yard.json", Assert.Single(list.Groups).File);   // both are the document's file

            int changes = 0;
            list.Changed += () => changes++;
            document.Execute(new SetOutputs(document, post, new List<Connection>()));
            Assert.Equal((1, 0), (list.Errors, list.Warnings));
            Assert.True(changes > 0);
            document.Execute(new RemovePlacement(document, stray));
            Assert.Empty(list.Problems);
            Assert.Equal("no problems", list.Summary);
            document.Undo();
            Assert.Equal(1, list.Errors);   // undo brings the problem back
        }
    }

    [Fact]
    public void DuplicateNamesAndIdsAreListed()
    {
        const string records = """
            [
              { "type": "prefab", "id": "post", "components": { "transform": {} } },
              { "type": "placements", "id": "yard", "place": [
                { "prefab": "post", "name": "a", "id": "one" },
                { "prefab": "post", "name": "a", "id": "one" }
              ] }
            ]
            """;
        var (engine, document, _) = Open(records);
        using (engine)
        using (var list = new ProblemList(engine, document))
        {
            Assert.Equal(2, list.Problems.Count(p => p.Message.Contains("same name")));
            Assert.Equal(2, list.Problems.Count(p => p.Message.Contains("used twice")));
        }
    }

    [Fact]
    public void ARecordFileWithAnErrorShowsWithItsFileAndLine_AndARowOpensTheRecord()
    {
        const string records = """
            [
              { "type": "prefab", "id": "post", "components": { "transform": {} } },
              { "type": "placements", "id": "broken", "place": [ { "prefab": "post", "at": "nowhere" } ] }
            ]
            """;
        var (engine, document, _) = Open(records);
        using (engine)
        using (var list = new ProblemList(engine, document))
        {
            var problem = Assert.Single(list.Problems, p => p.Record == new RecordId("sandbox", "broken"));
            Assert.Equal(ProblemSeverity.Error, problem.Severity);
            Assert.Equal("game:data/yard.json", problem.File);
            Assert.Equal(3, problem.Line);
            Assert.Contains(list.Groups, g => g.File == "game:data/yard.json" && g.Problems.Contains(problem));

            RecordId opened = default;
            Assert.True(ProblemList.Activate(problem, null, id => opened = id));
            Assert.Equal(new RecordId("sandbox", "broken"), opened);
        }
    }

    [Fact]
    public void AModConflictShowsFromTheContentReport()
    {
        var (engine, document, _) = Open(mods: true);
        using (engine)
        using (var list = new ProblemList(engine, document))
        {
            var conflict = Assert.Single(list.Problems, p => p.Message.StartsWith("conflict:"));
            Assert.Equal(ProblemSeverity.Warning, conflict.Severity);
            Assert.Equal("mods/two", conflict.File);
            Assert.Equal(new RecordId("sandbox", "post"), conflict.Record);
            Assert.Contains("one, two; two won", conflict.Message);
        }
    }

    [Fact]
    public void AReloadRefreshesAndARowSelectsItsPlacement()
    {
        var (engine, document, fixture) = Open();
        using (engine)
        using (var list = new ProblemList(engine, document))
        {
            var selection = new EditorSelection(document);
            var row = list.Problems.Single(p => p.PlacementId == "stray_1");
            Assert.True(ProblemList.Activate(row, selection, null));
            Assert.Same(document.Find("stray"), selection.Placement);

            int before = list.Problems.Count;
            fixture.Write("game", "data/more.json", """[{ "type": "nonsense", "id": "x" }]""");
            engine.Records.Reload();
            Assert.True(list.Problems.Count > before);
            Assert.Contains(list.Problems, p => p.File == "game:data/more.json" && p.Line == 1);
        }
    }

    [Fact]
    public void TheConsolePrintsTheProblemsByFile()
    {
        var (engine, document, _) = Open();
        using (engine)
        using (var log = new CaptureSink())
        {
            Assert.True(engine.CVars.Execute("ed_problems"));
            var lines = log.Entries.Select(e => e.Message).ToList();
            Assert.Contains("1 error, 1 warning:", lines);
            Assert.Contains(lines, l => l.StartsWith("  error: placement 'stray'"));
            Assert.Contains(lines, l => l.StartsWith("  warning: placement 'post': wire OnUse -> gate.Open"));
        }
    }
}
