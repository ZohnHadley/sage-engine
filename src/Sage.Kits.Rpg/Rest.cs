#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Kits.Rpg;

// Resting and waiting (issue 4g-7; the 4g plan's decision 7: rest goes through `Time.Pass`, and decision 8:
// the rule is the kit's, the base only passes time). Daggerfall's and Morrowind's: choose how many hours,
// then sleep or wait them out.
//
//   rest 8          sleep eight hours (refused with an enemy near)
//   rest 3 wait     wait three hours
//
// **The rule.** Sleeping is refused while a hostile creature — one with an AI (`sage:ai_state`), alive, that
// `Factions.Toward` says is hostile to the sleeper — is within `rpg_conventions.restEnemyRange` metres; the
// reason is the kit's words (`@rpg.rest.enemies`). Waiting is not: it is standing about, and the world does
// not tick through a skip either way. Neither works for the dead, nor for more than `restMaxHours`.
//
// **What it does** is ask `Time.Pass(world, hours, "rest" | "wait")`, which runs at the tick boundary and
// raises one `TimePassed` that routines and the rest catch up from. A sleep then applies
// `rpg_conventions.restEffect`, when there is one, with the hours slept as its magnitude: the heal is
// content, an effect like any other (16 §3.3: effects are the one way attributes change).
//
// The screen is `rpg:rest` over `RestView`; the console's `rest` is the same call, so they cannot disagree.
[Experimental(RpgKitModule.OpenWorld, UrlFormat = RpgKitModule.ExperimentalUrl)]
public enum RestKind
{
    Sleep,
    Wait,
}

[Experimental(RpgKitModule.OpenWorld, UrlFormat = RpgKitModule.ExperimentalUrl)]
public static class Rest
{
    // The word Time.Pass is given, for a rule that tells a sleep from a wait from a journey.
    public const string SleepReason = "rest", WaitReason = "wait";

    // Whether `who` may rest `hours` this way, and if not, why (a string-table key or plain words).
    public static bool Can(World world, Entity who, RestKind kind, double hours, out string reason)
    {
        var conventions = RpgConventions.Of(world);
        if (!world.IsAlive(who)) { reason = "@rpg.rest.nobody"; return false; }
        var dead = world.Conventions().Dead;
        if (!dead.IsEmpty && world.HasTag(who, dead)) { reason = "@rpg.rest.dead"; return false; }
        if (double.IsNaN(hours) || hours < 1 || hours > Math.Max(1, conventions.RestMaxHours)) { reason = "@rpg.rest.hours_range"; return false; }
        if (kind == RestKind.Sleep && !EnemyNear(world, who, conventions.RestEnemyRange).IsNull) { reason = "@rpg.rest.enemies"; return false; }
        reason = "";
        return true;
    }

    // The nearest hostile creature within `range` metres of `who` (null for none): one with an AI, alive,
    // that is hostile to it. Allocates nothing once the world's scan is made.
    public static Entity EnemyNear(World world, Entity who, float range)
    {
        if (range <= 0f || !world.TryGet<Transform>(who, out var at)) return default;
        var scan = world.Resources.GetOrAdd(static () => new RestScan());
        if (!ReferenceEquals(scan.World, world))
        {
            scan.World = world;
            scan.Creatures = world.Query<Transform, AIState>();
        }

        var dead = world.Conventions().Dead;
        float best = range * range;
        Entity nearest = default;
        var here = at.LocalPosition;
        foreach (var (transforms, _, entities) in scan.Creatures.Chunks)
        {
            var span = transforms.Span;
            for (int i = 0; i < span.Length; i++)
            {
                float squared = Vector3.DistanceSquared(span[i].LocalPosition, here);
                if (squared > best) continue;
                var creature = entities.EntityAt(i);
                if (creature == who) continue;
                if (!dead.IsEmpty && world.HasTag(creature, dead)) continue;
                if (!Factions.AreEnemies(world, creature, who)) continue;
                best = squared;
                nearest = creature;
            }
        }
        return nearest;
    }

    // Rests: passes `hours` (at the next tick boundary, through Time.Pass) and, for a sleep, applies the
    // rest effect. False, with the reason, when the rule refuses it.
    public static bool Begin(World world, Entity who, RestKind kind, double hours, out string reason)
    {
        if (!Can(world, who, kind, hours, out reason)) return false;
        if (!Time.Pass(world, hours, kind == RestKind.Sleep ? SleepReason : WaitReason))
        {
            reason = "@rpg.rest.no_clock";
            return false;
        }
        var effect = RpgConventions.Of(world).RestEffect.Id;
        if (kind == RestKind.Sleep && !effect.IsEmpty) Effects.Apply(world, who, effect, who, (float)hours);
        Log.Info(LogCat.Gameplay, $"{World.Describe(who)} {(kind == RestKind.Sleep ? "sleeps" : "waits")} {hours:0.##} h");
        return true;
    }

    // `rest <hours> [sleep|wait]` for the local player: what the screen's buttons do.
    internal static void RegisterCommand(Engine engine)
    {
        engine.CVars.RegisterCommand("rest", CVarFlags.None,
            "rest <hours> [sleep|wait]: sleep (the default; refused with an enemy near) or wait that many game hours (the RPG kit, 4g-7).", a =>
            {
                if (a.Count == 0 || !double.TryParse(a[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double hours))
                {
                    Log.Warn(LogCat.Console, "rest <hours> [sleep|wait]");
                    return;
                }
                var kind = RestKind.Sleep;
                if (a.Count > 1)
                {
                    if (string.Equals(a[1], "wait", StringComparison.OrdinalIgnoreCase)) kind = RestKind.Wait;
                    else if (!string.Equals(a[1], "sleep", StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Warn(LogCat.Console, $"rest: '{a[1]}' is neither sleep nor wait");
                        return;
                    }
                }
                engine.ForEachPlayer((world, player) =>
                {
                    if (!Begin(world, player, kind, hours, out string reason))
                        Log.Warn(LogCat.Console, $"{World.Describe(player)} cannot {(kind == RestKind.Sleep ? "sleep" : "wait")}: {RpgText.Of(world).Text(reason)}");
                });
            });
    }

    private sealed class RestScan
    {
        public World? World;
        public Query<Transform, AIState> Creatures;
    }
}
