#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Simulation;

// Who a condition or an action is about, written the way a wire writes its target (issue #275): an
// entity's name, or `!subject` / `!activator` / `!player` (the one it is about: the player in a
// conversation, the activator on a wire) or `!other` / `!self` / `!caller` (whoever is doing it: the
// speaker, the relay, the state machine). `fire` (#89) always read them so; every word that names an
// entity reads them through here, base and gameplay alike.
//
// A name is looked up in the world when it is asked, so something spawned or respawned since is found;
// a word asked often keeps what it found (`cache`) and looks again only when that has gone or been
// renamed, so asking does not walk the world each time.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // stage 2 logic (#89, #275): may change before 1.0
public static class LogicTargets
{
    public const string Subject = "!subject";
    public const string Other = "!other";

    // The entity `target` names; null when nothing by that name is in the world or `target` is empty.
    // A `!` word that is not one of the six is nobody (IsKnown says which are).
    public static Entity Find(World world, string target, Entity subject, Entity other)
    {
        Entity cache = default;
        return Find(world, target, subject, other, ref cache);
    }

    // The same, keeping a name's entity in `cache` between asks.
    public static Entity Find(World world, string target, Entity subject, Entity other, ref Entity cache)
    {
        if (string.IsNullOrEmpty(target)) return default;
        if (target[0] == '!')
        {
            if (Is(target, "!subject") || Is(target, "!activator") || Is(target, "!player")) return subject;
            if (Is(target, "!other") || Is(target, "!self") || Is(target, "!caller")) return other;
            return default;
        }
        if (!cache.IsNull && world.IsAlive(cache) && cache.Name == target) return cache;
        return cache = world.FindByName(target);
    }

    // Whether a `!` word is one of the six; a name is always "known" (it may turn up later).
    public static bool IsKnown(string target) =>
        string.IsNullOrEmpty(target) || target[0] != '!'
        || Is(target, "!subject") || Is(target, "!activator") || Is(target, "!player")
        || Is(target, "!other") || Is(target, "!self") || Is(target, "!caller");

    internal static void WarnUnknown(string word, string target) =>
        Log.Once(LogCat.Events, LogLevel.Warn, $"{word}-target:{target}",
            $"{word}: no target '{target}' (a name, or !subject / !activator / !other / !self)");

    private static bool Is(string target, string special) => string.Equals(target, special, StringComparison.OrdinalIgnoreCase);
}
