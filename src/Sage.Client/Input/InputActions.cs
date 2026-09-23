#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Microsoft.Xna.Framework.Input;

namespace sage_engine;

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
    }

    // Consumption slots: keys 0..255, mouse buttons 256.., delta 270, wheel 271, pad buttons 300..331, sticks 340/341.
    private const int SlotCount = 512;
    private const int MouseButtonSlot = 256, MouseDeltaSlot = 270, MouseWheelSlot = 271, PadButtonSlot = 300, PadStickSlot = 340;

    private static readonly InputContext[] ContextOrder = { InputContext.Editor, InputContext.Console, InputContext.UI, InputContext.Gameplay };

    private readonly ActionRegistry _registry;
    private readonly RecordStore _records;
    private readonly InputDevices _devices;
    private readonly CVar<float> _sensitivity;
    private readonly CVar<bool> _invertY;

    private readonly List<Binding>[] _bindings = Enumerable.Range(0, 4).Select(_ => new List<Binding>()).ToArray();
    private readonly bool[] _active = new bool[4];
    private readonly bool[] _consumed = new bool[SlotCount];

    private bool[] _held = Array.Empty<bool>();
    private bool[] _wasHeld = Array.Empty<bool>();
    private Vector2[] _axis = Array.Empty<Vector2>();
    private ulong _heldBits, _pressedBits, _releasedBits;
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
    {
        _registry = registry;
        _records = records;
        _devices = devices;
        _sensitivity = cvars.Register("m_sensitivity", 1f, CVarFlags.Archive, "Mouse look speed multiplier (Look action and the editor camera).", 0.01f, 20f);
        _invertY = cvars.Register("m_invert_y", false, CVarFlags.Archive, "Invert mouse look up/down.");
        cvars.RegisterCommand("bindlist", CVarFlags.None, "List input bindings per context.", _ => ListBindings());
        cvars.RegisterCommand("in_contexts", CVarFlags.None, "Show which input contexts are active.", _ =>
            Log.Info(LogCat.Console, $"  {string.Join(", ", ContextOrder.Select(c => $"{c}={(IsActive(c) ? "on" : "off")}"))}; UI captures keyboard={UiWantsKeyboard} mouse={UiWantsMouse}"));
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
    public bool Pressed(ActionId a) => Held(a) && !_wasHeld[a.Index];
    public bool Released(ActionId a) => a.IsValid && a.Index < _held.Length && !_held[a.Index] && _wasHeld[a.Index];
    public float Axis(ActionId a) => a.IsValid && a.Index < _axis.Length ? _axis[a.Index].X : 0f;
    public Vector2 Axis2(ActionId a) => a.IsValid && a.Index < _axis.Length ? _axis[a.Index] : Vector2.Zero;

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
            if (index >= _held.Length) { _scripted.RemoveAt(i); continue; }

            if (_registry.All[index].Kind == ActionKind.Button) _held[index] = true;
            else _axis[index] = s.Rate ? s.Value * dt : s.Value;

            if (s.OneFrame) { _scripted.RemoveAt(i); continue; }
            if (float.IsPositiveInfinity(s.Remaining)) continue;

            s.Remaining -= dt;
            if (s.Remaining <= 0f) _scripted.RemoveAt(i);
            else _scripted[i] = s;
        }
    }

    public ActionMask HeldMask => new(_heldBits);
    public ActionMask PressedMask => new(_pressedBits);
    public ActionMask ReleasedMask => new(_releasedBits);

    // ---- Per frame ----
    public void Update(float dt)
    {
        EnsureCapacity();
        Array.Copy(_held, _wasHeld, _held.Length);
        Array.Clear(_held);
        Array.Clear(_axis);
        Array.Clear(_consumed);

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
                if (!IsConsumed(b)) Evaluate(b, dt);
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
        ApplyScripted(dt);

        _heldBits = _pressedBits = _releasedBits = 0;
        var all = _registry.All;
        for (int a = 0; a < all.Count; a++)   // for, not foreach: no enumerator allocation per frame
        {
            var info = all[a];
            if (info.Kind != ActionKind.Button) continue;
            int i = info.Id.Index;
            ulong bit = 1UL << info.Id.Bit;
            if (_held[i]) _heldBits |= bit;
            if (_held[i] && !_wasHeld[i]) _pressedBits |= bit;
            if (!_held[i] && _wasHeld[i]) _releasedBits |= bit;
        }
    }

    private void ConsumeRange(int start, int count) => Array.Fill(_consumed, true, start, count);

    private bool IsConsumed(Binding b)
    {
        foreach (int slot in b.Slots)
            if (_consumed[slot]) return true;
        return false;
    }

    private void Evaluate(Binding b, float dt)
    {
        int i = b.Action.Id.Index;
        var kb = _devices.Keyboard;
        var mouse = _devices.Mouse;
        var pad = _devices.Gamepad;
        var d = b.Desc;

        if (b.Action.Kind == ActionKind.Button)
        {
            bool down = b.Source switch
            {
                Source.Key => kb.IsKeyDown(b.Key),
                Source.MouseButton => mouse.IsButtonDown(b.MouseButton),
                Source.PadButton => pad.IsDown(b.PadButton),
                _ => false,
            };
            _held[i] |= down;
            return;
        }

        Vector2 v = b.Source switch
        {
            Source.Key => new Vector2(kb.IsKeyDown(b.Key) ? 1 : 0, 0),
            Source.MouseButton => new Vector2(mouse.IsButtonDown(b.MouseButton) ? 1 : 0, 0),
            Source.PadButton => new Vector2(pad.IsDown(b.PadButton) ? 1 : 0, 0),
            Source.PadTrigger => new Vector2(DeadZone(b.RightSide ? pad.RightTrigger : pad.LeftTrigger, d.Deadzone), 0),
            Source.MouseWheel => new Vector2(mouse.ScrollWheelDelta / 120f, 0),
            Source.MouseDelta => new Vector2(mouse.PositionDelta.X, -mouse.PositionDelta.Y * (_invertY.Value ? -1 : 1)) * _sensitivity.Value,
            Source.PadStick => DeadZone(b.RightSide ? pad.RightStick.ToNumerics() : pad.LeftStick.ToNumerics(), d.Deadzone),
            Source.Composite => new Vector2(
                (kb.IsKeyDown(b.Composite[3]) ? 1 : 0) - (kb.IsKeyDown(b.Composite[2]) ? 1 : 0),
                (kb.IsKeyDown(b.Composite[0]) ? 1 : 0) - (kb.IsKeyDown(b.Composite[1]) ? 1 : 0)),
            _ => Vector2.Zero,
        };
        v *= d.Scale;
        if (d.Invert) v = b.Action.Kind == ActionKind.Axis1D ? -v : new Vector2(v.X, -v.Y);
        if (d.Rate) v *= dt;
        _axis[i] += v;
    }

    private static float DeadZone(float value, float deadzone) =>
        value <= deadzone ? 0f : (value - deadzone) / (1f - deadzone);

    // Radial dead zone, rescaled so the output still reaches 1.
    private static Vector2 DeadZone(Vector2 value, float deadzone)
    {
        float length = value.Length();
        if (length <= deadzone) return Vector2.Zero;
        return value / length * MathF.Min(1f, (length - deadzone) / (1f - deadzone));
    }

    private void EnsureCapacity()
    {
        int n = _registry.All.Count;
        if (_held.Length == n) return;
        Array.Resize(ref _held, n);
        Array.Resize(ref _wasHeld, n);
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
