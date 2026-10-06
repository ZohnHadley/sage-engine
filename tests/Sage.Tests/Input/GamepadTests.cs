#nullable enable
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Several pads, several players, the last device used, the stick response and scripted input for a chosen
// player (issue #331). All of it is the engine's decisions; the client only reads the pads.
public class GamepadTests
{
    private static bool[] Pads(params int[] connected)
    {
        var a = new bool[PadAssignment.MaxPads];
        foreach (int i in connected) a[i] = true;
        return a;
    }

    // ---- pad to player and hot-swap ----------------------------------------------------------------------

    [Xunit.Fact]
    public void PadsAreHandedOutToPlayersInOrder()
    {
        var pads = new PadAssignment { Players = 2 };
        var changes = pads.Update(Pads(1, 2, 3)).ToArray();     // pad 0 is absent; 1 -> player 0, 2 -> player 1, 3 unwanted
        Assert.Equal(2, changes.Length);
        Assert.Equal(1, pads.PadOf(0));
        Assert.Equal(2, pads.PadOf(1));
        Assert.Equal(-1, pads.PlayerOf(3));
        Assert.Equal(0, pads.PlayerOf(1));
        Assert.Equal(-1, pads.PadOf(2));                        // no third player
        Assert.Empty(pads.Update(Pads(1, 2, 3)).ToArray());     // steady state: nothing changes
    }

    // Hot-swap: an unplugged pad leaves its player without one; the same pad coming back returns to that
    // player even though another player was waiting first.
    [Xunit.Fact]
    public void AnUnpluggedPadReturnsToItsOwnPlayer()
    {
        var pads = new PadAssignment { Players = 2 };
        pads.Update(Pads(0, 1));
        var lost = pads.Update(Pads(0)).ToArray();
        Assert.Equal(new PadAssignment.Change(1, 1, false), Assert.Single(lost));
        Assert.Equal(-1, pads.PadOf(1));

        pads.Update(Pads());                                    // both gone
        Assert.Equal(-1, pads.PadOf(0));
        var back = pads.Update(Pads(1)).ToArray();              // player 1's pad returns first, and goes to player 1
        Assert.Equal(new PadAssignment.Change(1, 1, true), Assert.Single(back));
        pads.Update(Pads(0, 1));
        Assert.Equal(0, pads.PadOf(0));
    }

    // A different pad plugged in while a player waits takes the free slot; lowering the player count frees pads.
    [Xunit.Fact]
    public void ANewPadTakesAFreeSlotAndPlayersCanBeReduced()
    {
        var pads = new PadAssignment { Players = 2 };
        pads.Update(Pads(0, 1));
        pads.Update(Pads(0));                                   // player 2's pad goes
        pads.Update(Pads(0, 3));                                // a different pad arrives
        Assert.Equal(3, pads.PadOf(1));

        pads.Players = 1;
        Assert.Equal(-1, pads.PadOf(1));
        Assert.Equal(-1, pads.PlayerOf(3));
        Assert.True(pads.Assign(0, 3));                         // by hand: player 1 takes pad 4
        Assert.Equal(3, pads.PadOf(0));
        Assert.False(pads.Assign(0, 2));                        // not plugged in
    }

    // ---- stick response ----------------------------------------------------------------------------------

    [Xunit.Fact]
    public void ADeadZoneIsRadialAndRescaledToReachOne()
    {
        Assert.Equal(Vector2.Zero, StickResponse.Apply(new Vector2(0.14f, 0.14f), 0.2f));   // inside, though each axis is under too
        Assert.Equal(Vector2.Zero, StickResponse.Apply(new Vector2(0.15f, 0.15f), 0.3f));
        var full = StickResponse.Apply(new Vector2(0f, 1f), 0.2f);
        Assert.Equal(1f, full.Y, 4);
        var mid = StickResponse.Apply(new Vector2(0.6f, 0f), 0.2f);
        Assert.Equal(0.5f, mid.X, 4);                                                        // (0.6-0.2)/(1-0.2)
        // direction is kept: a diagonal stays a diagonal
        var diag = StickResponse.Apply(Vector2.Normalize(new Vector2(1, 1)) * 0.6f, 0.2f);
        Assert.Equal(diag.X, diag.Y, 4);
        Assert.Equal(0.5f, diag.Length(), 4);
        Assert.Equal(-0.5f, StickResponse.Apply(-0.6f, 0.2f), 4);
        Assert.Equal(0f, StickResponse.Apply(0.1f, 0.2f));
    }

    [Xunit.Theory]
    [Xunit.InlineData("linear", 1f)]
    [Xunit.InlineData("quadratic", 2f)]
    [Xunit.InlineData("Cubic", 3f)]
    [Xunit.InlineData("1.5", 1.5f)]
    public void ResponseCurvesParse(string text, float exponent)
    {
        Assert.True(StickResponse.TryParseCurve(text, out float parsed));
        Assert.Equal(exponent, parsed);
    }

    [Xunit.Theory]
    [Xunit.InlineData("wobbly")]
    [Xunit.InlineData("0")]
    [Xunit.InlineData("20")]
    public void BadResponseCurvesAreRefused(string text) => Assert.False(StickResponse.TryParseCurve(text, out _));

    // A curve is finer near the centre and still reaches 1 at full travel.
    [Xunit.Fact]
    public void ACurveIsFinerNearTheCentreAndStillReachesOne()
    {
        var linear = StickResponse.Apply(new Vector2(0.5f, 0), 0f, 1f);
        var quadratic = StickResponse.Apply(new Vector2(0.5f, 0), 0f, 2f);
        var cubic = StickResponse.Apply(new Vector2(0.5f, 0), 0f, 3f);
        Assert.Equal(0.5f, linear.X, 4);
        Assert.Equal(0.25f, quadratic.X, 4);
        Assert.Equal(0.125f, cubic.X, 4);
        Assert.Equal(1f, StickResponse.Apply(new Vector2(0, 1), 0.1f, 3f).Y, 4);
    }

    // ---- last used device --------------------------------------------------------------------------------

    [Xunit.Fact]
    public void TheLastUsedDeviceFollowsRealUseNotNoise()
    {
        var last = new LastUsedDevice();
        var seen = new System.Collections.Generic.List<InputDeviceKind>();
        last.Changed += seen.Add;
        Assert.Equal(InputDeviceKind.KeyboardMouse, last.Current);

        last.Observe(new DeviceActivity { PadStick = 0.2f, MouseMoved = 1f });      // drift and a nudge: nothing
        Assert.Empty(seen);
        last.Observe(new DeviceActivity { PadButton = true });
        Assert.Equal(InputDeviceKind.Gamepad, last.Current);
        last.Observe(new DeviceActivity { PadStick = 0.9f });                       // still the pad: no new signal
        last.Observe(new DeviceActivity { Key = true, PadStick = 0.9f });            // both at once: stays
        Assert.Equal(InputDeviceKind.Gamepad, last.Current);
        last.Observe(new DeviceActivity { MouseMoved = 10f });
        Assert.Equal(InputDeviceKind.KeyboardMouse, last.Current);
        Assert.Equal(new[] { InputDeviceKind.Gamepad, InputDeviceKind.KeyboardMouse }, seen);
    }

    [Xunit.Fact]
    public void APromptShowsTheBindingOfTheDeviceInUse()
    {
        var bindings = new[] { new InputBinding { Key = "E" }, new InputBinding { Gamepad = "A" } };
        Assert.Equal("E", InputGlyphs.Pick(bindings, InputDeviceKind.KeyboardMouse)!.DisplayName);
        Assert.Equal("Pad A", InputGlyphs.Pick(bindings, InputDeviceKind.Gamepad)!.DisplayName);
        Assert.Equal("E", InputGlyphs.Pick(new[] { bindings[0] }, InputDeviceKind.Gamepad)!.DisplayName);   // only a key: show it
        Assert.Null(InputGlyphs.Pick(null, InputDeviceKind.Gamepad));
    }

    // ---- scripted input for a chosen player ---------------------------------------------------------------

    private static (ActionRegistry registry, ActionId attack, ActionId move) Registry()
    {
        var registry = new ActionRegistry();
        var attack = registry.Register("Attack", ActionKind.Button);
        var move = registry.Register("Move", ActionKind.Axis2D);
        return (registry, attack, move);
    }

    private sealed class Frame
    {
        public bool[] Held = new bool[2], Raw = new bool[2];
        public Vector2[] Axis = new Vector2[2];
    }

    // Done: `in_` scripted input covers a second player. What is injected for player 2 lands only in player 2's
    // frame, ages in time, and `in_clear @2` leaves the first player's script alone.
    [Xunit.Fact]
    public void AScriptForTheSecondPlayerLandsInTheirFrameOnly()
    {
        var (registry, attack, move) = Registry();
        var script = new ScriptedInput();
        script.Inject(1, attack, Vector2.One, 1f);
        script.Inject(1, move, new Vector2(0, 1), float.PositiveInfinity);
        script.Inject(0, move, new Vector2(1, 0), 0.5f);

        var p1 = new Frame();
        var p2 = new Frame();
        script.Apply(0, 0.25f, registry, p1.Held, p1.Raw, p1.Axis);
        script.Apply(1, 0.25f, registry, p2.Held, p2.Raw, p2.Axis);
        Assert.False(p1.Held[attack.Index]);
        Assert.Equal(new Vector2(1, 0), p1.Axis[move.Index]);
        Assert.True(p2.Held[attack.Index] && p2.Raw[attack.Index]);
        Assert.Equal(new Vector2(0, 1), p2.Axis[move.Index]);

        Assert.Contains("P2 Attack", string.Join("\n", script.Describe(registry)));

        script.Apply(0, 0.5f, registry, new bool[2], new bool[2], new Vector2[2]);          // player 1's script ends
        script.Apply(1, 1f, registry, new bool[2], new bool[2], new Vector2[2]);             // player 2's attack ends, the axis stays
        Assert.Equal(1, script.Count);
        script.Clear(1);
        Assert.Equal(0, script.Count);
    }

    [Xunit.Fact]
    public void ATapIsOneFrameAndTheLastScriptForAnActionWins()
    {
        var (registry, attack, _) = Registry();
        var script = new ScriptedInput();
        script.Inject(1, attack, Vector2.One, 0f, oneFrame: true);
        script.Inject(1, attack, Vector2.One, 5f);                                  // replaces the tap
        Assert.Equal(1, script.Count);
        script.Release(1, attack);
        script.Inject(1, attack, Vector2.One, 0f, oneFrame: true);
        var f = new Frame();
        script.Apply(1, 0.016f, registry, f.Held, f.Raw, f.Axis);
        Assert.True(f.Held[attack.Index]);
        Assert.Equal(0, script.Count);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Attack|@2", true, 1, 1)]
    [Xunit.InlineData("Attack", true, 0, 1)]
    [Xunit.InlineData("Move|0|1|3|@4", true, 3, 4)]
    [Xunit.InlineData("Attack|@0", false, 0, 0)]
    [Xunit.InlineData("Attack|@9", false, 0, 0)]
    [Xunit.InlineData("Attack|@x", false, 0, 0)]
    public void TheConsoleNamesAPlayerWithATrailingAt(string args, bool ok, int player, int remaining)
    {
        var list = args.Split('|');
        Assert.Equal(ok, ScriptedInput.TakePlayer(list, out int p, out int n));
        if (!ok) return;
        Assert.Equal(player, p);
        Assert.Equal(remaining, n);
    }
}
