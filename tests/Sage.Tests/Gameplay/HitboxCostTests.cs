#nullable enable
using System;
using System.Diagnostics;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// What many hitboxes cost a tick (issue #394; docs/design/16 "As built (hit locations)" left the step time
// at hundreds of boxes unmeasured). tests/games/skeletal's mannequins, eleven kinematic boxes each on the
// query-only layer following their bones, stepping and animating through the whole pipeline: 20 of them
// (220 boxes), 80 (880 boxes), and the same 80 with the hitbox budget switching every box off. The bounds
// are loose on purpose — they catch a quadratic step or a box that costs like a creature, not a slow
// machine — and the numbers are written to the test's output either way. Timed alone, like every measurement.
[Xunit.Collection(MeasurementsCollection.Name)]
public class HitboxCostTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public HitboxCostTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _ = TestEnv.UserRoot;
        _output = output;
    }

    private const int Boxes = 11;   // the mannequin's hitboxes record

    [Fact]
    public void HundredsOfHitboxesStepWithinABudgetAndGrowLinearly()
    {
        var (small, smallBoxes) = TimePerTick(creatures: 20, boxesOn: true);
        var (large, largeBoxes) = TimePerTick(creatures: 80, boxesOn: true);
        var (off, offBoxes) = TimePerTick(creatures: 80, boxesOn: false);

        string report = $"20 creatures, {smallBoxes} boxes: {small:F2} ms a tick; 80 creatures, {largeBoxes} boxes: {large:F2} ms; " +
                        $"80 creatures, boxes off by the budget ({offBoxes} with bodies): {off:F2} ms";
        _output.WriteLine(report);

        Assert.Equal(20 * Boxes, smallBoxes);
        Assert.Equal(80 * Boxes, largeBoxes);
        Assert.Equal(0, offBoxes);

        // 880 boxes on 80 animated creatures at a sixtieth of a second: a tick must not cost several.
        Assert.True(large < 50, $"over the 50 ms bound: {report}");

        // Four times the creatures and boxes. Linear is ~4x, quadratic ~16x; under 8x is a slope a crowd can live with.
        double ratio = large / Math.Max(small, 0.0001);
        Assert.True(ratio < 8.0, $"4x the boxes cost {ratio:F1}x the time: {report}");
    }

    // Milliseconds a tick (fixed step and frame) for `creatures` mannequins in the yard, and how many of their
    // boxes have a physics body. `boxesOn` false: the yard's player stands far off and the budget's distance is
    // a metre, so every box is ColliderOff (no body, nothing in the step), and only the bones still pose them.
    private static (double MsPerTick, int Bodies) TimePerTick(int creatures, bool boxesOn)
    {
        using var app = HitboxBudgetTests.EmptyYard();
        var world = app.World;
        var budget = app.Records.Get<HitboxBudgetRecord>(world.Conventions().HitboxBudget.Id);
        budget.MaxCreatures = 0;
        budget.Distance = boxesOn ? 0f : 1f;
        HitboxBudgetTests.Player(world, new Vector3(0, 0, boxesOn ? -16f : -400f));

        var npcs = new Entity[creatures];
        for (int i = 0; i < npcs.Length; i++)
            npcs[i] = world.Spawn(NpcLocomotionTests.Npc, new Vector3(-9 + (i % 10) * 2f, 0, -8 + (i / 10) * 2f));

        int tick = 0;
        void Step()
        {
            tick++;
            for (int i = 0; i < npcs.Length; i++)
            {
                world.Get<Transform>(npcs[i]).LocalPosition += new Vector3(MathF.Sin(tick * 0.01f + i) * 2f / 60f, 0, 0);
                int beat = (tick + i * 7) % 240;
                if (beat == 0) Animators.SetParam(world, npcs[i], "aiming", true);
                if (beat == 60) Animators.SetTrigger(world, npcs[i], "attack");
                if (beat == 200) Animators.SetParam(world, npcs[i], "aiming", false);
            }
            world.RunFixed(NpcLocomotionTests.Dt);
            world.RunFrame(NpcLocomotionTests.Dt, 1f);
            Profiler.EndFrame();
        }

        for (int i = 0; i < 120; i++) Step();   // warm: JIT, archetypes, the budget settled

        int bodies = 0;
        foreach (var (boxes, entities) in world.Query<Hitbox>().Chunks)
            for (int n = 0; n < boxes.Span.Length; n++)
                if (entities.EntityAt(n).HasComponent<PhysicsBody>()) bodies++;

        const int Ticks = 240;
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < Ticks; i++) Step();
        return (clock.Elapsed.TotalMilliseconds / Ticks, bodies);
    }
}
