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

public readonly record struct Message(string Text, MessageKind Kind, float Remaining, float Duration)
{
    // 1 when it arrives, 0 as it goes: a HUD fades on this rather than inventing its own timing.
    public float Fade => Duration <= 0f ? 1f : Math.Clamp(Remaining / Duration, 0f, 1f);
}

public sealed class MessageLog
{
    private const int MaxMessages = 32;   // what scrolls past, not what is remembered (F24)

    private readonly List<Message> _messages = new();

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

    // Ages messages in display time; called once a frame by World.RunFrame.
    public void Advance(float seconds)
    {
        for (int i = _messages.Count - 1; i >= 0; i--)
        {
            var message = _messages[i] with { Remaining = _messages[i].Remaining - seconds };
            if (message.Remaining <= 0f) _messages.RemoveAt(i);
            else _messages[i] = message;
        }
    }

    public void Clear() => _messages.Clear();
}

public static class MessageLogExtensions
{
    // Every world has one (03 §3.4), like DebugDraw: telling the player something never needs a null
    // check at the call site.
    public static MessageLog Messages(this World world) => world.Resources.Get<MessageLog>();

    public static void Say(this World world, string text, MessageKind kind = MessageKind.Info, float seconds = 5f) =>
        world.Messages().Add(text, kind, seconds);
}
