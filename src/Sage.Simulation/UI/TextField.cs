#nullable enable
using System;
using System.Text;

namespace Sage.Simulation;

// A line of text a player types (docs/design/13 §3, TODO F38).
//
// In the engine beside `Screen`, and for the same reason: what a field *contains* is simulation data —
// the name of a spell that is about to become a record — and only the caret drawn after it is the
// client's. A headless test types "Bob's Fire", presses backspace twice and asserts the draft.
//
// It takes **characters, not keys** (08 §3.1): the operating system owns the keyboard layout, the dead
// keys and the modifiers, so `Keys.A` does not mean "a" on every keyboard in the world, and a name
// typed on an AZERTY one would come out wrong if this read key states.
//
// No selection, no cursor movement, no clipboard: a name and a search box are what v1 needs, and the
// day something needs more, this grows rather than a widget library arriving.
public sealed class TextField
{
    private readonly StringBuilder _text = new();

    public TextField(int maxLength = 40, string placeholder = "")
    {
        MaxLength = Math.Max(maxLength, 1);
        Placeholder = placeholder;
    }

    public int MaxLength { get; }

    // What to show when it is empty, so a screen does not have to explain the box.
    public string Placeholder { get; }

    public string Text => _text.ToString();

    public int Length => _text.Length;

    public bool IsEmpty => _text.Length == 0;

    // Raised when the text changes, so a screen can re-price a spell as it is named.
    public event Action? Changed;

    public void Clear()
    {
        if (_text.Length == 0) return;
        _text.Clear();
        Changed?.Invoke();
    }

    public void Set(string text)
    {
        _text.Clear();
        _text.Append(text.Length > MaxLength ? text[..MaxLength] : text);
        Changed?.Invoke();
    }

    public bool Backspace()
    {
        if (_text.Length == 0) return false;
        _text.Length--;
        Changed?.Invoke();
        return true;
    }

    // One character as the window reported it. Returns true if the field took it, which is how the
    // caller knows whether to let anything else have it.
    public bool Type(char c)
    {
        if (c == '\b') return Backspace();

        // Control characters are somebody else's: Return is "done" and Escape is "give up", and both
        // belong to the screen. Rejecting them here is what keeps that decision in one place.
        if (char.IsControl(c)) return false;
        if (_text.Length >= MaxLength) return false;

        _text.Append(c);
        Changed?.Invoke();
        return true;
    }

    // A whole frame's worth of typing.
    public bool Type(ReadOnlySpan<char> typed)
    {
        bool any = false;
        foreach (char c in typed) any |= Type(c);
        return any;
    }

    public override string ToString() => _text.ToString();
}
