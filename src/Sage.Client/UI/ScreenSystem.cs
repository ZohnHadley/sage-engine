#nullable enable
using System;
using Sage.UI;

namespace Sage.Client;

// Drawing screens and reading their input (docs/design/13 §3, TODO F38; decision D8 = our own).
//
// One kind of screen since issue #350 retired the panel screens (Screen, ScreenStack, PanelView): Sage.UI's
// widget screens (`UiScreenStack`: `screen` records over view-models, trees a game built, and the HUD
// layers — the game's and the engine's crosshair). The stack decides focus, Back, click-outside and the
// fade, headless; this fills its `UiInput` from the `ui` input context and the mouse and draws each layer
// (WidgetRenderer).
//
// It runs in **Overlay, before the UI is rendered**, not in FrameUpdate: anything queued into UiDraw in
// FrameUpdate is drawn under the screens, and the last thing queued is the thing on top. The first
// screenshot of the screens had an interaction prompt and a fist painted across the spellbook, which is the
// sort of thing only a screenshot tells you.
[System("sage.client.screens", Phase.Overlay, Before = new[] { "sage.client.ui" })]
internal sealed class ScreenSystem : ISystem
{
    private readonly UiScreenStack? _widgets;
    private readonly UiStyles? _styles;
    private readonly Localisation? _text;
    private readonly ContentService _content;
    private readonly TrueTypeText _truetype;   // glyph atlases for the TTF fonts styles name (#338)
    private readonly UiPictures? _pictures;    // render targets and fog masks widgets show (#348)
    private readonly UiDraw _ui;
    private readonly InputActions _actions;
    private readonly InputDevices _devices;
    private readonly Query<Transform> _players;
    private double _lastRealTime = -1d;

    private readonly ActionId _up, _down, _left, _right, _tab, _confirm, _alternate, _back, _rotate, _split;

    public ScreenSystem(World world, InputActions actions, InputDevices devices, ActionRegistry registry, ContentService content, Renderer? renderer = null)
    {
        if (renderer != null) _pictures = world.Resources.GetOrAdd(() => new UiPictures(renderer));
        _devices = devices;
        _content = content;
        _truetype = new TrueTypeText(content.Device);
        _ui = world.Resources.Get<UiDraw>();
        _actions = actions;
        _players = world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>());

        // Sage.UI is a base plugin, so every world a client makes has these; a host without it (a
        // trimmed test host) simply has no widget screens.
        if (world.Resources.TryGet<UiScreenStack>(out var widgets) && widgets != null
            && world.Resources.TryGet<UiStyles>(out var styles) && world.Resources.TryGet<Localisation>(out var text))
        {
            _widgets = widgets;
            _styles = styles;
            _text = text;
            _widgets.Text = BitmapFontMeasure.Instance;   // layout in the engine font's pixels (#97)
        }

        _up = registry.Get("MenuUp");
        _down = registry.Get("MenuDown");
        _left = registry.Get("MenuLeft");
        _right = registry.Get("MenuRight");
        _tab = registry.Get("MenuTab");
        _confirm = registry.Get("MenuConfirm");
        _alternate = registry.Get("MenuAlternate");
        _back = registry.Get("MenuBack");
        _rotate = registry.Get("MenuRotate");
        _split = registry.Get("MenuSplit");
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        Entity player = default;
        foreach (var entity in _players.Entities) { player = entity; break; }   // one local player

        // Frame time, not simulation time: the UI's transitions run on what the player sees, and keep
        // running with the game paused or slowed (`host_timescale`). Clamped so a hitch does not skip
        // a whole fade.
        double now = ctx.Frame.RealTime;
        float dt = _lastRealTime < 0d ? 0f : (float)Math.Clamp(now - _lastRealTime, 0d, 0.1d);
        _lastRealTime = now;

        if (_widgets == null) { _actions.SetActive(InputContext.UI, false); return; }

        // Opening and closing. Checked with nothing open too, which is how a screen gets opened at
        // all; while one is open the same press closes it. **Not while a text field has the keyboard**
        // (UiScreenStack.Typing): `I`, `B` and `M` open screens, and they are also letters; naming a spell
        // "Misty Bind" would otherwise open the inventory on the way. Escape leaves a field's screen.
        if (!_widgets.Typing)
        {
            var openers = _widgets.OpenActions;
            for (int i = 0; i < openers.Count; i++)
                if (_actions.Pressed(openers[i]) && _widgets.Toggle(openers[i], new UiBindContext(world, player))) break;
        }

        bool widgetsOpen = _widgets.IsOpen;

        // The UI context is active exactly while something is open, so the gameplay bindings are
        // consumed and nobody walks about behind an open inventory (08 §3.3).
        _actions.SetActive(InputContext.UI, widgetsOpen);
        _widgets.SetViewport(new System.Numerics.Vector2(_ui.Size.X, _ui.Size.Y));
        var controls = widgetsOpen ? Controls(dt) : new UiControls { DeltaTime = dt };
        _widgets.Update(UiInputMap.From(in controls));
        WidgetRenderer.Draw(_ui, _widgets, _styles!, _text!, _content, _truetype, _pictures);
    }

    // What the `ui` context's buttons and the mouse say this frame (issue #97). Menu* are the screens'
    // actions, gamepad bindings and all; MenuLeft/MenuRight walk a grid's rows and MenuTab
    // goes round the tab order (Shift: backwards).
    private UiControls Controls(float dt)
    {
        var mouse = _devices.Mouse;
        var keyboard = _devices.Keyboard;
        return new UiControls
        {
            Up = _actions.Pressed(_up),
            Down = _actions.Pressed(_down),
            Left = _actions.Pressed(_left),
            Right = _actions.Pressed(_right),
            Tab = _actions.Pressed(_tab),
            Shift = keyboard.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.LeftShift) || keyboard.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.RightShift),
            Confirm = _actions.Pressed(_confirm),
            ConfirmHeld = _actions.Held(_confirm),
            Back = _actions.Pressed(_back),
            // The focused thing's other commands (issue #346): drop, turn, split an item on a grid.
            Alternate = _actions.Pressed(_alternate),
            Rotate = _actions.Pressed(_rotate),
            Split = _actions.Pressed(_split),
            Pointer = new System.Numerics.Vector2(mouse.Position.X, mouse.Position.Y),
            PointerMoved = mouse.IsMoving,
            PointerPressed = mouse.IsButtonPressed(MouseButton.LEFT),
            PointerDown = mouse.IsButtonDown(MouseButton.LEFT),
            Wheel = mouse.ScrollWheelDelta,
            PointerTaken = _actions.UiWantsMouse,   // ImGui has the cursor: a dev window is under it
            // A widget text field's characters (issue #340); a string only on a frame something was typed,
            // and none while the dev console has the keyboard.
            Typed = _actions.UiWantsKeyboard || _devices.Typed.Length == 0 ? null : new string(_devices.Typed),
            DeltaTime = dt,
        };
    }
}
