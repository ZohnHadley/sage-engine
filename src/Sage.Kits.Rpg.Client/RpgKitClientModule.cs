#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Kits.Rpg;

// The RPG kit's client half (issue #27): which screen is which, and the buttons that open them. The
// base client asks for "dialogue" when somebody is talked to and draws whatever screen it is given;
// this is where the RPG's conversation, journal and spellmaker become those screens. Which key opens
// which is still the game's (its input_map records, and ScreenStack.Bind in its client module).
//
// Loaded with `sage.kits.rpg` by a host with a window (game.json "kits"; SageAppOptions.LoadKitClients).
[Plugin("sage.kits.rpg.client", "0.1.0")]
[RequiresPlugin("sage", ">=0.1")]   // the engine versions it is built for (issue #31)
public sealed class RpgKitClientModule : IModule
{
    // The ids a game asks the ScreenRegistry for. "dialogue" is the client's own (ScreenRegistry.Dialogue).
    public const string Journal = "journal";
    public const string Spellmaker = "spellmaker";

    public IReadOnlyList<Type> Dependencies => new[] { typeof(ClientModule), typeof(RpgKitModule) };

    public void Init(ModuleContext ctx)
    {
        // The buttons that open the kit's screens (08 §3.2). Bound by the game: the engine's input maps
        // no longer name them, so a game without the kit has no Spellmaker key doing nothing.
        var actions = ctx.Engine.Actions;
        actions.Register("Spellbook", ActionKind.Button);
        actions.Register("Spellmaker", ActionKind.Button);
        actions.Register("Journal", ActionKind.Button);
        // The rest screen's button (4g-7); the screen is the kit's widget screen `rpg:rest`, which a game binds
        // (UiScreenStack.Bind) as it binds its map.
        actions.Register("Rest", ActionKind.Button);

        var screens = ctx.Get<ScreenRegistry>();
        screens.Register(ScreenRegistry.Dialogue, () => new DialogueScreen());
        screens.Register(Journal, () => new JournalScreen());
        screens.Register(Spellmaker, () => new SpellmakerScreen());
    }
}
