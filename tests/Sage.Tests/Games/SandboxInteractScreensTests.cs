#nullable enable
using System.Linq;
using System.Numerics;
using Sage.Kits.Rpg;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Loot, topics and the shop reachable from play (issue #344), in the Sandbox's real content: the Use key on
// a dead watcher opens the RPG kit's loot screen over its body, and the hermit's "Show me what you have."
// opens the shop over his goods — the player as the screen's subject, the body or the speaker its other.
public class SandboxInteractScreensTests
{
    public SandboxInteractScreensTests() { _ = TestEnv.UserRoot; }

    private static readonly RecordId Pelt = new("sandbox", "pelt");
    private static readonly RecordId Bolt = new("sandbox", "bolt");

    private static void Step(World world, int ticks = 1) => NpcLocomotionTests.Step(world, ticks);

    // Presses Use as the player for one tick, looking along -Z, the way the client's input does.
    private static void Use(HeadlessApp app)
    {
        var input = app.World.Resources.Get<PlayerInput>();
        input.HasCommand = true;
        input.Command = new PlayerCommand { Tick = input.Command.Tick + 1, Pressed = default(ActionMask).With(app.Engine.Actions.Get("Use")) };
        Step(app.World);
        input.Command = new PlayerCommand { Tick = input.Command.Tick + 1 };
    }

    // Stands the player a step south (+Z) of `target`, facing it.
    private static void StandBefore(World world, Entity player, Entity target)
    {
        var at = world.Get<Transform>(target).LocalPosition;
        world.Teleport(player, Transform.At(at + new Vector3(0f, 0f, 1.4f)));
        Step(world, 2);
    }

    private static Entity Named(World world, string name) =>
        world.QueryAll().Entities.ToEntityList().FirstOrDefault(e => e.Name == name);

    [Xunit.Fact]
    public void TheUseKeyLootsADeadWatcher_AndTheHermitsShopOpensFromHisDialogue()
    {
        using var app = SandboxScreensTests.Boot();
        var world = app.World;
        var player = SandboxScreensTests.Player(world);
        var stack = SandboxScreensTests.Stack(world);
        Step(world, 3);

        // A watcher, killed where it stands, and the player in front of its body.
        var watcher = Named(world, "watcher");
        Assert.False(watcher.IsNull);
        Assert.Equal(1, world.CountOf(watcher, Pelt));
        var died = new EventProbe<Died>(world);
        Combat.ApplyDamage(world, new DamageInfo(default, watcher, new RecordId("sage", "physical"), 5000f, Vector3.Zero, Vector3.UnitY));
        Step(world, 2);
        Assert.True(world.HasTag(watcher, world.Conventions().Dead.Id));
        Assert.Contains(died.All, d => d.Victim == watcher);
        Assert.True(watcher.Tags.Has<Interactable>(), "a body with an inventory is usable");
        StandBefore(world, player, watcher);

        Use(app);
        var loot = Assert.Single(stack.Layers, l => l.Modal);
        Assert.Equal(RpgKitModule.LootScreen, loot.Screen!.Id);
        Assert.Equal(player, loot.Screen.Context.Subject);
        Assert.Equal(watcher, loot.Screen.Context.Other);
        var view = Assert.IsType<LootView>(loot.Screen.ViewModel);
        Assert.Equal(1, view.TakeAll(world, watcher, player));
        Assert.Equal(1, world.CountOf(player, Pelt));
        stack.CloseAll();

        // The hermit: Use reaches him and opens no widget screen — the client's DialogueSystem starts the
        // conversation on that `Used`, as here — and his option opens the shop over his goods.
        var hermit = Named(world, "hermit");
        Assert.False(hermit.IsNull);
        StandBefore(world, player, hermit);
        var used = new EventProbe<Used>(world);
        Use(app);
        Assert.Contains(used.All, u => u.User == player && u.Target == hermit);
        Assert.False(stack.IsOpen, "talking is the conversation's screen, not a widget screen");
        Assert.True(DialogueRules.Start(world, hermit, player));
        var conversation = world.Resources.Get<Conversation>();

        var trade = DialogueRules.Current(world)!.Options.Single(o => o.Text == "Show me what you have.");
        Assert.True(DialogueRules.Pick(world, trade));
        Assert.False(conversation.Running);
        var shop = Assert.Single(stack.Layers, l => l.Modal);
        Assert.Equal(RpgKitModule.ShopScreen, shop.Screen!.Id);
        Assert.Equal(player, shop.Screen.Context.Subject);
        Assert.Equal(hermit, shop.Screen.Context.Other);
        var goods = Assert.IsType<ShopView>(shop.Screen.ViewModel);
        Assert.Contains(goods.Stock.Items, i => i.Item == Bolt);

        // His topics, the same way.
        stack.CloseAll();
        Assert.True(DialogueRules.Start(world, hermit, player));
        Assert.True(DialogueRules.Pick(world, DialogueRules.Current(world)!.Options.Single(o => o.Text == "I have questions.")));
        var topics = Assert.Single(stack.Layers, l => l.Modal);
        Assert.Equal(RpgKitModule.TopicsScreen, topics.Screen!.Id);
        Assert.Contains(Assert.IsType<TopicsView>(topics.Screen.ViewModel).Topics, t => t.Keyword == "the watchers");
    }
}
