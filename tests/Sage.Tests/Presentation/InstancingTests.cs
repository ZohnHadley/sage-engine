#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Mesh and sprite instancing (issue 4n-5, docs/design/06 §3.7): which neighbours in a stage's sorted items
// make one instanced draw, decided headless by `Instancing.Plan` as the client's renderer asks it with
// `r_instancing 1`, and the instanced techniques the shaders must have for it. The drawing, and the
// fallback where the device cannot instance, are the smoke run's.
public class InstancingTests
{
    private static InstanceCandidate Item(int material = 1, int mesh = 1, int part = 0, int state = 0, bool eligible = true) =>
        new(material, mesh, part, state, eligible);

    private static InstanceRun[] Plan(InstanceCandidate[] items, int min = Instancing.DefaultMin, int max = Instancing.MaxPerDraw)
    {
        var runs = new InstanceRun[items.Length];
        int count = Instancing.Plan(items, min, max, runs);
        return runs[..count];
    }

    [Xunit.Fact]
    public void RunsOfAtLeastMinCollapseToOneDraw()
    {
        var runs = Plan(Enumerable.Repeat(Item(), 12).ToArray());
        Assert.Equal(new[] { new InstanceRun(0, 12, true) }, runs);
        Assert.Equal(11, Instancing.Saved(runs));   // twelve draws became one
    }

    [Xunit.Fact]
    public void RunsShorterThanTheMinimumAreDrawnOneByOne()
    {
        var runs = Plan(Enumerable.Repeat(Item(), 7).ToArray(), min: 8);
        Assert.Equal(new[] { new InstanceRun(0, 7, false) }, runs);
        Assert.Equal(0, Instancing.Saved(runs));

        // The same seven with the minimum at their count are one draw.
        Assert.Equal(new[] { new InstanceRun(0, 7, true) }, Plan(Enumerable.Repeat(Item(), 7).ToArray(), min: 7));
    }

    [Xunit.Fact]
    public void ARunBreaksWhereMeshPartMaterialOrStateChange()
    {
        var items = Enumerable.Repeat(Item(), 3)                          // too short: single draws
            .Concat(Enumerable.Repeat(Item(mesh: 2), 4))                  // another mesh
            .Concat(Enumerable.Repeat(Item(mesh: 2, part: 1), 4))         // another part of it
            .Concat(Enumerable.Repeat(Item(mesh: 2, part: 1, material: 5), 4))   // another material
            .Concat(Enumerable.Repeat(Item(mesh: 2, part: 1, material: 5, state: 1), 4))   // another tint or lamps
            .ToArray();
        var runs = Plan(items, min: 4);
        Assert.Equal(new[]
        {
            new InstanceRun(0, 3, false),
            new InstanceRun(3, 4, true),
            new InstanceRun(7, 4, true),
            new InstanceRun(11, 4, true),
            new InstanceRun(15, 4, true),
        }, runs);
        Assert.Equal(12, Instancing.Saved(runs));
    }

    [Xunit.Fact]
    public void WhatCannotBeInstancedIsDrawnOneByOne()
    {
        // Skinned items, a material whose effect has no instanced technique, or instancing off: never in a run,
        // and they split the identical items around them.
        var items = Enumerable.Repeat(Item(), 8)
            .Append(Item(eligible: false))
            .Concat(Enumerable.Repeat(Item(), 5))
            .Concat(Enumerable.Repeat(Item(eligible: false), 10))
            .ToArray();
        Assert.Equal(new[] { new InstanceRun(0, 8, true), new InstanceRun(8, 16, false) }, Plan(items));
    }

    [Xunit.Fact]
    public void LongRunsAreSplitAtTheInstanceBuffer()
    {
        var runs = Plan(Enumerable.Repeat(Item(), 2500).ToArray(), min: 8, max: 1024);
        Assert.Equal(new[] { new InstanceRun(0, 1024, true), new InstanceRun(1024, 1024, true), new InstanceRun(2048, 452, true) }, runs);

        // A remainder shorter than the minimum is drawn one by one.
        runs = Plan(Enumerable.Repeat(Item(), 1027).ToArray(), min: 8, max: 1024);
        Assert.Equal(new[] { new InstanceRun(0, 1024, true), new InstanceRun(1024, 3, false) }, runs);
    }

    [Xunit.Fact]
    public void EveryItemIsInExactlyOneRunInOrder()
    {
        var random = new Random(4005);
        for (int trial = 0; trial < 200; trial++)
        {
            var items = new InstanceCandidate[random.Next(0, 300)];
            for (int i = 0; i < items.Length; i++)
                items[i] = random.Next(10) < 7 && i > 0 ? items[i - 1] : Item(random.Next(3), random.Next(3), random.Next(2), random.Next(2), random.Next(10) > 0);
            int min = random.Next(2, 12);
            var runs = Plan(items, min, max: random.Next(min, 64));
            int next = 0;
            foreach (var run in runs)
            {
                Assert.Equal(next, run.Start);
                Assert.True(run.Count > 0);
                if (run.Instanced)
                {
                    Assert.True(run.Count >= min);
                    for (int i = run.Start; i < run.Start + run.Count; i++)
                        Assert.True(items[i].Eligible && items[i].SameDraw(items[run.Start]));
                }
                next = run.Start + run.Count;
            }
            Assert.Equal(items.Length, next);
        }
    }

    [Xunit.Fact]
    public void InstancedTechniquesAreNamedAfterTheirTwin()
    {
        Assert.Equal("Instanced", Instancing.TechniqueFor("Default"));
        Assert.Equal("AlphaTestInstanced", Instancing.TechniqueFor("AlphaTest"));
        Assert.Equal("ShadowCasterInstanced", Instancing.TechniqueFor("ShadowCaster"));
        Assert.Equal("LitInstanced", Instancing.TechniqueFor("Lit"));
    }

    // The shaders compile only where mgfxc runs (Windows CI), so this checks what can be checked here: each
    // engine technique a material or the caster pass draws with has its instanced twin, and every vertex and
    // pixel shader a technique compiles is a function the file defines.
    [Xunit.Theory]
    [Xunit.InlineData("lit.fx", new[] { "Default", "AlphaTest", "Unlit", "ShadowCaster" })]
    [Xunit.InlineData("sprite.fx", new[] { "Unlit", "Lit", "UnlitBlend" })]
    public void EngineShadersHaveInstancedTwins(string file, string[] techniques)
    {
        var source = File.ReadAllText(Path.Combine(TestEnv.FolderAbove("engine_content"), "engine_content", "shaders", file));
        var declared = Regex.Matches(source, @"^technique\s+(\w+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToHashSet();
        foreach (var technique in techniques)
        {
            Assert.Contains(technique, declared);
            Assert.Contains(Instancing.TechniqueFor(technique), declared);
        }
        foreach (Match compile in Regex.Matches(source, @"compile [vp]s_3_0 (\w+)\(\)"))
            Assert.Matches(new Regex(@"\b" + compile.Groups[1].Value + @"\s*\([^)]*\)\s*(:\s*COLOR0\s*)?\r?\n\{"), source);
    }
}
