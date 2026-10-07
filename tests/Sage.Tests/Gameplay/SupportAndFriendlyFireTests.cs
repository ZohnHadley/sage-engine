#nullable enable
using System;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Friendly fire and the ability gaps (issue #393; docs/design/16 §3.3–3.5): a creature heals and buffs by
// condition (an ability's `aiUse`), an ability's `affects` is the faction filter attacks' `friendlyFire`
// is (#390) for its burst and what it strikes, a bolt flies past whom its filter spares, and a projectile
// can bounce off scenery and stick where it stops.
public class SupportAndFriendlyFireTests
{
    public SupportAndFriendlyFireTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;
    private const byte EnemyLayer = 2, PlayerLayer = 1;
    private static RecordId Id(string name) => new("sage", name);

    private const string Records = """
        [{ "type": "gameplay_conventions", "id": "default_conventions", "health": "health", "dead": "state.dead", "invulnerable": "state.invulnerable", "damageType": "physical",
           "aiProfile": "default_ai", "playerFaction": "player", "costAttribute": "mana",
           "schedules": { "idle": "idle", "chase": "chase", "meleeAttack": "melee_attack", "castSpell": "cast_spell", "holdGround": "hold_ground" } },
         { "type": "faction", "id": "player" },
         { "type": "faction", "id": "wolves", "label": "wolves", "standing": -50 },
         { "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
         { "type": "attribute", "id": "mana", "start": 100, "min": 0, "max": 100, "spendEffect": "spend_mana" },
         { "type": "tag", "id": "state.dead" },
         { "type": "tag", "id": "state.invulnerable" },
         { "type": "tag", "id": "warded" },
         { "type": "effect", "id": "damage", "duration": "Instant", "blockTags": ["state.invulnerable"],
           "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
         { "type": "effect", "id": "heal", "duration": "Instant", "modifiers": [ { "attribute": "health", "op": "Add", "value": 1 } ] },
         { "type": "effect", "id": "ward", "duration": "Timed", "time": 5, "grantTags": ["warded"], "modifiers": [] },
         { "type": "effect", "id": "spend_mana", "modifiers": [ { "attribute": "mana", "op": "Add", "value": -1 } ] },
         { "type": "damage_type", "id": "physical", "effect": "damage" },

         { "type": "ability", "id": "bolt", "name": "bolt", "costAttribute": "mana", "cost": 10, "targeting": "Touch",
           "range": 14, "width": 0.3, "castTime": 0.2, "damage": 7, "damageType": "physical" },
         { "type": "ability", "id": "mend", "name": "mend", "costAttribute": "mana", "cost": 5, "targeting": "Touch",
           "range": 10, "width": 0.3, "effects": ["heal"], "magnitude": 30, "affects": "Allies", "aiUse": "Heal" },
         { "type": "ability", "id": "ward_self", "name": "ward", "costAttribute": "mana", "cost": 1, "targeting": "Self",
           "effects": ["ward"], "aiUse": "Buff" },
         { "type": "ability", "id": "ember", "name": "ember", "costAttribute": "mana", "cost": 5, "targeting": "Projectile",
           "range": 20, "width": 0.2, "projectileSpeed": 20, "damage": 10, "damageType": "physical" },
         { "type": "ability", "id": "healing_circle", "name": "healing circle", "targeting": "Area", "radius": 5,
           "effects": ["heal"], "magnitude": 20, "affects": "Allies" },
         { "type": "ability", "id": "nova", "name": "nova", "targeting": "Area", "radius": 5,
           "damage": 15, "damageType": "physical", "affects": "NotAllies" },

         { "type": "attack", "id": "rebound", "delivery": "projectile", "damage": 15, "range": 40, "radius": 0.1,
           "projectileSpeed": 20, "projectileBounces": 1, "projectileBounciness": 0.5 },
         { "type": "attack", "id": "arrow", "delivery": "projectile", "damage": 15, "range": 40, "radius": 0.05,
           "projectileSpeed": 30, "projectileSticks": true, "projectileStickSeconds": 1 },

         { "type": "ai_profile", "id": "default_ai", "sightRange": 30, "meleeRange": 1.8, "thinkRate": 20 },
         { "type": "ai_schedule", "id": "idle", "tasks": [{ "task": "Wait", "seconds": 1.5 }], "interrupts": ["SeeEnemy"] },
         { "type": "ai_schedule", "id": "chase", "tasks": [{ "task": "MoveToTarget", "distance": 1.6 }], "interrupts": ["EnemyInMeleeRange", "LostEnemy", "NoEnemy"] },
         { "type": "ai_schedule", "id": "melee_attack", "tasks": ["FaceTarget", { "task": "MeleeAttack", "giveUpAfter": 0.2 }, { "task": "Wait", "seconds": 0.4 }], "interrupts": ["LostEnemy", "NoEnemy"] },
         { "type": "ai_schedule", "id": "cast_spell", "tasks": ["FaceTarget", { "task": "CastSpell", "giveUpAfter": 2 }, { "task": "Wait", "seconds": 0.4 }], "interrupts": ["LostEnemy", "NoEnemy"] },
         { "type": "ai_schedule", "id": "hold_ground", "tasks": ["FaceTarget", { "task": "Wait", "seconds": 0.3 }], "interrupts": ["LostEnemy", "NoEnemy"] }]
        """;

    private static HeadlessApp NewGame()
    {
        var app = HeadlessApp.Gameplay().File("data/support.json", Records).Boot("support");
        Assert.Equal(0, app.Records.ErrorCount);
        var ground = app.World.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        app.World.Add(ground, Collider.Box(new Vector3(400, 1, 400)));
        return app;
    }

    // A wolf: faces -Z, thinks, casts what it is taught.
    private static Entity Wolf(World world, Vector3 at, string name, params string[] spells)
    {
        var entity = world.Create(Transform.At(at), name);
        world.AddCharacter(entity, EnemyLayer);
        world.Add(entity, new AIState { Schedule = Conventional.Idle });
        world.AddAttributes(entity);
        world.Add(entity, new Faction { Id = Id("wolves") });
        foreach (var spell in spells) world.Teach(entity, Id(spell));
        return entity;
    }

    private static Entity Player(World world, Vector3 at)
    {
        var entity = world.Create(Transform.At(at), "player");
        world.Add(entity, Collider.Standing(0.35f, 1.8f, PlayerLayer));
        world.Add(entity, RigidBody.Kinematic());
        world.AddAttributes(entity);
        entity.AddTag<PlayerControlled>();
        return entity;
    }

    private static void Hurt(World world, Entity who, float amount) =>
        Effects.Apply(world, who, Id("damage"), default, amount);

    private static void Tick(World world, int ticks)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(Dt);
    }

    private static float Health(World world, Entity who) => world.Attribute(who, Id("health"));

    // Acceptance: a healer heals an ally. The healer is bolting the player when its packmate (beside it, not
    // in front) drops to 40 of 100, below the mend's `aiHealBelow` (0.5): its next choice is the mend, over
    // the dearer bolt, aimed at the packmate, and it lands once; at 70 the packmate is well and the healer
    // goes back to bolting. The mend never lands on the player: its `affects` is Allies.
    [Xunit.Fact]
    public void AHealerHealsAHurtAllyInTheMiddleOfAFight()
    {
        using var app = NewGame();
        var world = app.World;
        var healer = Wolf(world, new Vector3(0, 0.1f, 0), "healer", "bolt", "mend");
        var packmate = Wolf(world, new Vector3(3, 0.1f, -1), "packmate");
        var player = Player(world, new Vector3(0, 0.1f, -8));
        Tick(world, 60);
        Assert.True(Health(world, player) < 100f, "it should have been bolting the player");
        Assert.Equal(100f, Health(world, packmate));

        Hurt(world, packmate, 60f);
        Assert.Equal(40f, Health(world, packmate));
        var casts = new EventProbe<AbilityCast>(world);
        Tick(world, 180);

        var mine = casts.All.Where(c => c.Caster == healer).ToList();
        int mend = mine.FindIndex(c => c.Ability == Id("mend"));
        Assert.InRange(mend, 0, 1);                                          // next, or after the bolt it was winding up
        Assert.Equal(1, mine[mend].Targets);
        Assert.Single(mine, c => c.Ability == Id("mend"));                   // once: 70 is above half
        Assert.Equal(70f, Health(world, packmate), 3);
        Assert.Contains(mine.Skip(mend + 1), c => c.Ability == Id("bolt"));  // then it fought on
    }

    // A buff by condition: a wolf with a self-ward casts it once the fight starts, and not again while the
    // ward holds; one at full health never casts a heal on itself.
    [Xunit.Fact]
    public void ACreatureBuffsItselfOnceInAFightAndDoesNotHealWhenWell()
    {
        using var app = NewGame();
        var world = app.World;
        var wolf = Wolf(world, new Vector3(0, 0.1f, 0), "wolf", "ward_self", "bolt");
        Player(world, new Vector3(0, 0.1f, -8));
        var casts = new EventProbe<AbilityCast>(world);

        Tick(world, 180);

        var mine = casts.All.Where(c => c.Caster == wolf).ToList();
        Assert.Equal(Id("ward_self"), mine[0].Ability);
        Assert.Single(mine, c => c.Ability == Id("ward_self"));
        Assert.True(Effects.IsActive(world, wolf, Id("ward")));
        Assert.Contains(mine, c => c.Ability == Id("bolt"));
    }

    // Acceptance: a bolt does not hurt its own faction. A firebug's ember flies past the packmate standing in
    // its line of fire, which it may not hurt, and lands on the player behind it.
    [Xunit.Fact]
    public void ABoltFliesPastItsOwnFactionAndHitsTheEnemyBehind()
    {
        using var app = NewGame();
        var world = app.World;
        var firebug = Wolf(world, new Vector3(0, 0.1f, 0), "firebug", "ember");
        var packmate = Wolf(world, new Vector3(0, 0.1f, -4), "packmate");
        var player = Player(world, new Vector3(0, 0.1f, -10));
        var damage = new EventProbe<Damaged>(world);

        Tick(world, 120);

        Assert.Equal(100f, Health(world, packmate));
        Assert.DoesNotContain(damage.All, d => d.Hit.Target == packmate);
        Assert.Contains(damage.All, d => d.Hit.Target == player && d.Hit.Attacker == firebug);
        Assert.True(Health(world, player) < 100f);
    }

    // `affects` filters a burst: a healing circle with Allies mends the caster and its packmate and not the
    // player beside them; a nova with NotAllies burns the player and spares the caster's kind.
    [Xunit.Fact]
    public void AnAreasAffectsChoosesWhoItReaches()
    {
        using var app = NewGame();
        var world = app.World;
        var wolf = Wolf(world, new Vector3(0, 0.1f, 0), "wolf", "healing_circle", "nova");
        var packmate = Wolf(world, new Vector3(2, 0.1f, 0), "packmate");
        var player = Player(world, new Vector3(-2, 0.1f, 0));
        world.Get<AIState>(wolf).NextThink = float.MaxValue;              // cast by hand, not by its brain
        world.Get<AIState>(packmate).NextThink = float.MaxValue;
        foreach (var who in new[] { wolf, packmate, player }) Hurt(world, who, 50f);

        world.Cast(wolf, Id("healing_circle"));
        Tick(world, 2);
        Assert.Equal(70f, Health(world, wolf), 3);
        Assert.Equal(70f, Health(world, packmate), 3);
        Assert.Equal(50f, Health(world, player), 3);

        world.Cast(wolf, Id("nova"));
        Tick(world, 2);
        Assert.Equal(70f, Health(world, wolf), 3);
        Assert.Equal(70f, Health(world, packmate), 3);
        Assert.Equal(35f, Health(world, player), 3);
    }

    // A bouncing bolt: it meets a wall with a bounce left, comes back at half its speed and lands on what is
    // behind where it was thrown from; the wall itself takes nothing.
    [Xunit.Fact]
    public void ABouncingBoltComesBackOffAWallAndHitsWhatIsBehind()
    {
        using var app = NewGame();
        var world = app.World;
        var wall = world.Create(Transform.At(new Vector3(0, 1, -6)), "wall");
        world.Add(wall, Collider.Box(new Vector3(10, 4, 1)));
        var target = Player(world, new Vector3(0, 0, 4));
        var damage = new EventProbe<Damaged>(world);

        var record = app.Records.Get<AttackRecord>(Id("rebound"));
        var bolt = world.Launch(default, Id("rebound"), record, new Vector3(0, 1, 0), -Vector3.UnitZ);
        Tick(world, 24);                                                     // 0.4 s: 5.5 m out, bounced, on the way back
        Assert.True(world.IsAlive(bolt));
        var flying = world.Get<Projectile>(bolt);
        Assert.Equal(0, flying.Bounces);
        Assert.Equal(10f, flying.Velocity.Z, 2);                             // back the way it came, at half
        Assert.True(world.Get<Transform>(bolt).LocalPosition.Z > -5.5f);

        Tick(world, 90);
        var hit = Assert.Single(damage.All);
        Assert.Equal(target, hit.Hit.Target);
        Assert.False(world.IsAlive(bolt));
    }

    // A sticking arrow stays where it stops: in a wall for its `projectileStickSeconds`, then gone; in a body,
    // as that body's child, so it goes where the body goes.
    [Xunit.Fact]
    public void AStickingArrowStaysInTheWallAndInTheBodyItStruck()
    {
        using var app = NewGame();
        var world = app.World;
        var wall = world.Create(Transform.At(new Vector3(0, 1, -6)), "wall");
        world.Add(wall, Collider.Box(new Vector3(10, 4, 1)));
        var record = app.Records.Get<AttackRecord>(Id("arrow"));

        var arrow = world.Launch(default, Id("arrow"), record, new Vector3(0, 1, 0), -Vector3.UnitZ);
        Tick(world, 30);
        Assert.True(world.IsAlive(arrow));
        var stuck = world.Get<Projectile>(arrow);
        Assert.True(stuck.Stuck);
        Assert.Equal(Vector3.Zero, stuck.Velocity);
        Assert.InRange(world.Get<Transform>(arrow).LocalPosition.Z, -5.6f, -5.4f);   // at the wall's face
        Assert.True(arrow.Parent.IsNull);
        Tick(world, 60);                                                     // its second is up
        Assert.False(world.IsAlive(arrow));

        var deer = Player(world, new Vector3(5, 0, -3));
        var shot = world.Launch(default, Id("arrow"), record, new Vector3(5, 1, 0), -Vector3.UnitZ);
        Tick(world, 20);
        Assert.True(world.IsAlive(shot));
        Assert.True(world.Get<Projectile>(shot).Stuck);
        Assert.Equal(deer, shot.Parent);
        Assert.Equal(85f, Health(world, deer), 3);
    }

    // Stuck arrows are part of the world a save keeps: the one in the wall still stuck where it was with the
    // time it had left, the one in the deer still the deer's.
    [Xunit.Fact]
    public void StuckArrowsSurviveASave()
    {
        using var app = NewGame();
        var world = app.World;
        var wall = world.Create(Transform.At(new Vector3(0, 1, -6)), "wall");
        world.Add(wall, Collider.Box(new Vector3(10, 4, 1)));
        var deer = Player(world, new Vector3(5, 0, -3));
        world.MakePersistent(deer);
        var record = app.Records.Get<AttackRecord>(Id("arrow"));
        world.Launch(default, Id("arrow"), record, new Vector3(0, 1, 0), -Vector3.UnitZ);
        world.Launch(default, Id("arrow"), record, new Vector3(5, 1, 0), -Vector3.UnitZ);
        Tick(world, 20);
        var stuck = world.Query<Transform, Projectile>().Entities.ToEntityList().ToArray();
        Assert.Equal(2, stuck.Length);
        Assert.All(stuck, e => Assert.True(world.Get<Projectile>(e).Stuck));
        var inWall = stuck.Single(e => e.Parent.IsNull);
        var wallAt = world.Get<Transform>(inWall).LocalPosition;
        float left = world.Get<Projectile>(inWall).Life;
        Assert.True(app.Engine.Saves.Save("arrows"));

        Tick(world, 90);
        Assert.Empty(world.Query<Transform, Projectile>().Entities.ToEntityList());   // both gone

        Assert.True(app.Engine.Saves.Load("arrows"));
        deer = world.FindByName("player");
        Tick(world, 1);                                                      // the deer's arrow is its child again
        var loaded = world.Query<Transform, Projectile>().Entities.ToEntityList().ToArray();
        Assert.Equal(2, loaded.Length);
        Assert.All(loaded, e => Assert.True(world.Get<Projectile>(e).Stuck));
        var wallArrow = loaded.Single(e => e.Parent.IsNull);
        Assert.Equal(wallAt, world.Get<Transform>(wallArrow).LocalPosition);
        Assert.Equal(left - Dt, world.Get<Projectile>(wallArrow).Life, 4);
        Assert.Contains(loaded, e => e.Parent == deer);
    }

    // A heal or buff a creature casts on others must be able to land on an ally, and a buff must have an
    // effect to look for: each a load error at its field.
    [Xunit.Fact]
    public void SupportAbilityMistakesAreLoadErrors()
    {
        const string bad = """
        [
          { "type": "ability", "id": "spares_its_friends", "targeting": "Touch", "aiUse": "Heal" },
          { "type": "ability", "id": "empty_blessing", "targeting": "Self", "aiUse": "Buff" },
          { "type": "ability", "id": "over_eager", "targeting": "Self", "aiUse": "Heal", "aiHealBelow": 2 },
          { "type": "ability", "id": "fine_heal", "targeting": "Projectile", "aiUse": "Heal", "affects": "Allies" }
        ]
        """;
        using var app = HeadlessApp.Gameplay().File("data/bad.json", bad).Boot("bad");
        Assert.Equal(3, app.Records.ErrorCount);
        Assert.Contains(app.Records.LoadErrors, e => e.Contains("spares_its_friends") && e.Contains("affects"));
        Assert.Contains(app.Records.LoadErrors, e => e.Contains("empty_blessing") && e.Contains("no effects"));
        Assert.Contains(app.Records.LoadErrors, e => e.Contains("over_eager") && e.Contains("between 0 and 1"));
    }
}
