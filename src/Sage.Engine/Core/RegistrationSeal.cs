#nullable enable
using System;

namespace sage_engine;

// A registry that closes at a point in the app's boot (REDESIGN §3.3, issue #12).
//
// Until now every registry accepted a registration at any time, and a late one did nothing useful and
// said nothing: a cvar registered in Start never read config.cfg (#58), a record type registered after
// the records loaded left its JSON as dead JSON, a module added after Init never ran it. MAKING_A_GAME
// §10 is mostly a list of these. A sealed registry turns each into an exception at the line that did
// it, saying what was missed and where the registration belongs.
public sealed class RegistrationSeal
{
    private readonly string _what;
    private readonly string _consequence;
    private string? _stage;

    // `what` names the thing registered ("cvar"); `consequence` says what registering it late misses.
    public RegistrationSeal(string what, string consequence)
    {
        _what = what;
        _consequence = consequence;
    }

    public bool IsSealed => _stage != null;

    // Closes the registry. `stage` says when, for the message ("config.cfg was read"). Later calls
    // keep the first stage.
    public void Seal(string stage) => _stage ??= stage;

    public void Check(string name)
    {
        if (_stage != null)
            throw new InvalidOperationException(
                $"{_what} '{name}' was registered after {_stage}, so {_consequence}. " +
                $"Register it in a module's Init (docs/MAKING_A_GAME.md §10).");
    }
}
