#nullable enable
using System.Collections.Generic;
using System.Numerics;
using Sage.Editing;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// What the editor's panels decide, without ImGui (issue #371): `ed_panel`, which brings every panel to the
// front in turn so CI's `-edit` smoke draws each one in the real host, and the status bar's line. The
// drawing itself is the smoke run's (tools/editor_smoke.sh).
public class EditorPanelModelTests
{
    public EditorPanelModelTests() { _ = TestEnv.UserRoot; }

    private static List<string?> Frames(PanelTour tour, int count)
    {
        var titles = new List<string?>();
        for (int i = 0; i < count; i++) titles.Add(tour.Next());
        return titles;
    }

    // `ed_panel all`: each panel in the order listed, in front for FramesEach frames, opened as its turn comes.
    [Fact]
    public void AllBringsEveryPanelForwardInTurnOpeningEachOnItsTurn()
    {
        var opened = new List<string>();
        var tour = new PanelTour { FramesEach = 2 };
        tour.Add("Outliner");
        tour.Add("Inspector");
        tour.Add("Visual log", () => opened.Add("Visual log"));

        Assert.Null(tour.Next());                      // nothing asked for
        Assert.True(tour.Show("all", out _));
        Assert.True(tour.IsTouring);
        Assert.Equal(new[] { "Outliner", "Outliner", "Inspector" }, Frames(tour, 3));
        Assert.Empty(opened);                          // not yet its turn
        Assert.Equal(new[] { "Inspector", "Visual log", "Visual log" }, Frames(tour, 3));
        Assert.Equal(new[] { "Visual log" }, opened);  // once
        Assert.Equal("Visual log", tour.Current);
        Assert.Equal(new string?[] { null, null }, Frames(tour, 2));
        Assert.False(tour.IsTouring);
        Assert.Null(tour.Current);
    }

    // One panel by its title in any case, or by the start of exactly one; anything else says what there is.
    [Fact]
    public void OnePanelIsNamedByItsTitleOrItsUniqueStart()
    {
        var tour = new PanelTour();
        foreach (string title in new[] { "Assets", "Animation", "Audio", "I/O" }) tour.Add(title);

        Assert.True(tour.Show("i/o", out _));
        Assert.Equal(new string?[] { "I/O", "I/O", "I/O", null }, Frames(tour, 4));   // DefaultFrames, then done

        Assert.True(tour.Show("au", out _));
        Assert.Equal("Audio", tour.Next());

        Assert.False(tour.Show("a", out string ambiguous));
        Assert.Contains("Assets or Animation or Audio", ambiguous);
        Assert.False(tour.Show("terrain", out string unknown));
        Assert.Contains("Assets, Animation, Audio, I/O", unknown);
        Assert.Equal("Audio", tour.Next());            // a refused request leaves the one under way
    }

    // A new request replaces the rest of the last; a title is listed once; a panel is held at least two frames
    // (focused in one, drawn in front in the next).
    [Fact]
    public void ANewRequestReplacesTheRestAndTitlesAreListedOnce()
    {
        var tour = new PanelTour { FramesEach = 1 };
        Assert.Equal(2, tour.FramesEach);
        tour.Add("Log");
        tour.Add("Problems");
        Assert.Throws<System.ArgumentException>(() => tour.Add("log"));
        Assert.Throws<System.ArgumentException>(() => tour.Add(" "));
        Assert.Equal(new[] { "Log", "Problems" }, tour.Titles);

        Assert.True(tour.Show("all", out _));
        Assert.Equal("Log", tour.Next());
        Assert.True(tour.Show("problems", out _));
        Assert.Equal(new string?[] { "Problems", "Problems", null }, Frames(tour, 3));
    }

    // The console command: with no tour (a host not in the editor) it says so and does nothing.
    [Fact]
    public void TheCommandShowsPanelsOnlyInTheEditor()
    {
        using var app = HeadlessApp.Bare().Build();
        var tour = new PanelTour();
        tour.Add("Outliner");
        bool editing = false;
        PanelTour.Register(app.CVars, () => editing ? tour : null);

        Assert.True(app.CVars.Execute("ed_panel all"));
        Assert.False(tour.IsTouring);
        editing = true;
        Assert.True(app.CVars.Execute("ed_panel"));          // lists them
        Assert.False(tour.IsTouring);
        Assert.True(app.CVars.Execute("ed_panel all"));
        Assert.True(tour.IsTouring);
        Assert.Equal("Outliner", tour.Next());
    }

    // The status bar: the document and whether it is saved, the selection, the world, the camera, the problems.
    [Fact]
    public void TheStatusLineSaysTheDocumentTheSelectionTheWorldAndTheCamera()
    {
        var files = new MountFixture();
        files.Write("game", "data/scene.json", """
            [
              { "type": "prefab", "id": "rock", "name": "rock" },
              { "type": "placements", "id": "yard", "place": [ { "prefab": "rock", "at": [0, 0, 9], "name": "yard rock" } ] },
              { "type": "scene", "id": "main", "placements": ["yard"] }
            ]
            """);
        files.Mount("game", "game");
        using var app = HeadlessApp.Bare().Mount(files).StartScene("game:main").Build();
        var world = app.App.CreateEditWorld("edit");
        var document = new EditDocument(world);

        string none = EditorStatus.Line(null, world, default, new Vector3(1, 2.26f, -3));
        Assert.StartsWith("no document   |   nothing selected   |   edit (", none);
        Assert.EndsWith("camera 1.0 2.3 -3.0", none);

        document.Open(new RecordId("game", "yard"));
        var rock = document.EntityOf(document.Record.Place[0]);
        string line = EditorStatus.Line(document, world, rock, Vector3.Zero, "2 errors");
        Assert.StartsWith("game:yard  saved   |   " + World.Describe(rock) + "   |   edit (", line);
        Assert.EndsWith("camera 0.0 0.0 0.0   |   2 errors", line);
    }
}
