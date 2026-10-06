#nullable enable
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Bindings, contexts, composites, rates and dead zones, headless (issue 4o-8). They used to live in the client's
// `InputActions`, which tests cannot reference; `ActionMapper` is the same logic over a fake device.
public class ActionMapperTests
{
    // A device vocabulary with a handful of keys and pad buttons.
    private sealed class Vocab : IInputVocabulary
    {
        private static readonly Dictionary<string, int> Keys = new(System.StringComparer.OrdinalIgnoreCase)
            { ["W"] = 87, ["A"] = 65, ["S"] = 83, ["D"] = 68, ["E"] = 69, ["Space"] = 32, ["Up"] = 38, ["Down"] = 40, ["Left"] = 37, ["Right"] = 39 };
        private static readonly Dictionary<string, int> Pad = new(System.StringComparer.OrdinalIgnoreCase)
            { ["A"] = 12, ["LeftTrigger"] = 8, ["RightTrigger"] = 9, ["LeftStick"] = 4 };

        public bool TryKey(string name, out int code) => Keys.TryGetValue(name, out code);
        public bool TryPadButton(string name, out int bit, out int trigger)
        {
            trigger = name.Equals("LeftTrigger", System.StringComparison.OrdinalIgnoreCase) ? 1
                    : name.Equals("RightTrigger", System.StringComparison.OrdinalIgnoreCase) ? 2 : 0;
            return Pad.TryGetValue(name, out bit);
        }
    }

    private sealed class Device : IInputState
    {
        public readonly HashSet<int> Down = new();
        public readonly HashSet<MouseInput> Mouse = new();
        public float Left, Right;
        public Vector2 LeftStick, RightStick;
        public float WheelDelta { get; set; }
        public Vector2 MouseDelta { get; set; }
        public bool KeyDown(int key) => Down.Contains(key);
        public bool MouseDown(MouseInput button) => Mouse.Contains(button);
        public bool PadDown(int bit) => false;
        public float Trigger(bool right) => right ? Right : Left;
        public Vector2 Stick(bool right) => right ? RightStick : LeftStick;
    }

    private const int W = 87, A = 65, E = 69, Space = 32;

    private readonly ActionRegistry _actions = new();
    private readonly Device _dev = new();
    private readonly ActionMapper _map;
    private readonly ActionInfo _attack, _jump, _move, _look, _zoom, _use;

    public ActionMapperTests()
    {
        _attack = _actions[_actions.Register("Attack", ActionKind.Button)];
        _jump = _actions[_actions.Register("Jump", ActionKind.Button)];
        _use = _actions[_actions.Register("Use", ActionKind.Button)];
        _move = _actions[_actions.Register("Move", ActionKind.Axis2D)];
        _look = _actions[_actions.Register("Look", ActionKind.Axis2D)];
        _zoom = _actions[_actions.Register("Zoom", ActionKind.Axis1D)];
        _map = new ActionMapper(_actions, new Vocab());
    }

    private void Bind(InputContext context, ActionInfo action, InputBinding b)
    {
        Assert.True(_map.Add(context, action, b, "test", out string? error), error);
    }

    private void Tick(float dt = 1f / 60f)
    {
        _map.Update(dt, _dev);
        _map.Finish();
    }

    // ---- Parse errors ----

    [Xunit.Theory]
    [Xunit.InlineData("", "no input given")]
    [Xunit.InlineData("mouse:", "names no input")]
    [Xunit.InlineData("joystick:1", "unknown input kind")]
    public void ConsoleBindTextThatDoesNotParseSaysWhy(string text, string reason)
    {
        Assert.False(InputBinding.TryParse(text, out _, out string error));
        Assert.Contains(reason, error);
    }

    [Xunit.Fact]
    public void ConsoleBindTextParsesEachKind()
    {
        Assert.True(InputBinding.TryParse("E", out var key, out _));
        Assert.Equal("E", key.Key);
        Assert.True(InputBinding.TryParse("mouse:Right", out var mouse, out _));
        Assert.Equal("Right", mouse.Mouse);
        Assert.True(InputBinding.TryParse("pad:A", out var pad, out _));
        Assert.Equal("A", pad.Gamepad);
        Assert.True(InputBinding.TryParse("composite:WASD", out var comp, out _));
        Assert.Equal("WASD", comp.Composite);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Attack", "Key", "Nonsense", "unknown key")]
    [Xunit.InlineData("Attack", "Mouse", "Back", "unknown mouse input")]
    [Xunit.InlineData("Attack", "Gamepad", "Bogus", "unknown gamepad input")]
    [Xunit.InlineData("Move", "Composite", "IJKL", "unknown composite")]
    [Xunit.InlineData("Attack", "Mouse", "Delta", "can't drive")]      // a delta cannot press a button
    [Xunit.InlineData("Move", "Key", "E", "can't drive")]              // a key cannot steer an Axis2D
    [Xunit.InlineData("Zoom", "Composite", "WASD", "can't drive")]
    public void MalformedBindingsAreRejectedWithAReason(string action, string field, string value, string reason)
    {
        var b = new InputBinding();
        switch (field)
        {
            case "Key": b.Key = value; break;
            case "Mouse": b.Mouse = value; break;
            case "Gamepad": b.Gamepad = value; break;
            default: b.Composite = value; break;
        }
        Assert.True(_actions.TryGet(action, out var info));
        Assert.False(_map.Add(InputContext.Gameplay, info, b, "test", out string? error));
        Assert.Contains(reason, error);
        Assert.Equal(0, _map.BindingCount(InputContext.Gameplay));
        Assert.Equal(error, ActionMapper.Validate(new Vocab(), info, b));
    }

    [Xunit.Fact]
    public void ABindingNeedsExactlyOneSource()
    {
        Assert.False(_map.Add(InputContext.Gameplay, _attack, new InputBinding(), "t", out string? none));
        Assert.Contains("exactly one", none);
        Assert.False(_map.Add(InputContext.Gameplay, _attack, new InputBinding { Key = "E", Mouse = "Left" }, "t", out string? two));
        Assert.Contains("exactly one", two);
        Assert.Null(ActionMapper.Validate(new Vocab(), _attack, new InputBinding { Key = "E" }));
    }

    // ---- Context masking ----

    [Xunit.Fact]
    public void ADisplacedContextDoesNotHearWhatAHigherOneBinds()
    {
        Bind(InputContext.Gameplay, _attack, new InputBinding { Key = "E" });
        Bind(InputContext.UI, _use, new InputBinding { Key = "E" });
        _map.SetActive(InputContext.UI, true);
        _dev.Down.Add(E);

        Tick();
        Assert.True(_map.Held(_use.Id));       // the UI binds E and reads it first
        Assert.False(_map.Held(_attack.Id));   // so gameplay never sees it
        Assert.False(_map.Pressed(_attack.Id));
    }

    [Xunit.Fact]
    public void AnInactiveContextBindsNothing()
    {
        Bind(InputContext.Gameplay, _attack, new InputBinding { Key = "E" });
        Bind(InputContext.Editor, _use, new InputBinding { Key = "E" });
        _dev.Down.Add(E);
        Tick();
        Assert.False(_map.Held(_use.Id));      // the editor is not active
        Assert.True(_map.Held(_attack.Id));

        _map.SetActive(InputContext.Editor, true);
        Tick();
        Assert.True(_map.Held(_use.Id));
        Assert.False(_map.Held(_attack.Id));
    }

    [Xunit.Fact]
    public void AnOpenConsoleSwallowsTheWholeKeyboardButNotTheMouse()
    {
        Bind(InputContext.Gameplay, _jump, new InputBinding { Key = "Space" });
        Bind(InputContext.Gameplay, _attack, new InputBinding { Mouse = "Left" });
        _dev.Down.Add(Space);
        _dev.Mouse.Add(MouseInput.Left);
        _map.SetActive(InputContext.Console, true);

        Tick();
        Assert.False(_map.Held(_jump.Id));
        Assert.True(_map.Held(_attack.Id));
    }

    [Xunit.Fact]
    public void WhenTheUiWantsTheMouseItTakesTheMouseAndTheLookDelta()
    {
        Bind(InputContext.Gameplay, _attack, new InputBinding { Mouse = "Left" });
        Bind(InputContext.Gameplay, _look, new InputBinding { Mouse = "Delta" });
        Bind(InputContext.Gameplay, _jump, new InputBinding { Key = "Space" });
        _dev.Mouse.Add(MouseInput.Left);
        _dev.MouseDelta = new Vector2(10, 0);
        _dev.Down.Add(Space);
        _map.UiWantsMouse = true;

        Assert.True(_map.IsActive(InputContext.UI));
        Tick();
        Assert.False(_map.Held(_attack.Id));
        Assert.Equal(Vector2.Zero, _map.Axis2(_look.Id));
        Assert.True(_map.Held(_jump.Id));      // the keyboard is not captured
    }

    // The player swung at nothing (#60): the button was swallowed by a screen, the screen closed with the button
    // still down, and the action looked newly pressed. Edges come from the raw state.
    [Xunit.Fact]
    public void RegressionClosingAScreenWithAButtonHeldIsNotANewPress()
    {
        Bind(InputContext.Gameplay, _attack, new InputBinding { Mouse = "Left" });
        _dev.Mouse.Add(MouseInput.Left);
        _map.UiWantsMouse = true;
        Tick();
        Tick();
        Assert.False(_map.Held(_attack.Id));
        Assert.False(_map.Pressed(_attack.Id));

        _map.UiWantsMouse = false;             // the screen closes, the finger never moved
        Tick();
        Assert.True(_map.Held(_attack.Id));    // held, yes
        Assert.False(_map.Pressed(_attack.Id)); // but nobody pressed it
        Assert.True((_map.PressedMask.Bits & (1UL << _attack.Id.Bit)) == 0);
    }

    [Xunit.Fact]
    public void RegressionAPressStartedWhileSwallowedStillReleasesLoudly()
    {
        Bind(InputContext.Gameplay, _attack, new InputBinding { Mouse = "Left" });
        _dev.Mouse.Add(MouseInput.Left);
        Tick();
        Assert.True(_map.Pressed(_attack.Id));
        Tick();
        Assert.False(_map.Pressed(_attack.Id));   // one press, not one per frame

        _map.UiWantsMouse = true;
        _dev.Mouse.Clear();
        Tick();
        Assert.True(_map.Released(_attack.Id));   // the release lands though the action was swallowed
    }

    // Two keys on one action do not make two presses (edges come from the action's state).
    [Xunit.Fact]
    public void TwoKeysOnOneActionPressOnce()
    {
        Bind(InputContext.Gameplay, _jump, new InputBinding { Key = "Space" });
        Bind(InputContext.Gameplay, _jump, new InputBinding { Key = "E" });
        _dev.Down.Add(Space);
        Tick();
        Assert.True(_map.Pressed(_jump.Id));
        _dev.Down.Add(E);
        Tick();
        Assert.False(_map.Pressed(_jump.Id));
        Assert.True(_map.Held(_jump.Id));
    }

    // ---- Trigger hysteresis (#60) ----

    [Xunit.Fact]
    public void RegressionARestingTriggerDoesNotChatter()
    {
        Bind(InputContext.Gameplay, _attack, new InputBinding { Gamepad = "RightTrigger" });
        int presses = 0;
        // Wobbles around 0.5, between the release (0.4) and press (0.6) thresholds, after crossing up once.
        foreach (float v in new[] { 0.1f, 0.65f, 0.5f, 0.55f, 0.45f, 0.58f, 0.42f, 0.5f })
        {
            _dev.Right = v;
            Tick();
            if (_map.Pressed(_attack.Id)) presses++;
        }
        Assert.Equal(1, presses);
        Assert.True(_map.Held(_attack.Id));

        _dev.Right = 0.3f;
        Tick();
        Assert.False(_map.Held(_attack.Id));
        Assert.True(_map.Released(_attack.Id));
    }

    [Xunit.Fact]
    public void TwoActionsOnOneTriggerKeepTheirOwnHysteresis()
    {
        Bind(InputContext.Gameplay, _attack, new InputBinding { Gamepad = "LeftTrigger" });
        Bind(InputContext.UI, _use, new InputBinding { Gamepad = "LeftTrigger" });
        _map.SetActive(InputContext.UI, true);
        _dev.Left = 0.7f;
        Tick();
        _dev.Left = 0.5f;
        Tick();
        Assert.True(_map.Held(_use.Id));          // UI heard it; each binding stepped its hysteresis once
    }

    // ---- Composites and axes ----

    [Xunit.Fact]
    public void WasdCompositeResolvesAndCancelsOpposites()
    {
        Bind(InputContext.Gameplay, _move, new InputBinding { Composite = "WASD" });
        _dev.Down.Add(W);
        Tick();
        Assert.Equal(new Vector2(0, 1), _map.Axis2(_move.Id));
        _dev.Down.Add(A);
        Tick();
        Assert.Equal(new Vector2(-1, 1), _map.Axis2(_move.Id));
        _dev.Down.Add(68);   // D
        Tick();
        Assert.Equal(new Vector2(0, 1), _map.Axis2(_move.Id));   // A and D cancel
        _dev.Down.Add(83);   // S
        Tick();
        Assert.Equal(Vector2.Zero, _map.Axis2(_move.Id));
    }

    [Xunit.Fact]
    public void ArrowsCompositeUsesTheArrowKeys()
    {
        Bind(InputContext.Gameplay, _move, new InputBinding { Composite = "arrows" });
        _dev.Down.Add(39);   // Right
        Tick();
        Assert.Equal(new Vector2(1, 0), _map.Axis2(_move.Id));
    }

    [Xunit.Fact]
    public void ACompositeAndAStickAddUp()
    {
        Bind(InputContext.Gameplay, _move, new InputBinding { Composite = "WASD" });
        Bind(InputContext.Gameplay, _move, new InputBinding { Gamepad = "LeftStick", Deadzone = 0f });
        _dev.Down.Add(W);
        _dev.LeftStick = new Vector2(0.5f, 0);
        Tick();
        Assert.Equal(new Vector2(0.5f, 1), _map.Axis2(_move.Id));
    }

    [Xunit.Fact]
    public void ScaleAndInvertApplyToAxes()
    {
        Bind(InputContext.Gameplay, _move, new InputBinding { Composite = "WASD", Scale = 2f, Invert = true });
        _dev.Down.Add(W);
        Tick();
        Assert.Equal(new Vector2(0, -2), _map.Axis2(_move.Id));   // invert flips Y for an Axis2D
    }

    [Xunit.Fact]
    public void AConsumedCompositeContributesNothing()
    {
        Bind(InputContext.Gameplay, _move, new InputBinding { Composite = "WASD" });
        Bind(InputContext.UI, _use, new InputBinding { Key = "W" });
        _map.SetActive(InputContext.UI, true);
        _dev.Down.Add(W);
        Tick();
        Assert.Equal(Vector2.Zero, _map.Axis2(_move.Id));
    }

    // ---- Rate bindings ----

    [Xunit.Fact]
    public void RateBindingsScaleByFrameTimeAndOthersDoNot()
    {
        Bind(InputContext.Gameplay, _look, new InputBinding { Gamepad = "RightStick", Deadzone = 0f, Rate = true, Scale = 3f });
        _dev.RightStick = new Vector2(1, 0);
        Tick(0.5f);
        Assert.Equal(new Vector2(1.5f, 0), _map.Axis2(_look.Id));
        Tick(0.1f);
        Assert.Equal(0.3f, _map.Axis2(_look.Id).X, 4);

        var steady = new ActionMapper(_actions, new Vocab());
        Assert.True(steady.Add(InputContext.Gameplay, _look, new InputBinding { Gamepad = "RightStick", Deadzone = 0f, Scale = 3f }, "t", out _));
        steady.Update(0.5f, _dev);
        Assert.Equal(new Vector2(3, 0), steady.Axis2(_look.Id));
    }

    [Xunit.Fact]
    public void MouseDeltaUsesSensitivityAndInvert()
    {
        Bind(InputContext.Gameplay, _look, new InputBinding { Mouse = "Delta" });
        _dev.MouseDelta = new Vector2(10, 4);
        _map.Update(0.016f, _dev, sensitivity: 2f, invertY: false);
        Assert.Equal(new Vector2(20, -8), _map.Axis2(_look.Id));
        _map.Update(0.016f, _dev, sensitivity: 1f, invertY: true);
        Assert.Equal(new Vector2(10, 4), _map.Axis2(_look.Id));
    }

    [Xunit.Fact]
    public void WheelAndTriggerDriveAnAxis1D()
    {
        Bind(InputContext.Gameplay, _zoom, new InputBinding { Mouse = "Wheel" });
        _dev.WheelDelta = 240;
        Tick();
        Assert.Equal(2f, _map.Axis(_zoom.Id));
    }

    // ---- Dead zones ----

    [Xunit.Fact]
    public void StickDeadZoneIsRadialAndRescaled()
    {
        Assert.Equal(Vector2.Zero, ActionMapper.DeadZone(new Vector2(0.1f, 0.1f), 0.2f));
        var full = ActionMapper.DeadZone(new Vector2(1, 0), 0.2f);
        Assert.Equal(1f, full.X, 4);
        var half = ActionMapper.DeadZone(new Vector2(0.6f, 0), 0.2f);
        Assert.Equal(0.5f, half.X, 4);   // (0.6 - 0.2) / 0.8
        // Direction is kept: a diagonal past the zone stays diagonal.
        var diag = ActionMapper.DeadZone(new Vector2(0.6f, 0.6f), 0.2f);
        Assert.Equal(diag.X, diag.Y, 5);
        Assert.True(ActionMapper.DeadZone(new Vector2(2, 0), 0.2f).Length() <= 1.0001f);   // clamped
    }

    [Xunit.Fact]
    public void TriggerAxisDeadZoneRescalesToOne()
    {
        Assert.Equal(0f, ActionMapper.DeadZone(0.2f, 0.2f));
        Assert.Equal(0.5f, ActionMapper.DeadZone(0.6f, 0.2f), 4);
        Assert.Equal(1f, ActionMapper.DeadZone(1f, 0.2f), 4);

        Bind(InputContext.Gameplay, _zoom, new InputBinding { Gamepad = "RightTrigger", Deadzone = 0.2f });
        _dev.Right = 0.15f;
        Tick();
        Assert.Equal(0f, _map.Axis(_zoom.Id));
        _dev.Right = 0.6f;
        Tick();
        Assert.Equal(0.5f, _map.Axis(_zoom.Id), 4);
    }

    // ---- State listing (in_showactions) ----

    [Xunit.Fact]
    public void DescribeStateListsOnlyWhatIsLive()
    {
        Bind(InputContext.Gameplay, _attack, new InputBinding { Key = "E" });
        Bind(InputContext.Gameplay, _move, new InputBinding { Composite = "WASD" });
        Tick();
        Assert.Equal("", _map.DescribeState());
        _dev.Down.Add(E);
        _dev.Down.Add(W);
        Tick();
        Assert.Equal("Attack(held, pressed) Move=[0.00, 1.00]", _map.DescribeState());
        Tick();
        Assert.Equal("Attack(held) Move=[0.00, 1.00]", _map.DescribeState());
    }
}
