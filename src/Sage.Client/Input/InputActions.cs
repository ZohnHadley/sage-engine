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
    private static readonly InputContext[] ContextOrder = ActionMapper.ContextOrder;

    private readonly ActionRegistry _registry;
    private readonly RecordStore _records;
    private readonly InputDevices _devices;
    private readonly InputRebinds? _rebinds;
    private readonly CVar<float> _sensitivity;
    private readonly CVar<bool> _invertY;

    // The binding compile, context masking, composites, dead zones and edges live in Sage.Simulation's
    // ActionMapper so they are tested headlessly (4o-8); this class reads the devices for it.
    private readonly ActionMapper _mapper;
    private readonly DeviceState _state;
    private bool _showActions;
    private string _lastShown = "";
    // Scripted input (08 §9): actions driven from the console instead of a device, for automated
    // checks and for repeating a bug without a person at the keyboard. Injected *after* the bindings
    // are evaluated, so a script goes through the same PlayerCommand path a keyboard does — what it
    // exercises is the real chain, not a shortcut into the simulation.
    private readonly List<Scripted> _scripted = new();

    private struct Scripted
    {
        public ActionId Action;
        public Vector2 Value;      // buttons: held when Remaining > 0; axes: the value to report
        public float Remaining;    // seconds; <= 0 after this frame's use, so 0 means "one frame"
        public bool OneFrame;
        public bool Rate;          // Value is per *second*, scaled by dt (turning, not a stick position)
    }

    public InputActions(ActionRegistry registry, RecordStore records, InputDevices devices, CVarRegistry cvars)
        : this(registry, records, devices, cvars, null) { }

    public InputActions(ActionRegistry registry, RecordStore records, InputDevices devices, CVarRegistry cvars, InputRebinds? rebinds)
    {
        _registry = registry;
        _rebinds = rebinds;
        _state = new DeviceState(devices);
        _mapper = new ActionMapper(registry, Vocabulary.Instance);
        // The names of keys and buttons are MonoGame's, so the one place that can check a rebind is here:
        // the same compile the bindings from records go through.
        if (rebinds != null) rebinds.Validator = (action, desc) => ActionMapper.Validate(Vocabulary.Instance, action, desc);
        _records = records;
        _devices = devices;
        _sensitivity = cvars.Register("m_sensitivity", 1f, CVarFlags.Archive, "Mouse look speed multiplier (Look action and the editor camera).", 0.01f, 20f);
        _invertY = cvars.Register("m_invert_y", false, CVarFlags.Archive, "Invert mouse look up/down.");
        cvars.RegisterCommand("bindlist", CVarFlags.None, "List input bindings per context.", _ => _mapper.ListBindings());
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

        cvars.RegisterCommand("in_tap", CVarFlags.DevOnly, "in_tap <action>: press a button action for one frame (automated tests).", a =>
        {
            if (a.Count == 0 || !_registry.TryGet(a[0], out var info) || info.Kind != ActionKind.Button)
                Log.Warn(LogCat.Console, "in_tap <button action>");
            else Inject(info.Id, Vector2.One, 0f, oneFrame: true);
        });

        cvars.RegisterCommand("in_hold", CVarFlags.DevOnly,
            "in_hold <action> <seconds>: hold a button action down for a while (0 releases it).", a =>
        {
            if (a.Count < 1 || !_registry.TryGet(a[0], out var info) || info.Kind != ActionKind.Button)
            {
                Log.Warn(LogCat.Console, "in_hold <button action> <seconds>");
                return;
            }
            float seconds = a.Count > 1 ? Number(a[1]) : 1f;
            if (seconds <= 0f) Release(info.Id);
            else Inject(info.Id, Vector2.One, seconds, oneFrame: false);
        });

        cvars.RegisterCommand("in_axis", CVarFlags.DevOnly,
            "in_axis <action> <x> [y] [seconds]: drive an axis action (no seconds = until in_axis 0 or in_clear).", a =>
        {
            if (a.Count < 2 || !_registry.TryGet(a[0], out var info) || info.Kind == ActionKind.Button)
            {
                Log.Warn(LogCat.Console, "in_axis <axis action> <x> [y] [seconds]");
                return;
            }
            var value = new Vector2(Number(a[1]), a.Count > 2 ? Number(a[2]) : 0f);
            float seconds = a.Count > 3 ? Number(a[3]) : float.PositiveInfinity;
            if (value == Vector2.Zero) Release(info.Id);
            else Inject(info.Id, value, seconds, oneFrame: false);
        });

        cvars.RegisterCommand("in_look", CVarFlags.DevOnly,
            "in_look <yaw°/s> [pitch°/s] [seconds]: turn the view at a steady rate (0 stops).", a =>
        {
            if (!_registry.TryGet("Look", out var look) || look.Kind != ActionKind.Axis2D)
            {
                Log.Warn(LogCat.Console, "in_look: there is no Look axis action");
                return;
            }
            // Degrees per second, not the raw axis: Look is a per-frame delta in radians (the client
            // has already scaled the mouse), so a script that wrote it directly would turn at a rate
            // that depended on the frame rate — and an automated check that is not reproducible is
            // not a check. The rate is converted here and multiplied by dt where it is applied.
            float yaw = a.Count > 0 ? Number(a[0]) : 0f;
            float pitch = a.Count > 1 ? Number(a[1]) : 0f;
            float seconds = a.Count > 2 ? Number(a[2]) : float.PositiveInfinity;
            const float ToRadians = MathF.PI / 180f;
            // +x turns right, and AddLook subtracts it (yaw is counter-clockwise), so a positive
            // "yaw" here means "turn right" the way a player would describe it.
            if (yaw == 0f && pitch == 0f) Release(look.Id);
            else Inject(look.Id, new Vector2(yaw * ToRadians, pitch * ToRadians), seconds, oneFrame: false, rate: true);
        });

        cvars.RegisterCommand("in_release", CVarFlags.DevOnly, "in_release <action>: stop driving one action from a script.", a =>
        {
            if (a.Count == 0 || !_registry.TryGet(a[0], out var info)) Log.Warn(LogCat.Console, "in_release <action>");
            else Release(info.Id);
        });

        cvars.RegisterCommand("in_clear", CVarFlags.DevOnly, "Stop driving every action from a script (hands control back).", _ =>
        {
            _scripted.Clear();
            Log.Info(LogCat.Console, "scripted input cleared");
        });

        cvars.RegisterCommand("in_scripted", CVarFlags.DevOnly, "What a script is currently holding down.", _ =>
        {
            if (_scripted.Count == 0) { Log.Info(LogCat.Console, "  nothing scripted"); return; }
            foreach (var s in _scripted)
            {
                string left = float.IsPositiveInfinity(s.Remaining) ? "held" : $"{s.Remaining:F2}s left";
                Log.Info(LogCat.Console, $"  {_registry.All[s.Action.Index].Name} = [{s.Value.X:F2}, {s.Value.Y:F2}] ({left})");
            }
        });

        cvars.RegisterCommand("in_showactions", CVarFlags.None,
            "in_showactions [on|off]: no argument prints the live action state now; on logs it whenever it changes.", a =>
        {
            if (a.Count > 0 && a[0] is "on" or "1") { _showActions = true; _lastShown = ""; Log.Info(LogCat.Console, "in_showactions on (logs each change)"); }
            else if (a.Count > 0 && a[0] is "off" or "0") { _showActions = false; Log.Info(LogCat.Console, "in_showactions off"); }
            else
            {
                string now = _mapper.DescribeState();
                Log.Info(LogCat.Console, now.Length == 0 ? "  all actions idle" : "  " + now);
            }
        });
        _records.Reloaded += Rebuild;   // the first record load builds the bindings
    }

    public float MouseSensitivity => _sensitivity.Value;
    public bool InvertMouseY => _invertY.Value;

    // Set by the host each frame from ImGui's capture flags (the UI context).
    public bool UiWantsKeyboard { get => _mapper.UiWantsKeyboard; set => _mapper.UiWantsKeyboard = value; }
    public bool UiWantsMouse { get => _mapper.UiWantsMouse; set => _mapper.UiWantsMouse = value; }

    public void SetActive(InputContext context, bool active) => _mapper.SetActive(context, active);
    public bool IsActive(InputContext context) => _mapper.IsActive(context);

    // ---- Queries (this frame) ----
    public bool Held(ActionId a) => _mapper.Held(a);
    public bool Pressed(ActionId a) => _mapper.Pressed(a);
    public bool Released(ActionId a) => _mapper.Released(a);
    public float Axis(ActionId a) => _mapper.Axis(a);
    public Vector2 Axis2(ActionId a) => _mapper.Axis2(a);

    // ---- Scripted input ----------------------------------------------------------------------------

    private static float Number(string text) =>
        float.TryParse(text, System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out float value) ? value : 0f;

    private void Inject(ActionId action, Vector2 value, float seconds, bool oneFrame, bool rate = false)
    {
        Release(action);   // driving an action twice is a script contradicting itself; the last wins
        _scripted.Add(new Scripted { Action = action, Value = value, Remaining = seconds, OneFrame = oneFrame, Rate = rate });
    }

    private void Release(ActionId action)
    {
        for (int i = _scripted.Count - 1; i >= 0; i--)
            if (_scripted[i].Action == action) _scripted.RemoveAt(i);
    }

    // After the bindings, so a script adds to a device rather than fighting it: holding W while a
    // script drives Move forward is still forward, and releasing the script hands control straight
    // back. Ages in real time, like the console's `wait`, so a paused game still runs its script.
    private void ApplyScripted(float dt)
    {
        for (int i = _scripted.Count - 1; i >= 0; i--)
        {
            var s = _scripted[i];
            int index = s.Action.Index;
            if (index >= _mapper.ActionCapacity) { _scripted.RemoveAt(i); continue; }

            // Both: a script *is* the device as far as edges are concerned, and consumption does not
            // apply to it — a script drives the game rather than playing it.
            if (_registry.All[index].Kind == ActionKind.Button) _mapper.ForceButton(index);
            else _mapper.ForceAxis(index, s.Rate ? s.Value * dt : s.Value);

            if (s.OneFrame) { _scripted.RemoveAt(i); continue; }
            if (float.IsPositiveInfinity(s.Remaining)) continue;

            s.Remaining -= dt;
            if (s.Remaining <= 0f) _scripted.RemoveAt(i);
            else _scripted[i] = s;
        }
    }

    public ActionMask HeldMask => _mapper.HeldMask;
    public ActionMask PressedMask => _mapper.PressedMask;
    public ActionMask ReleasedMask => _mapper.ReleasedMask;

    // ---- Per frame ----
    public void Update(float dt)
    {
        CaptureNextInput();
        _mapper.Update(dt, _state, _sensitivity.Value, _invertY.Value);
        ApplyScripted(dt);
        _mapper.Finish();
        if (_showActions)
        {
            string now = _mapper.DescribeState();
            if (now != _lastShown) { _lastShown = now; Log.Info(LogCat.Console, "actions: " + (now.Length == 0 ? "(idle)" : now)); }
        }
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

    // Rebuilds every context's bindings from the merged input_map records (boot and hot reload).
    public void Rebuild() => _mapper.Rebuild(_records);

    // MonoGame's names for the mapper (`Keys`, `Buttons`): the one thing the pure logic cannot know.
    private sealed class Vocabulary : IInputVocabulary
    {
        public static readonly Vocabulary Instance = new();

        public bool TryKey(string name, out int code)
        {
            code = 0;
            if (!Enum.TryParse(name, true, out Keys key) || !Enum.IsDefined(key)) return false;
            code = (int)key;
            return true;
        }

        public bool TryPadButton(string name, out int bit, out int trigger)
        {
            bit = 0; trigger = 0;
            if (!Enum.TryParse(name, true, out Buttons button) || !Enum.IsDefined(button)) return false;
            bit = System.Numerics.BitOperations.Log2((uint)button);
            trigger = button == Buttons.LeftTrigger ? 1 : button == Buttons.RightTrigger ? 2 : 0;
            return true;
        }
    }

    // The devices as the mapper reads them.
    private sealed class DeviceState : IInputState
    {
        private readonly InputDevices _devices;
        public DeviceState(InputDevices devices) => _devices = devices;
        public bool KeyDown(int key) => _devices.Keyboard.IsKeyDown((Keys)key);
        public bool MouseDown(MouseInput button) => _devices.Mouse.IsButtonDown((MouseButton)(1 << (int)button));
        public bool PadDown(int bit) => _devices.Gamepad.IsDown((Buttons)(1 << bit));
        public float Trigger(bool right) => right ? _devices.Gamepad.RightTrigger : _devices.Gamepad.LeftTrigger;
        public System.Numerics.Vector2 Stick(bool right) => (right ? _devices.Gamepad.RightStick : _devices.Gamepad.LeftStick).ToNumerics();
        public float WheelDelta => _devices.Mouse.ScrollWheelDelta;
        public System.Numerics.Vector2 MouseDelta { get { var d = _devices.Mouse.PositionDelta; return new System.Numerics.Vector2(d.X, d.Y); } }
    }
}
