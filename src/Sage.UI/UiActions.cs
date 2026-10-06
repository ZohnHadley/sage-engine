#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;

namespace Sage.UI;

// Opening and closing widget screens from content (issue #344): actions by name, so a dialogue option, a
// layout's button (UiNode.Actions), a trigger's `then` or a quest stage reaches a screen without C#.
//
//   { "text": "Show me your wares.", "end": true, "actions": [ { "open_screen": "rpg:shop" } ] }
//
// The screen opens on the world's UiScreenStack with the action's Subject and Other as its own — in a
// conversation the player and the speaker, on a button the screen's own — so the shop sells the
// speaker's goods and the loot window shows the corpse that was used.

[Action("open_screen", Plugin = UiModule.Id)]
internal sealed class OpenScreenAction : IAction
{
    [EntryValue, Property(Tooltip = "The screen record to open, about the subject (the player) and the other (the speaker, the corpse)")]
    public RecordRef<ScreenRecord> Screen;

    public void Run(in ActionContext context)
    {
        if (!Screen.IsEmpty) UiScreenActions.Open(context.World, Screen.Id, context.Subject, context.Other);
    }
}

// `{ "close_screen": {} }`: the top window closes, as Back would; `{ "close_screen": { "all": true } }`: every one.
[Action("close_screen", Plugin = UiModule.Id)]
internal sealed class CloseScreenAction : IAction
{
    [Property(Tooltip = "Every window at once, not only the top one")]
    public bool All;

    public void Run(in ActionContext context)
    {
        if (!context.World.Resources.TryGet<UiScreenStack>(out var stack) || stack == null) return;
        if (All) stack.CloseAll();
        else stack.CloseTop();
    }
}

[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public static class UiScreenActions
{
    // Opens `screen` on the world's widget stack about `subject` and `other`; null when the world has no
    // stack (a game without sage.ui) or there is no such screen record, which is said once.
    public static UiLayer? Open(World world, RecordId screen, Entity subject, Entity other = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!world.Resources.TryGet<UiScreenStack>(out var stack) || stack == null) return null;
        if (!world.Resources.Get<RecordStore>().TryGet(screen, out ScreenRecord _))
        {
            Log.Once(LogCat.UI, LogLevel.Error, $"open_screen:{screen}", $"No screen record {screen} to open");
            return null;
        }
        return stack.Open(screen, new UiBindContext(world, subject, other));
    }
}
