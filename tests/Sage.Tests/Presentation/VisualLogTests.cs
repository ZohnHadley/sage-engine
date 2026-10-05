#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The visual logger (docs/design/02 §9, issue #300): debug shapes kept per tick and category, scrubbed
// back through with vlog_at / vlog_step, filtered with vlog_show, drawn through the debug lines.
public class VisualLogTests
{
    public VisualLogTests() { _ = TestEnv.UserRoot; }

    // Records a line at x = the tick, so which tick a shape came from can be read off it.
    private sealed class Marks : ISystem
    {
        public void Run(in SystemContext ctx)
        {
            var log = ctx.World.VisualLog();
            long tick = ctx.World.Tick;
            log.Line("test", new Vector3(tick, 0, 0), new Vector3(tick, 1, 0), DebugColour.Red, log.Recording ? $"tick {tick}" : null);
            log.Sphere("other", new Vector3(tick, 0, 0), 0.5f);
        }
    }

    private static (HeadlessApp App, World World) Boot()
    {
        var app = HeadlessApp.Simulation().Boot("vlog");
        app.World.AddSystem(new Marks(), Phase.Late);
        return (app, app.World);
    }

    private static void Tick(World world, int ticks)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    [Fact]
    public void NothingIsRecordedUntilVlogRecordIsOn()
    {
        var (app, world) = Boot();
        using var _ = app;
        Tick(world, 5);
        Assert.Equal(0, world.VisualLog().Count);

        app.CVars.Execute("vlog_record 1");
        Tick(world, 5);
        Assert.Equal(10, world.VisualLog().Count);   // two shapes a tick
        Assert.Equal(new[] { "other", "test" }, world.VisualLog().Categories);
    }

    [Fact]
    public void ShapesAreKeptPerTickAndScrubbedBackTo()
    {
        var (app, world) = Boot();
        using var _ = app;
        app.CVars.Execute("vlog_record 1");
        Tick(world, 20);
        var log = world.VisualLog();
        long newest = world.Tick;

        Assert.Null(log.ScrubTick);
        Assert.Equal(newest, log.ShownTick);   // live: the newest tick

        Assert.True(app.CVars.Execute("vlog_at -5"));
        Assert.Equal(newest - 5, log.ShownTick);
        var entries = new List<VisualLogEntry>();
        log.CollectAt(log.ShownTick, entries);
        var line = Assert.Single(entries, e => e.Category == "test");
        Assert.Equal(newest - 5, (long)line.A.X);
        Assert.Equal($"tick {newest - 5}", line.Text);

        // What is drawn is the scrubbed tick's shapes, as line segments.
        var lines = new List<DebugLine>();
        log.DrawShown(lines);
        Assert.Contains(lines, l => l.A == new Vector3(newest - 5, 0, 0) && l.B == new Vector3(newest - 5, 1, 0));
        Assert.DoesNotContain(lines, l => l.A.X == newest);

        // Stepping moves it; ticking on does not, while scrubbed.
        Assert.True(app.CVars.Execute("vlog_step 2"));
        Assert.Equal(newest - 3, log.ShownTick);
        Tick(world, 3);
        Assert.Equal(newest - 3, log.ShownTick);

        Assert.True(app.CVars.Execute("vlog_at live"));
        Assert.Equal(world.Tick, log.ShownTick);
        Assert.True(app.CVars.Execute($"vlog_at {newest - 10}"));
        Assert.Equal(newest - 10, log.ShownTick);
    }

    [Fact]
    public void OnlyTheLastVlogTicksAreKept()
    {
        var (app, world) = Boot();
        using var _ = app;
        app.CVars.Execute("vlog_record 1");
        app.CVars.Execute("vlog_ticks 8");
        Tick(world, 30);
        var log = world.VisualLog();
        Assert.Equal(world.Tick - 7, log.OldestTick);
        Assert.Equal(world.Tick, log.NewestTick);
        Assert.Equal(16, log.Count);

        // A tick scrubbed to that has since gone shows the oldest still kept.
        log.ScrubTick = 1;
        Assert.Equal(log.OldestTick, log.ShownTick);
    }

    [Fact]
    public void VlogShowPicksTheCategoriesDrawnAndListed()
    {
        var (app, world) = Boot();
        using var _ = app;
        app.CVars.Execute("vlog_record 1");
        Tick(world, 3);
        var log = world.VisualLog();
        Assert.Equal(2, log.CountAt(log.ShownTick));

        app.CVars.Execute("vlog_show test");
        Assert.Equal(1, log.CountAt(log.ShownTick));
        var lines = new List<DebugLine>();
        log.DrawShown(lines);
        Assert.Single(lines);   // the line; the sphere's circles are not drawn
        var entries = new List<VisualLogEntry>();
        log.CollectAt(log.ShownTick, entries, all: true);
        Assert.Equal(2, entries.Count);   // still recorded

        app.CVars.Execute("vlog_show *");
        Assert.Equal(2, log.CountAt(log.ShownTick));
    }

    [Fact]
    public void ARebaseClearsTheHistoryItsPositionsNoLongerMatch()
    {
        var (app, world) = Boot();
        using var _ = app;
        app.CVars.Execute("vlog_record 1");
        Tick(world, 3);
        world.Rebase(new SectorCoord(1, 0));
        Assert.Equal(0, world.VisualLog().Count);
    }
}

[Xunit.Collection(MeasurementsCollection.Name)]
public class VisualLogAllocationTests
{
    // Recording allocates nothing per shape beyond the text a caller builds (none here), and with
    // vlog_record off a call is one bool test.
    [Fact]
    public void AShapeWithoutTextAllocatesNothing()
    {
        var log = new VisualLog { Recording = true, HistoryTicks = 2 };
        long tick = 0;
        for (int i = 0; i < 4; i++) { log.BeginTick(++tick); log.Line("warm", Vector3.Zero, Vector3.One); }
        var report = AllocationProbe.Measure(50, () =>
        {
            log.BeginTick(++tick);
            log.Line("warm", Vector3.Zero, Vector3.One);
            log.Cone("warm", Vector3.Zero, 0f, 90f, 5f);
        });
        Assert.Equal(0, report.Bytes);
    }
}
