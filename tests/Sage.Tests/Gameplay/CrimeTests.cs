#nullable enable
using System;
using System.IO;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Crime, witnesses and bounty (issue #389): a theft from an owned chest, an assault, a murder and a trespass
// become crimes when somebody who sides with the wronged faction sees them; the bounty is owed to that faction,
// saved, paid off; and a guard of its law pursues whoever owes it, choosing its response in data.
public class CrimeTests
{
    public CrimeTests() { _ = TestEnv.UserRoot; }

    internal const string Records = """
        [{ "type": "gameplay_conventions", "id": "default_conventions", "health": "health", "dead": "state.dead", "invulnerable": "state.invulnerable", "damageType": "physical",
           "aiProfile": "default_ai", "playerFaction": "player", "schedules": { "idle": "idle", "chase": "chase", "meleeAttack": "melee_attack" } },
         { "type": "faction", "id": "player" },
         { "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
         { "type": "tag", "id": "state.dead" },
         { "type": "tag", "id": "state.invulnerable" },
         { "type": "effect", "id": "damage", "duration": "Instant", "blockTags": ["state.invulnerable"],
           "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
         { "type": "damage_type", "id": "physical", "effect": "damage" },
         { "type": "attack", "id": "fists", "damage": 5, "damageType": "physical", "reach": 2.2,
           "radius": 0.45, "windupTime": 0.2, "recoverTime": 0.1, "cooldown": 0.5 },

         { "type": "ai_profile", "id": "default_ai", "sightRange": 30, "meleeRange": 1.8, "thinkRate": 20 },
         { "type": "ai_schedule", "id": "idle", "tasks": [{ "task": "Wait", "seconds": 1.5 }], "interrupts": ["SeeEnemy"] },
         { "type": "ai_schedule", "id": "chase", "tasks": [{ "task": "MoveToTarget", "distance": 1.6 }], "interrupts": ["EnemyInMeleeRange", "LostEnemy", "NoEnemy"] },
         { "type": "ai_schedule", "id": "melee_attack", "tasks": ["FaceTarget", { "task": "MeleeAttack", "giveUpAfter": 0.2 }, { "task": "Wait", "seconds": 0.4 }], "interrupts": ["LostEnemy", "NoEnemy"] },

         // The guard's response, in data: somebody wanted but not an outlaw is walked up to and confronted.
         { "type": "ai_profile", "id": "guard_ai", "sightRange": 30, "meleeRange": 1.8, "thinkRate": 20,
           "rules": [ { "when": ["target_wanted"], "unless": ["target_outlaw"], "schedule": "arrest" } ] },
         { "type": "ai_schedule", "id": "arrest", "tasks": [{ "task": "MoveToTarget", "distance": 2 }, "FaceTarget", { "task": "Wait", "seconds": 1 }],
           "interrupts": ["LostEnemy", "NoEnemy"] },

         { "type": "faction", "id": "town", "label": "the town" },
         { "type": "faction", "id": "temple", "label": "the temple", "relations": [ { "faction": "sage:town", "stance": "Ally" } ] },
         { "type": "faction", "id": "bandits", "label": "bandits", "relations": [ { "faction": "sage:town", "stance": "Hostile" } ] },

         { "type": "crime", "id": "theft", "kind": "Theft", "bounty": 5, "perValue": 1, "standing": 4 },
         { "type": "crime", "id": "assault", "kind": "Assault", "bounty": 40, "standing": 10 },
         { "type": "crime", "id": "murder", "kind": "Murder", "bounty": 1000, "standing": 30 },
         { "type": "crime", "id": "trespass", "kind": "Trespass", "bounty": 5 },

         { "type": "item", "id": "bread", "label": "bread", "weight": 0.2, "value": 2 },
         { "type": "item", "id": "gold", "label": "gold", "weight": 0 },

         { "type": "prefab", "id": "chest", "name": "chest",
           "parts": { "inventory": { "items": [ { "item": "bread", "count": 3 } ] }, "container": { "faction": "town" } } },
         { "type": "prefab", "id": "guard", "name": "guard",
           "components": { "ai_state": { "profile": "guard_ai", "schedule": "idle" } },
           "parts": { "character": { "layer": "enemy" }, "attributes": {}, "faction": "town",
                      "guard": { "arrests": 100, "hears": 15 }, "melee": { "attack": "fists" } } },
         { "type": "prefab", "id": "townsman", "name": "townsman",
           "components": { "ai_state": { "schedule": "idle" } },
           "parts": { "character": { "layer": "enemy" }, "attributes": {}, "faction": "town" } },
         { "type": "prefab", "id": "bandit", "name": "bandit",
           "components": { "ai_state": { "schedule": "idle" } },
           "parts": { "character": { "layer": "enemy" }, "attributes": {}, "faction": "bandits" } },
         { "type": "prefab", "id": "shop", "name": "shop",
           "parts": { "owned_place": { "size": [6, 4, 6], "faction": "town", "openFrom": 8, "openTo": 20 } } },
         { "type": "dialogue", "id": "captain",
           "nodes": [ { "id": "halt", "text": "Halt.",
             "options": [
               { "text": "I'll pay my fine.", "end": true, "conditions": [ { "bounty": "town" } ],
                 "actions": [ { "pay_bounty": "town", "currency": "gold" } ] },
               { "text": "I have a pardon.", "end": true, "actions": [ { "clear_bounty": "town" } ] },
               { "text": "I stole the bread.", "end": true, "actions": [ { "commit_crime": "theft", "faction": "town" } ] } ] } ] }]
        """;

    private static RecordId Id(string name) => new("sage", name);

    private static HeadlessApp NewGame()
    {
        var app = HeadlessApp.Gameplay().File("data/crime.json", Records).Boot("crime");
        Assert.Equal(0, app.Records.ErrorCount);
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        var ground = app.World.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        app.World.Add(ground, Collider.Box(new Vector3(400, 1, 400)));
        return app;
    }

    private static Entity Hero(World world, Vector3 at = default)
    {
        var hero = world.Create(Transform.At(at), "hero");
        world.Add(hero, Collider.Standing(0.35f, 1.8f, world.Resources.Get<IPhysicsWorld>().Layers.Player));
        world.Add(hero, RigidBody.Kinematic());
        world.AddAttributes(hero);
        world.AddInventory(hero, 50f);
        world.Add(hero, new Persistent { Id = PersistentId.FromName("hero") });
        hero.AddTag<PlayerControlled>();
        return hero;
    }

    // Spawned facing `yawDegrees` (0: looking down -Z).
    private static Entity Spawn(World world, string prefab, Vector3 at, float yawDegrees = 0f) =>
        world.Spawn(Id(prefab), at, yawDegrees);

    private static void Tick(World world, int ticks)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    private static float Distance(World world, Entity a, Entity b) =>
        SageMath.DistanceXZ(world.Get<Transform>(a).LocalPosition, world.Get<Transform>(b).LocalPosition);

    // Done (#389): stealing from a chest the town owns, in view of the town's guard, raises a bounty with the
    // town and costs standing; the guard is set on the thief and its profile's rule — in data — picks the
    // `arrest` schedule, which walks it to the thief.
    [Xunit.Fact]
    public void StealingFromAFlaggedChestInViewOfAGuard_RaisesABountyAndThePursuit()
    {
        using var app = NewGame();
        var world = app.World;
        var crimes = new EventProbe<CrimeCommitted>(world);
        var hero = Hero(world);
        var chest = Spawn(world, "chest", new Vector3(0, 0, -1));
        var guard = Spawn(world, "guard", new Vector3(0, 0, 10));      // facing -Z: looking at the hero
        Tick(world, 30);
        Assert.Equal(Stance.Neutral, Factions.Toward(world, guard, hero));
        Assert.True(world.Get<AIState>(guard).Target.IsNull);
        float before = Distance(world, guard, hero);

        Assert.True(world.Take(chest, Id("bread"), 3));
        Assert.True(world.Give(hero, Id("bread"), 3));
        Containers.Took(world, hero, chest, Id("bread"), 3);
        Tick(world, 2);

        var crime = Assert.Single(crimes.All);
        Assert.Equal((hero, Id("theft"), Id("town"), guard), (crime.Criminal, crime.Crime, crime.Faction, crime.Witness));
        Assert.Equal(5f + 3 * 2, Crime.BountyWith(world, Id("town")), 3);
        Assert.Equal(-4f, Factions.StandingWith(world, Id("town")), 3);
        Assert.Equal(Stance.Hostile, Factions.Toward(world, guard, hero));
        Assert.Equal(hero, world.Get<AIState>(guard).Target);

        Tick(world, 60 * 3);
        Assert.Equal(Id("arrest"), world.Get<AIState>(guard).Schedule);
        Assert.True(Distance(world, guard, hero) < before - 5f, $"the guard did not pursue: {Distance(world, guard, hero):0.0} m from {before:0.0}");
        Assert.True(world.Attribute(hero, Conventional.Health) >= 100f, "a guard come to arrest swung at the thief");
    }

    // Nobody saw it: the guard has its back turned. The theft is a fact with no witness and costs nothing; and a
    // bandit, who has no love for the town, sees it and says nothing.
    [Xunit.Fact]
    public void ATheftNobodyWhoCaresSeesCostsNothing()
    {
        using var app = NewGame();
        var world = app.World;
        var crimes = new EventProbe<CrimeCommitted>(world);
        var hero = Hero(world);
        var chest = Spawn(world, "chest", new Vector3(0, 0, -1));
        var guard = Spawn(world, "guard", new Vector3(0, 0, 10), 180f);   // facing +Z: away
        Spawn(world, "bandit", new Vector3(3, 0, 3), 0f);
        Tick(world, 10);

        Containers.Took(world, hero, chest, Id("bread"), 1);
        Tick(world, 2);

        var crime = Assert.Single(crimes.All);
        Assert.True(crime.Witness.IsNull);
        Assert.Equal(0f, crime.Bounty);
        Assert.Equal(0f, Crime.BountyWith(world, Id("town")));
        Assert.Equal(Stance.Neutral, Factions.Toward(world, guard, hero));
    }

    // Hitting a townsman is an assault he witnesses himself; the beating is one crime, not one per blow. A guard
    // within earshot of the report is set on the attacker without having seen it, and the bounty is above what
    // it arrests for, so the hero is an outlaw (target_outlaw) and the guard's profile falls back to fighting.
    [Xunit.Fact]
    public void HittingABystanderIsAssault_OnceABeating_AndBringsTheGuard()
    {
        using var app = NewGame();
        var world = app.World;
        var hero = Hero(world);
        var townsman = Spawn(world, "townsman", new Vector3(0, 0, -1.5f));
        var guard = Spawn(world, "guard", new Vector3(0, 0, -12), 0f);    // facing away, within `hears`
        world.Get<Guard>(guard).Arrests = 30f;
        Tick(world, 10);

        for (int blow = 0; blow < 3; blow++)
        {
            Combat.ApplyDamage(world, new DamageInfo(hero, townsman, default, 5f, Vector3.Zero, -Vector3.UnitZ));
            Tick(world, 2);
        }
        Assert.Equal(40f, Crime.BountyWith(world, Id("town")), 3);
        Assert.Equal(hero, world.Get<AIState>(guard).Target);
        Assert.True(Crime.IsOutlaw(world, guard, hero));

        // Fighting back against the guard who came for you is no new crime.
        Combat.ApplyDamage(world, new DamageInfo(hero, guard, default, 5f, Vector3.Zero, -Vector3.UnitZ));
        Tick(world, 2);
        Assert.Equal(40f, Crime.BountyWith(world, Id("town")), 3);
    }

    // A murder is judged by what the victim thought of its killer while it lived, before the kill's own cost to
    // reputation; the temple, the town's ally, reports it to the town.
    [Xunit.Fact]
    public void AMurderSeenByAnAllyIsReportedToTheVictimsFaction()
    {
        using var app = NewGame();
        var world = app.World;
        var crimes = new EventProbe<CrimeCommitted>(world);
        var hero = Hero(world);
        var townsman = Spawn(world, "townsman", new Vector3(0, 0, -1.5f));
        var priest = world.Spawn(Id("townsman"), new Vector3(0, 0, 8), 0f);
        world.Get<Faction>(priest).Id = Id("temple");
        Tick(world, 10);

        Combat.ApplyDamage(world, new DamageInfo(hero, townsman, default, 500f, Vector3.Zero, -Vector3.UnitZ));
        Tick(world, 3);

        Assert.Contains(crimes.All, c => c.Crime == Id("murder") && c.Witness == priest && c.Faction == Id("town"));
        Assert.True(Crime.BountyWith(world, Id("town")) >= 1000f);
    }

    // An owned place out of hours: in it at 22:00 under a townsman's eye is trespass, once a visit; at noon, when
    // it is open, nothing. Its members come and go freely.
    [Xunit.Fact]
    public void BeingInAShutPlaceIsTrespass_OnceAVisit()
    {
        using var app = NewGame();
        var world = app.World;
        var crimes = new EventProbe<CrimeCommitted>(world);
        var clock = WorldClock.Of(world);
        clock.Scale = 0.0;
        clock.Hour = 12;
        var hero = Hero(world, new Vector3(0, 0, 20));
        Spawn(world, "shop", Vector3.Zero);
        Spawn(world, "townsman", new Vector3(0, 0, 8), 0f);
        Tick(world, 5);

        world.Get<Transform>(hero).LocalPosition = new Vector3(0, 0.9f, 1);
        Tick(world, 60);
        Assert.Empty(crimes.All);

        clock.Hour = 22;
        Tick(world, 60);
        var crime = Assert.Single(crimes.All);
        Assert.Equal(Id("trespass"), crime.Crime);
        Assert.Equal(5f, Crime.BountyWith(world, Id("town")), 3);

        // Out and in again is a new visit.
        world.Get<Transform>(hero).LocalPosition = new Vector3(0, 0.9f, 20);
        Tick(world, 5);
        world.Get<Transform>(hero).LocalPosition = new Vector3(0, 0.9f, 1);
        Tick(world, 60);
        Assert.Equal(2, crimes.All.Count);

        // A member of the town is no trespasser.
        world.Add(hero, new Faction { Id = Id("town") });
        world.Get<Transform>(hero).LocalPosition = new Vector3(0, 0.9f, 20);
        Tick(world, 5);
        world.Get<Transform>(hero).LocalPosition = new Vector3(0, 0.9f, 1);
        Tick(world, 60);
        Assert.Equal(2, crimes.All.Count);
    }

    // The bounty is saved with the world; paying it in gold clears it and calls the guards off.
    [Xunit.Fact]
    public void TheBountyIsSaved_AndPayingItCallsOffTheGuards()
    {
        using var app = NewGame();
        var world = app.World;
        var hero = Hero(world);
        var guard = Spawn(world, "guard", new Vector3(0, 0, 10));
        Crime.SetBounty(world, Id("town"), 25f);
        Tick(world, 30);
        Assert.Equal(hero, world.Get<AIState>(guard).Target);

        var guardId = world.Get<Persistent>(guard).Id;
        Assert.True(app.Engine.Saves.Save("crime"));
        Crime.SetBounty(world, Id("town"), 0f);
        Assert.True(app.Engine.Saves.Load("crime"));
        Assert.Equal(25f, Crime.BountyWith(world, Id("town")), 3);

        var loadedHero = world.Resolve(PersistentId.FromName("hero"));
        var loadedGuard = world.Resolve(guardId);
        Assert.False(Crime.Pay(world, loadedHero, Id("town"), Id("gold"), out string why));
        Assert.Contains("25", why);
        Assert.True(world.Give(loadedHero, Id("gold"), 30));
        Assert.True(Crime.Pay(world, loadedHero, Id("town"), Id("gold"), out _));
        Assert.Equal(5, world.CountOf(loadedHero, Id("gold")));
        Assert.Equal(0f, Crime.BountyWith(world, Id("town")));
        Assert.True(world.Get<AIState>(loadedGuard).Target.IsNull);
        Assert.NotEqual(Stance.Hostile, Factions.Toward(world, loadedGuard, loadedHero));
    }

    // The words content asks with, from a captain's conversation: `bounty` gates "pay the fine", `pay_bounty`
    // pays it in gold, `clear_bounty` pardons, and `commit_crime` confesses — witnessed by the captain himself.
    [Xunit.Fact]
    public void ACaptainsConversationAsksAndSettlesTheBounty()
    {
        using var app = NewGame();
        var world = app.World;
        var hero = Hero(world);
        var captain = Spawn(world, "townsman", new Vector3(0, 0, -2), 180f);   // facing the hero
        world.Add(captain, new Dialogue { Record = Id("captain") });
        Tick(world, 5);

        Assert.True(DialogueRules.Start(world, captain, hero));
        var options = DialogueRules.Current(world)!.Options;
        Assert.False(DialogueRules.CanPick(world, hero, options[0], out string why));
        Assert.Equal("you owe them nothing", why);

        Assert.True(DialogueRules.Pick(world, options[2]));                      // confess
        Assert.Equal(5f, Crime.BountyWith(world, Id("town")), 3);

        Assert.True(world.Give(hero, Id("gold"), 20));
        Assert.True(DialogueRules.Start(world, captain, hero));
        Assert.True(DialogueRules.Pick(world, options[0]));                      // pay
        Assert.Equal(0f, Crime.BountyWith(world, Id("town")));
        Assert.Equal(15, world.CountOf(hero, Id("gold")));

        Crime.SetBounty(world, Id("town"), 300f);
        Assert.True(DialogueRules.Start(world, captain, hero));
        Assert.True(DialogueRules.Pick(world, options[1]));                      // pardoned
        Assert.Equal(0f, Crime.BountyWith(world, Id("town")));
    }
}
