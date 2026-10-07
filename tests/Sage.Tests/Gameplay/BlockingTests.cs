#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Blocks, parries, knockback and hit reactions (issue #390; docs/design/16 §3.2), in the style of
// HitPipelineTests: each is an option of a record — a `block` an attack names, an attack's `knockback`,
// `stagger`, `reaction`, `cleave` and `friendlyFire`, an effect's `damage` execution — and each is seen
// through the one hit pipeline, Combat.ApplyHit.
public class BlockingTests
{
    public BlockingTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;
    private static RecordId Id(string name) => new("sage", name);
    private static readonly RecordId Health = Id("health"), Stamina = Id("stamina");

    private const string Records = """
    [
      { "type": "gameplay_conventions", "id": "default_conventions", "health": "health", "dead": "state.dead",
        "damageType": "physical", "playerFaction": "player" },
      { "type": "tag", "id": "state.dead" },
      { "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
      { "type": "attribute", "id": "stamina", "start": 50, "min": 0, "max": 100, "spendEffect": "spend_stamina" },
      { "type": "attribute", "id": "fire_resist", "start": 0, "min": 0, "max": 95 },
      { "type": "effect", "id": "spend_stamina", "modifiers": [ { "attribute": "stamina", "op": "Add", "value": -1 } ] },
      { "type": "effect", "id": "fireproof", "modifiers": [ { "attribute": "fire_resist", "op": "Add", "value": 50 } ] },
      { "type": "effect", "id": "damage", "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
      { "type": "damage_type", "id": "physical", "effect": "damage" },
      { "type": "damage_type", "id": "fire", "effect": "damage", "resist": "fire_resist" },

      { "type": "effect", "id": "burning", "duration": "Timed", "time": 3.05, "period": 1,
        "executions": [ { "execution": "damage", "amount": 10, "damageType": "fire" } ] },
      { "type": "effect", "id": "inferno", "duration": "Timed", "time": 5, "period": 1,
        "executions": [ { "execution": "damage", "amount": 40, "damageType": "fire" } ] },

      { "type": "block", "id": "shield", "arcDegrees": 120, "reduction": 0.75, "stamina": "stamina",
        "staminaCost": 5, "staminaPerDamage": 0.5, "parryWindow": 0.1, "parryStagger": 0.6,
        "blockTrigger": "guard", "parryTrigger": "recoil" },
      { "type": "block", "id": "heavy_guard", "stamina": "stamina", "staminaCost": 60, "guardBreakStagger": 0.8 },
      { "type": "block", "id": "duel", "directional": true },

      { "type": "attack", "id": "sword", "damage": 20, "reach": 2.0, "radius": 0.3, "arcDegrees": 120,
        "windupTime": 0.2, "recoverTime": 0.1, "cooldown": 0.5, "block": "shield" },
      { "type": "attack", "id": "heavy_sword", "base": "sword", "block": "heavy_guard" },
      { "type": "attack", "id": "duel_sword", "base": "sword", "block": "duel" },
      { "type": "attack", "id": "careful_sword", "base": "sword", "friendlyFire": "NotAllies" },
      { "type": "attack", "id": "maul", "damage": 10, "reach": 2.0, "radius": 0.3, "windupTime": 0.1, "recoverTime": 0.1,
        "cooldown": 0.5, "knockback": 8, "knockbackLift": 2, "stagger": 1, "reaction": "hurt" },
      { "type": "attack", "id": "greataxe", "damage": 20, "reach": 2.5, "radius": 0.35, "arcDegrees": 160,
        "windupTime": 0.2, "recoverTime": 0.1, "cooldown": 0.3, "cleave": 2 },
      { "type": "attack", "id": "greataxe3", "base": "greataxe", "cleave": 3 },

      { "type": "anim_graph", "id": "body", "initial": "idle", "fade": 0,
        "params": { "hurt": { "kind": "Trigger" }, "guard": { "kind": "Trigger" }, "recoil": { "kind": "Trigger" } },
        "states": { "idle": { "clip": "idle" }, "flinch": { "clip": "walk", "loop": false },
                    "blocking": { "clip": "walk", "loop": false }, "recoiling": { "clip": "walk", "loop": false } },
        "transitions": [ { "to": "flinch", "on": "hurt" }, { "to": "blocking", "on": "guard" }, { "to": "recoiling", "on": "recoil" } ] },

      { "type": "faction", "id": "player" },
      { "type": "faction", "id": "bandits", "standing": -50 },
      { "type": "faction", "id": "town", "standing": 0 }
    ]
    """;

    private static HeadlessApp NewGame(string records = Records)
    {
        var files = new MountFixture();
        SkinnedModelBuilder.Write(Path.Combine(files.Dir("game"), "models", "rig.glb"));
        files.Mount("game", "game");
        var app = HeadlessApp.Gameplay().File("data/blocking.json", records).Mount(files).Boot("blocking");
        var ground = app.World.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        app.World.Add(ground, Collider.Box(new Vector3(400, 1, 400)));
        return app;
    }

    // A fighter at `feet` facing `lookAt`, holding `attack`, with a stance (so it can guard) and an animator.
    private static Entity Body(HeadlessApp app, Vector3 feet, string name, Vector3 lookAt, string attack = "sword",
                               AttackDirection direction = AttackDirection.None)
    {
        var world = app.World;
        var e = world.Create(Transform.At(feet), name);
        world.AddCharacter(e, world.Resources.Get<IPhysicsWorld>().Layers.Player);
        world.AddAttributes(e);
        world.Add(e, Melee.With(Id(attack)));
        world.Add(e, new AttackStance { Direction = direction });
        world.Add(e, new Animator { Graph = Id("body"), Model = AssetPath.Intern("models/rig.glb") });
        world.Get<PawnIntent>(e).Yaw = SageMath.YawTo(feet, lookAt);
        return e;
    }

    private static void Tick(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(Dt);
    }

    private static void Press(HeadlessApp app, Entity fighter)
    {
        app.World.Get<PawnIntent>(fighter).Pressed = default(ActionMask).With(app.Engine.Actions.Get("Attack"));
        Tick(app.World);
        app.World.Get<PawnIntent>(fighter).Pressed = default;
    }

    private static void Guard(HeadlessApp app, Entity fighter, bool up) =>
        app.World.Get<PawnIntent>(fighter).Held = up ? default(ActionMask).With(app.Engine.Actions.Get("Block")) : default;

    // Swings and waits for the blow to land and the fighter to be ready again.
    private static void Swing(HeadlessApp app, Entity fighter, int ticks = 50)
    {
        Press(app, fighter);
        Tick(app.World, ticks);
    }

    // Acceptance: a guard that has been up a while, facing the blow, stops `reduction` of it for its
    // stamina (a flat cost and a cost per point stopped, spent through the attribute's spendEffect),
    // sets its block trigger and says so; the same guard facing away stops nothing.
    [Xunit.Fact]
    public void AGuardFacingTheBlowBlocksItForStamina_AndOneFacingAwayDoesNot()
    {
        using var app = NewGame();
        Assert.Equal(0, app.Records.ErrorCount);
        var world = app.World;
        var attacker = Body(app, Vector3.Zero, "attacker", new Vector3(0, 0, -2));
        var defender = Body(app, new Vector3(0, 0, -1.2f), "defender", Vector3.Zero);
        var blocked = new EventProbe<Blocked>(world);
        Guard(app, defender, true);
        Tick(world, 30);                                   // long past the parry window
        Assert.True(AttackStances.IsBlocking(world, defender));

        Swing(app, attacker);
        Assert.Equal(95f, world.Attribute(defender, Health), 3);          // 20 × (1 − 0.75)
        Assert.Equal(50f - (5f + 0.5f * 15f), world.Attribute(defender, Stamina), 3);
        var block = Assert.Single(blocked.All);
        Assert.Equal(GuardOutcome.Blocked, block.Outcome);
        Assert.Equal(defender, block.Defender);
        Assert.Equal(attacker, block.Attacker);
        Assert.Equal(15f, block.Stopped, 3);
        Assert.Equal("blocking", Animators.StateOf(world, defender));

        // Turned away, guard still up: the blow comes from behind and lands whole.
        world.Get<PawnIntent>(defender).Yaw = SageMath.YawTo(new Vector3(0, 0, -1.2f), new Vector3(0, 0, -5));
        Swing(app, attacker);
        Assert.Equal(75f, world.Attribute(defender, Health), 3);
        Assert.Single(blocked.All);
    }

    // Acceptance: a guard raised inside the parry window as the blow arrives stops all of it, costs
    // nothing, and staggers the attacker — its recoil trigger set, and no swing until the stagger is over.
    [Xunit.Fact]
    public void AGuardRaisedJustInTimeParries_AndTheAttackerStaggers()
    {
        using var app = NewGame();
        var world = app.World;
        var attacker = Body(app, Vector3.Zero, "attacker", new Vector3(0, 0, -2));
        var defender = Body(app, new Vector3(0, 0, -1.2f), "defender", Vector3.Zero);
        var blocked = new EventProbe<Blocked>(world);
        var staggered = new EventProbe<Staggered>(world);
        var fired = new EventProbe<WeaponFired>(world);
        Tick(world, 3);

        Press(app, attacker);
        // The guard goes up a few ticks before the blow lands (windup 0.2 s; the window is 0.1 s).
        for (int t = 0; t < 30 && blocked.All.Count == 0; t++)
        {
            if (world.Get<Melee>(attacker).Timer >= 0.13f) Guard(app, defender, true);
            Tick(world);
        }

        var parry = Assert.Single(blocked.All);
        Assert.Equal(GuardOutcome.Parried, parry.Outcome);
        Assert.Equal(20f, parry.Stopped, 3);
        Assert.Equal(100f, world.Attribute(defender, Health), 3);
        Assert.Equal(50f, world.Attribute(defender, Stamina), 3);
        var stagger = Assert.Single(staggered.All);
        Assert.Equal(attacker, stagger.Target);
        Assert.Equal(defender, stagger.Source);
        Assert.Equal(0.6f, stagger.Seconds, 3);
        Assert.Equal("recoiling", Animators.StateOf(world, attacker));

        // Staggered (and past its own cooldown), the attacker cannot swing...
        Tick(world, 31);
        Assert.True(world.Get<Melee>(attacker).Staggered > 0f);
        int swings = fired.All.Count;
        Press(app, attacker);
        Assert.Equal(swings, fired.All.Count);
        // ...until the stagger is over.
        Tick(world, 10);
        Assert.Equal(0f, world.Get<Melee>(attacker).Staggered);
        Press(app, attacker);
        Assert.Equal(swings + 1, fired.All.Count);
    }

    // A defender that cannot pay for the block has its guard broken: the blow lands whole, nothing is
    // spent, and it is staggered — its guard down until the stagger is over.
    [Xunit.Fact]
    public void AGuardThatCannotPayForTheBlockBreaks()
    {
        using var app = NewGame();
        var world = app.World;
        var attacker = Body(app, Vector3.Zero, "attacker", new Vector3(0, 0, -2));
        var defender = Body(app, new Vector3(0, 0, -1.2f), "defender", Vector3.Zero, "heavy_sword");
        var blocked = new EventProbe<Blocked>(world);
        var staggered = new EventProbe<Staggered>(world);
        Guard(app, defender, true);
        Tick(world, 30);

        Press(app, attacker);
        Tick(world, 15);
        Assert.Equal(GuardOutcome.Broken, Assert.Single(blocked.All).Outcome);
        Assert.Equal(80f, world.Attribute(defender, Health), 3);
        Assert.Equal(50f, world.Attribute(defender, Stamina), 3);
        var stagger = Assert.Single(staggered.All);
        Assert.Equal(defender, stagger.Target);
        Assert.Equal(0.8f, stagger.Seconds, 3);
        Assert.False(AttackStances.IsBlocking(world, defender));          // Block still held
        Tick(world, 50);
        Assert.True(AttackStances.IsBlocking(world, defender));
    }

    // A `directional` block stops only a swing coming the way its guard faces (Warband's rule): a guard
    // to the right lets an overhead blow through, a guard overhead stops it.
    [Xunit.Fact]
    public void ADirectionalGuardBlocksOnlyTheSwingItFaces()
    {
        using var app = NewGame();
        var world = app.World;
        var attacker = Body(app, Vector3.Zero, "attacker", new Vector3(0, 0, -2), direction: AttackDirection.Overhead);
        var defender = Body(app, new Vector3(0, 0, -1.2f), "defender", Vector3.Zero, "duel_sword", AttackDirection.Right);
        Guard(app, defender, true);
        Tick(world, 30);

        Swing(app, attacker);
        Assert.Equal(80f, world.Attribute(defender, Health), 3);

        AttackStances.Set(world, defender, AttackDirection.Overhead);
        Swing(app, attacker);
        Assert.Equal(80f, world.Attribute(defender, Health), 3);          // reduction 1: all of it
    }

    // Acceptance: hit data drives the reaction. A maul's blow pushes its victim along the blow's
    // direction (and up), sets its `reaction` trigger on the victim's animator, and staggers it — the
    // swing it was winding up is lost and never lands.
    [Xunit.Fact]
    public void AHeavyBlowKnocksBack_SetsTheReactionTrigger_AndInterruptsTheVictimsSwing()
    {
        using var app = NewGame();
        var world = app.World;
        var attacker = Body(app, Vector3.Zero, "attacker", new Vector3(0, 0, -2), "maul");
        var victim = Body(app, new Vector3(0, 0, -1.2f), "victim", Vector3.Zero);
        var staggered = new EventProbe<Staggered>(world);
        var damaged = new EventProbe<Damaged>(world);
        Tick(world, 3);

        // Both swing at once; the maul (windup 0.1 s) lands before the sword (0.2 s).
        var attack = app.Engine.Actions.Get("Attack");
        world.Get<PawnIntent>(attacker).Pressed = default(ActionMask).With(attack);
        world.Get<PawnIntent>(victim).Pressed = default(ActionMask).With(attack);
        Tick(world);
        world.Get<PawnIntent>(attacker).Pressed = default;
        world.Get<PawnIntent>(victim).Pressed = default;
        Tick(world, 8);

        Assert.Equal(90f, world.Attribute(victim, Health), 3);
        var stagger = Assert.Single(staggered.All);
        Assert.Equal(victim, stagger.Target);
        Assert.Equal(1f, stagger.Seconds, 3);
        Assert.Equal("flinch", Animators.StateOf(world, victim));
        Assert.Equal(MeleePhase.Ready, world.Get<Melee>(victim).Phase);

        Tick(world, 30);
        Assert.True(world.Get<Transform>(victim).LocalPosition.Z < -1.5f, $"pushed back: z = {world.Get<Transform>(victim).LocalPosition.Z}");
        Assert.Equal(100f, world.Attribute(attacker, Health), 3);         // the victim's swing never landed
        Assert.DoesNotContain(damaged.All, d => d.Hit.Target == attacker);
    }

    // Acceptance: a sweep with `cleave` strikes up to that many bodies in its reach and arc, nearest
    // first — never one behind the swing, and never one behind a wall.
    [Xunit.Fact]
    public void ACleaveStrikesThatManyBodiesInItsArc_NearestFirst_ButNotThroughAWall()
    {
        using var app = NewGame();
        var world = app.World;
        var attacker = Body(app, Vector3.Zero, "attacker", new Vector3(0, 0, -2), "greataxe");
        var a = Body(app, new Vector3(0, 0, -1.2f), "a", Vector3.Zero);
        var b = Body(app, new Vector3(0.9f, 0, -1.5f), "b", Vector3.Zero);
        var c = Body(app, new Vector3(-1.0f, 0, -1.9f), "c", Vector3.Zero);
        var behind = Body(app, new Vector3(0, 0, 1.2f), "behind", Vector3.Zero);
        Tick(world, 3);

        Swing(app, attacker, 50);                                  // cleave 2: the two nearest
        Assert.Equal(new[] { 80f, 80f, 100f, 100f }, new[] { a, b, c, behind }.Select(e => world.Attribute(e, Health)).ToArray());

        world.Get<Melee>(attacker).Attack = Id("greataxe3");       // cleave 3: all three in front
        Swing(app, attacker, 50);
        Assert.Equal(new[] { 60f, 60f, 80f, 100f }, new[] { a, b, c, behind }.Select(e => world.Attribute(e, Health)).ToArray());

        var wall = world.Create(Transform.At(new Vector3(-0.75f, 1f, -1.45f)), "wall");
        world.Add(wall, Collider.Box(new Vector3(0.4f, 3f, 0.1f)));
        Tick(world, 2);
        Swing(app, attacker, 50);
        Assert.Equal(new[] { 40f, 40f, 80f, 100f }, new[] { a, b, c, behind }.Select(e => world.Attribute(e, Health)).ToArray());
    }

    // Acceptance: damage over time goes through the damage pipeline. A burning effect's `damage`
    // execution deals its damage type each period, so fire_resist halves it, every pulse is a Damaged
    // from the burner, and a death by burning is the burner's kill.
    [Xunit.Fact]
    public void DamageOverTimeGoesThroughResistances_AndCreditsItsSource()
    {
        using var app = NewGame();
        var world = app.World;
        var burner = Body(app, new Vector3(5, 0, 0), "burner", Vector3.Zero);
        var plain = Body(app, new Vector3(0, 0, 0), "plain", Vector3.Zero);
        var proofed = Body(app, new Vector3(2, 0, 0), "proofed", Vector3.Zero);
        var doomed = Body(app, new Vector3(-2, 0, 0), "doomed", Vector3.Zero);
        Assert.True(Effects.Apply(world, proofed, Id("fireproof")));
        var damaged = new EventProbe<Damaged>(world);
        var died = new EventProbe<Died>(world);
        Tick(world);

        Assert.True(Effects.Apply(world, plain, Id("burning"), burner));     // a pulse now, then one a second
        Assert.True(Effects.Apply(world, proofed, Id("burning"), burner));
        Assert.Equal(90f, world.Attribute(plain, Health), 3);
        Assert.Equal(95f, world.Attribute(proofed, Health), 3);
        Tick(world, 61);
        Assert.Equal(80f, world.Attribute(plain, Health), 3);
        Assert.Equal(90f, world.Attribute(proofed, Health), 3);
        Tick(world, 180);                                                    // four pulses in all
        Assert.Equal(60f, world.Attribute(plain, Health), 3);
        Assert.Equal(80f, world.Attribute(proofed, Health), 3);
        Assert.All(damaged.All, d => Assert.Equal(burner, d.Hit.Attacker));
        Assert.All(damaged.All, d => Assert.Equal(Id("fire"), d.Hit.Type));

        Assert.True(Effects.Apply(world, doomed, Id("inferno"), burner));    // 40 a pulse: dead on the third
        Tick(world, 125);
        var death = Assert.Single(died.All);
        Assert.Equal(doomed, death.Victim);
        Assert.Equal(burner, death.Killer);
    }

    // Acceptance: friendly fire is one faction filter. FactionFilters.Allows answers it for any source
    // and target, and an attack's `friendlyFire` is that filter: by default a player's blow lands on a
    // companion, with NotAllies it passes through them and still lands on an enemy.
    [Xunit.Fact]
    public void FriendlyFireIsOneFactionFilter()
    {
        using var app = NewGame();
        var world = app.World;
        var player = Body(app, Vector3.Zero, "player", new Vector3(0, 0, -2));
        player.AddTag<PlayerControlled>();
        var companion = Body(app, new Vector3(0, 0, -1.2f), "companion", Vector3.Zero);
        world.Add(companion, new Faction { Id = Id("player") });
        var bandit = Body(app, new Vector3(10, 0, -1.2f), "bandit", Vector3.Zero);
        world.Add(bandit, new Faction { Id = Id("bandits") });
        var townsman = Body(app, new Vector3(-10, 0, -1.2f), "townsman", Vector3.Zero);
        world.Add(townsman, new Faction { Id = Id("town") });

        bool Allows(Entity source, Entity target, FactionFilter filter) => FactionFilters.Allows(world, source, target, filter);
        // Default: the engine's rule — a player reaches anyone, anybody else spares its allies.
        Assert.True(Allows(player, companion, FactionFilter.Default));
        Assert.False(Allows(companion, player, FactionFilter.Default));
        Assert.True(Allows(bandit, player, FactionFilter.Default));
        // NotAllies: allies (and itself) spared, for a player too.
        Assert.False(Allows(player, companion, FactionFilter.NotAllies));
        Assert.False(Allows(player, player, FactionFilter.NotAllies));
        Assert.True(Allows(player, townsman, FactionFilter.NotAllies));
        Assert.True(Allows(player, bandit, FactionFilter.NotAllies));
        // Hostile: only enemies.
        Assert.False(Allows(player, townsman, FactionFilter.Hostile));
        Assert.True(Allows(player, bandit, FactionFilter.Hostile));
        Assert.True(Allows(bandit, player, FactionFilter.Hostile));
        // Allies: a heal's — itself and its friends.
        Assert.True(Allows(player, player, FactionFilter.Allies));
        Assert.True(Allows(player, companion, FactionFilter.Allies));
        Assert.False(Allows(player, bandit, FactionFilter.Allies));
        Assert.False(Allows(player, townsman, FactionFilter.Allies));
        // Anyone.
        Assert.True(Allows(companion, player, FactionFilter.Anyone));

        Tick(world, 3);
        Swing(app, player);
        Assert.Equal(80f, world.Attribute(companion, Health), 3);          // the default: friendly fire

        world.Get<Melee>(player).Attack = Id("careful_sword");
        Swing(app, player);
        Assert.Equal(80f, world.Attribute(companion, Health), 3);          // NotAllies: through the companion

        world.Get<Transform>(player).LocalPosition = new Vector3(10, 0, 0);
        world.Teleport(player, world.Get<Transform>(player));
        Tick(world, 3);
        Swing(app, player);
        Assert.Equal(80f, world.Attribute(bandit, Health), 3);             // and onto the enemy
    }

    // Mistakes in a block or an attack's reaction numbers are load errors, each at its field.
    [Xunit.Fact]
    public void BlockAndHitReactionMistakesAreLoadErrors()
    {
        const string bad = """
        [
          { "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
          { "type": "attribute", "id": "focus", "start": 10, "min": 0, "max": 10 },
          { "type": "block", "id": "too_much", "reduction": 2 },
          { "type": "block", "id": "costly_nothing", "staminaCost": 5 },
          { "type": "block", "id": "unspendable", "stamina": "focus", "staminaCost": 5 },
          { "type": "attack", "id": "no_targets", "cleave": 0 },
          { "type": "attack", "id": "pulls", "knockback": -3 }
        ]
        """;
        using var app = HeadlessApp.Gameplay().File("data/bad.json", bad).Boot("bad");
        Assert.Equal(5, app.Records.ErrorCount);
        Assert.Contains(app.Records.LoadErrors, e => e.Contains("too_much") && e.Contains("reduction"));
        Assert.Contains(app.Records.LoadErrors, e => e.Contains("costly_nothing") && e.Contains("must name the attribute"));
        Assert.Contains(app.Records.LoadErrors, e => e.Contains("unspendable") && e.Contains("spendEffect"));
        Assert.Contains(app.Records.LoadErrors, e => e.Contains("no_targets") && e.Contains("cleave"));
        Assert.Contains(app.Records.LoadErrors, e => e.Contains("pulls") && e.Contains("knockback"));
    }
}
