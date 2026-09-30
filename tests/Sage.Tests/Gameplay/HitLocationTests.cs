#nullable enable
using System;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Hit locations (issue #137, docs/design/16 "As built (hit locations)"): tests/games/skeletal's mannequin
// has hitboxes on its bones (its `hitboxes` record and the `hitboxes` part), a strike that meets one lands
// on its hit_location — the location's multiplier, then its resist, then the damage type's — and a strike
// that meets only a capsule, or anything with no hitboxes, lands on the body.
public class HitLocationTests
{
    public HitLocationTests() { _ = TestEnv.UserRoot; }

    private const float Dt = NpcLocomotionTests.Dt;
    private static readonly RecordId Health = new("sage", "health");
    private static readonly RecordId Head = new("skeletal", "head");
    private static readonly RecordId ArmL = new("skeletal", "arm_l");
    private static readonly RecordId Pistol = new("hits", "pistol");
    private static readonly RecordId Bolt = new("hits", "bolt");
    private static readonly RecordId Fireball = new("hits", "fireball");

    // A pistol, a helmet that is only data (an item whose effect adds armor_head while it is worn) and a
    // mannequin with a capsule round it as well as its hitboxes.
    private const string Records = """
        [{ "type": "attack", "id": "pistol", "delivery": "ray", "damage": 10, "range": 60,
           "windupTime": 0, "recoverTime": 0.1, "cooldown": 0.3 },
         { "type": "attack", "id": "peashooter", "base": "pistol", "damage": 0.001 },
         { "type": "attack", "id": "bolt", "delivery": "projectile", "damage": 10, "range": 60, "radius": 0.02,
           "projectileSpeed": 50, "windupTime": 0, "recoverTime": 0.1, "cooldown": 1 },
         { "type": "attack", "id": "piercing_bolt", "base": "bolt", "projectilePierce": 1 },
         { "type": "ability", "id": "fireball", "targeting": "Projectile", "damage": 10, "radius": 0,
           "projectileSpeed": 30, "range": 40 },
         { "type": "effect", "id": "helmet_armor", "duration": "Infinite",
           "modifiers": [ { "attribute": "skeletal:armor_head", "op": "Add", "value": 50 } ] },
         { "type": "item", "id": "helmet", "slot": "Head", "effects": ["helmet_armor"] },
         { "type": "prefab", "id": "capsuled", "base": "skeletal:npc",
           "parts": { "body": { "shape": "Capsule", "radius": 0.4, "height": 1.9 } } }]
        """;

    // The skeletal game has combat and no magic, so a bolt flies on the carrier combat adds (issue #138);
    // `abilities` adds the abilities plugin, for a fireball.
    internal static HeadlessApp Yard(bool abilities = false)
    {
        var builder = HeadlessApp.ForGame(NpcLocomotionTests.Game("skeletal")).WithEngineContent();
        if (abilities) builder.With(new AbilitiesModule());
        var app = builder
            .OnRegistered(a => a.Engine.Modules.Modules.OfType<ItemsModule>().Single().Slots.Register("Head"))
            .File("data/hits.json", Records, "hits").Boot();
        Assert.Equal(0, app.Records.ErrorCount);
        return app;
    }

    private static Entity Npc(World world, Vector3 at, string prefab = "skeletal:npc")
    {
        var npc = world.Spawn(RecordId.Parse(prefab, "skeletal"), at);
        Assert.False(npc.IsNull);
        return npc;
    }

    // A ray from `origin` along `aim`, struck by `attacker` with `attack`, through the whole pipeline.
    private static (HitResult Result, float Applied) Shoot(World world, Entity attacker, Vector3 origin, Vector3 aim, RecordId attack)
    {
        var space = world.Resources.Get<IPhysicsWorld>();
        var record = world.Resources.Get<RecordStore>().Get<AttackRecord>(attack);
        aim = Vector3.Normalize(aim);
        if (!Hits.Ray(space, attacker, origin, aim, record.Range, out var result)) return (result, 0f);
        return (result, Combat.ApplyHit(world, new HitRequest(attacker, origin, aim, attack), result, record));
    }

    private static int HitboxesOf(World world, Entity owner)
    {
        int count = 0;
        foreach (var (boxes, _) in world.Query<Hitbox>().Chunks)
            foreach (ref readonly var box in boxes.Span)
                if (box.Owner == owner) count++;
        return count;
    }

    // Acceptance: a pistol shot at the mannequin's head — from another mannequin, pressing Attack like any
    // fighter, its eye inside its own head's box — lands on `head` and does twice the pistol's damage; a
    // helmet (armor_head 50, data only) halves that while it is worn.
    [Xunit.Fact]
    public void AShotAtTheHeadLandsOnHeadForDoubleDamage_AndAHelmetHalvesIt()
    {
        using var app = Yard();
        var world = app.World;
        var target = Npc(world, new Vector3(12, 0, 14));
        var shooter = Npc(world, new Vector3(12, 0, 19));
        Assert.Equal(11, HitboxesOf(world, target));
        world.AddCharacter(shooter, world.Resources.Get<IPhysicsWorld>().Layers.Player);
        world.Add(shooter, Melee.With(Pistol));
        world.Get<PawnIntent>(shooter).Yaw = SageMath.YawTo(new Vector3(12, 0, 19), new Vector3(12, 0, 14));
        NpcLocomotionTests.Step(world, 5);                      // posed, and the boxes on the bones
        var damage = new EventProbe<Damaged>(world);

        void Fire()
        {
            world.Get<PawnIntent>(shooter).Pressed = default(ActionMask).With(app.Engine.Actions.Get("Attack"));
            NpcLocomotionTests.Step(world);
            world.Get<PawnIntent>(shooter).Pressed = default;
            NpcLocomotionTests.Step(world, 30);                // lands on the next tick; past the cooldown
        }

        // Level from a standing eye (1.62 m) meets the head's box: 10 × 2.
        Fire();
        var hit = Assert.Single(damage.All);
        Assert.Equal(target, hit.Hit.Target);
        Assert.Equal(Head, hit.Location);
        Assert.Equal(20f, hit.Applied, 3);
        Assert.Equal(80f, world.Attribute(target, Health), 3);
        Assert.Equal(100f, world.Attribute(shooter, Health), 3);   // not its own head, which the eye is in
        Assert.True(HitLocations.HasTag(app.Records, hit.Location, new RecordId("skeletal", "vital")));

        // A helmet is an item whose effect adds armor_head while it is worn: 10 × 2 × (1 − 0.5).
        world.AddInventory(target);
        Assert.True(world.Give(target, new RecordId("hits", "helmet")));
        Assert.True(world.Equip(target, new RecordId("hits", "helmet")));
        NpcLocomotionTests.Step(world);                         // an infinite effect is counted in on the tick
        Assert.Equal(50f, world.Attribute(target, new RecordId("skeletal", "armor_head")), 3);
        Fire();
        Assert.Equal(2, damage.All.Count);
        Assert.Equal(Head, damage.All[1].Location);
        Assert.Equal(10f, damage.All[1].Applied, 3);

        // Off again, and the head takes the full double.
        world.Unequip(target, "Head");
        Fire();
        Assert.Equal(20f, damage.All[2].Applied, 3);
        Assert.Equal(50f, world.Attribute(target, Health), 3);
    }

    // Acceptance: a shot at the hanging left arm (the mannequin's -X, beside the chest) lands on arm_l,
    // for half damage — and the location, the collider it met and the point come back from the query.
    [Xunit.Fact]
    public void ALimbShotHitsArmL()
    {
        using var app = Yard();
        var world = app.World;
        var target = Npc(world, new Vector3(12, 0, 14));
        var shooter = world.Create(Transform.At(new Vector3(0, 0, 30)), "shooter");
        NpcLocomotionTests.Step(world, 5);
        var damage = new EventProbe<Damaged>(world);

        var (result, applied) = Shoot(world, shooter, new Vector3(11.75f, 1.25f, 19f), -Vector3.UnitZ, Pistol);
        Assert.Equal(target, result.Target);
        Assert.Equal(ArmL, result.Location);
        Assert.True(world.TryGet<Hitbox>(result.Collider, out var box) && box.Owner == target && box.Location == ArmL);
        Assert.InRange(result.Point.Z, 14f, 14.2f);             // on the box's back face, not the ray's end
        Assert.Equal(5f, applied, 3);
        Assert.Equal(ArmL, Assert.Single(damage.All).Location);
        Assert.False(HitLocations.HasTag(app.Records, ArmL, new RecordId("skeletal", "vital")));
        Assert.True(HitLocations.HasTag(app.Records, ArmL, new RecordId("skeletal", "limb")));

        // The chest beside it is the torso, at full damage; between the legs below the hands is nothing.
        Assert.Equal(new RecordId("skeletal", "torso"), Shoot(world, shooter, new Vector3(12f, 1.25f, 19f), -Vector3.UnitZ, Pistol).Result.Location);
        Assert.True(Shoot(world, shooter, new Vector3(12f, 0.6f, 19f), -Vector3.UnitZ, Pistol).Result.Target.IsNull);
    }

    // A strike that meets the capsule and misses every hitbox lands on the body (an empty location, the
    // plain damage); one that goes on into the capsule and meets the head lands on the head.
    [Xunit.Fact]
    public void ACapsuleShotThatMissesEveryBoxLandsOnTheBody()
    {
        using var app = Yard();
        var world = app.World;
        var target = Npc(world, new Vector3(12, 0, 14), "hits:capsuled");
        var shooter = world.Create(Transform.At(new Vector3(0, 0, 30)), "shooter");
        NpcLocomotionTests.Step(world, 5);

        // Beside the shins, below the hands: inside the 0.4 m capsule, outside every box.
        var (body, applied) = Shoot(world, shooter, new Vector3(12.34f, 0.6f, 19f), -Vector3.UnitZ, Pistol);
        Assert.Equal(target, body.Target);
        Assert.Equal(target, body.Collider);                    // the capsule itself
        Assert.True(body.Location.IsEmpty);
        Assert.Equal(10f, applied, 3);

        // At eye height the ray enters the capsule first and meets the head's box inside it.
        var (head, doubled) = Shoot(world, shooter, new Vector3(12f, 1.62f, 19f), -Vector3.UnitZ, Pistol);
        Assert.Equal(target, head.Target);
        Assert.Equal(Head, head.Location);
        Assert.Equal(20f, doubled, 3);
    }

    // Acceptance: a creature with no hitboxes — the Sandbox's sprite creatures, any capsule — is still hit
    // as the body, as it was before hitboxes existed.
    [Xunit.Fact]
    public void ACreatureWithNoHitboxesIsHitAsTheBody()
    {
        using var app = Yard();
        var world = app.World;
        var goblin = HitPipelineTests.Body(world, new Vector3(12, 0, 14), "goblin", new Vector3(12, 0, 19));
        var shooter = world.Create(Transform.At(new Vector3(0, 0, 30)), "shooter");
        NpcLocomotionTests.Step(world, 3);
        var damage = new EventProbe<Damaged>(world);

        var (result, applied) = Shoot(world, shooter, new Vector3(12f, 1.62f, 19f), -Vector3.UnitZ, Pistol);
        Assert.Equal(goblin, result.Target);
        Assert.True(result.Location.IsEmpty);
        Assert.Equal(10f, applied, 3);
        Assert.True(Assert.Single(damage.All).Location.IsEmpty);
    }

    // Acceptance (issue #138): a bolt lands through the same hitbox resolution as a sweep or a ray. The
    // mannequin has no capsule, only its boxes, so before this a bolt flew straight through it; a bolt at
    // its head now lands on `head` for double damage, one at its shins (between the boxes) still misses,
    // and a piercing bolt is one landing on the mannequin, not one per box it passes through.
    [Xunit.Fact]
    public void ABoltAtTheHeadLandsOnHead()
    {
        using var app = Yard();
        var world = app.World;
        var target = Npc(world, new Vector3(12, 0, 14));
        var shooter = world.Create(Transform.At(new Vector3(0, 0, 30)), "shooter");
        NpcLocomotionTests.Step(world, 5);
        var damage = new EventProbe<Damaged>(world);
        var records = world.Resources.Get<RecordStore>();

        Entity Fire(RecordId attack, Vector3 from)
        {
            var bolt = world.Launch(shooter, attack, records.Get<AttackRecord>(attack), from, -Vector3.UnitZ);
            for (int i = 0; i < 20; i++) world.RunFixed(Dt);
            return bolt;
        }

        var bolt = Fire(Bolt, new Vector3(12f, 1.62f, 19f));
        var hit = Assert.Single(damage.All);
        Assert.Equal(target, hit.Hit.Target);
        Assert.Equal(shooter, hit.Hit.Attacker);
        Assert.Equal(Head, hit.Location);
        Assert.Equal(20f, hit.Applied, 3);
        Assert.InRange(hit.Hit.Point.Z, 14f, 14.3f);            // on the head's box, not past it
        Assert.False(world.IsAlive(bolt));

        // Beside the legs, below the hands, there is no box (and no capsule): it flies on.
        Fire(Bolt, new Vector3(12.6f, 0.6f, 19f));
        Assert.Single(damage.All);

        // A piercing bolt through the chest lands once on the mannequin, then flies on.
        Fire(new RecordId("hits", "piercing_bolt"), new Vector3(12f, 1.25f, 19f));
        Assert.Equal(2, damage.All.Count);
        Assert.Equal(target, damage.All[1].Hit.Target);
        Assert.Equal(new RecordId("skeletal", "torso"), damage.All[1].Location);
    }

    // Acceptance (issue #139): an ability's projectile lands through the same hitbox resolution as an
    // attack's bolt, and its damage lands at the hitbox's hit_location: a fireball at the head is twice
    // its damage, on `head`.
    [Xunit.Fact]
    public void AFireballAtTheHeadLandsOnHead()
    {
        using var app = Yard(abilities: true);
        var world = app.World;
        var target = Npc(world, new Vector3(12, 0, 14));
        var shooter = world.Create(Transform.At(new Vector3(0, 0, 30)), "shooter");
        NpcLocomotionTests.Step(world, 5);
        var damage = new EventProbe<Damaged>(world);
        var record = world.Resources.Get<RecordStore>().Get<AbilityRecord>(Fireball);

        var fireball = world.Launch(shooter, Fireball, record, new Vector3(12f, 1.62f, 19f), -Vector3.UnitZ);
        for (int i = 0; i < 20; i++) world.RunFixed(Dt);
        var hit = Assert.Single(damage.All);
        Assert.Equal(target, hit.Hit.Target);
        Assert.Equal(shooter, hit.Hit.Attacker);
        Assert.Equal(Head, hit.Location);
        Assert.Equal(20f, hit.Applied, 3);
        Assert.False(world.IsAlive(fireball));
    }

    // Hitboxes follow the bones (the Late phase's attachments) and the body (composed in PrePhysics from
    // the owner's transform as it is that tick): a strike in Gameplay meets the previous tick's pose where
    // the body is now. An aimed arm is where the aim put it, and a body moved a metre is hit where it went.
    [Xunit.Fact]
    public void TheBoxesFollowTheBonesAndTheBody()
    {
        using var app = Yard();
        var world = app.World;
        var target = Npc(world, new Vector3(12, 0, 14));
        var shooter = world.Create(Transform.At(new Vector3(0, 0, 30)), "shooter");
        NpcLocomotionTests.Step(world, 5);

        // Aiming: the right forearm comes up in front of the chest. Its box's centre, from the pose, is
        // where a shot from the front lands on arm_r.
        Animators.SetParam(world, target, "aiming", true);
        NpcLocomotionTests.Step(world, 40);
        Assert.True(Animators.TryGetPose(world, target, out var pose));
        int forearm = pose.Skeleton.IndexOf("forearm_r");
        var centre = Vector3.Transform(new Vector3(0.005f, -0.20f, 0), pose.ModelSpace[forearm]) + new Vector3(12, 0, 14);
        Assert.True(centre.Y > 1.2f, $"the aimed forearm is at {centre.Y} m, not up in front");
        var (aimed, _) = Shoot(world, shooter, centre + new Vector3(0, 0, -5), Vector3.UnitZ, Pistol);
        Assert.Equal(new RecordId("skeletal", "arm_r"), aimed.Location);

        // Moved a metre east between ticks: one fixed tick later, the head is hit where the body went.
        world.Get<Transform>(target).LocalPosition += Vector3.UnitX;
        world.RunFixed(Dt);
        var (moved, _) = Shoot(world, shooter, new Vector3(13f, 1.62f, 19f), -Vector3.UnitZ, Pistol);
        Assert.Equal(target, moved.Target);
        Assert.Equal(Head, moved.Location);
        Assert.True(Shoot(world, shooter, new Vector3(12f, 1.62f, 19f), -Vector3.UnitZ, Pistol).Result.Target.IsNull);
    }

    // The boxes are the owner's: gone with it, and back from its prefab after a load (never saved).
    [Xunit.Fact]
    public void HitboxesGoWithTheirOwnerAndComeBackFromThePrefabAfterALoad()
    {
        using var app = Yard();
        var world = app.World;
        var walker = world.FindByName("walker");
        Assert.Equal(11, HitboxesOf(world, walker));
        var spare = Npc(world, new Vector3(12, 0, 14));
        NpcLocomotionTests.Step(world, 2);
        world.Destroy(spare);
        NpcLocomotionTests.Step(world);
        Assert.Equal(0, HitboxesOf(world, spare));

        int total = world.Query<Hitbox>().Count;
        Assert.Equal(5 * 11, total);                            // the yard's five NPCs
        Assert.True(app.Engine.Saves.Save("hitboxes"));
        Assert.DoesNotContain("sage:hitbox\"", System.IO.File.ReadAllText(System.IO.Path.Combine(app.Engine.Saves.Root, "hitboxes", "world_main.json")));
        Assert.True(app.Engine.Saves.Load("hitboxes"));
        NpcLocomotionTests.Step(world);
        Assert.Equal(total, world.Query<Hitbox>().Count);
        Assert.Equal(11, HitboxesOf(world, world.FindByName("walker")));
    }

    // A hitboxes record is checked as it loads: a box with no location, nothing to follow or no size.
    [Xunit.Fact]
    public void AHitboxWithNoLocationBoneOrSizeIsALoadError()
    {
        const string bad = """
            [{ "type": "hitboxes", "id": "broken", "model": "models/mannequin.glb",
               "boxes": { "nothing": { "size": [0.1, 0.1, 0.1] }, "flat": { "location": "skeletal:head", "bone": "head" } } }]
            """;
        using var log = new CaptureSink();
        using var app = HeadlessApp.ForGame(NpcLocomotionTests.Game("skeletal")).WithEngineContent().File("data/bad.json", bad, "bad").Build();
        var errors = log.Entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Message).ToList();
        Assert.Contains(errors, e => e.Contains("hitboxes bad:broken: names no hit_location"));
        Assert.Contains(errors, e => e.Contains("hitboxes bad:broken: names neither a bone nor a socket to follow"));
        Assert.Contains(errors, e => e.Contains("hitboxes bad:broken: is not a Box's size"));
        Assert.Equal(3, errors.Count(e => e.Contains("bad:broken")));
    }
}

// Acceptance: fifty mannequins with hitboxes — eleven kinematic boxes each on the query-only layer,
// following their bones — and a shooter putting a ray into one of them every tick allocate nothing per
// tick, beside the physics backend's own step. Allocation is measured per thread, alone.
[Xunit.Collection(MeasurementsCollection.Name)]
public class HitboxAllocationTests
{
    public HitboxAllocationTests() { _ = TestEnv.UserRoot; }

    [Xunit.Fact]
    public void FiftyNpcsWithHitboxesAllocateNothingPerTick()
    {
        using var app = HitLocationTests.Yard();
        var world = app.World;
        var npcs = new Entity[50];
        for (int i = 0; i < npcs.Length; i++)
            npcs[i] = world.Spawn(NpcLocomotionTests.Npc, new Vector3(-12 + (i % 10) * 2.5f, 0, 8 + (i / 10) * 2f));
        int boxes = world.Query<Hitbox>().Count;
        Assert.True(boxes >= 50 * 11, $"{boxes} hitboxes");

        var space = world.Resources.Get<IPhysicsWorld>();
        var attack = app.Records.Get<AttackRecord>(new RecordId("hits", "peashooter"));
        var shooter = world.Create(Transform.At(new Vector3(0, 0, 30)), "shooter");
        int tick = 0, heads = 0;
        void Step()
        {
            tick++;
            for (int i = 0; i < npcs.Length; i++)
            {
                world.Get<Transform>(npcs[i]).LocalPosition += new Vector3(MathF.Sin(tick * 0.01f + i) * 2f / 60f, 0, 0);
                int beat = (tick + i * 7) % 240;
                if (beat == 0) Animators.SetParam(world, npcs[i], "aiming", true);
                if (beat == 60) Animators.SetTrigger(world, npcs[i], "attack");
                if (beat == 200) Animators.SetParam(world, npcs[i], "aiming", false);
            }
            world.RunFixed(NpcLocomotionTests.Dt);
            world.RunFrame(NpcLocomotionTests.Dt, 1f);

            // A shot at one of them, at eye height from in front of its row, through the whole pipeline.
            var target = npcs[tick % npcs.Length];
            var at = world.Get<Transform>(target).LocalPosition;
            var origin = new Vector3(at.X, 1.62f, at.Z - 0.9f);
            if (Hits.Ray(space, shooter, origin, Vector3.UnitZ, 1.5f, out var result))
            {
                Combat.ApplyHit(world, new HitRequest(shooter, origin, Vector3.UnitZ, new RecordId("hits", "peashooter")), result, attack);
                if (!result.Location.IsEmpty) heads++;
            }
            Profiler.EndFrame();
        }

        for (int i = 0; i < 300; i++) Step();
        Assert.True(heads > 250, $"only {heads} of 300 shots met a hitbox");

        long physicsBefore = ScopeBytes("Fixed.Physics");
        var allocated = AllocationProbe.Measure(300, Step);
        long physics = ScopeBytes("Fixed.Physics") - physicsBefore;
        Assert.True(allocated.Bytes - physics == 0, allocated.ToString());
    }

    private static long ScopeBytes(string name)
    {
        foreach (var entry in Profiler.All)
            if (entry.Name == name) return entry.AllocatedBytes;
        return 0;
    }
}
