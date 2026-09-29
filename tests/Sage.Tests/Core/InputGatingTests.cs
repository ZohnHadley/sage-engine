#nullable enable

namespace Sage.Tests;

using Assert = Xunit.Assert;

// When the game may act on input, and what counts as a press (docs/design/08 §3.1, review #60).
//
// These are the three ways the player swung with nobody pressing anything: a window that did not have
// focus, a button a screen had already swallowed, and an analogue trigger resting on its threshold. The
// devices live in `Sage.Client` and cannot be tested here — which is exactly why the *decisions* are in
// `Sage.Simulation`, where this file can see them.
public class InputGatingTests
{
    // Alt-tab away mid-stride and the player must stop walking; alt-tab back with the key still down and
    // they must not have "just pressed" it. Both cases are one frame at the seam, and both were wrong.
    [Xunit.Fact]
    public void FocusIsReadOnTheWayInAndNeutralWhileAway()
    {
        var policy = new FocusPolicy();

        // Frame one is a resync whatever it says: there is no previous frame, so a key already down as
        // the game starts is not a press on frame one.
        Assert.Equal(FocusStep.ReadAndResync, policy.Step(true));
        Assert.True(policy.Focused);
        Assert.Equal(FocusStep.Read, policy.Step(true));

        // Losing focus: this frame is neutral, so the release lands and the walking stops.
        Assert.Equal(FocusStep.Neutral, policy.Step(false));
        Assert.False(policy.Focused);
        Assert.Equal(FocusStep.Neutral, policy.Step(false));   // and stays quiet

        // Coming back: read *and* forget, so a held button is not a new press and the cursor does not
        // jump by however far it travelled across the desk.
        Assert.Equal(FocusStep.ReadAndResync, policy.Step(true));
        Assert.Equal(FocusStep.Read, policy.Step(true));
    }

    // A game that starts unfocused never sees a phantom press either.
    [Xunit.Fact]
    public void StartingUnfocusedIsNeutralUntilTheWindowIsOurs()
    {
        var policy = new FocusPolicy();

        Assert.Equal(FocusStep.Neutral, policy.Step(false));
        Assert.Equal(FocusStep.Neutral, policy.Step(false));
        Assert.Equal(FocusStep.ReadAndResync, policy.Step(true));
    }

    // Holding the attack button, opening a screen and closing it again must not swing: the press is
    // swallowed while the screen is up, and what follows is the *same* press, not a new one.
    [Xunit.Fact]
    public void AButtonSwallowedByAScreenIsNotPressedAgainWhenItCloses()
    {
        bool raw = true, wasRaw = false;

        // Frame 1: pressed for real.
        Assert.True(InputEdges.Pressed(raw, wasRaw, consumed: false));
        Assert.True(InputEdges.Held(raw, consumed: false));

        // Frame 2: a screen opens and swallows it. Not held, not pressed.
        wasRaw = true;
        Assert.False(InputEdges.Pressed(raw, wasRaw, consumed: true));
        Assert.False(InputEdges.Held(raw, consumed: true));

        // Frame 3: the screen closes with the button still down. This was the bug — the filtered state
        // had gone false, so the finger that never moved read as a fresh press.
        Assert.False(InputEdges.Pressed(raw, wasRaw, consumed: false));
        Assert.True(InputEdges.Held(raw, consumed: false));

        // Frame 4: let go, press again. *That* is a new press.
        Assert.True(InputEdges.Released(rawHeld: false, wasRawHeld: true));
        Assert.True(InputEdges.Pressed(rawHeld: true, wasRawHeld: false, consumed: false));
    }

    // A release lands even while consumed: whatever began the press has to be told it ended, or an
    // action swallowed half-way through is held down for ever.
    [Xunit.Fact]
    public void AReleaseStillLandsWhileSomethingIsSwallowingTheButton()
    {
        Assert.True(InputEdges.Released(rawHeld: false, wasRawHeld: true));
        Assert.False(InputEdges.Released(rawHeld: true, wasRawHeld: true));
        Assert.False(InputEdges.Released(rawHeld: false, wasRawHeld: false));
    }

    // An analogue trigger resting near its threshold used to cross it every frame, and a button that
    // chatters is a press every frame — which for a sword is a swing every time its cooldown ends.
    [Xunit.Fact]
    public void ATriggerRestingOnItsThresholdDoesNotChatter()
    {
        bool down = false;

        // Creeping up to the middle: not yet a press.
        foreach (float resting in new[] { 0.45f, 0.5f, 0.55f, 0.5f, 0.45f })
        {
            down = InputEdges.TriggerDown(resting, down);
            Assert.False(down, $"a trigger resting at {resting} is not a press");
        }

        // Pulled properly: down, and it stays down through the same jitter that never pressed it.
        down = InputEdges.TriggerDown(0.8f, down);
        Assert.True(down);
        foreach (float jitter in new[] { 0.7f, 0.55f, 0.45f, 0.55f })
        {
            down = InputEdges.TriggerDown(jitter, down);
            Assert.True(down, $"a trigger held at {jitter} has not been let go");
        }

        // Let go: below the lower threshold, and only then.
        Assert.False(InputEdges.TriggerDown(0.3f, down));
    }
}
