#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Kits.Rpg;

// The RPG kit's client half (issue #27). It registered the kit's panel screens — the conversation, the
// journal and the spellmaker — with the client's screen registry and bound the kit's keys to them; since
// issue #350 every kit screen is a widget screen in the kit's own content, opened by the kit's simulation
// half (RpgKitModule binds Spellbook, Spellmaker, Journal and Rest; using somebody opens rpg:dialogue), so
// a headless test drives them all. Nothing is left here but the plugin itself, which a game's client
// module may still name as a dependency; what the kit needs from a window, when it needs something, goes here.
//
// Loaded with `sage.kits.rpg` by a host with a window (game.json "kits"; SageAppOptions.LoadKitClients).
[Plugin("sage.kits.rpg.client", "0.1.0")]
[RequiresPlugin("sage", ">=0.1")]   // the engine versions it is built for (issue #31)
public sealed class RpgKitClientModule : IModule
{
    public IReadOnlyList<Type> Dependencies => new[] { typeof(ClientModule), typeof(RpgKitModule) };

    public void Init(ModuleContext ctx) { }
}
