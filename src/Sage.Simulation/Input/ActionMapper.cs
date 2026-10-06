#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Sage.Simulation;

// Which mouse input a binding names, as the bit index of the device's button (Left = 0, Right = 1, Middle = 2).
internal enum MouseInput { Left, Right, Middle }

// The names a device layer understands, which is the one thing the pure binding logic cannot know: "Space" and
// "LeftShoulder" are MonoGame's. The client implements it over `Keys` / `Buttons`; tests implement a tiny one.
internal interface IInputVocabulary
{
    // A key's code (it also picks the key's consumption slot, so it must be below 256). False when the name is no key.
    bool TryKey(string name, out int code);

    // A pad button's bit index (consumption slot), and whether it is an analogue trigger (0 none, 1 left, 2 right).
    // `LeftStick` / `RightStick` here mean the stick *buttons* (the mapper handles the sticks themselves).
    bool TryPadButton(string name, out int bit, out int trigger);
}

// What the devices say this frame (docs/design/08 §3.1), as the mapper reads it. The client implements it over
// its listeners; tests implement it with plain fields.
internal interface IInputState
{
    bool KeyDown(int key);
    bool MouseDown(MouseInput button);
    bool PadDown(int bit);
    float Trigger(bool right);
    Vector2 Stick(bool right);        // +Y = up
    float WheelDelta { get; }         // in notches * 120
    Vector2 MouseDelta { get; }       // pixels, +Y = down
}

// Actions and input contexts, without the devices (docs/design/08 §3.2–3.3; issue 4o-8). This was the body of the
// client's `InputActions`; it moved here so bindings, contexts, composites, dead zones and the #60 edge rules are
// tested headlessly. The client keeps the console commands, scripted input and the device adapters.
//
// Each frame the contexts are evaluated top-down (Editor → Console → UI → Gameplay) and an input a higher active
// context binds (or captures) is consumed: lower contexts don't see it that frame. Edges (Pressed/Released) come
// from the *raw* held state so a consumed action does not read as newly pressed when the context closes (#60).
internal sealed class ActionMapper
{
    private enum Source { Key, MouseButton, MouseDelta, MouseWheel, PadButton, PadTrigger, PadStick, Composite }

    private sealed class Binding
    {
        public required ActionInfo Action;
        public required InputBinding Desc;
        public required string Where;       // "sage:gameplay Jump[0]"
        public Source Source;
        public int Key;
        public MouseInput MouseButton;
        public int PadButton;
        public int Trigger;                 // 0 none, 1 left, 2 right (pad buttons that are analogue triggers)
        public bool RightSide;              // right stick / right trigger
        public int[] Composite = Array.Empty<int>();     // up, down, left, right
        public int[] Slots = Array.Empty<int>();         // consumption slots this binding reads

        // Whether this binding's analogue trigger counted as down last frame, for the hysteresis in
        // `InputEdges.TriggerDown` (#60). Per binding, because two actions may read the same trigger.
        public bool TriggerWasDown;

        // What the device said this frame, read once in the raw pass and used again when the contexts decide who
        // hears it. Reading it twice would tick the trigger hysteresis twice.
        public bool RawDown;

        public bool IsTrigger => Source == Source.PadButton && Trigger != 0;
    }

    // Consumption slots: keys 0..255, mouse buttons 256.., delta 270, wheel 271, pad buttons 300..331, sticks 340/341.
    private const int SlotCount = 512;
    private const int MouseButtonSlot = 256, MouseDeltaSlot = 270, MouseWheelSlot = 271, PadButtonSlot = 300, PadStickSlot = 340;

    internal static readonly InputContext[] ContextOrder = { InputContext.Editor, InputContext.Console, InputContext.UI, InputContext.Gameplay };

    private readonly ActionRegistry _registry;
    private readonly IInputVocabulary _vocabulary;

    private readonly List<Binding>[] _bindings = Enumerable.Range(0, 4).Select(_ => new List<Binding>()).ToArray();
    private readonly bool[] _active = new bool[4];
    private readonly bool[] _consumed = new bool[SlotCount];

    // What the devices say, before anything swallows it. **Edges come from this**, not from the filtered `_held`
    // (#60, `InputEdges`).
    private bool[] _rawHeld = Array.Empty<bool>();
    private bool[] _wasRawHeld = Array.Empty<bool>();
    private bool[] _held = Array.Empty<bool>();
    private Vector2[] _axis = Array.Empty<Vector2>();
    private ActionMask _heldBits, _pressedBits, _releasedBits;

    public ActionMapper(ActionRegistry registry, IInputVocabulary vocabulary)
    {
        _registry = registry;
        _vocabulary = vocabulary;
        _active[(int)InputContext.Gameplay] = true;
    }

    // Set by the host each frame from ImGui's capture flags (the UI context).
    public bool UiWantsKeyboard { get; set; }
    public bool UiWantsMouse { get; set; }

    public void SetActive(InputContext context, bool active) => _active[(int)context] = active;

    // The UI context is active when ImGui wants input *or* when a game screen is open (13 §3, F38): both need the
    // gameplay bindings out of the way, and they arrive by different routes.
    public bool IsActive(InputContext context) =>
        context == InputContext.UI ? _active[(int)context] || UiWantsKeyboard || UiWantsMouse : _active[(int)context];

    public int BindingCount(InputContext context) => _bindings[(int)context].Count;

    // ---- Queries (this frame) ----
    public bool Held(ActionId a) => a.IsValid && a.Index < _held.Length && _held[a.Index];
    public bool Pressed(ActionId a) =>
        a.IsValid && a.Index < _held.Length &&
        InputEdges.Pressed(_rawHeld[a.Index], _wasRawHeld[a.Index], _rawHeld[a.Index] && !_held[a.Index]);
    public bool Released(ActionId a) =>
        a.IsValid && a.Index < _held.Length && InputEdges.Released(_rawHeld[a.Index], _wasRawHeld[a.Index]);
    public float Axis(ActionId a) => a.IsValid && a.Index < _axis.Length ? _axis[a.Index].X : 0f;
    public Vector2 Axis2(ActionId a) => a.IsValid && a.Index < _axis.Length ? _axis[a.Index] : Vector2.Zero;

    public ActionMask HeldMask => _heldBits;
    public ActionMask PressedMask => _pressedBits;
    public ActionMask ReleasedMask => _releasedBits;

    // Scripted input drives an action as if a device had (the client owns the script; 08 §9). Both held states, so
    // it counts for edges, and consumption does not apply to it.
    public void ForceButton(int index) { if (index < _held.Length) _held[index] = _rawHeld[index] = true; }
    public void ForceAxis(int index, Vector2 value) { if (index < _axis.Length) _axis[index] = value; }
    public int ActionCapacity => _held.Length;

    // ---- Per frame ----

    // Everything but scripted input; call `Finish` after the script has had its say.
    public void Update(float dt, IInputState input, float sensitivity = 1f, bool invertY = false)
    {
        EnsureCapacity();
        Array.Copy(_rawHeld, _wasRawHeld, _rawHeld.Length);
        Array.Clear(_held);
        Array.Clear(_rawHeld);
        Array.Clear(_axis);
        Array.Clear(_consumed);

        // **First, what the devices say** — every binding in every context, active or not. Edges are computed from
        // this, so a context switching back on (a console closing, a screen closing) does not look like a finger
        // arriving on a button that never moved (#60).
        foreach (var list in _bindings)
            foreach (var b in list)
                if (b.Action.Kind == ActionKind.Button)
                    _rawHeld[b.Action.Id.Index] |= ReadRaw(b, input);

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
                Evaluate(b, dt, IsConsumed(b), input, sensitivity, invertY);
            foreach (var b in list)
                foreach (int slot in b.Slots) _consumed[slot] = true;
            if (context == InputContext.Console) ConsumeRange(0, 256);
            // A game screen is modal: it reads its own actions first (above) and then swallows the rest, so nobody
            // walks about or swings behind an open inventory.
            if (context == InputContext.UI && _active[(int)InputContext.UI])
            {
                ConsumeRange(0, 256);
                ConsumeRange(MouseButtonSlot, 8);
                _consumed[MouseDeltaSlot] = true;
            }
        }
    }

    // The button masks, from the raw and held state (after any scripted input).
    public void Finish()
    {
        _heldBits = _pressedBits = _releasedBits = default;
        var all = _registry.All;
        for (int a = 0; a < all.Count; a++)   // for, not foreach: no enumerator allocation per frame
        {
            var info = all[a];
            if (info.Kind != ActionKind.Button) continue;
            int i = info.Id.Index;
            // Swallowed: the devices say it is down but something above this took it, so it is not held *and* the
            // press it began must not surface later (`InputEdges`).
            bool swallowed = _rawHeld[i] && !_held[i];
            if (InputEdges.Held(_rawHeld[i], swallowed)) _heldBits = _heldBits.With(info.Id);
            if (InputEdges.Pressed(_rawHeld[i], _wasRawHeld[i], swallowed)) _pressedBits = _pressedBits.With(info.Id);
            if (InputEdges.Released(_rawHeld[i], _wasRawHeld[i])) _releasedBits = _releasedBits.With(info.Id);
        }
    }

    // What one button binding's device says, regardless of who is listening. Called once per binding per frame,
    // which matters: the trigger hysteresis has state, and reading it twice would step it twice.
    private static bool ReadRaw(Binding b, IInputState input)
    {
        bool down = b.Source switch
        {
            Source.Key => input.KeyDown(b.Key),
            Source.MouseButton => input.MouseDown(b.MouseButton),
            // A trigger is analogue, so "down" needs two thresholds or it chatters where it rests (#60).
            Source.PadButton when b.IsTrigger => InputEdges.TriggerDown(input.Trigger(b.RightSide), b.TriggerWasDown),
            Source.PadButton => input.PadDown(b.PadButton),
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

    private void Evaluate(Binding b, float dt, bool consumed, IInputState input, float sensitivity, bool invertY)
    {
        int i = b.Action.Id.Index;
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
            Source.Key => new Vector2(input.KeyDown(b.Key) ? 1 : 0, 0),
            Source.MouseButton => new Vector2(input.MouseDown(b.MouseButton) ? 1 : 0, 0),
            Source.PadButton => new Vector2(input.PadDown(b.PadButton) ? 1 : 0, 0),
            Source.PadTrigger => new Vector2(DeadZone(input.Trigger(b.RightSide), d.Deadzone), 0),
            Source.MouseWheel => new Vector2(input.WheelDelta / 120f, 0),
            Source.MouseDelta => new Vector2(input.MouseDelta.X, -input.MouseDelta.Y * (invertY ? -1 : 1)) * sensitivity,
            Source.PadStick => DeadZone(input.Stick(b.RightSide), d.Deadzone),
            Source.Composite => new Vector2(
                (input.KeyDown(b.Composite[3]) ? 1 : 0) - (input.KeyDown(b.Composite[2]) ? 1 : 0),
                (input.KeyDown(b.Composite[0]) ? 1 : 0) - (input.KeyDown(b.Composite[1]) ? 1 : 0)),
            _ => Vector2.Zero,
        };
        v *= d.Scale;
        if (d.Invert) v = b.Action.Kind == ActionKind.Axis1D ? -v : new Vector2(v.X, -v.Y);
        if (d.Rate) v *= dt;
        _axis[i] += v;
    }

    internal static float DeadZone(float value, float deadzone) =>
        value <= deadzone ? 0f : (value - deadzone) / (1f - deadzone);

    // Radial dead zone, rescaled so the output still reaches 1.
    internal static Vector2 DeadZone(Vector2 value, float deadzone)
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
        Array.Resize(ref _rawHeld, n);
        Array.Resize(ref _wasRawHeld, n);
        Array.Resize(ref _axis, n);
    }

    // ---- Bindings from records ----

    // Rebuilds every context's bindings from the merged input_map records (boot and hot reload).
    public void Rebuild(RecordStore records)
    {
        Clear();
        int count = 0, errors = 0;
        foreach (var id in records.Ids("input_map"))
        {
            if (!records.TryGet(id, out InputMapRecord map)) continue;
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
                    if (Add(map.Context, action, bindings[n], where, out string? error)) count++;
                    else { Log.Error(LogCat.Input, $"{where}: {error}"); errors++; }
                }
            }
        }
        WarnConflicts();
        Log.Info(LogCat.Input, $"Input: {count} bindings in {_bindings.Count(l => l.Count > 0)} contexts" + (errors > 0 ? $" ({errors} invalid, see above)" : ""));
    }

    public void Clear()
    {
        foreach (var list in _bindings) list.Clear();
    }

    // Compiles one binding into a context. False (and the reason) when it is malformed or cannot drive the action.
    public bool Add(InputContext context, ActionInfo action, InputBinding desc, string where, out string? error)
    {
        var compiled = Compile(_vocabulary, action, desc, where, out error);
        if (compiled == null) return false;
        _bindings[(int)context].Add(compiled);
        return true;
    }

    // The check a rebind goes through before it is accepted: null when it would compile.
    public static string? Validate(IInputVocabulary vocabulary, ActionInfo action, InputBinding desc) =>
        Compile(vocabulary, action, desc, "", out string? error) == null ? error : null;

    private static Binding? Compile(IInputVocabulary vocab, ActionInfo action, InputBinding desc, string where, out string? error)
    {
        error = null;
        var b = new Binding { Action = action, Desc = desc, Where = where };
        int set = (desc.Key != null ? 1 : 0) + (desc.Mouse != null ? 1 : 0) + (desc.Gamepad != null ? 1 : 0) + (desc.Composite != null ? 1 : 0);
        if (set != 1) { error = "a binding needs exactly one of key, mouse, gamepad, composite"; return null; }

        if (desc.Key != null)
        {
            if (!vocab.TryKey(desc.Key, out int key)) { error = $"unknown key '{desc.Key}'"; return null; }
            b.Source = Source.Key; b.Key = key; b.Slots = new[] { key };
        }
        else if (desc.Mouse != null)
        {
            switch (desc.Mouse.ToLowerInvariant())
            {
                case "left": b.Source = Source.MouseButton; b.MouseButton = MouseInput.Left; break;
                case "right": b.Source = Source.MouseButton; b.MouseButton = MouseInput.Right; break;
                case "middle": b.Source = Source.MouseButton; b.MouseButton = MouseInput.Middle; break;
                case "delta": b.Source = Source.MouseDelta; break;
                case "wheel": b.Source = Source.MouseWheel; break;
                default: error = $"unknown mouse input '{desc.Mouse}' (Left, Right, Middle, Delta, Wheel)"; return null;
            }
            b.Slots = new[] { b.Source switch
            {
                Source.MouseDelta => MouseDeltaSlot,
                Source.MouseWheel => MouseWheelSlot,
                _ => MouseButtonSlot + (int)b.MouseButton,
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
                if (!vocab.TryPadButton(buttonName, out int bit, out int trigger)) { error = $"unknown gamepad input '{desc.Gamepad}'"; return null; }
                b.Source = trigger != 0 && action.Kind == ActionKind.Axis1D ? Source.PadTrigger : Source.PadButton;
                b.PadButton = bit;
                b.Trigger = trigger;
                b.RightSide = trigger == 2;
                b.Slots = new[] { PadButtonSlot + bit };
            }
        }
        else
        {
            b.Source = Source.Composite;
            string[] keys = desc.Composite!.ToUpperInvariant() switch
            {
                "WASD" => new[] { "W", "S", "A", "D" },
                "ARROWS" => new[] { "Up", "Down", "Left", "Right" },
                _ => Array.Empty<string>(),
            };
            if (keys.Length == 0) { error = $"unknown composite '{desc.Composite}' (WASD, Arrows)"; return null; }
            b.Composite = new int[4];
            for (int k = 0; k < 4; k++)
                if (!vocab.TryKey(keys[k], out b.Composite[k])) { error = $"composite {desc.Composite}: the device has no key '{keys[k]}'"; return null; }
            b.Slots = (int[])b.Composite.Clone();
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

    public void ListBindings()
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

    // The live state for `in_showactions`: every action that is held or off-centre this frame, e.g.
    // "Attack(held, pressed) Move=[0.00, 1.00]". Empty when everything is idle. Allocates, so it is for a console
    // command, not the frame.
    public string DescribeState()
    {
        var sb = new StringBuilder();
        foreach (var info in _registry.All)
        {
            int i = info.Id.Index;
            if (i >= _held.Length) continue;
            if (info.Kind == ActionKind.Button)
            {
                bool held = _held[i], pressed = Pressed(info.Id), released = Released(info.Id);
                if (!held && !pressed && !released) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(info.Name).Append('(');
                var parts = new List<string>(3);
                if (held) parts.Add("held");
                if (pressed) parts.Add("pressed");
                if (released) parts.Add("released");
                sb.Append(string.Join(", ", parts)).Append(')');
            }
            else
            {
                var v = _axis[i];
                if (v == Vector2.Zero) continue;
                if (sb.Length > 0) sb.Append(' ');
                var f = System.Globalization.CultureInfo.InvariantCulture;
                sb.Append(info.Name).Append('=')
                  .Append(info.Kind == ActionKind.Axis1D ? v.X.ToString("F2", f) : $"[{v.X.ToString("F2", f)}, {v.Y.ToString("F2", f)}]");
            }
        }
        return sb.ToString();
    }
}
