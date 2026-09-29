#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Sage.Client;

// Screens by id (docs/design/13 §3, issue #27). The client asks for "dialogue" when somebody is talked
// to (DialogueSystem) and a game binds "journal" to a key, without either knowing which class draws
// it: a kit registers its screens here (Sage.Kits.Rpg.Client: "dialogue", "journal", "spellmaker"), a
// game its own, and the base client ships none. So the base engine never names an RPG's screen, and a
// game without a conversation screen simply has no conversations shown.
//
// A factory rather than an instance: a screen holds its selection and its panel, and those belong to
// one world's stack (each world makes its own). Provided by ClientModule; register in Init.
public sealed class ScreenRegistry
{
    // The one the client itself asks for: what opens when somebody with a `dialogue` is used.
    public const string Dialogue = "dialogue";

    private readonly Dictionary<string, Func<Screen>> _screens = new(StringComparer.OrdinalIgnoreCase);

    // Closed when the client starts: worlds made after that must all see the same screens.
    public RegistrationSeal Seal { get; } = new("screen", "a world created before it has already looked for it and found nothing");

    public IEnumerable<string> Ids => _screens.Keys.OrderBy(id => id, StringComparer.OrdinalIgnoreCase);

    public void Register(string id, Func<Screen> create)
    {
        Seal.Check(id);
        if (!_screens.TryAdd(id, create))
            throw new InvalidOperationException($"Two screens claim the id '{id}'.");
    }

    public bool Has(string id) => _screens.ContainsKey(id);

    // A new screen for this id, or null when nothing registered one.
    public Screen? Create(string id) => _screens.TryGetValue(id, out var create) ? create() : null;
}
