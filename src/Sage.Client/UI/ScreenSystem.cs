#nullable enable
using System;
using Friflo.Engine.ECS;
using Microsoft.Xna.Framework;

namespace sage_engine;

// Drawing screens and reading their input (docs/design/13 §3, TODO F38; decision D8 = our own).
//
// What a screen *is* lives in the engine (`Screen`, `ScreenStack`), because a panel of rows and what
// Enter does to the world are simulation. This is the other half: the buttons, and the box.
//
// It runs in **Overlay, before the UI is rendered**, not in FrameUpdate: a game's HUD queues its own
// drawing in FrameUpdate and the last thing queued is the thing on top, so a screen has to queue after
// it. The first screenshot of this feature had an interaction prompt and a fist painted across the
// spellbook, which is the sort of thing only a screenshot tells you.
public sealed class ScreenSystem : ISystem
{
    private readonly ScreenStack _stack;
    private readonly UiDraw _ui;
    private readonly InputActions _actions;
    private readonly ArchetypeQuery<Transform> _players;

    private readonly ActionId _up, _down, _confirm, _alternate, _back;

    public ScreenSystem(World world, InputActions actions, ActionRegistry registry)
    {
        _stack = world.Resources.Get<ScreenStack>();
        _ui = world.Resources.Get<UiDraw>();
        _actions = actions;
        _players = world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>());

        _up = registry.Get("MenuUp");
        _down = registry.Get("MenuDown");
        _confirm = registry.Get("MenuConfirm");
        _alternate = registry.Get("MenuAlternate");
        _back = registry.Get("MenuBack");
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        Entity player = default;
        foreach (var entity in _players.Entities) { player = entity; break; }   // one local player

        // Opening and closing. Checked with nothing open too, which is how a screen gets opened at
        // all; while one is open the same press closes it.
        foreach (var opener in _stack.OpenActions)
            if (_actions.Pressed(opener) && _stack.Toggle(opener, world, player)) break;

        // The UI context is active exactly while something is open, so the gameplay bindings are
        // consumed and nobody walks about behind an open inventory (08 §3.3).
        _actions.SetActive(InputContext.UI, _stack.IsOpen);

        var screen = _stack.Top;
        if (screen == null) return;

        if (_actions.Pressed(_back)) { _stack.Close(); return; }
        if (_actions.Pressed(_down)) _stack.Move(1);
        if (_actions.Pressed(_up)) _stack.Move(-1);
        if (_actions.Pressed(_confirm)) _stack.Activate(world, player);
        else if (_actions.Pressed(_alternate)) _stack.Alternate(world, player);

        PanelView.Draw(_ui, screen);
    }
}

// Draws a screen: a framed box, a title, the rows, and a hint line. Pixels, not layout — the same
// level the HUD works at (13 "As built (the v1 HUD)").
public static class PanelView
{
    private static readonly Color Backdrop = new(0, 0, 0, 170);
    private static readonly Color Box = new(12, 12, 16, 235);
    private static readonly Color Edge = new(120, 120, 140, 255);
    private static readonly Color TitleColour = new(235, 235, 245, 255);
    private static readonly Color RowColour = new(205, 205, 215, 255);
    private static readonly Color DisabledColour = new(120, 120, 130, 255);
    private static readonly Color DetailColour = new(160, 175, 200, 255);
    private static readonly Color SelectedBar = new(58, 74, 110, 255);
    private static readonly Color ReasonColour = new(210, 150, 150, 255);
    private static readonly Color HintColour = new(140, 140, 155, 255);
    private static readonly Color Tick = new(235, 215, 120, 255);

    private const float Pad = 14f, Rows = 12f;   // rows visible at once before it scrolls

    public static void Draw(UiDraw ui, Screen screen)
    {
        var panel = screen.Panel;
        float line = ui.LineHeight;
        int shown = (int)Math.Min(Rows, Math.Max(panel.Count, 1));
        float width = screen.Width;
        float height = Pad * 2f + line * (shown + 3f);           // title, rows, reason, hint
        float x = MathF.Round((ui.Size.X - width) * 0.5f);
        float y = MathF.Round((ui.Size.Y - height) * 0.5f);

        ui.Rect(0, 0, ui.Size.X, ui.Size.Y, Backdrop);           // dim the world behind it
        ui.Rect(x, y, width, height, Box);
        ui.Frame(x, y, width, height, Edge);

        float textX = x + Pad, textY = y + Pad;
        ui.Text(textX, textY, panel.Title, TitleColour);
        textY += line * 1.5f;

        if (panel.Problem.Length > 0)
        {
            ui.Text(textX, textY, panel.Problem, DisabledColour);
        }
        else if (panel.Count == 0)
        {
            ui.Text(textX, textY, "(nothing)", DisabledColour);
        }
        else
        {
            // A window that follows the selection, so a long spellbook scrolls instead of overflowing.
            int first = Math.Clamp(screen.Index - shown / 2, 0, Math.Max(panel.Count - shown, 0));
            for (int i = first; i < Math.Min(first + shown, panel.Count); i++)
            {
                var row = panel[i];
                if (i == screen.Index) ui.Rect(x + 4f, textY - 2f, width - 8f, line + 2f, SelectedBar);
                if (row.Selected) ui.Text(textX, textY, "•", Tick);

                var colour = row.Enabled ? RowColour : DisabledColour;
                string name = row.Count > 1 ? $"{row.Name} ×{row.Count}" : row.Name;
                ui.Text(textX + 14f, textY, name, colour);

                if (row.Detail.Length > 0)
                {
                    float right = x + width - Pad - ui.Measure(row.Detail).X;
                    ui.Text(right, textY, row.Detail, row.Enabled ? DetailColour : DisabledColour);
                }
                textY += line;
            }

            if (panel.Count > shown)
                ui.Text(x + width - Pad - ui.Measure("more").X, y + Pad, "more", HintColour);

            // Why the highlighted row cannot be used, under the list: the reason the *rules* gave, not
            // a guess this screen made (R17).
            var current = panel[Math.Clamp(screen.Index, 0, panel.Count - 1)];
            if (!current.Enabled && current.Reason.Length > 0)
                ui.Text(textX, y + height - Pad - line * 2f, current.Reason, ReasonColour);
        }

        ui.Text(textX, y + height - Pad - line, screen.Hint, HintColour);
    }
}
