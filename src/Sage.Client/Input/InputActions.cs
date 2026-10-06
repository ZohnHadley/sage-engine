#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Microsoft.Xna.Framework.Input;

namespace Sage.Client;

// Actions and input contexts (docs/design/08 §3.2–3.3). Bindings come from `input_map` records; each
// frame the contexts are evaluated top-down (Editor → Console → UI → Gameplay) and an input a
// higher active context binds (or captures) is consumed: lower contexts don't see it that frame.
// That's what stops a click on an ImGui window from also firing Attack.
//
//   Console  (active while the console is open)  consumes the whole keyboard
//   UI       (active while ImGui wants input)    consumes the keyboard and/or mouse ImGui captures
//
// Action state is per frame. Button edges (Pressed/Released) are computed from the action's held
// state, so two keys bound to one action don't produce two presses. The host feeds the result to a
// CommandLatch for the fixed ticks.
public sealed class InputActions
{
    private enum Source { Key, MouseButton, MouseDelta, MouseWheel, PadButton, PadTrigger, PadStick, Composite }

    private sealed class Binding
    {
        public required ActionInfo Action;
        public required InputBinding Desc;
        public required string Where;       // "sage:gameplay Jump[0]"
        public Source Source;
        public Keys Key;
        public MouseButton MouseButton;
        public Buttons PadButton;
        public bool RightSide;             // right stick / right trigger
        public Keys[] Composite = Array.Empty<Keys>();   // up, down, left, right
        public int[] Slots = Array.Empty<int>();         // consumption slots this binding reads

        // Whether this binding's analogue trigger counted as down last frame, for the hysteresis in
        // `InputEdges.TriggerDown` (#60). Per binding, because two actions may read the same trigger.
        public bool TriggerWasDown;
        public readonly bool[] TriggerWasDownOthers = new bool[PadAssignment.MaxPads];   // the same, for each other player's pad

        // What the device said this frame, read once in the raw pass and used again when the contexts
        // decide who hears it. Reading it twice would tick the trigger hysteresis twice.
        public bool RawDown;

        public bool IsTrigger => Source == Source.PadButton &&
                                 PadButton is Buttons.LeftTrigger or Buttons.RightTrigger;
    }

    // Consumption slots: keys 0..255, mouse buttons 256.., delta 270, wheel 271, pad buttons 300..331, sticks 340/341.
    private const int SlotCount = 512;
    private const int MouseButtonSlot = 256, MouseDeltaSlot = 270, MouseWheelSlot = 271, PadButtonSlot = 300, PadStickSlot = 340;

    private static readonly InputContext[] ContextOrder = { InputContext.Editor, InputContext.Console, InputContext.UI, InputContext.Gameplay };

    private readonly ActionRegistry _registry;
    private readonly RecordStore _records;
    private readonly InputDevices _devices;
    private readonly InputRebinds? _rebinds;
    private readonly CVar<float> _sensitivity;
    private readonly CVar<bool> _invertY;

    private readonly List<Binding>[] _bindings = Enumerable.Range(0, 4).Select(_ => new List<Binding>()).ToArray();
    private readonly bool[] _active = new bool[4];
    private readonly bool[] _consumed = new bool[SlotCount];

    // What the devices say, before anything swallows it. **Edges come from this**, not from the
    // filtered `_held`: an action consumed for one frame used to look un-held, so the frame the screen
    // closed it looked newly *pressed* and the character swung at nothing (#60, `InputEdges`).
    private bool[] _rawHeld = Array.Empty<bool>();
    private bool[] _wasRawHeld = Array.Empty<bool>();
    private bool[] _held = Array.Empty<bool>();
    private Vector2[] _axis = Array.Empty<Vector2>();
    private ActionMask _heldBits, _pressedBits, _releasedBits;
    // Scripted input (08 §9): actions driven from the console instead of a device, for automated
    // checks and for repeating a bug without a person at the keyboard. Injected *after* the bindings
    // are evaluated, so a script goes through the same PlayerCommand path a keyboard does — what it
    // exercises is the real chain, not a shortcut into the simulation.
    private readonly ScriptedInput _script = new();

    // The other local players (issue #331): player 1.. read their own pad, and only the gameplay context's
    // pad bindings (a keyboard has one owner). Player 0 is the state above, unchanged.
    private sealed class PlayerFrame
    {
        public bool[] RawHeld = Array.Empty<bool>(), WasRawHeld = Array.Empty<bool>(), Held = Array.Empty<bool>();
        public Vector2[] Axis = Array.Empty<Vector2>();
        public ActionMask HeldBits, PressedBits, ReleasedBits;
    }
    private readonly PlayerFrame[] _frames = Enumerable.Range(0, PadAssignment.MaxPads).Select(_ => new PlayerFrame()).ToArray();
    private readonly CVar<float> _joyDeadzone, _joyRumble;
    private readonly CVar<string> _joyCurve;
    private readonly CVar<int> _joyPlayers;
    private float _curve = 1f;

    public InputActions(ActionRegistry registry, RecordStore records, InputDevices devices, CVarRegistry cvars)
        : this(registry, records, devices, cvars, null) { }

    public InputActions(ActionRegistry registry, RecordStore records, InputDevices devices, CVarRegistry cvars, InputRebinds? rebinds)
    {
        _registry = registry;
        _rebinds = rebinds;
        // The names of keys and buttons are MonoGame's, so the one place that can check a rebind is here:
        // the same compile the bindings from records go through.
        if (rebinds != null) rebinds.Validator = (action, desc) => Compile(action, desc, "", out string? error) == null ? error : null;
        _records = records;
        _devices = devices;
        _sensitivity = cvars.Register("m_sensitivity", 1f, CVarFlags.Archive, "Mouse look speed multiplier (Look action and the editor camera).", 0.01f, 20f);
        _invertY = cvars.Register("m_invert_y", false, CVarFlags.Archive, "Invert mouse look up/down.");
        // Sticks (issue #331): a floor under every binding's own dead zone (a drifting pad), and the response
        // curve of the whole stick. Radial, and the output still reaches 1.
        _joyDeadzone = cvars.Register("joy_deadzone", 0f, CVarFlags.Archive,
            "Dead zone every stick and trigger has at least, 0..0.9 (radial; a binding's own `deadzone` still applies if it is larger).", 0f, 0.9f);
        _joyCurve = cvars.Register("joy_curve", "linear", CVarFlags.Archive,
            "Stick response: linear, quadratic, cubic, or an exponent 0.1..8 (higher is finer near the centre).");
        _joyCurve.Changed += _ => ParseCurve();
        ParseCurve();
        _joyRumble = cvars.Register("joy_rumble", 1f, CVarFlags.Archive, "Strength of controller rumble, 0 (off) to 1.", 0f, 1f);
        _joyPlayers = cvars.Register("joy_players", 1, CVarFlags.None, "How many local players have a pad (1-4): the pads are handed out in order, and a pad that is unplugged and replugged returns to its player.", 1, 4);
        _joyPlayers.Changed += _ => _devices.Pads.Players = _joyPlayers.Value;
        _devices.Pads.Players = _joyPlayers.Value;
        cvars.RegisterCommand("pad_list", CVarFlags.None, "Show which pad drives which player.", _ =>
        {
            for (int pad = 0; pad < PadAssignment.MaxPads; pad++)
            {
                int player = _devices.Pads.PlayerOf(pad);
                Log.Info(LogCat.Console, $"  pad {pad + 1}: {(_devices.Pads.IsConnected(pad) ? "connected" : "absent")}, " +
                                         (player >= 0 ? $"player {player + 1}" : "no player"));
            }
            Log.Info(LogCat.Console, $"  last used: {_devices.LastDevice.Current}; {_devices.Pads.Players} local player(s)");
        });
        cvars.RegisterCommand("pad_assign", CVarFlags.None, "pad_assign <player> <pad>: give a connected pad to a player (both from 1).", a =>
        {
            if (a.Count < 2 || !int.TryParse(a[0], out int player) || !int.TryParse(a[1], out int pad) || !_devices.Pads.Assign(player - 1, pad - 1))
                Log.Warn(LogCat.Console, "pad_assign <player> <pad>: both from 1, the pad must be connected and the player within joy_players");
        });
        cvars.RegisterCommand("bindlist", CVarFlags.None, "List input bindings per context.", _ => ListBindings());
        cvars.RegisterCommand("in_contexts", CVarFlags.None, "Show which input contexts are active.", _ =>
            Log.Info(LogCat.Console, $"  {string.Join(", ", ContextOrder.Select(c => $"{c}={(IsActive(c) ? "on" : "off")}"))}; " +
                                     $"UI captures keyboard={UiWantsKeyboard} mouse={UiWantsMouse}; " +
                                     $"window {(_devices.Focused ? "focused" : "unfocused — devices are ignored (#60)")}"));
        // Typing, for the same reason the other in_* commands exist: a screen with a field in it is
        // only checkable end to end if a script can put characters into it (08 §3.5). It pushes them
        // through the same buffer the window fills, so what a test drives is what a player types.
        cvars.RegisterCommand("in_type", CVarFlags.DevOnly,
            "in_type <text>: type this into whatever has the keyboard (automated tests). `\b` is backspace.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "in_type <text>"); return; }
            string text = string.Join(' ', a.Args).Replace("\b", "");
            foreach (char c in text) devices.PushTyped(c);
        });

        // The cursor, for the same reason: a screen that answers the mouse can only be checked end to
        // end if a script can put the pointer somewhere (08 §3.5, 13 §3). Hovering is enough to prove
        // the mapping from window pixels to the UI's — a click then lands where the hover highlighted.
        cvars.RegisterCommand("in_cursor", CVarFlags.DevOnly,
            "in_cursor <x> <y>: move the pointer, in window pixels (automated tests).", a =>
        {
            if (a.Count < 2 || !int.TryParse(a[0], out int x) || !int.TryParse(a[1], out int y))
            {
                Log.Warn(LogCat.Console, "in_cursor <x> <y>");
                return;
            }
            Microsoft.Xna.Framework.Input.Mouse.SetPosition(x, y);
        });

        // Every in_* command takes a trailing `@N` to aim at the Nth local player (`in_tap Attack @2`); with none it
        // is the first player's (issue #331).
        cvars.RegisterCommand("in_tap", CVarFlags.DevOnly, "in_tap <action> [@player]: press a button action for one frame (automated tests).", a =>
        {
            if (!ScriptedInput.TakePlayer(a.Args, out int player, out int n) || n == 0 || !_registry.TryGet(a[0], out var info) || info.Kind != ActionKind.Button)
                Log.Warn(LogCat.Console, "in_tap <button action> [@player]");
            else _script.Inject(player, info.Id, Vector2.One, 0f, oneFrame: true);
        });

        cvars.RegisterCommand("in_hold", CVarFlags.DevOnly,
            "in_hold <action> <seconds> [@player]: hold a button action down for a while (0 releases it).", a =>
        {
            if (!ScriptedInput.TakePlayer(a.Args, out int player, out int n) || n < 1 || !_registry.TryGet(a[0], out var info) || info.Kind != ActionKind.Button)
            {
                Log.Warn(LogCat.Console, "in_hold <button action> <seconds> [@player]");
                return;
            }
            float seconds = n > 1 ? Number(a[1]) : 1f;
            if (seconds <= 0f) _script.Release(player, info.Id);
            else _script.Inject(player, info.Id, Vector2.One, seconds);
        });

        cvars.RegisterCommand("in_axis", CVarFlags.DevOnly,
            "in_axis <action> <x> [y] [seconds] [@player]: drive an axis action (no seconds = until in_axis 0 or in_clear).", a =>
        {
            if (!ScriptedInput.TakePlayer(a.Args, out int player, out int n) || n < 2 || !_registry.TryGet(a[0], out var info) || info.Kind == ActionKind.Button)
            {
                Log.Warn(LogCat.Console, "in_axis <axis action> <x> [y] [seconds] [@player]");
                return;
            }
            var value = new Vector2(Number(a[1]), n > 2 ? Number(a[2]) : 0f);
            float seconds = n > 3 ? Number(a[3]) : float.PositiveInfinity;
            if (value == Vector2.Zero) _script.Release(player, info.Id);
            else _script.Inject(player, info.Id, value, seconds);
        });

        cvars.RegisterCommand("in_look", CVarFlags.DevOnly,
            "in_look <yaw°/s> [pitch°/s] [seconds] [@player]: turn the view at a steady rate (0 stops).", a =>
        {
            if (!ScriptedInput.TakePlayer(a.Args, out int player, out int n) || !_registry.TryGet("Look", out var look) || look.Kind != ActionKind.Axis2D)
            {
                Log.Warn(LogCat.Console, "in_look: there is no Look axis action, or a bad @player");
                return;
            }
            // Degrees per second, not the raw axis: Look is a per-frame delta in radians (the client
            // has already scaled the mouse), so a script that wrote it directly would turn at a rate
            // that depended on the frame rate — and an automated check that is not reproducible is
            // not a check. The rate is converted here and multiplied by dt where it is applied.
            float yaw = n > 0 ? Number(a[0]) : 0f;
            float pitch = n > 1 ? Number(a[1]) : 0f;
            float seconds = n > 2 ? Number(a[2]) : float.PositiveInfinity;
            const float ToRadians = MathF.PI / 180f;
            // +x turns right, and AddLook subtracts it (yaw is counter-clockwise), so a positive
            // "yaw" here means "turn right" the way a player would describe it.
            if (yaw == 0f && pitch == 0f) _script.Release(player, look.Id);
            else _script.Inject(player, look.Id, new Vector2(yaw * ToRadians, pitch * ToRadians), seconds, rate: true);
        });

        cvars.RegisterCommand("in_release", CVarFlags.DevOnly, "in_release <action> [@player]: stop driving one action from a script.", a =>
        {
            if (!ScriptedInput.TakePlayer(a.Args, out int player, out int n) || n == 0 || !_registry.TryGet(a[0], out var info))
                Log.Warn(LogCat.Console, "in_release <action> [@player]");
            else _script.Release(player, info.Id);
        });

        cvars.RegisterCommand("in_clear", CVarFlags.DevOnly, "in_clear [@player]: stop driving every action from a script (hands control back).", a =>
        {
            if (!ScriptedInput.TakePlayer(a.Args, out int player, out int n)) { Log.Warn(LogCat.Console, "in_clear [@player]"); return; }
            if (a.Count > 0) _script.Clear(player); else _script.Clear();
            Log.Info(LogCat.Console, "scripted input cleared");
        });

        cvars.RegisterCommand("in_scripted", CVarFlags.DevOnly, "What a script is currently holding down.", _ =>
        {
            if (_script.Count == 0) { Log.Info(LogCat.Console, "  nothing scripted"); return; }
            foreach (string line in _script.Describe(_registry)) Log.Info(LogCat.Console, "  " + line);
        });
        _records.Reloaded += Rebuild;   // the first record load builds the bindings
        _active[(int)InputContext.Gameplay] = true;
    }

    public float MouseSensitivity => _sensitivity.Value;
    public bool InvertMouseY => _invertY.Value;

    // Set by the host each frame from ImGui's capture flags (the UI context).
    public bool UiWantsKeyboard { get; set; }
    public bool UiWantsMouse { get; set; }

    public void SetActive(InputContext context, bool active) => _active[(int)context] = active;
    // The UI context is active when ImGui wants input *or* when a game screen is open (13 §3, F38):
    // both need the gameplay bindings out of the way, and they arrive by different routes.
    public bool IsActive(InputContext context) =>
        context == InputContext.UI ? _active[(int)context] || UiWantsKeyboard || UiWantsMouse : _active[(int)context];

    // ---- Queries (this frame) ----
    public bool Held(ActionId a) => a.IsValid && a.Index < _held.Length && _held[a.Index];
    public bool Pressed(ActionId a) =>
        a.IsValid && a.Index < _held.Length &&
        InputEdges.Pressed(_rawHeld[a.Index], _wasRawHeld[a.Index], _rawHeld[a.Index] && !_held[a.Index]);
    public bool Released(ActionId a) =>
        a.IsValid && a.Index < _held.Length && InputEdges.Released(_rawHeld[a.Index], _wasRawHeld[a.Index]);
    public float Axis(ActionId a) => a.IsValid && a.Index < _axis.Length ? _axis[a.Index].X : 0f;
    public Vector2 Axis2(ActionId a) => a.IsValid && a.Index < _axis.Length ? _axis[a.Index] : Vector2.Zero;

    // ---- Scripted input ----------------------------------------------------------------------------

    private static float Number(string text) =>
        float.TryParse(text, System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out float value) ? value : 0f;

    public ActionMask HeldMask => _heldBits;
    public ActionMask PressedMask => _pressedBits;
    public ActionMask ReleasedMask => _releasedBits;

    // ---- Per frame ----
    public void Update(float dt)
    {
        EnsureCapacity();
        CaptureNextInput();
        Array.Copy(_rawHeld, _wasRawHeld, _rawHeld.Length);
        Array.Clear(_held);
        Array.Clear(_rawHeld);
        Array.Clear(_axis);
        Array.Clear(_consumed);

        // **First, what the devices say** — every binding in every context, active or not. Edges are
        // computed from this, so a context switching back on (a console closing, a screen closing) does
        // not look like a finger arriving on a button that never moved (#60).
        foreach (var list in _bindings)
            foreach (var b in list)
                if (b.Action.Kind == ActionKind.Button)
                    _rawHeld[b.Action.Id.Index] |= ReadRaw(b);

        foreach (var context in ContextOrder)
        {
            if (!IsActive(context)) continue;
            if (context == InputContext.UI)
            {
                if (UiWantsKeyboard) ConsumeRange(0, 256);
                if (UiWantsMouse) { ConsumeRange(MouseButtonSlot, 8); _consumed[MouseDeltaSlot] = _consumed[MouseWheelSlot] = true; }
            }
            var list = _bindings[(int)context];
            foreach (var b in list)
                Evaluate(b, dt, IsConsumed(b));
            foreach (var b in list)
                foreach (int slot in b.Slots) _consumed[slot] = true;
            if (context == InputContext.Console) ConsumeRange(0, 256);
            // A game screen is modal: it reads its own actions first (above) and then swallows the
            // rest, so nobody walks about or swings behind an open inventory.
            if (context == InputContext.UI && _active[(int)InputContext.UI])
            {
                ConsumeRange(0, 256);
                ConsumeRange(MouseButtonSlot, 8);
                _consumed[MouseDeltaSlot] = true;
            }
        }
        _script.Apply(0, dt, _registry, _held, _rawHeld, _axis);

        _heldBits = _pressedBits = _releasedBits = default;
        var all = _registry.All;
        for (int a = 0; a < all.Count; a++)   // for, not foreach: no enumerator allocation per frame
        {
            var info = all[a];
            if (info.Kind != ActionKind.Button) continue;
            int i = info.Id.Index;
            // Swallowed: the devices say it is down but something above this took it, so it is not
            // held *and* the press it began must not surface later (`InputEdges`).
            bool swallowed = _rawHeld[i] && !_held[i];
            if (InputEdges.Held(_rawHeld[i], swallowed)) _heldBits = _heldBits.With(info.Id);
            if (InputEdges.Pressed(_rawHeld[i], _wasRawHeld[i], swallowed)) _pressedBits = _pressedBits.With(info.Id);
            if (InputEdges.Released(_rawHeld[i], _wasRawHeld[i])) _releasedBits = _releasedBits.With(info.Id);
        }

        for (int p = 1; p < _devices.Pads.Players; p++) UpdateSecondary(p, dt);
    }

    // Capture-next-input (the controls screen, #328): while the rebinds are waiting for a press, the first
    // new key, mouse button or pad button is offered to them. Escape, the left mouse button and the pad's
    // B, Back and Start are not offered: they are how a player cancels or clicks the screen's own buttons.
    // The frame a capture begins is skipped, so the click that began it is not what it captures.
    private static readonly Keys[] AllKeys = (Keys[])Enum.GetValues(typeof(Keys));
    private static readonly Buttons[] PadButtons =
    {
        Buttons.A, Buttons.X, Buttons.Y, Buttons.LeftShoulder, Buttons.RightShoulder, Buttons.LeftTrigger, Buttons.RightTrigger,
        Buttons.LeftStick, Buttons.RightStick, Buttons.DPadUp, Buttons.DPadDown, Buttons.DPadLeft, Buttons.DPadRight,
    };

    private void CaptureNextInput()
    {
        if (_rebinds is not { Capturing: true } || _rebinds.TakeFresh()) return;
        var kb = _devices.Keyboard;
        foreach (var key in AllKeys)
            if (key != Keys.None && key != Keys.Escape && kb.IsKeyPressed(key))
            {
                _rebinds.Offer(new InputBinding { Key = key.ToString() });
                return;
            }
        var mouse = _devices.Mouse;
        foreach (var button in new[] { MouseButton.RIGHT, MouseButton.MIDDLE })
            if (mouse.IsButtonPressed(button))
            {
                _rebinds.Offer(new InputBinding { Mouse = button == MouseButton.RIGHT ? "Right" : "Middle" });
                return;
            }
        foreach (var button in PadButtons)
            if (_devices.Gamepad.IsPressed(button))
            {
                string name = button switch { Buttons.LeftStick => "LeftStickButton", Buttons.RightStick => "RightStickButton", _ => button.ToString() };
                _rebinds.Offer(new InputBinding { Gamepad = name });
                return;
            }
    }

    // What one button binding's device says, regardless of who is listening. Called once per binding
    // per frame, which matters: the trigger hysteresis has state, and reading it twice would step it
    // twice (`InputEdges.TriggerDown`).
    private bool ReadRaw(Binding b)
    {
        var pad = _devices.Gamepad;
        bool down = b.Source switch
        {
            Source.Key => _devices.Keyboard.IsKeyDown(b.Key),
            Source.MouseButton => _devices.Mouse.IsButtonDown(b.MouseButton),
            // A trigger is analogue, so "down" needs two thresholds or it chatters where it rests
            // (#60). `IsButtonDown(LeftTrigger)` is one fixed threshold, which is the chatter.
            Source.PadButton when b.IsTrigger =>
                InputEdges.TriggerDown(b.RightSide ? pad.RightTrigger : pad.LeftTrigger, b.TriggerWasDown),
            Source.PadButton => pad.IsDown(b.PadButton),
            _ => false,
        };
        if (b.IsTrigger) b.TriggerWasDown = down;
        b.RawDown = down;
        return down;
    }

    private void ConsumeRange(int start, int count) => Array.Fill(_consumed, true, start, count);

    private bool IsConsumed(Binding b)
    {
        foreach (int slot in b.Slots)
            if (_consumed[slot]) return true;
        return false;
    }

    private void Evaluate(Binding b, float dt, bool consumed)
    {
        int i = b.Action.Id.Index;
        var kb = _devices.Keyboard;
        var mouse = _devices.Mouse;
        var pad = _devices.Gamepad;
        var d = b.Desc;

        if (b.Action.Kind == ActionKind.Button)
        {
            // Already read in the raw pass; this only decides whether the action *hears* it.
            if (!consumed) _held[i] |= b.RawDown;
            return;
        }

        if (consumed) return;

        Vector2 v = b.Source switch
        {
            Source.Key => new Vector2(kb.IsKeyDown(b.Key) ? 1 : 0, 0),
            Source.MouseButton => new Vector2(mouse.IsButtonDown(b.MouseButton) ? 1 : 0, 0),
            Source.PadButton or Source.PadTrigger or Source.PadStick => ReadPadAxis(b, pad),
            Source.MouseWheel => new Vector2(mouse.ScrollWheelDelta / 120f, 0),
            Source.MouseDelta => new Vector2(mouse.PositionDelta.X, -mouse.PositionDelta.Y * (_invertY.Value ? -1 : 1)) * _sensitivity.Value,
            Source.Composite => new Vector2(
                (kb.IsKeyDown(b.Composite[3]) ? 1 : 0) - (kb.IsKeyDown(b.Composite[2]) ? 1 : 0),
                (kb.IsKeyDown(b.Composite[0]) ? 1 : 0) - (kb.IsKeyDown(b.Composite[1]) ? 1 : 0)),
            _ => Vector2.Zero,
        };
        _axis[i] += Shape(b, v, dt);
    }

    private static Vector2 Shape(Binding b, Vector2 v, float dt)
    {
        var d = b.Desc;
        v *= d.Scale;
        if (d.Invert) v = b.Action.Kind == ActionKind.Axis1D ? -v : new Vector2(v.X, -v.Y);
        if (d.Rate) v *= dt;
        return v;
    }

    // A pad binding's value as an axis, from whichever pad is asked. `joy_deadzone` is a floor under the binding's
    // own dead zone; the curve shapes sticks (a trigger's travel stays linear). Radial, in `StickResponse`.
    private Vector2 ReadPadAxis(Binding b, GamepadListener pad)
    {
        float deadzone = MathF.Max(b.Desc.Deadzone, _joyDeadzone.Value);
        return b.Source switch
        {
            Source.PadButton => new Vector2(pad.IsDown(b.PadButton) ? 1 : 0, 0),
            Source.PadTrigger => new Vector2(StickResponse.Apply(b.RightSide ? pad.RightTrigger : pad.LeftTrigger, deadzone), 0),
            Source.PadStick => StickResponse.Apply(b.RightSide ? pad.RightStick.ToNumerics() : pad.LeftStick.ToNumerics(), deadzone, _curve),
            _ => Vector2.Zero,
        };
    }

    private void ParseCurve()
    {
        if (StickResponse.TryParseCurve(_joyCurve.Value, out float exponent)) _curve = exponent;
        else Log.Warn(LogCat.Input, $"joy_curve '{_joyCurve.Value}': use linear, quadratic, cubic or a number from 0.1 to 8");
    }

    // ---- The other local players (issue #331) ----------------------------------------------------------

    public int LocalPlayers => _devices.Pads.Players;
    public ActionMask HeldMaskOf(int player) => player == 0 ? _heldBits : _frames[player].HeldBits;
    public ActionMask PressedMaskOf(int player) => player == 0 ? _pressedBits : _frames[player].PressedBits;
    public ActionMask ReleasedMaskOf(int player) => player == 0 ? _releasedBits : _frames[player].ReleasedBits;
    public Vector2 Axis2Of(int player, ActionId a)
    {
        if (player == 0) return Axis2(a);
        var axis = _frames[player].Axis;
        return a.IsValid && a.Index < axis.Length ? axis[a.Index] : Vector2.Zero;
    }

    // Player `p`'s frame: their pad against the gameplay context's pad bindings, then their scripts. Held back
    // while a console, a screen or the dev UI has the input (the first player's keyboard is theirs; a second
    // person's pad must not walk their pawn behind an open inventory). Scripts are not held back, as for the first.
    private void UpdateSecondary(int p, float dt)
    {
        var f = _frames[p];
        int n = _registry.All.Count;
        if (f.Held.Length != n)
        {
            Array.Resize(ref f.Held, n); Array.Resize(ref f.RawHeld, n); Array.Resize(ref f.WasRawHeld, n); Array.Resize(ref f.Axis, n);
        }
        Array.Copy(f.RawHeld, f.WasRawHeld, n);
        Array.Clear(f.Held); Array.Clear(f.RawHeld); Array.Clear(f.Axis);

        if (!IsActive(InputContext.Console) && !IsActive(InputContext.UI) && !IsActive(InputContext.Editor))
        {
            var pad = _devices.PadFor(p);
            foreach (var b in _bindings[(int)InputContext.Gameplay])
            {
                if (b.Source is not (Source.PadButton or Source.PadTrigger or Source.PadStick)) continue;
                int i = b.Action.Id.Index;
                if (b.Action.Kind == ActionKind.Button)
                {
                    bool down = b.IsTrigger
                        ? InputEdges.TriggerDown(b.RightSide ? pad.RightTrigger : pad.LeftTrigger, b.TriggerWasDownOthers[p])
                        : pad.IsDown(b.PadButton);
                    if (b.IsTrigger) b.TriggerWasDownOthers[p] = down;
                    f.Held[i] |= down;
                    f.RawHeld[i] |= down;
                }
                else f.Axis[i] += Shape(b, ReadPadAxis(b, pad), dt);
            }
        }
        _script.Apply(p, dt, _registry, f.Held, f.RawHeld, f.Axis);

        f.HeldBits = f.PressedBits = f.ReleasedBits = default;
        var all = _registry.All;
        for (int a = 0; a < all.Count; a++)
        {
            var info = all[a];
            if (info.Kind != ActionKind.Button) continue;
            int i = info.Id.Index;
            bool swallowed = f.RawHeld[i] && !f.Held[i];
            if (InputEdges.Held(f.RawHeld[i], swallowed)) f.HeldBits = f.HeldBits.With(info.Id);
            if (InputEdges.Pressed(f.RawHeld[i], f.WasRawHeld[i], swallowed)) f.PressedBits = f.PressedBits.With(info.Id);
            if (InputEdges.Released(f.RawHeld[i], f.WasRawHeld[i])) f.ReleasedBits = f.ReleasedBits.With(info.Id);
        }
    }

    // The mixer's motors go to the pads (`joy_rumble` is its gain). The host calls this once a frame.
    public void ApplyRumble(RumbleMixer? mixer)
    {
        if (mixer != null) mixer.Gain = _joyRumble.Value;
        _devices.ApplyRumble(mixer, _devices.Focused);
    }

    private void EnsureCapacity()
    {
        int n = _registry.All.Count;
        if (_held.Length == n) return;
        Array.Resize(ref _held, n);
        Array.Resize(ref _rawHeld, n);
        Array.Resize(ref _wasRawHeld, n);
        Array.Resize(ref _axis, n);
    }

    // ---- Bindings from records ----

    // Rebuilds every context's bindings from the merged input_map records (boot and hot reload).
    public void Rebuild()
    {
        foreach (var list in _bindings) list.Clear();
        int count = 0, errors = 0;
        foreach (var id in _records.Ids("input_map"))
        {
            if (!_records.TryGet(id, out InputMapRecord map)) continue;
            foreach (var (actionName, bindings) in map.Actions)
            {
                if (!_registry.TryGet(actionName, out var action))
                {
                    Log.Warn(LogCat.Input, $"{id}: action '{actionName}' isn't registered by any module; its bindings are ignored");
                    continue;
                }
                for (int n = 0; n < bindings.Count; n++)
                {
                    string where = $"{id} {actionName}[{n}]";
                    var compiled = Compile(action, bindings[n], where, out string? error);
                    if (compiled == null) { Log.Error(LogCat.Input, $"{where}: {error}"); errors++; continue; }
                    _bindings[(int)map.Context].Add(compiled);
                    count++;
                }
            }
        }
        WarnConflicts();
        Log.Info(LogCat.Input, $"Input: {count} bindings in {_bindings.Count(l => l.Count > 0)} contexts" + (errors > 0 ? $" ({errors} invalid, see above)" : ""));
    }

    private static Binding? Compile(ActionInfo action, InputBinding desc, string where, out string? error)
    {
        error = null;
        var b = new Binding { Action = action, Desc = desc, Where = where };
        int set = (desc.Key != null ? 1 : 0) + (desc.Mouse != null ? 1 : 0) + (desc.Gamepad != null ? 1 : 0) + (desc.Composite != null ? 1 : 0);
        if (set != 1) { error = "a binding needs exactly one of key, mouse, gamepad, composite"; return null; }

        if (desc.Key != null)
        {
            if (!Enum.TryParse(desc.Key, true, out Keys key) || !Enum.IsDefined(key)) { error = $"unknown key '{desc.Key}'"; return null; }
            b.Source = Source.Key; b.Key = key; b.Slots = new[] { (int)key };
        }
        else if (desc.Mouse != null)
        {
            switch (desc.Mouse.ToLowerInvariant())
            {
                case "left": b.Source = Source.MouseButton; b.MouseButton = MouseButton.LEFT; break;
                case "right": b.Source = Source.MouseButton; b.MouseButton = MouseButton.RIGHT; break;
                case "middle": b.Source = Source.MouseButton; b.MouseButton = MouseButton.MIDDLE; break;
                case "delta": b.Source = Source.MouseDelta; break;
                case "wheel": b.Source = Source.MouseWheel; break;
                default: error = $"unknown mouse input '{desc.Mouse}' (Left, Right, Middle, Delta, Wheel)"; return null;
            }
            b.Slots = new[] { b.Source switch
            {
                Source.MouseDelta => MouseDeltaSlot,
                Source.MouseWheel => MouseWheelSlot,
                _ => MouseButtonSlot + System.Numerics.BitOperations.Log2((uint)b.MouseButton),
            } };
        }
        else if (desc.Gamepad != null)
        {
            string name = desc.Gamepad.ToLowerInvariant();
            if (name is "leftstick" or "rightstick")
            {
                b.Source = Source.PadStick; b.RightSide = name == "rightstick"; b.Slots = new[] { PadStickSlot + (b.RightSide ? 1 : 0) };
            }
            else
            {
                string buttonName = name switch { "leftstickbutton" => "LeftStick", "rightstickbutton" => "RightStick", _ => desc.Gamepad };
                if (!Enum.TryParse(buttonName, true, out Buttons button) || !Enum.IsDefined(button)) { error = $"unknown gamepad input '{desc.Gamepad}'"; return null; }
                bool trigger = button is Buttons.LeftTrigger or Buttons.RightTrigger;
                b.Source = trigger && action.Kind == ActionKind.Axis1D ? Source.PadTrigger : Source.PadButton;
                b.PadButton = button;
                b.RightSide = button == Buttons.RightTrigger;
                b.Slots = new[] { PadButtonSlot + System.Numerics.BitOperations.Log2((uint)button) };
            }
        }
        else
        {
            b.Source = Source.Composite;
            b.Composite = desc.Composite!.ToUpperInvariant() switch
            {
                "WASD" => new[] { Keys.W, Keys.S, Keys.A, Keys.D },
                "ARROWS" => new[] { Keys.Up, Keys.Down, Keys.Left, Keys.Right },
                _ => Array.Empty<Keys>(),
            };
            if (b.Composite.Length == 0) { error = $"unknown composite '{desc.Composite}' (WASD, Arrows)"; return null; }
            b.Slots = b.Composite.Select(k => (int)k).ToArray();
        }

        bool fits = action.Kind switch
        {
            ActionKind.Button => b.Source is Source.Key or Source.MouseButton or Source.PadButton,
            ActionKind.Axis1D => b.Source is not (Source.MouseDelta or Source.PadStick or Source.Composite),
            _ => b.Source is Source.MouseDelta or Source.PadStick or Source.Composite,
        };
        if (!fits) { error = $"{desc} can't drive the {action.Kind} action {action.Name}"; return null; }
        return b;
    }

    // Two actions bound to the same input in one context: both fire. Warn, since it's usually a mistake.
    private void WarnConflicts()
    {
        for (int c = 0; c < _bindings.Length; c++)
        {
            var seen = new Dictionary<int, Binding>();
            foreach (var b in _bindings[c])
                foreach (int slot in b.Slots)
                {
                    if (seen.TryGetValue(slot, out var other) && other.Action != b.Action)
                        Log.Warn(LogCat.Input, $"{(InputContext)c}: {b.Where} and {other.Where} use the same input ({b.Desc})");
                    else seen[slot] = b;
                }
        }
    }

    private void ListBindings()
    {
        foreach (var context in ContextOrder)
        {
            var list = _bindings[(int)context];
            if (list.Count == 0) continue;
            Log.Info(LogCat.Console, $"{context}:");
            foreach (var group in list.GroupBy(b => b.Action.Name))
                Log.Info(LogCat.Console, $"  {group.Key,-14} {string.Join(", ", group.Select(b => b.Desc.ToString()))}");
        }
    }
}
