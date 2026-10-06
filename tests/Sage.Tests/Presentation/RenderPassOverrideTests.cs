#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Replacing and disabling an engine render pass by id, `r_snapshot_dump`'s text and world-space Text3D
// (issue 4n-18, docs/design/06 §3.11). The registry and the dump are headless; the client's renderer
// only walks the one and fills the other.
public class RenderPassOverrideTests
{
    private interface IProbe { }

    [RenderPass("sage:opaque", RenderStage.Opaque)] private sealed class Opaque : IProbe { }
    [RenderPass("sage:sky", RenderStage.Sky)] private sealed class EngineSky : IProbe { }
    [RenderPass("sage:transparent", RenderStage.Transparent)] private sealed class Transparent : IProbe { }
    [RenderPass("sage:debug", RenderStage.Debug)] private sealed class DebugLines : IProbe { }
    [RenderPass("game:sky", RenderStage.Sky)] private sealed class GameSky : IProbe { }
    [RenderPass("other:sky", RenderStage.Sky)] private sealed class OtherSky : IProbe { }
    [RenderPass("sage:outline", RenderStage.Opaque, After = new[] { "sage:opaque" })] private sealed class Outline : IProbe { }
    [RenderPass("game:toon", RenderStage.Opaque)] private sealed class Toon : IProbe { }
    [RenderPass("game:oit", RenderStage.Transparent)] private sealed class Oit : IProbe { }
    [RenderPass("game:fog", RenderStage.Transparent, After = new[] { "sage:transparent" })] private sealed class FogAfter : IProbe { }

    private static RenderPassRegistry<IProbe> Engine()
    {
        var passes = new RenderPassRegistry<IProbe>();
        passes.Add(new Opaque());
        passes.Add(new EngineSky());
        passes.Add(new Transparent());
        passes.Add(new DebugLines());
        return passes;
    }

    [Fact]
    public void ReplacingTheSkyPass_KeepsItsIdAndSlot_AndSaysWhoDidIt()
    {
        var passes = Engine();
        passes.Replace("sage:sky", new GameSky(), "my_game");
        passes.Seal("the test");

        Assert.Equal(new[] { "sage:opaque", "sage:sky", "sage:transparent", "sage:debug" }, passes.Ordered.Select(p => p.Id));
        Assert.IsType<GameSky>(passes.In(RenderStage.Sky)[0]);
        var sky = passes.Ordered[1];
        Assert.Equal(typeof(GameSky), sky.Type);
        Assert.Equal(typeof(EngineSky), sky.Replaces);
        Assert.Equal("my_game", sky.By);
        Assert.Empty(passes.Disabled);
    }

    [Fact]
    public void AReplacementMayBeAskedForBeforeTheEnginePassIsAdded_AndInheritsItsOrdering()
    {
        var passes = new RenderPassRegistry<IProbe>();
        passes.Replace("sage:outline", new Toon(), "my_game");   // a plugin's Init can run first
        passes.Add(new Outline());
        passes.Add(new Opaque());
        passes.Seal("the test");

        // game:toon declares no After, but it holds the slot of sage:outline, which draws after sage:opaque.
        Assert.Equal(new[] { "sage:opaque", "sage:outline" }, passes.Ordered.Select(p => p.Id));
        Assert.IsType<Toon>(passes.In(RenderStage.Opaque)[1]);
        Assert.Contains("sage:opaque", passes.Ordered[1].After);
    }

    [Fact]
    public void DisablingAPass_LeavesItOut_AndConstraintsNamingItAreDropped()
    {
        var passes = Engine();
        passes.Add(new FogAfter());   // "after sage:transparent", which is going away
        passes.Disable("sage:transparent", "my_game");
        passes.Seal("the test");

        Assert.Equal(new[] { "sage:opaque", "sage:sky", "game:fog", "sage:debug" }, passes.Ordered.Select(p => p.Id));
        Assert.IsType<FogAfter>(Assert.Single(passes.In(RenderStage.Transparent).ToArray()));
        var off = Assert.Single(passes.Disabled);
        Assert.Equal("sage:transparent", off.Id);
        Assert.Equal("my_game", off.By);
        Assert.Equal(typeof(Transparent), off.Type);
    }

    [Fact]
    public void TwoModulesChangingOnePass_IsALoadError_NamingBoth()
    {
        var passes = Engine();
        passes.Replace("sage:sky", new GameSky(), "my_game");
        var twice = Assert.Throws<InvalidOperationException>(() => passes.Replace("sage:sky", new OtherSky(), "other_mod"));
        Assert.Contains("'sage:sky'", twice.Message);
        Assert.Contains("my_game", twice.Message);
        Assert.Contains("other_mod", twice.Message);

        var both = Assert.Throws<InvalidOperationException>(() => passes.Disable("sage:sky", "third"));
        Assert.Contains("replaced by my_game", both.Message);
    }

    [Fact]
    public void ReplacingAnUnknownPass_OrInAnotherStage_IsALoadErrorAtTheSeal()
    {
        var unknown = Engine();
        unknown.Replace("sage:skyy", new GameSky(), "my_game");
        var error = Assert.Throws<InvalidOperationException>(() => unknown.Seal("the test"));
        Assert.Contains("my_game replaces render pass 'sage:skyy', which no module added", error.Message);

        var gone = Engine();
        gone.Disable("nobody:pass", "my_game");
        Assert.Contains("my_game disables render pass 'nobody:pass'", Assert.Throws<InvalidOperationException>(() => gone.Seal("the test")).Message);

        var stage = Engine();
        stage.Replace("sage:sky", new Oit(), "my_game");   // Transparent for a Sky pass
        Assert.Contains("draws in Transparent", Assert.Throws<InvalidOperationException>(() => stage.Seal("the test")).Message);
    }

    [Fact]
    public void ReplaceAndDisableAfterTheSeal_Throw()
    {
        var passes = Engine();
        passes.Seal("the test");
        Assert.Throws<InvalidOperationException>(() => passes.Replace("sage:sky", new GameSky(), "my_game"));
        Assert.Throws<InvalidOperationException>(() => passes.Disable("sage:sky", "my_game"));
    }

    [Fact]
    public void SnapshotDump_WritesThePasses_TheViewsAndTheFramesItems()
    {
        var passes = Engine();
        passes.Replace("sage:sky", new GameSky(), "my_game");
        passes.Disable("sage:debug", "my_game");
        passes.Seal("the test");

        var dump = new RenderFrameDump(42);
        dump.Passes(passes.Ordered, passes.Disabled);
        dump.View(0, null, 0, 0, 1280, 720, new Vector3(1, 2, 3), 2, 1);
        dump.Item(0, "meshes/rock.glb", 0, "sage:lit", 0x0000000000000123, new Vector3(0, 0, -5));
        dump.Item(0, "meshes/tree.glb", 1, "sage:lit", 0x0000000000000456, new Vector3(2.5f, 0, -7));
        dump.Sprite(0, "sage:sprite_default", 0x7, new Vector3(1, 1, -2));
        dump.Totals(culled: 3, lights: 1, debugLines: 0);

        string dir = Path.Combine(Path.GetTempPath(), "sage-snapshot-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = dump.Write(dir, Path.Combine("logs", "snapshot.txt"));
            Assert.Equal(Path.Combine(dir, "logs", "snapshot.txt"), path);
            string[] lines = File.ReadAllLines(path);

            Assert.Equal("frame 42", lines[0]);
            Assert.Contains(lines, l => l.StartsWith("pass  Sky") && l.Contains("sage:sky") && l.Contains("GameSky (replaces EngineSky, by my_game)"));
            Assert.Contains(lines, l => l.StartsWith("off   Debug") && l.Contains("disabled by my_game"));
            Assert.Contains("view  0 target=screen viewport=0,0,1280,720 camera=1.00,2.00,3.00 items=2 sprites=1", lines);
            Assert.Contains("item  view=0 mesh=meshes/rock.glb part=0 material=sage:lit key=0x0000000000000123 at=0.00,0.00,-5.00", lines);
            Assert.Contains("item  view=0 mesh=meshes/tree.glb part=1 material=sage:lit key=0x0000000000000456 at=2.50,0.00,-7.00", lines);
            Assert.Equal(2, lines.Count(l => l.StartsWith("item ")));
            Assert.Equal("totals items=2 sprites=1 culled=3 lights=1 debug_lines=0", lines[^1]);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Text3D_BecomesStrokesFacingTheCamera_AndExpires()
    {
        var debug = new DebugDraw { Enabled = true };
        debug.Text3D(new Vector3(0, 2, 0), "HP 10", DebugColour.Green, height: 0.5f, seconds: 1f);
        debug.Text3D(new Vector3(5, 0, 0), "GONE");   // momentary: this tick only
        Assert.Equal(2, debug.LabelCount);

        var lines = new System.Collections.Generic.List<DebugLine>();
        debug.CopyLabelsTo(lines, Vector3.UnitX, Vector3.UnitY);   // a camera looking down -Z
        Assert.NotEmpty(lines);
        // Facing the camera: every stroke lies in the label's plane (z = 0), and the label is `height` tall.
        Assert.All(lines, l => { Assert.Equal(0f, l.A.Z); Assert.Equal(0f, l.B.Z); });
        var green = lines.Where(l => l.Rgba == DebugColour.Green).ToList();
        Assert.InRange(green.Max(l => MathF.Max(l.A.Y, l.B.Y)) - green.Min(l => MathF.Min(l.A.Y, l.B.Y)), 0.4f, 0.5001f);
        Assert.InRange(green.Min(l => MathF.Min(l.A.X, l.B.X)), -1.5f, 0f);   // centred on x = 0
        Assert.InRange(green.Max(l => MathF.Max(l.A.X, l.B.X)), 0f, 1.5f);

        debug.BeginTick();
        Assert.Equal(1, debug.LabelCount);   // the momentary one went with its tick
        debug.Advance(1.5f);
        Assert.Equal(0, debug.LabelCount);   // the timed one drew, then aged out

        var off = new DebugDraw();
        off.Text3D(Vector3.Zero, "NOPE");
        Assert.Equal(0, off.LabelCount);   // gated by Enabled like every shape
    }
}
