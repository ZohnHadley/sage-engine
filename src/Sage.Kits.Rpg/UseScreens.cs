#nullable enable
using System;
using Sage.UI;

namespace Sage.Kits.Rpg;

// Using something opens its screen (issue #344): the Use action (the base's InteractionSystem, `Used`)
// on a chest opens the loot window, on a body the same — the thing used is the screen's Other, the
// player its Subject. Two ways in:
//
// - **A container**, from content: the `use_screen` part names the screen and makes the thing usable.
//     "parts": { "inventory": { "items": [ { "item": "bread", "count": 2 } ] }, "use_screen": "rpg:loot" }
// - **A body**: anything that dies with an inventory becomes usable, and using it opens
//   rpg_conventions `lootScreen` (the kit's rpg:loot). A dead speaker has nothing to say (DialogueRules),
//   so using the hermit's body loots it.
//
// - **Somebody with something to say** (a `dialogue`, issue #350): using them starts the conversation
//   (DialogueRules.Start) and opens rpg:dialogue (DialogueView) about them. The conversation and its screen
//   end together: an option that ends it closes the screen, and a screen closed by Back or a click beside
//   it ends the conversation. A game without a UI (no UiScreenStack) starts none, since a conversation
//   nobody can see would only take the player's input.
//
// A conversation reaches a screen the other way, with an action: `{ "open_screen": "rpg:shop" }` on a
// dialogue option opens the shop over the speaker's goods (Sage.UI's open_screen, UiScreenActions).

// Using this entity opens a screen about it; the `use_screen` part adds it.
[Component("sage:use_screen")]   // the engine's namespace, as map_marker: content writes it bare
public struct UseScreen : IComponent
{
    [RecordRef("screen"), Property(Tooltip = "The screen using it opens, with it as the screen's other (the chest, the body)")]
    public RecordId Screen;
}

// "use_screen": "rpg:loot" — using it opens that screen about it. It also makes the thing usable.
[PrefabPart("use_screen", Plugin = RpgKitModule.Id, Shorthand = nameof(Screen))]
public sealed class UseScreenPart : IPrefabPart
{
    [Property(Tooltip = "The screen record using it opens; empty: rpg_conventions lootScreen (the kit's rpg:loot)")]
    public RecordRef<ScreenRecord> Screen;

    public void Apply(in PrefabPartContext ctx)
    {
        ctx.World.Add(ctx.Entity, new UseScreen { Screen = Screen.Id });
        ctx.Entity.AddTag<Interactable>();
    }
}

// Gameplay phase, after the Use action: a player's `Used` on something with a screen opens it, and a
// death leaves a body with an inventory usable.
[System("rpg.use_screens", Phase.Gameplay, After = new[] { "sage.items.use" })]
internal sealed class UseScreenSystem : ISystem
{
    private readonly EventReader<Used> _used;
    private readonly EventReader<Died> _died;

    public UseScreenSystem(World world)
    {
        _used = world.Events.Reader<Used>(this);
        _died = world.Events.Reader<Died>(this);
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        if (_died.HasPending)
            foreach (ref readonly var died in _died.Read())
                if (world.IsAlive(died.Victim) && world.Has<Inventory>(died.Victim) && !died.Victim.Tags.Has<PlayerControlled>())
                    died.Victim.AddTag<Interactable>();

        world.Resources.TryGet<UiScreenStack>(out var stack);
        world.Resources.TryGet<Conversation>(out var conversation);
        if (stack != null && conversation != null) KeepTogether(stack, conversation);

        if (!_used.HasPending) return;
        foreach (ref readonly var used in _used.Read())
        {
            if (!world.IsAlive(used.User) || !world.IsAlive(used.Target) || !used.User.Tags.Has<PlayerControlled>()) continue;
            if (stack is { IsOpen: true }) continue;   // one window from a press
            var screen = ScreenFor(world, used.Target);
            if (!screen.IsEmpty) { UiScreenActions.Open(world, screen, used.User, used.Target); continue; }
            if (stack != null && world.Has<Dialogue>(used.Target) && DialogueRules.Start(world, used.Target, used.User))
                _dialogue = UiScreenActions.Open(world, RpgKitModule.DialogueScreen, used.User, used.Target);
        }
    }

    // A conversation this opened and its screen end together: its screen gone (Back, a click beside it), it
    // ends; it ended (an option said so), its screen goes. One begun some other way (a script) is left alone.
    private void KeepTogether(UiScreenStack stack, Conversation conversation)
    {
        if (_dialogue == null) return;
        if (_dialogue.IsClosing || _dialogue.IsClosed || !Holds(stack, _dialogue))
        {
            if (conversation.Running) conversation.Stop();
            _dialogue = null;
        }
        else if (!conversation.Running)
        {
            stack.Close(_dialogue);
            _dialogue = null;
        }
    }

    private static bool Holds(UiScreenStack stack, UiLayer layer)
    {
        var layers = stack.Layers;
        for (int i = 0; i < layers.Count; i++) if (ReferenceEquals(layers[i], layer)) return true;
        return false;
    }

    private UiLayer? _dialogue;   // the conversation screen this opened, while it is up

    // What using `target` opens: its own screen, the loot screen for a body with an inventory, or nothing.
    internal static RecordId ScreenFor(World world, Entity target)
    {
        var loot = RpgConventions.Of(world.Records()).LootScreen;
        var lootScreen = loot.IsEmpty ? RpgKitModule.LootScreen : loot.Id;
        if (world.TryGet<UseScreen>(target, out var own)) return own.Screen.IsEmpty ? lootScreen : own.Screen;
        if (!world.Has<Inventory>(target)) return default;
        var dead = world.Conventions().Dead;
        return !dead.IsEmpty && world.Has<GameplayTags>(target) && world.HasTag(target, dead.Id) ? lootScreen : default;
    }
}
