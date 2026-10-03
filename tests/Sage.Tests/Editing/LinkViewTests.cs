#nullable enable
using System;
using System.Linq;
using System.Numerics;
using Sage.Editing;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The editor's link view (issue #276): which wires leave and reach the selected entity, with every target
// they resolve to now — a group fanned out to its members, a missing name unresolved. The client draws
// these as lines over the viewport (yellow out, green in, red for nothing); this is what it draws.
public class LinkViewTests
{
    public LinkViewTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
        [
          { "type": "prefab", "id": "plate", "components": { "transform": {} } },
          { "type": "prefab", "id": "lamp", "components": { "transform": {}, "sage:io_group": { "names": "lamps" } } },
          {
            "type": "placements",
            "id": "hall",
            "place": [
              { "prefab": "plate", "at": [0, 0, 0], "name": "plate", "outputs": [
                  { "output": "OnStartTouch", "target": "@lamps", "input": "Kill" },
                  { "output": "OnEndTouch", "target": "ghost", "input": "Kill" } ] },
              { "prefab": "lamp", "at": [2, 0, 0], "name": "lamp_1" },
              { "prefab": "lamp", "at": [4, 0, 0], "name": "lamp_2" },
              { "prefab": "lamp", "at": [6, 0, 0], "name": "lamp_3", "outputs": [
                  { "output": "OnUser1", "target": "lamp_1", "input": "Kill" } ] }
            ]
          }
        ]
        """;

    private static (HeadlessApp App, EditDocument Document) Open()
    {
        var fixture = new MountFixture();
        fixture.Write("game", "data/hall.json", Records);
        fixture.Mount("game", "sandbox");
        var app = HeadlessApp.Gameplay().Mount(fixture).Build();
        var document = new EditDocument(app.Engine.CreateWorld("edit"));
        EditorCommands.Register(app.Engine.CVars, () => document);
        Assert.True(app.Engine.CVars.Execute("doc_open hall"));
        return (app, document);
    }

    [Fact]
    public void TheLinkViewFansAGroupWireOutToEveryMember_AndShowsWhatReachesAnEntity()
    {
        var (app, document) = Open();
        using var _ = app;
        var plate = document.Find("plate")!;
        var lamp1 = document.Find("lamp_1")!;

        // Out of the plate: the group wire reaches three lamps where they stand; the wire to a name nobody
        // has reaches nothing, and the view says so.
        var view = WiringModel.Links(document, plate);
        Assert.Equal(2, view.Outgoing.Count);
        Assert.Empty(view.Incoming);
        var group = view.Outgoing[0];
        Assert.Equal(3, group.Targets.Count);
        Assert.Null(group.Problem);
        Assert.Equal(new[] { 2f, 4f, 6f }, group.To.Select(p => p.X).OrderBy(x => x));
        Assert.Equal(new Vector3(0, 0, 0), group.From);
        Assert.False(view.Outgoing[1].Resolved);
        Assert.NotNull(view.Outgoing[1].Problem);

        // Into lamp 1: the plate's group wire and lamp 3's wire by name.
        var into = WiringModel.Links(document, lamp1);
        Assert.Equal(2, into.Incoming.Count);
        Assert.Contains(into.Incoming, l => l.Source == document.EntityOf(plate) && l.Wire.Target == "@lamps");
        Assert.Contains(into.Incoming, l => l.Source == document.EntityOf(document.Find("lamp_3")!) && l.Wire.Target == "lamp_1");

        // The lines the viewport draws: one per lamp for the group, one unresolved for the ghost, one lamp to lamp.
        var lines = WiringModel.Lines(document);
        Assert.Equal(5, lines.Count);
        Assert.Equal(3, lines.Count(l => l.Wire.Target == "@lamps" && l.To != null));
        Assert.Single(lines, l => l.To == null);
    }

    [Fact]
    public void AGroupCanBeWiredFromTheConsole_AndAnEmptyOneIsRefusedWithAReason()
    {
        var (app, document) = Open();
        using var _ = app;
        var console = app.Engine.CVars;
        var lamp3 = document.Find("lamp_3")!;

        Assert.True(console.Execute("ed_wire lamp_3 OnUser2 @lamps Kill"));
        Assert.Equal(2, lamp3.Outputs.Count);
        Assert.Equal("@lamps", lamp3.Outputs[1].Target);

        Assert.NotNull(Wiring.Check(document, "@nobody_here", "Kill"));
        Assert.NotNull(Wiring.Check(document, "@tag:sage:no_such_tag", "Kill"));
        Assert.Null(Wiring.Check(document, "@class:lamp", "Kill"));
        Assert.Equal(3, Wiring.TargetsOf(document, document.EntityOf(lamp3), "@class:lamp").Count);
    }

    [Fact]
    public void ThePlayWorldsHistoryIsWhatTheViewShowsAsRecent()
    {
        using var app = HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule()).Boot("io");
        var world = app.World;
        var lamp = world.Create(Transform.At(Vector3.Zero), "lamp");
        lamp.Name = "lamp";
        world.IO().FireInput(lamp, "Say", "hello");
        world.RunFixed(1f / 60f);

        var recent = WiringModel.Recent(world, lamp);
        Assert.Equal("Say", Assert.Single(recent).Input);
        Assert.Equal(IOOutcome.Delivered, recent[0].Outcome);
    }
}
