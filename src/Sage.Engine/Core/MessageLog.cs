#nullable enable
using System;
using System.Collections.Generic;

namespace sage_engine;

// Short-lived messages for the player (docs/design/13 §3): "you picked up a sword", "the skeleton
// hits you for 12", "you died". A world resource, so any system can push one without knowing whether
// a HUD exists — a headless server pushes into a log nobody draws, which costs a string and a slot.
//
// This is not the game's dialogue or quest log (F24): it is the line of text that scrolls past the
// bottom of the screen and is gone in a few seconds.

// What a message is *about*, so presentation can colour it without the simulation naming colours.
public enum MessageKind { Info, Good, Bad }

// What the simulation says. It is sent, not stored: a headless server sends these and nothing
// buffers them, and a HUD reads them with its own cursor like any other presentation system
// (04 §3.1, "simulation → presentation"). `MessageLog` below is one such reader.
[GameEvent]
public readonly record struct Said(string Text, MessageKind Kind, float Seconds);

public readonly record struct Message(string Text, MessageKind Kind, float Remaining, float Duration)
{
    // 1 when it arrives, 0 as it goes: a HUD fades on this rather than inventing its own timing.
    public float Fade => Duration <= 0f ? 1f : Math.Clamp(Remaining / Duration, 0f, 1f);
}

// The on-screen buffer: the last few things said, fading out. It is *display state* built from
// Said events, which is why it ages in display time (a message lasts five seconds whether the
// simulation ran once or a hundred times in them) and why a world with no HUD still has one.
public sealed class MessageLog
{
    private const int MaxMessages = 32;   // what scrolls past, not what is remembered (F24)

    private readonly List<Message> _messages = new();
    private readonly EventReader<Said> _said;

    internal MessageLog(GameEvents events) => _said = events.Reader<Said>(this, Schedule.Fixed);

    public int Count => _messages.Count;
    public Message this[int index] => _messages[index];

    // Newest last, which is the order a HUD draws them in.
    public ReadOnlySpan<Message> Messages => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_messages);

    public void Add(string text, MessageKind kind = MessageKind.Info, float seconds = 5f)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (_messages.Count >= MaxMessages) _messages.RemoveAt(0);
        _messages.Add(new Message(text, kind, seconds, seconds));
    }

    // Takes whatever was said since the last frame, then ages what is on screen in display time.
    // Called once a frame by World.RunFrame, before the Frame phases, so a HUD drawn this frame sees
    // what the ticks before it said.
    // Takes what was said without ageing anything: called at the end of every tick so the log keeps
    // up even in a world that never draws a frame (a dedicated server).
    public void Take()
    {
        foreach (ref readonly var said in _said.Read()) Add(said.Text, said.Kind, said.Seconds);
    }

    public void Advance(float seconds)
    {
        for (int i = _messages.Count - 1; i >= 0; i--)
        {
            var message = _messages[i] with { Remaining = _messages[i].Remaining - seconds };
            if (message.Remaining <= 0f) _messages.RemoveAt(i);
            else _messages[i] = message;
        }

        Take();   // after the ageing, so a message arriving this frame gets its whole duration
    }

    public void Clear() => _messages.Clear();
}

public static class MessageLogExtensions
{
    // Every world has one (03 §3.4), like DebugDraw: telling the player something never needs a null
    // check at the call site.
    public static MessageLog Messages(this World world) => world.Resources.Get<MessageLog>();

    // Telling the player something is a fact the simulation sends, not a write into a HUD.
    public static void Say(this World world, string text, MessageKind kind = MessageKind.Info, float seconds = 5f)
    {
        if (!string.IsNullOrEmpty(text)) world.Events.Send(new Said(text, kind, seconds));
    }
}
