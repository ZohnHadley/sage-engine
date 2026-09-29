#nullable enable
using System;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Sparks, embers and numbers (docs/design/06 §3.12, TODO F39).
//
// Where a particle is after four tenths of a second is arithmetic, so all of it is checked here without
// a window: emission, ageing, gravity, the fade, the budget, and what happens when the world moves under
// them. What is *not* here is the drawing, which is two triangles and the client's business.
public class ParticleTests
{
    public ParticleTests() { _ = TestEnv.UserRoot; }

    private static readonly RecordId Sparks = new("sage", "sparks");

    private static ParticleRecord Record(int burst = 8, float life = 1f, float gravity = 0f,
                                         EmitShape shape = EmitShape.Cone, int max = 256) =>
        new()
        {
            Burst = burst,
            LifeMin = life,
            LifeMax = life,
            SpeedMin = 2f,
            SpeedMax = 2f,
            Gravity = gravity,
            Shape = shape,
            MaxParticles = max,
            SizeStart = 0.2f,
            SizeEnd = 0f,
            ColourStart = 0xFFFFFFFF,
            ColourEnd = 0x00FFFFFF,
        };

    private static World NewWorld()
    {
        return HeadlessApp.Bare().Boot("particles").World;
    }

    // A burst is a burst: ask for eight and eight exist, each with a life and somewhere to go.
    [Fact]
    public void ABurstThrowsWhatItWasAskedFor()
    {
        var particles = new Particles();
        Assert.Equal(8, particles.Emit(Sparks, Record(), Vector3.Zero, Vector3.UnitY, 8));
        Assert.Equal(8, particles.Live);

        var group = particles.Groups[0];
        Assert.Equal(8, group.Count);
        for (int i = 0; i < group.Count; i++)
        {
            Assert.True(group.Life[i] > 0f);
            Assert.True(group.Velocity[i].LengthSquared() > 0f);
        }
    }

    // They age out, and the arrays go back to empty rather than growing for ever.
    [Fact]
    public void ParticlesDieWhenTheirTimeIsUp()
    {
        var world = NewWorld();
        var particles = new Particles();
        particles.Emit(Sparks, Record(burst: 8, life: 0.5f), Vector3.Zero, Vector3.UnitY, 8);

        for (int i = 0; i < 20; i++) particles.Update(world, 1f / 60f);   // a third of a second
        Assert.Equal(8, particles.Live);

        for (int i = 0; i < 20; i++) particles.Update(world, 1f / 60f);   // past half a second
        Assert.Equal(0, particles.Live);
        Assert.Equal(0, particles.Groups[0].Count);
    }

    // Gravity pulls them down and drag slows them: the two knobs that make smoke rise and sparks fall.
    [Fact]
    public void GravityAndDragDoWhatTheySay()
    {
        var world = NewWorld();
        var particles = new Particles();
        var record = Record(burst: 1, life: 5f, gravity: -9.81f, shape: EmitShape.Point);
        particles.Emit(Sparks, record, Vector3.Zero, Vector3.UnitY, 1);

        for (int i = 0; i < 60; i++) particles.Update(world, 1f / 60f);
        var afterOneSecond = particles.Groups[0].Position[0];

        // Thrown up at 2 m/s under gravity: about a fifth of a metre up and already falling.
        Assert.True(afterOneSecond.Y < 2f, $"it rose to {afterOneSecond.Y:F2} m, so gravity is not pulling");
        Assert.True(particles.Groups[0].Velocity[0].Y < 0f, "it should be falling by now");

        // With drag, the same throw does not get as far.
        var dragged = new Particles();
        var slowed = Record(burst: 1, life: 5f, gravity: 0f, shape: EmitShape.Point);
        slowed.Drag = 4f;
        dragged.Emit(Sparks, slowed, Vector3.Zero, Vector3.UnitY, 1);
        for (int i = 0; i < 60; i++) dragged.Update(world, 1f / 60f);

        var free = new Particles();
        free.Emit(Sparks, Record(burst: 1, life: 5f, gravity: 0f, shape: EmitShape.Point), Vector3.Zero, Vector3.UnitY, 1);
        for (int i = 0; i < 60; i++) free.Update(world, 1f / 60f);

        Assert.True(dragged.Groups[0].Position[0].Y < free.Groups[0].Position[0].Y,
            "drag made no difference to how far it got");
    }

    // Size and colour are pure functions of age, so nothing stores them and a fade cannot drift out of
    // step with the life it belongs to.
    [Fact]
    public void SizeAndColourFollowAge()
    {
        var world = NewWorld();
        var particles = new Particles();
        particles.Emit(Sparks, Record(burst: 1, life: 1f), Vector3.Zero, Vector3.UnitY, 1);
        var group = particles.Groups[0];

        Assert.Equal(0.2f, group.SizeOf(0), 3);
        Assert.Equal(0xFFu, group.ColourOf(0) >> 24);          // fully opaque at birth

        for (int i = 0; i < 30; i++) particles.Update(world, 1f / 60f);
        Assert.Equal(0.1f, group.SizeOf(0), 2);                 // half way through, half the size
        Assert.InRange(group.ColourOf(0) >> 24, 100u, 160u);    // and half faded
    }

    // The ceilings: per effect, so one spell cannot flood the screen, and across everything, so a
    // hundred spells cannot either. Both are counted rather than hoped for.
    [Fact]
    public void BothCeilingsHold()
    {
        var particles = new Particles();
        Assert.Equal(10, particles.Emit(Sparks, Record(max: 10), Vector3.Zero, Vector3.UnitY, 50));
        Assert.Equal(10, particles.Live);
        Assert.True(particles.Refused > 0);

        var flooded = new Particles { Budget = 12 };
        flooded.Emit(Sparks, Record(max: 1000), Vector3.Zero, Vector3.UnitY, 100);
        Assert.Equal(12, flooded.Live);
    }

    // A carried effect rides its owner — an aura, a burning coat — and goes when the owner does, which
    // is the campfire lesson from F4 applied before it could bite twice.
    [Fact]
    public void CarriedParticlesFollowTheirOwnerAndDieWithIt()
    {
        var world = NewWorld();
        var particles = new Particles();
        var record = Record(burst: 4, life: 10f);
        record.Local = true;

        var torch = world.Create(Transform.At(new Vector3(0, 0, 0)), "torch");
        particles.Emit(Sparks, record, Vector3.Zero, Vector3.UnitY, 4, follows: torch);

        world.Get<Transform>(torch).LocalPosition = new Vector3(10, 0, 0);
        particles.Update(world, 1f / 60f);
        Assert.True(particles.Groups[0].Position[0].X > 9f, "the sparks stayed behind");

        world.Destroy(torch);
        particles.Update(world, 1f / 60f);
        Assert.Equal(0, particles.Live);
    }

    // The world moves under them (R6). Free particles move with it; carried ones are already where
    // their owner is, and moving them twice would fling them a kilometre.
    [Fact]
    public void ParticlesMoveWithTheWorld()
    {
        var world = NewWorld();
        var particles = new Particles();
        var free = Record(burst: 1, life: 10f);
        var carried = Record(burst: 1, life: 10f);
        carried.Local = true;

        var owner = world.Create(Transform.At(Vector3.Zero), "owner");
        particles.Emit(new RecordId("sage", "free"), free, new Vector3(5, 0, 0), Vector3.UnitY, 1);
        particles.Emit(new RecordId("sage", "carried"), carried, Vector3.Zero, Vector3.UnitY, 1, follows: owner);

        var offset = new Vector3(-1024, 0, 0);
        particles.Rebase(offset);

        Assert.Equal(5f + offset.X, particles.Groups[0].Position[0].X, 3);
        Assert.Equal(0f, particles.Groups[1].Position[0].X, 3);
    }

    // A cone stays a cone: every spark off a blade goes roughly the way the blade went, or the effect
    // is a sphere with extra steps.
    [Fact]
    public void AConeThrowsThingsRoughlyOneWay()
    {
        var particles = new Particles();
        var record = Record(burst: 64, life: 1f);
        record.AngleDegrees = 30f;
        particles.Emit(Sparks, record, Vector3.Zero, Vector3.UnitY, 64);

        var group = particles.Groups[0];
        for (int i = 0; i < group.Count; i++)
            Assert.True(Vector3.Normalize(group.Velocity[i]).Y > 0.3f,
                $"a spark went off at {group.Velocity[i]}, which is not up a 30° cone");
    }

    // ---- damage numbers ---------------------------------------------------------------------------

    [Fact]
    public void ANumberRisesAndFadesAndGoes()
    {
        var texts = new FloatingTexts();
        texts.Add(FloatingTexts.Number(12), Vector3.Zero, DamageNumbers.Default, life: 1f);

        Assert.Equal(1, texts.Count);
        Assert.Equal("12", texts[0].Text);
        Assert.Equal(0xFFu, texts.ColourOf(0) >> 24);

        for (int i = 0; i < 30; i++) texts.Update(1f / 60f);
        Assert.True(texts[0].Position.Y > 0.5f, "it did not rise");
        Assert.Equal(0xFFu, texts.ColourOf(0) >> 24);          // still solid at half life

        for (int i = 0; i < 25; i++) texts.Update(1f / 60f);
        Assert.True(texts.ColourOf(0) >> 24 < 0xFF, "it should be fading by now");

        for (int i = 0; i < 20; i++) texts.Update(1f / 60f);
        Assert.Equal(0, texts.Count);
    }

    // A fight is a lot of small numbers, and small numbers cost nothing: the strings are already made.
    [Fact]
    public void SmallNumbersAreNotBuiltTwice()
    {
        Assert.Same(FloatingTexts.Number(7), FloatingTexts.Number(7));
        Assert.Equal("999", FloatingTexts.Number(999));
        Assert.Equal("1200", FloatingTexts.Number(1200));      // big ones still work, just not cached
    }

    // Sixty-four numbers at once is already noise; the oldest goes rather than the newest being dropped,
    // because the newest is the one the player is waiting for.
    [Fact]
    public void TheOldestNumberGoesWhenThereAreTooMany()
    {
        var texts = new FloatingTexts();
        for (int i = 0; i < 80; i++) texts.Add(FloatingTexts.Number(i), Vector3.Zero, DamageNumbers.Default);

        Assert.Equal(64, texts.Count);
        Assert.Equal("79", texts[texts.Count - 1].Text);
        Assert.Equal("16", texts[0].Text);
    }

}
