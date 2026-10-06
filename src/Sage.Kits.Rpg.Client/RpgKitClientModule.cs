#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Kits.Rpg;

// The RPG kit's client half (issue #27): which screen is which, and the buttons that open them. The
// base client asks for "dialogue" when somebody is talked to and draws whatever screen it is given;
// this is where the RPG's conversation, journal and spellmaker become those screens. The keys are the
// kit's defaults (its content's `rpg:ui` and `rpg:gameplay` maps, issue #354), bound to the spellmaker and
// the journal here; a game patches the maps for other keys.
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
        var screens = ctx.Get<ScreenRegistry>();
        screens.Register(ScreenRegistry.Dialogue, () => new DialogueScreen());
        screens.Register(Journal, () => new JournalScreen());
        screens.Register(Spellmaker, () => new SpellmakerScreen());
        _screens = screens;
        _actions = ctx.Engine.Actions;
    }

    private ScreenRegistry? _screens;
    private ActionRegistry? _actions;

    // What the kit's default keys open (issue #354): a bare kit game has the spellmaker on M and the journal on J
    // without a client module of its own. The actions and their keys are the kit's (`rpg:ui`, `rpg:gameplay`,
    // RpgKitModule); a game that binds the same action to another screen after this (the Sandbox's own journal)
    // replaces it, since widget screens are asked first.
    public void OnWorldCreated(World world)
    {
        var stack = world.Resources.Get<ScreenStack>();
        stack.Bind(_actions!.Get("Spellmaker"), _screens!.Create(Spellmaker)!);
        stack.Bind(_actions!.Get("Journal"), _screens.Create(Journal)!);
    }
}
