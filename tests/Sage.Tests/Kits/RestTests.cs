#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Sage.Kits.Rpg;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The RPG kit's rest rule and screen (issue 4g-7): sleep is refused with a hostile creature near, waiting
// is not, and either passes time through Time.Pass (one TimePassed, at the tick boundary); waking applies
// the game's rest effect by the hours slept. The screen is a slider of hours and two buttons over that rule.
public class RestTests
{
    public RestTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
    [
      { "type": "effect", "id": "hurt", "modifiers": [ { "attribute": "sage:health", "op": "Add", "value": -1 } ] },
      { "type": "effect", "id": "rest_heal", "modifiers": [ { "attribute": "sage:health", "op": "Add", "value": 5 } ] },
      { "type": "rpg_conventions", "id": "rpg", "restEnemyRange": 25, "restMaxHours": 12, "restEffect": "rest_heal" }
    ]
    """;

    private static readonly RecordId Health = new("sage", "health");
    private static readonly RecordId Dead = new("sage", "state.dead");

    private static HeadlessApp Boot()
    {
        var app = HeadlessApp.Gameplay().With(new UiModule(), new RpgKitModule()).WithEngineContent()
            .File("data/rest.json", Records).Boot("rest");
        Assert.Equal(0, app.Records.ErrorCount);
        var clock = WorldClock.Of(app.World);
        clock.Scale = 0;
        clock.Hour = 22;
        return app;
    }

    private static Entity Hero(World world, float health)
    {
        var hero = world.Create(Transform.At(Vector3.Zero), "hero");
        world.AddAttributes(hero);
        hero.AddTag<PlayerControlled>();
        Effects.Apply(world, hero, new RecordId("sage", "hurt"), hero, 100f - health);
        world.FlushCommands();
        return hero;
    }

    // A creature with a mind of its own and no faction: hostile to the player, as the engine has always had it.
    private static Entity Wolf(World world, Vector3 at)
    {
        var wolf = world.Create(Transform.At(at), "wolf");
        world.AddAttributes(wolf);
        world.Add(wolf, new AIState());
        world.FlushCommands();
        return wolf;
    }

    private static void Tick(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    [Xunit.Fact]
    public void SleepIsRefusedWithAnEnemyNearAndWaitingIsNot()
    {
        using var app = Boot();
        var world = app.World;
        var clock = WorldClock.Of(world);
        var hero = Hero(world, 40f);
        var wolf = Wolf(world, new Vector3(10, 0, 0));

        Assert.False(Rest.Can(world, hero, RestKind.Sleep, 8, out string reason));
        Assert.Equal("@rpg.rest.enemies", reason);
        Assert.Equal(wolf, Rest.EnemyNear(world, hero, 25f));
        Assert.True(Rest.Can(world, hero, RestKind.Wait, 8, out _));        // standing about is allowed
        Assert.False(Rest.Can(world, hero, RestKind.Wait, 13, out reason)); // past restMaxHours
        Assert.Equal("@rpg.rest.hours_range", reason);
        Assert.False(Rest.Begin(world, hero, RestKind.Sleep, 8, out _));
        Assert.Equal(22, clock.Hour, 6);                                    // a refusal passes nothing

        world.Get<Transform>(wolf).LocalPosition = new Vector3(40, 0, 0);    // out of range
        Assert.True(Rest.Can(world, hero, RestKind.Sleep, 8, out _));
        world.Get<Transform>(wolf).LocalPosition = new Vector3(5, 0, 0);
        world.AddTag(wolf, Dead);                                           // the dead do not keep you up
        Assert.True(Rest.Can(world, hero, RestKind.Sleep, 8, out _));

        // Through Time.Pass, at once outside a tick: one skip, the reason "rest", and the heal by the hour.
        var watch = new Watch(world);
        double from = clock.Elapsed;
        Assert.True(app.CVars.Execute("rest 8"));
        Assert.Equal(from + 8, clock.Elapsed, 6);
        Assert.Equal(6, clock.Hour, 6);
        Tick(world);
        var passed = Assert.Single(watch.Seen);
        Assert.Equal(Rest.SleepReason, passed.Reason);
        Assert.Equal(80f, world.Attribute(hero, Health));                   // 40 + 5 × 8

        // A wait passes time and heals nothing.
        Assert.True(Rest.Begin(world, hero, RestKind.Wait, 3, out _));
        Tick(world);
        Assert.Equal(Rest.WaitReason, watch.Seen[^1].Reason);
        Assert.Equal(9, clock.Hour, 6);
        Assert.Equal(80f, world.Attribute(hero, Health));
    }

    // Asked for inside a tick (a screen's button pressed while systems run), the rest waits for its end like
    // any Time.Pass: no system later in the tick sees the hour change.
    [Xunit.Fact]
    public void ARestAskedForDuringATickPassesAtItsEnd()
    {
        using var app = Boot();
        var world = app.World;
        var hero = Hero(world, 100f);
        var during = new RestDuringTick(hero);
        world.AddSystem(during, Phase.Commands);
        Tick(world);
        Assert.True(during.Asked);
        Assert.Equal(22, during.HourAfterAsking, 6);
        Assert.Equal(4, WorldClock.Of(world).Hour, 6);
    }

    // The screen (the kit's `rpg:rest` over RestView), clicked: the slider's buttons move the hours within
    // 1..restMaxHours, Sleep is disabled with the reason shown while the wolf is near, and once it is gone
    // Sleep passes the hours and says so.
    [Xunit.Fact]
    public void TheRestScreenSlidesTheHoursAndSleepsWhenNoEnemyIsNear()
    {
        using var app = Boot();
        var world = app.World;
        var clock = WorldClock.Of(world);
        var hero = Hero(world, 50f);
        var wolf = Wolf(world, new Vector3(0, 0, 12));

        var stack = world.Resources.Get<UiScreenStack>();
        stack.SetViewport(new Vector2(1280f, 720f));
        stack.OpenSeconds = stack.CloseSeconds = 0f;
        var layer = stack.Open(RpgKitModule.RestScreen, new UiBindContext(world, hero));
        stack.Update(UiInput.Wait(0f));
        var rest = Assert.IsType<RestView>(layer.Screen!.ViewModel);
        var view = layer.Screen.View;

        Assert.Equal(RestView.DefaultHours, rest.Hours);
        Assert.Equal(12, rest.MaxHours);
        Assert.False(rest.CanSleep);
        Assert.True(rest.CanWait);
        Assert.False(view.Find("sleep")!.Enabled);
        Assert.Equal("You cannot sleep with enemies nearby.", view.Find<Label>("refused")!.Text);
        Assert.Equal("Day 0, 22:00", view.Find<Label>("now")!.Text);

        Click(stack, layer, "more");
        Click(stack, layer, "more");
        Assert.Equal(10, rest.Hours);
        Assert.Equal("10 hours", view.Find<Label>("hours")!.Text);
        for (int i = 0; i < 5; i++) Click(stack, layer, "more");
        Assert.Equal(12, rest.Hours);                                       // no further than restMaxHours
        for (int i = 0; i < 15; i++) Click(stack, layer, "less");
        Assert.Equal(1, rest.Hours);
        Assert.Equal("1 hour", view.Find<Label>("hours")!.Text);
        Click(stack, layer, "sleep");                                       // disabled: nothing happens
        Assert.Equal(22, clock.Hour, 6);

        world.Destroy(wolf);
        world.FlushCommands();
        stack.Update(UiInput.Wait(0f));
        Assert.True(rest.CanSleep);
        Assert.True(view.Find("sleep")!.Enabled);
        Assert.Equal("", view.Find<Label>("refused")!.Text);
        for (int i = 0; i < 5; i++) Click(stack, layer, "more");
        Click(stack, layer, "sleep");
        Tick(world);
        Assert.Equal(4, clock.Hour, 6);
        Assert.Equal(80f, world.Attribute(hero, Health));                   // 50 + 5 × 6
        Assert.Equal("You sleep for 6 hours.", view.Find<Label>("message")!.Text);
        Assert.Equal("Day 1, 04:00", view.Find<Label>("now")!.Text);
    }

    private static void Click(UiScreenStack stack, UiLayer layer, string node)
    {
        var widget = layer.Content.Find(node)!;
        var centre = new Vector2(widget.Rect.X + widget.Rect.Width * 0.5f, widget.Rect.Y + widget.Rect.Height * 0.5f);
        stack.Update(UiInput.Click(layer.Root.ToPixels(centre)));
        stack.Update(UiInput.Wait(0f));
    }

    private sealed class RestDuringTick(Entity hero) : ISystem
    {
        public bool Asked;
        public double HourAfterAsking;

        public void Run(in SystemContext ctx)
        {
            if (Asked) return;
            Asked = true;
            Assert.True(Rest.Begin(ctx.World, hero, RestKind.Sleep, 6, out _));
            HourAfterAsking = WorldClock.Of(ctx.World).Hour;
        }
    }

    private sealed class Watch : ISystem
    {
        public readonly List<TimePassed> Seen = new();
        private readonly EventReader<TimePassed> _reader;

        public Watch(World world)
        {
            _reader = world.Events.Reader<TimePassed>(this, Schedule.Fixed);
            world.AddSystem(this, Phase.Gameplay);
        }

        public void Run(in SystemContext ctx)
        {
            foreach (ref readonly var e in _reader.Read()) Seen.Add(e);
        }
    }
}
