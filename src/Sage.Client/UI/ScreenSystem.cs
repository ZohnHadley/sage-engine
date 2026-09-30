#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Sage.UI;
using Color = Microsoft.Xna.Framework.Color;

namespace Sage.Client;

// Drawing screens and reading their input (docs/design/13 §3, TODO F38; decision D8 = our own).
//
// Two kinds of screen, one system (issue #97):
// - **Panel screens** (`Screen`, `ScreenStack`): what a screen *is* lives in the engine, because a panel
//   of rows and what Enter does to the world are simulation, and where its parts are is `PanelLayout`,
//   also in the engine, because the mouse and the drawing must agree about it. This draws the box.
// - **Widget screens** (Sage.UI's `UiScreenStack`: `screen` records over view-models, or trees a game
//   built): the stack decides focus, Back, click-outside and the fade, headless; this fills its
//   `UiInput` from the same `ui` input context and the mouse and draws each layer (WidgetRenderer).
//   While a widget screen is open it is on top and takes the input; the panel under it waits.
//
// It runs in **Overlay, before the UI is rendered**, not in FrameUpdate: a game's HUD queues its own
// drawing in FrameUpdate and the last thing queued is the thing on top, so a screen has to queue after
// it. The first screenshot of this feature had an interaction prompt and a fist painted across the
// spellbook, which is the sort of thing only a screenshot tells you.
[System("sage.client.screens", Phase.Overlay, Before = new[] { "sage.client.ui" })]
internal sealed class ScreenSystem : ISystem
{
    private readonly ScreenStack _stack;
    private readonly UiScreenStack? _widgets;
    private readonly UiStyles? _styles;
    private readonly Localisation? _text;
    private readonly ContentService _content;
    private readonly UiDraw _ui;
    private readonly InputActions _actions;
    private readonly InputDevices _devices;
    private readonly Query<Transform> _players;
    private double _lastRealTime = -1d;

    private readonly ActionId _up, _down, _left, _right, _tab, _confirm, _alternate, _back;

    public ScreenSystem(World world, InputActions actions, InputDevices devices, ActionRegistry registry, ContentService content)
    {
        _devices = devices;
        _content = content;
        _stack = world.Resources.Get<ScreenStack>();
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

        // Opening and closing. Checked with nothing open too, which is how a screen gets opened at
        // all; while one is open the same press closes it.
        //
        // **Not while a field has the keyboard**: `I`, `B` and `M` open screens, and they are also
        // letters. Naming a spell "Misty Bind" would otherwise open the inventory on the way. A screen
        // with a field is left by Escape, which is not a letter.
        if (_stack.Top?.Field == null)
        {
            bool toggled = false;
            if (_widgets != null)
            {
                var openers = _widgets.OpenActions;
                for (int i = 0; i < openers.Count && !toggled; i++)
                    if (_actions.Pressed(openers[i])) toggled = _widgets.Toggle(openers[i], new UiBindContext(world, player));
            }
            if (!toggled && !(_widgets?.IsOpen ?? false))
                foreach (var opener in _stack.OpenActions)
                    if (_actions.Pressed(opener) && _stack.Toggle(opener, world, player)) break;
        }

        bool widgetsOpen = _widgets?.IsOpen ?? false;

        // The UI context is active exactly while something is open, so the gameplay bindings are
        // consumed and nobody walks about behind an open inventory (08 §3.3).
        _actions.SetActive(InputContext.UI, _stack.IsOpen || widgetsOpen);

        // The panel screens: driven only while no widget screen is over them, drawn either way.
        if (_stack.Top != null) Panels(world, player, acceptInput: !widgetsOpen);

        if (_widgets == null) return;
        _widgets.SetViewport(new System.Numerics.Vector2(_ui.Size.X, _ui.Size.Y));
        var controls = widgetsOpen ? Controls(dt) : new UiControls { DeltaTime = dt };
        _widgets.Update(UiInputMap.From(in controls));
        WidgetRenderer.Draw(_ui, _widgets, _styles!, _text!, _content);
    }

    // What the `ui` context's buttons and the mouse say this frame (issue #97). Menu* are the panel
    // screens' actions, gamepad bindings and all; MenuLeft/MenuRight walk a grid's rows and MenuTab
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
            Pointer = new System.Numerics.Vector2(mouse.Position.X, mouse.Position.Y),
            PointerMoved = mouse.IsMoving,
            PointerPressed = mouse.IsButtonPressed(MouseButton.LEFT),
            PointerDown = mouse.IsButtonDown(MouseButton.LEFT),
            Wheel = mouse.ScrollWheelDelta,
            PointerTaken = _actions.UiWantsMouse,   // ImGui has the cursor: a dev window is under it
            DeltaTime = dt,
        };
    }

    private void Panels(World world, Entity player, bool acceptInput)
    {
        var screen = _stack.Top!;
        if (acceptInput)
        {
            if (_actions.Pressed(_back)) { _stack.Close(); return; }

            // `UiWantsKeyboard` is ImGui's: the dev console subscribes to the same window event, so
            // without this a character typed into the console would also land in an open screen's field.
            var field = screen.Field;
            if (field != null && !_actions.UiWantsKeyboard && _devices.Typed.Length > 0 && field.Type(_devices.Typed))
                screen.Typed(world, player);

            if (_actions.Pressed(_down)) _stack.Move(1);
            if (_actions.Pressed(_up)) _stack.Move(-1);

            Mouse(world, player, screen, Layout(screen));

            if (_actions.Pressed(_confirm)) _stack.Activate(world, player);
            else if (_actions.Pressed(_alternate)) _stack.Alternate(world, player);
        }

        // Laid out again after acting: dropping a row changes how many there are, and drawing the list
        // as it was a moment ago would show the thing that is gone.
        var top = _stack.Top;
        if (top != null) PanelView.Draw(_ui, top, Layout(top));
    }

    private PanelLayout Layout(Screen screen) =>
        new(new Vector2(_ui.Size.X, _ui.Size.Y), _ui.LineHeight, screen.Width, screen.Panel.Count, screen.Index,
            screen.Field != null,
            // A conversation's title is the sentence somebody just said, so it may need several lines
            // (F24). Wrapped here because the layout has no font and the view does.
            PanelView.WrapTitle(_ui, screen.Panel.Title, screen.Width - PanelLayout.Padding * 2f).Count);

    // Pointing at a screen (13 §3). The keyboard stays the primary way through a list — a gamepad has
    // no cursor — so the mouse only ever *moves the same selection* the keys move. One notion of "the
    // current row" means a click activates exactly what the reason line is talking about.
    private void Mouse(World world, Entity player, Screen screen, in PanelLayout layout)
    {
        if (_actions.UiWantsMouse) return;     // ImGui has the cursor: a dev window is under it

        var mouse = _devices.Mouse;
        var point = new Vector2(mouse.Position.X, mouse.Position.Y);

        // Only when it *moved*: a cursor resting over row three must not drag the selection back every
        // time the keyboard moves it to row four.
        if (mouse.IsMoving)
        {
            int over = layout.RowAt(point);
            if (over >= 0) screen.Index = over;
        }

        // The wheel moves the selection, and the window follows the selection, so one thing moves and
        // the list scrolls with it.
        int wheel = mouse.ScrollWheelDelta;
        if (wheel != 0) _stack.Move(wheel > 0 ? -1 : 1);

        if (mouse.IsButtonPressed(MouseButton.LEFT))
        {
            // Outside the box is "I am done here", the way most inventories close.
            if (!layout.Contains(point)) { _stack.Close(); return; }
            if (layout.RowAt(point) >= 0) _stack.Activate(world, player);
        }
        else if (mouse.IsButtonPressed(MouseButton.RIGHT) && layout.RowAt(point) >= 0)
        {
            _stack.Alternate(world, player);   // the second button, as Delete is on the keyboard
        }
    }
}

// Draws a screen from its layout: a framed box, a title, the rows, and a hint line. Pixels, not
// layout — the same level the HUD works at (13 "As built (the v1 HUD)").
public static class PanelView
{
    private static readonly Color Backdrop = new(0, 0, 0, 170);
    private static readonly Color Box = new(12, 12, 16, 235);
    private static readonly Color Edge = new(120, 120, 140, 255);
    private static readonly Color TitleColour = new(235, 235, 245, 255);
    private static readonly Color RowColour = new(205, 205, 215, 255);
    private static readonly Color DisabledColour = new(120, 120, 130, 255);
    private static readonly Color DetailColour = new(160, 175, 200, 255);

    private static readonly List<string> _titleLines = new();
    private static string _wrappedTitle = "";
    private static float _wrappedWidth;

    // Splits the title across lines that fit, and remembers the answer: a panel is rebuilt when
    // something changes but drawn every frame, and cutting a sentence into substrings sixty times a
    // second is the allocation the engine forbids itself (02 §4.6).
    public static List<string> WrapTitle(UiDraw ui, string title, float room)
    {
        if (title == _wrappedTitle && MathF.Abs(room - _wrappedWidth) < 0.5f) return _titleLines;

        _wrappedTitle = title;
        _wrappedWidth = room;
        _titleLines.Clear();
        if (title.Length == 0) { _titleLines.Add(""); return _titleLines; }

        int start = 0;
        while (start < title.Length)
        {
            int fits = start;
            int lastSpace = -1;
            for (int i = start; i <= title.Length; i++)
            {
                if (i < title.Length && title[i] == ' ') lastSpace = i;
                if (i == title.Length || ui.Measure(title[start..i]).X > room)
                {
                    // Break at the last space that still fits; a word longer than the panel breaks
                    // wherever it has to, because the alternative is a line that runs off the edge.
                    fits = i == title.Length ? i : (lastSpace > start ? lastSpace : Math.Max(i - 1, start + 1));
                    break;
                }
            }
            _titleLines.Add(title[start..fits].TrimEnd());
            start = fits < title.Length && title[fits] == ' ' ? fits + 1 : fits;
        }
        return _titleLines;
    }

    // As much of a string as fits, with an ellipsis when it does not. Measured rather than counted:
    // the font is not monospaced and a row of narrow letters fits more than a row of wide ones.
    private static string Fit(UiDraw ui, string text, float room)
    {
        if (room <= 0f || ui.Measure(text).X <= room) return text;
        for (int length = text.Length - 1; length > 0; length--)
        {
            string shorter = text[..length] + "…";
            if (ui.Measure(shorter).X <= room) return shorter;
        }
        return "";
    }
    private static readonly Color SelectedBar = new(58, 74, 110, 255);
    private static readonly Color ReasonColour = new(210, 150, 150, 255);
    private static readonly Color HintColour = new(140, 140, 155, 255);
    private static readonly Color Tick = new(235, 215, 120, 255);

    public static void Draw(UiDraw ui, Screen screen, in PanelLayout layout)
    {
        var panel = screen.Panel;
        var box = layout.Box;

        ui.Rect(0, 0, ui.Size.X, ui.Size.Y, Backdrop);           // dim the world behind it
        ui.Rect(box.X, box.Y, box.Width, box.Height, Box);
        ui.Frame(box.X, box.Y, box.Width, box.Height, Edge);

        var lines = WrapTitle(ui, panel.Title, layout.Box.Width - PanelLayout.Padding * 2f);
        for (int i = 0; i < lines.Count; i++)
            ui.Text(layout.TextX, layout.TitleY + i * layout.LineHeight, lines[i], TitleColour);

        // The field, with a caret, so it is obvious that typing goes here rather than into the list.
        if (screen.Field is { } field)
        {
            string typed = field.IsEmpty ? field.Placeholder : field.Text;
            ui.Text(layout.TextX, layout.FieldY, typed, field.IsEmpty ? DisabledColour : RowColour);
            ui.Rect(layout.TextX + ui.Measure(field.Text).X + 2f, layout.FieldY + 2f, 2f, layout.LineHeight - 4f, Tick);
        }

        if (panel.Problem.Length > 0)
        {
            ui.Text(layout.TextX, layout.RowsY, panel.Problem, DisabledColour);
        }
        else if (panel.Count == 0)
        {
            ui.Text(layout.TextX, layout.RowsY, "(nothing)", DisabledColour);
        }
        else
        {
            for (int i = layout.First; i < Math.Min(layout.First + layout.Visible, panel.Count); i++)
            {
                var row = panel[i];
                float y = layout.RowY(i);
                if (i == screen.Index)
                {
                    var bar = layout.RowRect(i);
                    ui.Rect(bar.X, bar.Y, bar.Width, bar.Height, SelectedBar);
                }
                if (row.Selected) ui.Text(layout.TextX, y, "•", Tick);

                var colour = row.Enabled ? RowColour : DisabledColour;
                string name = row.Count > 1 ? $"{row.Name} ×{row.Count}" : row.Name;

                // The detail sits against the right edge, so the name has to stop before it starts.
                // Rows used to be short names and nothing collided; a dialogue row is a whole sentence
                // (F24) and the refusal was drawn straight through it. Clipped, with an ellipsis, rather
                // than overlapping: half a sentence you can read beats a whole one you cannot.
                float nameX = layout.TextX + 14f;
                float detailWidth = row.Detail.Length > 0 ? ui.Measure(row.Detail).X + 12f : 0f;
                float room = layout.Right - detailWidth - nameX;
                ui.Text(nameX, y, Fit(ui, name, room), colour);

                if (row.Detail.Length > 0)
                    ui.Text(layout.Right - ui.Measure(row.Detail).X, y, row.Detail, row.Enabled ? DetailColour : DisabledColour);
            }

            if (panel.Count > layout.Visible)
                ui.Text(layout.Right - ui.Measure("more").X, layout.TitleY, "more", HintColour);

            // Why the highlighted row cannot be used, under the list: the reason the *rules* gave, not
            // a guess this screen made (R17).
            var current = panel[Math.Clamp(screen.Index, 0, panel.Count - 1)];
            if (!current.Enabled && current.Reason.Length > 0)
                ui.Text(layout.TextX, layout.ReasonY, current.Reason, ReasonColour);
        }

        ui.Text(layout.TextX, layout.HintY, screen.Hint, HintColour);
    }
}
