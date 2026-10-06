#nullable enable
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// An effect has three moments and its cues say which (issue #330, docs/design/11 §13): `appliedCues`
// when it lands, `tickCues` on each period of a periodic one, `removedCues` when it ends — it ran out,
// was removed or was dispelled. And an attack names the sound of its own blow (`sound`), in place of its
// damage type's. All simulation: what a cue sounds like is the client's, and the headless world only
// raises it.
public class CueMomentTests
{
    public CueMomentTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
        [{ "type": "gameplay_conventions", "id": "default_conventions", "health": "health", "damageType": "physical" },
         { "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
         { "type": "cue", "id": "on" }, { "type": "cue", "id": "pulse" }, { "type": "cue", "id": "off" },

         { "type": "effect", "id": "venom", "duration": "Timed", "time": 1, "period": 0.25,
           "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ],
           "appliedCues": ["on"], "tickCues": ["pulse"], "removedCues": ["off"] },
         { "type": "effect", "id": "blessing", "duration": "Infinite", "modifiers": [],
           "appliedCues": ["on"], "removedCues": ["off"] },
         { "type": "effect", "id": "hex", "duration": "Infinite", "modifiers": [], "removedCues": ["off"] },
         { "type": "effect", "id": "mute", "duration": "Instant", "modifiers": [] },
         { "type": "effect", "id": "hit", "duration": "Instant", "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },

         { "type": "damage_type", "id": "physical", "effect": "hit", "sound": "sage:thud" },
         { "type": "sound", "id": "thud", "variations": ["audio/a.wav"] },
         { "type": "sound", "id": "clang", "variations": ["audio/a.wav"], "bus": "Sfx" },
         { "type": "attack", "id": "dagger", "damage": 5 },
         { "type": "attack", "id": "hammer", "damage": 5, "sound": "clang" }]
        """;

    private static RecordId Id(string name) => new("sage", name);

    private static (HeadlessApp App, World World, Entity Target, EventProbe<CueTriggered> Cues) Boot()
    {
        var app = HeadlessApp.Gameplay()
            .OnRegistered(a => a.Records.Register<SoundRecord>())   // the client's record type
            .File("data/cues.json", Records)
            .File("audio/a.wav", "RIFF")
            .Boot("cues");
        Assert.True(app.Records.ErrorCount == 0, string.Join("\n", app.Records.LoadErrors));
        var world = app.World;
        var target = world.Create(Transform.At(new Vector3(4, 0, -2)), "target");
        world.AddAttributes(target);
        return (app, world, target, new EventProbe<CueTriggered>(world));
    }

    private static void Tick(World world, int ticks)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    // Applied, ticked, removed: three raises, in order, at the target, each its own list.
    [Xunit.Fact]
    public void AnEffectRaisesItsAppliedTickedAndRemovedCuesAtItsTarget()
    {
        var (app, world, target, cues) = Boot();
        using (app)
        {
            Effects.Apply(world, target, Id("venom"));
            Tick(world, 1);
            Assert.Equal(new[] { Id("on") }, cues.All.Select(c => c.Cue));
            Assert.Equal(new Vector3(4, 0, -2), cues.All[0].Point);

            Tick(world, 70);                                    // 1.17 s: four periods and the end
            var ids = cues.All.Select(c => c.Cue).ToArray();
            Assert.Equal(Id("on"), ids[0]);
            Assert.Equal(4, ids.Count(c => c == Id("pulse")));
            Assert.Equal(Id("off"), ids[^1]);
            Assert.Single(ids, c => c == Id("off"));
            Assert.False(Effects.IsActive(world, target, Id("venom")));
        }
    }

    // Removing an effect by hand is an end like running out: its removed cues, and only if there was one.
    [Xunit.Fact]
    public void RemovingAnEffectRaisesItsRemovedCuesOnce()
    {
        var (app, world, target, cues) = Boot();
        using (app)
        {
            Effects.Apply(world, target, Id("blessing"));
            Tick(world, 1);
            Assert.Equal(1, Effects.Remove(world, target, Id("blessing")));
            Assert.Equal(0, Effects.Remove(world, target, Id("blessing")));   // nothing left: nothing raised
            Assert.Equal(new[] { Id("on"), Id("off") }, cues.All.Select(c => c.Cue));
        }
    }

    // A list left empty raises nothing, and a moment is not another's: `hex` has only a removed cue.
    [Xunit.Fact]
    public void AnEffectWithoutCuesIsSilentAndEachMomentHasItsOwn()
    {
        var (app, world, target, cues) = Boot();
        using (app)
        {
            Effects.Apply(world, target, Id("mute"));
            Effects.Apply(world, target, Id("hex"));
            Tick(world, 5);
            Assert.Empty(cues.All);
            Effects.Remove(world, target, Id("hex"));
            Assert.Equal(new[] { Id("off") }, cues.All.Select(c => c.Cue));
        }
    }

    // The attack's own sound wins; the one that names none is its damage type's.
    [Xunit.Fact]
    public void AnAttackNamesItsOwnSoundAndOtherwiseTheDamageTypesIsUsed()
    {
        var (app, world, target, _) = Boot();
        using (app)
        {
            var records = world.Records();
            var attacker = world.Create(Transform.At(Vector3.Zero), "attacker");
            var probe = new EventProbe<Damaged>(world);

            foreach (var (attack, expected) in new[] { ("dagger", "thud"), ("hammer", "clang") })
            {
                var request = new HitRequest(attacker, Vector3.Zero, Vector3.UnitZ, Id(attack));
                var landing = new HitResult(target, new Vector3(4, 0, -2), -Vector3.UnitZ, target, default);
#pragma warning disable SAGE0127 // phase 4e's experimental API: this is what a delivery calls
                Assert.True(Combat.ApplyHit(world, in request, in landing, records.Get<AttackRecord>(Id(attack))) > 0f);
#pragma warning restore SAGE0127
                Assert.Equal(Id(expected), Combat.HitSound(records, probe.All[^1].Hit));
            }
        }
    }
}
