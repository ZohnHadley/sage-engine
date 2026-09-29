#nullable enable
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Debug geometry (docs/design/06 §3.2, TODO F5). The queue is engine-side and headless, so what it
// records, when it forgets and what it costs when switched off are all testable without a window.
public class DebugDrawTests
{
    public DebugDrawTests() { _ = TestEnv.UserRoot; }

    private static DebugDraw Enabled() => new() { Enabled = true };

    private static List<DebugLine> Drain(DebugDraw debug)
    {
        var lines = new List<DebugLine>();
        debug.CopyTo(lines);
        return lines;
    }

    [Fact]
    public void NothingIsRecordedWhileItIsSwitchedOff()
    {
        var debug = new DebugDraw();          // Enabled defaults to false: a shipping build pays a bool test
        debug.Line(Vector3.Zero, Vector3.UnitX);
        debug.Sphere(Vector3.Zero, 1f);
        debug.Capsule(Vector3.Zero, 0.35f, 1.8f);

        Assert.Equal(0, debug.Count);
    }

    [Fact]
    public void ShapesBecomeLineSegments()
    {
        var debug = Enabled();

        debug.Line(Vector3.Zero, Vector3.UnitX);
        Assert.Equal(1, debug.Count);

        debug.Clear();
        debug.Cross(Vector3.Zero);
        Assert.Equal(3, debug.Count);         // one segment per axis

        debug.Clear();
        debug.Box(Vector3.Zero, Vector3.One);
        Assert.Equal(12, debug.Count);        // a box has twelve edges, each drawn once

        debug.Clear();
        debug.Sphere(Vector3.Zero, 1f);
        Assert.Equal(48, debug.Count);        // three rings of sixteen
    }

    [Fact]
    public void ACapsuleIsDrawnWhereItStands()
    {
        var debug = Enabled();
        debug.Capsule(new Vector3(0, 10, 0), radius: 0.5f, height: 2f);

        var lines = Drain(debug);
        Assert.NotEmpty(lines);
        float lowest = float.MaxValue, highest = float.MinValue;
        foreach (var line in lines)
        {
            lowest = MathF.Min(lowest, MathF.Min(line.A.Y, line.B.Y));
            highest = MathF.Max(highest, MathF.Max(line.A.Y, line.B.Y));
        }
        // Feet on the ground it was given, head two metres up: the shape a character collides with.
        Assert.Equal(10f, lowest, 2);
        Assert.Equal(12f, highest, 2);
    }

    [Fact]
    public void MomentaryShapesBelongToTheTickThatDrewThem()
    {
        var debug = Enabled();
        debug.Line(Vector3.Zero, Vector3.UnitX);
        debug.Line(Vector3.Zero, Vector3.UnitY);
        Assert.Equal(2, debug.Count);

        // A second tick with nothing drawn: the old shapes go, rather than piling up until the next
        // frame happens to look (which is how sixteen thousand lines appeared at 60 Hz against 50 fps).
        debug.BeginTick();
        Assert.Equal(0, debug.Count);
    }

    [Fact]
    public void TimedShapesSurviveUntilTheyExpire()
    {
        var debug = Enabled();
        debug.Line(Vector3.Zero, Vector3.UnitX, DebugColour.Red, seconds: 0.5f);

        debug.BeginTick();                    // ticks don't touch a timed shape
        Assert.Equal(1, debug.Count);

        Assert.Single(Drain(debug));          // a frame draws it...
        debug.Advance(0.2f);
        Assert.Equal(1, debug.Count);         // ...and it is still there a fifth of a second later

        Assert.Single(Drain(debug));
        debug.Advance(0.4f);
        Assert.Equal(0, debug.Count);         // and gone once its half second is up
    }

    [Fact]
    public void ARunawayLoopIsCappedRatherThanEatingTheHeap()
    {
        var debug = Enabled();
        using var capture = new CaptureSink();

        for (int i = 0; i < 20000; i++) debug.Line(Vector3.Zero, Vector3.UnitX);

        Assert.InRange(debug.Count, 1, 16384);
        Assert.Contains(capture.Entries, e => e.Message.Contains("Debug draw is over"));
    }

    // The point of BeginTick living in RunFixed: whatever a tick drew is still there for the frame
    // that follows it.
    [Fact]
    public void WhatATickDrawsSurvivesToTheNextFrame()
    {
        using var world = new World("debug");
        var debug = world.Debug();
        debug.Enabled = true;
        world.AddSystem(new DrawOneLine(), Phase.Late);

        world.RunFixed(1f / 60f);
        Assert.Equal(1, debug.Count);

        var lines = Drain(debug);
        Assert.Single(lines);
        Assert.Equal(Vector3.UnitZ, lines[0].B);
    }

    private sealed class DrawOneLine : ISystem
    {
        public void Run(in SystemContext ctx) => ctx.World.Debug().Line(Vector3.Zero, Vector3.UnitZ);
    }
}
