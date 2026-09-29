#nullable enable
using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;

namespace Sage.Simulation;

// A screen, and the stack of open ones (docs/design/13 §3, TODO F38; decision D8 = our own).
//
// **In the engine, not the client**, even though nothing here draws: a screen is a panel, a highlighted
// row, and what pressing a button on that row does to the world — all simulation. Only the drawing and
// the reading of input are the client's (`ScreenSystem`, `PanelView` in Sage.Client). The split is the
// same one `RenderSnapshot` makes, and it buys the same thing: **a headless test can open a screen,
// move down twice, press Enter and assert what happened to the world.**
//
// Deliberately not a widget toolkit. A screen is *a panel and what Enter does*, which covers a
// spellbook, a bag, a merchant's stock and a quest log. Anything that needs more — a paper doll, a
// dialogue tree with portraits — draws itself with `UiDraw`, as the HUD already does, and this stays
// small.

public abstract class Screen
{
    // Rebuilt when the screen opens and after every action, never per frame: a row holds strings, and
    // rebuilding at display rate would allocate per row per frame (02 §4.6).
    public Panel Panel { get; } = new();

    // The highlighted row.
    public int Index { get; set; }

    public abstract void Build(World world, Entity subject);

    // Enter on a row. Return true if the world changed, so the panel is rebuilt.
    public virtual bool Activate(World world, Entity subject, in PanelRow row) => false;

    // The other button (Delete / gamepad X): unequip, drop, forget. Same contract.
    public virtual bool Alternate(World world, Entity subject, in PanelRow row) => false;

    // The line along the bottom, saying what the buttons do on *this* screen.
    public virtual string Hint => "↑↓ choose    Enter use    Esc close";

    // How wide to draw it, in pixels. A list of spells does not need the whole window.
    public virtual float Width => 460f;

    // A screen that is typed into says so by returning a field. The client hands it every character
    // the window reported while this screen is on top, and draws it under the title with a caret.
    // Null — the usual case — means the screen is a list and nothing else.
    public virtual TextField? Field => null;

    // Called when the field changed, so a screen can re-price what is being named as it is typed.
    public virtual void Typed(World world, Entity subject) { }
}

// The open screens, innermost last. A world resource, because a screen acts on entities in a world.
public sealed class ScreenStack
{
    private readonly List<Screen> _open = new();
    private readonly Dictionary<int, (ActionId Action, Screen Screen)> _byAction = new();

    public bool IsOpen => _open.Count > 0;

    public Screen? Top => _open.Count > 0 ? _open[^1] : null;

    public IReadOnlyList<Screen> Open => _open;

    // "This action opens that screen", and pressing it again while that screen is on top closes it —
    // which is what every inventory key in every game does.
    public void Bind(ActionId action, Screen screen)
    {
        if (!action.IsValid) { Log.Warn(LogCat.UI, $"ScreenStack.Bind: {screen.GetType().Name} bound to an unregistered action"); return; }
        _byAction[action.Index] = (action, screen);
    }

    // What opens a screen, for whatever reads the buttons (ScreenSystem). The stack owns this so a
    // game can bind a third screen without the client learning its name.
    public IEnumerable<ActionId> OpenActions
    {
        get { foreach (var bound in _byAction.Values) yield return bound.Action; }
    }

    public bool Toggle(ActionId action, World world, Entity subject)
    {
        if (!action.IsValid || !_byAction.TryGetValue(action.Index, out var bound)) return false;
        if (ReferenceEquals(Top, bound.Screen)) { Close(); return true; }
        Show(bound.Screen, world, subject);
        return true;
    }

    public void Show(Screen screen, World world, Entity subject)
    {
        _open.Remove(screen);          // never twice in the stack
        _open.Add(screen);
        screen.Index = 0;
        screen.Build(world, subject);
        Log.Debug(LogCat.UI, $"screen: {screen.GetType().Name} open with {screen.Panel.Count} row(s)");
    }

    public void Close()
    {
        if (_open.Count == 0) return;
        _open.RemoveAt(_open.Count - 1);
    }

    public void CloseAll() => _open.Clear();

    // ---- what the buttons do -----------------------------------------------------------------------

    // Wraps at both ends: a list you can hold a key on and go round is what a player expects of a
    // short list, and every list here is short.
    public void Move(int delta)
    {
        var screen = Top;
        if (screen == null || screen.Panel.Count == 0) return;
        int count = screen.Panel.Count;
        screen.Index = ((screen.Index + delta) % count + count) % count;
    }

    public bool Activate(World world, Entity subject) => Act(world, subject, alternate: false);

    public bool Alternate(World world, Entity subject) => Act(world, subject, alternate: true);

    private bool Act(World world, Entity subject, bool alternate)
    {
        var screen = Top;
        if (screen == null) return false;
        if (screen.Panel.Count == 0)
        {
            screen.Build(world, subject);   // nothing to act on: at least re-ask the simulation
            return false;
        }

        screen.Index = Math.Clamp(screen.Index, 0, screen.Panel.Count - 1);
        var row = screen.Panel[screen.Index];
        bool changed = alternate ? screen.Alternate(world, subject, in row) : screen.Activate(world, subject, in row);
        if (!changed) return false;

        // The world moved under the screen, so the rows are re-asked — and the highlight stays where
        // it can: dropping the last thing in a bag must not leave the selection past the end.
        screen.Build(world, subject);
        screen.Index = Math.Clamp(screen.Index, 0, Math.Max(screen.Panel.Count - 1, 0));
        return true;
    }

    // After something *else* changed the world — a save being loaded, a pickpocket, a tick of poison.
    public void Rebuild(World world, Entity subject)
    {
        var screen = Top;
        if (screen == null) return;
        screen.Build(world, subject);
        screen.Index = Math.Clamp(screen.Index, 0, Math.Max(screen.Panel.Count - 1, 0));
    }
}
