#nullable enable
using System;
using System.Linq;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The render pass registry (issue 4h-1, REDESIGN §4.7, docs/design/06 "As built (render passes)"):
// the order the client's renderer draws its passes in, decided headless. The drawing half needs a GPU
// and is exercised by the smoke runs; the client's registry is this one over its IRenderPass.
public class RenderPassTests
{
    private interface IProbe { }

    [RenderPass("test:shadow", RenderStage.Shadow)] private sealed class Shadow : IProbe { }
    [RenderPass("sage:opaque", RenderStage.Opaque)] private sealed class Opaque : IProbe { }
    [RenderPass("test:outline", RenderStage.Opaque, After = new[] { "sage:opaque" })] private sealed class Outline : IProbe { }
    [RenderPass("test:decals", RenderStage.Opaque, After = new[] { "sage:opaque" }, Before = new[] { "test:outline" })] private sealed class Decals : IProbe { }
    [RenderPass("test:sky", RenderStage.Sky)] private sealed class Sky : IProbe { }
    [RenderPass("sage:transparent", RenderStage.Transparent)] private sealed class Transparent : IProbe { }
    [RenderPass("sage:debug", RenderStage.Debug)] private sealed class DebugLines : IProbe { }
    [RenderPass("test:grade", RenderStage.PostProcess)] private sealed class Grade : IProbe { }
    [RenderPass("sage:ui", RenderStage.Overlay)] private sealed class Ui : IProbe { }
    [RenderPass("test:fade", RenderStage.Overlay, After = new[] { "sage:ui" })] private sealed class Fade : IProbe { }
    [RenderPass("test:first", RenderStage.Opaque)] private sealed class First : IProbe { }
    [RenderPass("test:second", RenderStage.Opaque)] private sealed class Second : IProbe { }
    [RenderPass("test:a", RenderStage.Opaque, After = new[] { "test:b" })] private sealed class CycleA : IProbe { }
    [RenderPass("test:b", RenderStage.Opaque, After = new[] { "test:a" })] private sealed class CycleB : IProbe { }
    [RenderPass("sage:opaque", RenderStage.Transparent)] private sealed class SameId : IProbe { }
    [RenderPass("test:typo", RenderStage.Opaque, After = new[] { "sage:opaqe" })] private sealed class Typo : IProbe { }
    [RenderPass("test:soft", RenderStage.Opaque, After = new[] { "?other_mod:thing", "sage:opaque" })] private sealed class Soft : IProbe { }
    [RenderPass("test:across", RenderStage.Opaque, After = new[] { "sage:ui" })] private sealed class Across : IProbe { }
    private sealed class Undeclared : IProbe { }

    private static string[] Ids(RenderPassRegistry<IProbe> passes) => passes.Ordered.Select(p => p.Id).ToArray();

    [Fact]
    public void PassesDrawByStage_ThenByAfterAndBefore()
    {
        var passes = new RenderPassRegistry<IProbe>();
        // Added out of order on purpose: the stage decides first, whatever the order of adding.
        foreach (var pass in new IProbe[] { new Fade(), new Grade(), new Outline(), new Transparent(), new Ui(), new Decals(),
                                            new DebugLines(), new Sky(), new Opaque(), new Shadow() })
            passes.Add(pass);
        passes.Seal("the test");

        Assert.Equal(new[]
        {
            "test:shadow", "sage:opaque", "test:decals", "test:outline", "test:sky", "sage:transparent", "sage:debug",
            "test:grade", "sage:ui", "test:fade",
        }, Ids(passes));

        // What the renderer walks: one stage's passes, in order.
        var opaque = passes.In(RenderStage.Opaque).ToArray();
        Assert.Equal(new[] { typeof(Opaque), typeof(Decals), typeof(Outline) }, opaque.Select(p => p.GetType()));
        Assert.Equal(0, passes.In(RenderStage.AlphaTested).Length);
        Assert.IsType<Fade>(passes.In(RenderStage.Overlay)[1]);
    }

    [Fact]
    public void PassesNothingOrders_KeepTheOrderTheyWereAdded()
    {
        var passes = new RenderPassRegistry<IProbe>();
        passes.Add(new Second());
        passes.Add(new Opaque());
        passes.Add(new First());
        passes.Seal("the test");

        Assert.Equal(new[] { "test:second", "sage:opaque", "test:first" }, Ids(passes));
    }

    [Fact]
    public void ACycleIsALoadError_NamingThePasses()
    {
        var passes = new RenderPassRegistry<IProbe>();
        passes.Add(new Opaque());
        passes.Add(new CycleA());
        passes.Add(new CycleB());

        var error = Assert.Throws<InvalidOperationException>(() => passes.Seal("the test"));
        Assert.Contains("cycle in stage Opaque", error.Message);
        Assert.Contains("test:a", error.Message);
        Assert.Contains("test:b", error.Message);
        Assert.DoesNotContain("sage:opaque", error.Message);
    }

    [Fact]
    public void ADuplicateIdOrAPassWithoutADeclarationIsALoadError()
    {
        var passes = new RenderPassRegistry<IProbe>();
        passes.Add(new Opaque());

        var duplicate = Assert.Throws<InvalidOperationException>(() => passes.Add(new SameId()));
        Assert.Contains("Two render passes claim the id 'sage:opaque'", duplicate.Message);
        var undeclared = Assert.Throws<InvalidOperationException>(() => passes.Add(new Undeclared()));
        Assert.Contains("has no [RenderPass", undeclared.Message);
        Assert.Equal(1, passes.Count);
    }

    [Fact]
    public void AnAfterNamingNothing_OrAPassInAnotherStage_IsALoadError_UnlessItIsSoft()
    {
        var typo = new RenderPassRegistry<IProbe>();
        typo.Add(new Opaque());
        typo.Add(new Typo());
        Assert.Contains("'sage:opaqe', which no module added", Assert.Throws<InvalidOperationException>(() => typo.Seal("the test")).Message);

        var across = new RenderPassRegistry<IProbe>();
        across.Add(new Ui());
        across.Add(new Across());
        Assert.Contains("which draws in Overlay", Assert.Throws<InvalidOperationException>(() => across.Seal("the test")).Message);

        // "?id": a plugin that may not be installed. Its absence orders nothing; the rest still does.
        var soft = new RenderPassRegistry<IProbe>();
        soft.Add(new Soft());
        soft.Add(new Opaque());
        soft.Seal("the test");
        Assert.Equal(new[] { "sage:opaque", "test:soft" }, Ids(soft));
    }

    [Fact]
    public void AddingAfterTheSealThrows()
    {
        var passes = new RenderPassRegistry<IProbe>();
        passes.Add(new Opaque());
        Assert.Empty(passes.Ordered);   // nothing is ordered before the seal
        passes.Seal("the client started");
        Assert.True(passes.IsSealed);

        var late = Assert.Throws<InvalidOperationException>(() => passes.Add(new Outline()));
        Assert.Contains("render pass 'test:outline' was registered after the client started", late.Message);
        Assert.Contains("Register it in a module's Init", late.Message);
        Assert.Equal(new[] { "sage:opaque" }, Ids(passes));
    }

    // The scene stages each draw one run of a view's sort keys: the top four bits the client's
    // RenderSortKey writes for the material's pass (06 §3.5), with 2 kept for the sky.
    [Fact]
    public void EachSceneStageOwnsOneRunOfTheSortKey()
    {
        Assert.Equal(new[] { -1, 0, 1, 2, 3, -1, -1, -1 },
                     Enum.GetValues<RenderStage>().Select(RenderStages.SortKeyPass));
        Assert.Equal(RenderStage.Opaque, RenderStages.Of(RenderPass.Opaque));
        Assert.Equal(RenderStage.AlphaTested, RenderStages.Of(RenderPass.AlphaTested));
        Assert.Equal(RenderStage.Transparent, RenderStages.Of(RenderPass.Transparent));
        Assert.Equal(new[] { RenderStage.Opaque, RenderStage.AlphaTested, RenderStage.Sky, RenderStage.Transparent, RenderStage.Debug },
                     Enum.GetValues<RenderStage>().Where(RenderStages.IsPerView));
        Assert.Equal(RenderStages.Count, Enum.GetValues<RenderStage>().Length);
    }
}

// The renderer walks the stages every frame, for every view: that walk allocates nothing.
[Xunit.Collection(MeasurementsCollection.Name)]
public class RenderPassAllocationTests
{
    private interface IProbe { void Draw(ref int drawn); }

    [RenderPass("sage:opaque", RenderStage.Opaque)] private sealed class Opaque : IProbe { public void Draw(ref int drawn) => drawn++; }
    [RenderPass("test:outline", RenderStage.Opaque, After = new[] { "sage:opaque" })] private sealed class Outline : IProbe { public void Draw(ref int drawn) => drawn++; }
    [RenderPass("sage:ui", RenderStage.Overlay)] private sealed class Ui : IProbe { public void Draw(ref int drawn) => drawn++; }

    [Fact]
    public void WalkingTheStagesAllocatesNothing()
    {
        var passes = new RenderPassRegistry<IProbe>();
        passes.Add(new Ui());
        passes.Add(new Outline());
        passes.Add(new Opaque());
        passes.Seal("the test");

        int drawn = 0;
        void Frame()
        {
            for (int stage = 0; stage < RenderStages.Count; stage++)
            {
                var inStage = passes.In((RenderStage)stage);
                for (int i = 0; i < inStage.Length; i++) inStage[i].Draw(ref drawn);
            }
        }
        Frame();
        AllocationProbe.AssertNone(200, Frame);
        Assert.Equal(3 * 201, drawn);
    }
}
