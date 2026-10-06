#nullable enable
using System;
using Microsoft.Xna.Framework;

namespace Sage.Client;

// Draws the Shipping console (issue #353): Sage.Core's DropDownConsole, a panel that slides down from
// the top of the screen over everything the screens drew, in the game UI's own font renderer. Dear ImGui
// is a dev tool and a Shipping build has none, so this is what `con_enable 1` opens on the tilde key.
// The host feeds it keys and characters (Game1.FeedConsole); this only reads the model and draws it.
// Overlay, after the screens and before the UI is rendered, so it is the last thing queued: on top.
[System("sage.client.console", Phase.Overlay, After = new[] { "sage.client.screens" }, Before = new[] { "sage.client.ui" })]
internal sealed class ConsoleSystem : ISystem
{
    private static readonly Color Backdrop = new(8, 8, 12, 232);
    private static readonly Color Edge = new(120, 120, 140, 255);
    private static readonly Color InputBar = new(28, 28, 38, 245);
    private static readonly Color Prompt = new(235, 215, 120, 255);
    private static readonly Color Text = new(240, 240, 245, 255);
    private static readonly Color Hint = new(140, 140, 155, 255);

    // How much of the screen's height it covers when fully down.
    private const float Height = 0.5f;
    private const float Pad = 6f;

    private readonly DropDownConsole _console;
    private readonly UiDraw _ui;

    // The caret's offset in the line, measured when the line or the caret moved (measuring cuts a substring).
    private string _measuredLine = "";
    private int _measuredCaret = -1;
    private float _caretX;

    public ConsoleSystem(World world, DropDownConsole console)
    {
        _console = console;
        _ui = world.Resources.Get<UiDraw>();
    }

    public void Run(in SystemContext ctx)
    {
        var c = _console;
        if (!c.IsVisible) return;
        c.Refresh();

        float lh = _ui.LineHeight;
        float width = _ui.Size.X;
        float full = MathF.Max(_ui.Size.Y * Height, lh * 4f + Pad * 3f);
        float eased = 1f - (1f - c.Slide) * (1f - c.Slide);   // quick at first, settling
        float bottom = full * eased;
        float top = bottom - full;

        _ui.Rect(0f, top, width, full, Backdrop);
        _ui.Rect(0f, bottom - 1f, width, 1f, Edge);

        // The input line at the bottom of the panel, the scrollback above it, newest lowest.
        float inputY = bottom - lh - Pad * 1.5f;
        _ui.Rect(0f, inputY - Pad * 0.5f, width, lh + Pad, InputBar);
        float promptW = _ui.Measure("> ").X;
        _ui.Text(Pad, inputY, ">", Prompt);
        _ui.PushClip(0f, top, width, full);
        _ui.Text(Pad + promptW, inputY, c.Line, Text);
        if (c.IsOpen && ((int)(ctx.Frame.RealTime * 2d) & 1) == 0)
        {
            if (!ReferenceEquals(_measuredLine, c.Line) || _measuredCaret != c.Caret)
            {
                _measuredLine = c.Line;
                _measuredCaret = c.Caret;
                _caretX = c.Caret == 0 ? 0f : _ui.Measure(c.Line[..c.Caret]).X;
            }
            _ui.Rect(Pad + promptW + _caretX, inputY, 2f, lh, Prompt);
        }

        float listBottom = inputY - Pad;
        int rows = Math.Max((int)((listBottom - (top + Pad)) / lh), 1);
        c.PageLines = Math.Max(rows - 1, 1);
        var lines = c.Lines;
        int last = lines.Count - 1 - c.Scroll;   // the newest line shown
        for (int i = 0; i < rows && last - i >= 0; i++)
        {
            int index = last - i;
            _ui.Text(Pad, listBottom - (i + 1) * lh, lines[index], ColourFor(c.Levels[index]));
        }
        if (c.Scroll > 0)
            _ui.Text(width - Pad - _ui.Measure("PgDn: newer").X, top + Pad, "PgDn: newer", Hint);
        _ui.PopClip();
    }

    private static Color ColourFor(LogLevel level) => level switch
    {
        LogLevel.Trace => new Color(140, 140, 140),
        LogLevel.Debug => new Color(190, 190, 205),
        LogLevel.Info => new Color(240, 240, 240),
        LogLevel.Warn => new Color(255, 217, 77),
        LogLevel.Error => new Color(255, 102, 89),
        _ => new Color(255, 77, 255),
    };
}
