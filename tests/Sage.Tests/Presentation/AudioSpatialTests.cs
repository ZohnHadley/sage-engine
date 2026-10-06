#nullable enable
using System;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Doppler, rolloff curves and cones (issue #335): all decided in the mixer, so all headless.
public class AudioSpatialTests
{
    private static RecordId Id(string name) => new("sage", name);

    private static SoundRecord Sound(Rolloff rolloff = Rolloff.Linear, float min = 2f, float far = 42f) => new()
    {
        Variations = { AssetPath.Intern("audio/a.wav") },
        MinDistance = min,
        MaxDistance = far,
        Rolloff = rolloff,
        MaxInstances = 8,
    };

    private static (AudioMixer, Voice) Moving(Vector3 start, Vector3 velocityPerSecond, SoundRecord? record = null, AudioSettings? settings = null)
    {
        var mixer = new AudioMixer(settings);
        mixer.SetListener(Vector3.Zero, Quaternion.Identity);
        var handle = mixer.Play(Id("s"), record ?? Sound(far: 1000f), start, positional: true);
        mixer.Update(0);
        mixer.Move(handle, start + velocityPerSecond);
        mixer.Update(1);
        return (mixer, mixer.Find(handle)!);
    }

    [Xunit.Fact]
    public void AnApproachingEmitterRaisesPitchAndARecedingOneLowersIt()
    {
        var (_, toward) = Moving(new Vector3(0, 0, -100), new Vector3(0, 0, 30));
        var (_, away) = Moving(new Vector3(0, 0, -100), new Vector3(0, 0, -30));
        var (_, still) = Moving(new Vector3(0, 0, -100), Vector3.Zero);

        Assert.True(toward.Pitch > 0.05f, $"approaching: {toward.Pitch}");
        Assert.True(away.Pitch < -0.05f, $"receding: {away.Pitch}");
        Assert.Equal(0f, still.Pitch, 4);
        // 30 m/s at 343 m/s is log2(343/313)
        Assert.Equal(MathF.Log2(343f / 313f), toward.Pitch, 3);
    }

    [Xunit.Fact]
    public void AMovingListenerShiftsAStillEmitter()
    {
        var mixer = new AudioMixer();
        mixer.SetListener(Vector3.Zero, Quaternion.Identity);
        var handle = mixer.Play(Id("s"), Sound(far: 1000f), new Vector3(0, 0, -100), positional: true);
        mixer.Update(0);
        mixer.SetListener(new Vector3(0, 0, -30), Quaternion.Identity);   // driving toward it
        mixer.Update(1);
        Assert.True(mixer.Find(handle)!.Pitch > 0.05f);
        Assert.Equal(30f, mixer.ListenerVelocity.Length(), 2);
    }

    [Xunit.Fact]
    public void ASuppliedVelocityIsUsedInsteadOfTheDerivedOne()
    {
        var mixer = new AudioMixer();
        mixer.SetListener(Vector3.Zero, Quaternion.Identity);
        var handle = mixer.Play(Id("s"), Sound(far: 1000f), new Vector3(0, 0, -100), positional: true);
        mixer.SetVelocity(handle, new Vector3(0, 0, 30));
        mixer.Update(0);
        mixer.Update(1);
        Assert.True(mixer.Find(handle)!.Pitch > 0.05f);
    }

    [Xunit.Fact]
    public void DopplerScaleZeroDisablesItAndAnExtremeSpeedIsClamped()
    {
        var off = new AudioSettings { DopplerScale = 0f };
        var (_, voice) = Moving(new Vector3(0, 0, -100), new Vector3(0, 0, 30), settings: off);
        Assert.Equal(0f, voice.Pitch);

        var perSound = Sound(far: 1000f);
        perSound.Doppler = 0f;
        var (_, immune) = Moving(new Vector3(0, 0, -100), new Vector3(0, 0, 30), perSound);
        Assert.Equal(0f, immune.Pitch);

        var (_, fast) = Moving(new Vector3(0, 0, -100000), new Vector3(0, 0, 90000));   // faster than sound
        Assert.InRange(fast.Pitch, -1f, 1f);
        Assert.Equal(1f, fast.Pitch, 3);
    }

    [Xunit.Fact]
    public void ARebaseIsNotAMovement()
    {
        var (mixer, voice) = Moving(new Vector3(0, 0, -100), Vector3.Zero);
        mixer.Rebase(new Vector3(500, 0, 0));
        mixer.Update(2);
        Assert.Equal(0f, voice.Pitch, 4);
    }

    [Xunit.Theory]
    [Xunit.InlineData(Rolloff.Linear, 22, 0.5)]
    [Xunit.InlineData(Rolloff.Log, 2, 1.0)]
    [Xunit.InlineData(Rolloff.Log, 4, 0.475)]     // (2/4 - 2/42) / (1 - 2/42)
    [Xunit.InlineData(Rolloff.Log, 42, 0.0)]
    [Xunit.InlineData(Rolloff.Custom, 22, 0.8)]    // curve below
    public void EachRolloffCurveShapesTheGain(Rolloff rolloff, float distance, double expected)
    {
        var record = Sound(rolloff);
        record.RolloffCurve.AddRange(new[] { 1f, 0.8f, 0f });   // halfway (22) is the middle sample
        var mixer = new AudioMixer();
        mixer.SetListener(Vector3.Zero, Quaternion.Identity);
        var handle = mixer.Play(Id("s"), record, new Vector3(0, 0, -distance), positional: true);
        Assert.Equal(expected, mixer.Find(handle)!.Gain, 3);
    }

    [Xunit.Fact]
    public void AnEmptyCustomCurveFallsBackToLinear()
    {
        var mixer = new AudioMixer();
        mixer.SetListener(Vector3.Zero, Quaternion.Identity);
        var handle = mixer.Play(Id("s"), Sound(Rolloff.Custom), new Vector3(0, 0, -22), positional: true);
        Assert.Equal(0.5f, mixer.Find(handle)!.Gain, 3);
    }

    [Xunit.Fact]
    public void ACone_QuietensTheListenerOffAxis()
    {
        var record = Sound(far: 100f);
        record.ConeInner = 60f;
        record.ConeOuter = 120f;
        record.ConeOuterGain = 0.2f;
        var mixer = new AudioMixer();
        mixer.SetListener(Vector3.Zero, Quaternion.Identity);

        float At(Vector3 emitterForward)
        {
            // Emitter at -10 on Z, the listener at the origin is toward +Z from it.
            var handle = mixer.Play(Id("s"), record, new Vector3(0, 0, -10), positional: true);
            mixer.Aim(handle, emitterForward);
            return mixer.Find(handle)!.Gain;
        }

        float onAxis = At(Vector3.UnitZ);        // facing the listener
        float behind = At(-Vector3.UnitZ);       // facing away
        float edge = At(Vector3.Normalize(new Vector3(MathF.Sin(MathF.PI / 4), 0, MathF.Cos(MathF.PI / 4))));   // 45 off: between 30 and 60
        Assert.True(behind < onAxis * 0.25f);
        Assert.Equal(onAxis * 0.2f, behind, 4);
        Assert.InRange(edge, behind, onAxis);

        // No direction means no cone.
        var plain = mixer.Play(Id("s"), record, new Vector3(0, 0, -10), positional: true);
        Assert.Equal(onAxis, mixer.Find(plain)!.Gain, 4);
    }

    [Xunit.Fact]
    public void TheRolloffIsASoundRecordFieldThatLoadsFromJson()
    {
        var record = System.Text.Json.JsonSerializer.Deserialize<SoundRecord>(
            "{\"rolloff\":\"Custom\",\"rolloffCurve\":[1,0.5,0],\"coneInner\":90}",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
            { IncludeFields = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        Assert.NotNull(record);
        Assert.Equal(Rolloff.Custom, record!.Rolloff);
        Assert.Equal(3, record.RolloffCurve.Count);
        Assert.Equal(90f, record.ConeInner);
    }
}
