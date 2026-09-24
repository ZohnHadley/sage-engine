#nullable enable
using System;
using System.Linq;
using System.Numerics;
using sage_engine;

namespace sage_engine.Tests;

using Assert = Xunit.Assert;

// What plays, how loud, and what is dropped (docs/design/11 §3, TODO F4).
//
// Every decision audio makes is here rather than in the client, so all of it is testable without a
// sound card: attenuation, panning, bus volumes, per-sound limits, cooldowns and voice stealing. What
// the client owns is `SoundEffectInstance`, which has nothing to decide.
public class AudioTests
{
    private static SoundRecord Sound(string file = "audio/a.wav", float volume = 1f, int max = 4,
                                     float cooldown = 0f, float min = 3f, float far = 40f, int priority = 0,
                                     AudioBus bus = AudioBus.Sfx) =>
        new()
        {
            Variations = { AssetPath.Intern(file) },
            Volume = volume,
            MaxInstances = max,
            Cooldown = cooldown,
            MinDistance = min,
            MaxDistance = far,
            Priority = priority,
            Bus = bus,
        };

    private static RecordId Id(string name) => new("sage", name);

    // Close is loud, far is silent, and in between is a straight line — a curve two numbers describe
    // and a designer can predict.
    [Xunit.Theory]
    [Xunit.InlineData(0, 1.0)]
    [Xunit.InlineData(3, 1.0)]      // at MinDistance
    [Xunit.InlineData(21.5, 0.5)]   // halfway between 3 and 40
    [Xunit.InlineData(40, 0.0)]     // at MaxDistance
    [Xunit.InlineData(100, 0.0)]
    public void DistanceSetsTheGain(float distance, double expected)
    {
        var mixer = new AudioMixer();
        mixer.SetListener(Vector3.Zero, Quaternion.Identity);

        var handle = mixer.Play(Id("s"), Sound(), new Vector3(0, 0, -distance), positional: true);

        Assert.True(handle.IsValid);
        Assert.Equal(expected, mixer.Find(handle)!.Gain, 2);
    }

    // Pan follows the listener's own right, not the world's: turning your head moves the sound.
    [Xunit.Fact]
    public void PanFollowsWhereTheListenerIsFacing()
    {
        var mixer = new AudioMixer();
        mixer.SetListener(Vector3.Zero, Quaternion.Identity);          // facing -Z

        var right = mixer.Play(Id("r"), Sound(min: 100, far: 200), new Vector3(5, 0, 0), true);
        Assert.Equal(1.0, mixer.Find(right)!.Pan, 2);

        var ahead = mixer.Play(Id("a"), Sound(min: 100, far: 200), new Vector3(0, 0, -5), true);
        Assert.Equal(0.0, mixer.Find(ahead)!.Pan, 2);

        // Turn a quarter turn left (positive yaw): what was on the right is now ahead.
        mixer.SetListener(Vector3.Zero, SageMath.RotationFromYaw(MathF.PI / 2));
        mixer.Update(1.0);
        Assert.Equal(0.0, mixer.Find(right)!.Pan, 2);
    }

    // A 2D sound is about you, not about a place: full volume, no pan, wherever it was "played".
    [Xunit.Fact]
    public void ATwoDimensionalSoundIgnoresTheWorld()
    {
        var mixer = new AudioMixer();
        mixer.SetListener(Vector3.Zero, Quaternion.Identity);

        var handle = mixer.Play(Id("ui"), Sound(far: 0f, bus: AudioBus.Ui), new Vector3(500, 0, 500), positional: true);

        var voice = mixer.Find(handle)!;
        Assert.False(voice.Positional);
        Assert.Equal(1.0, voice.Gain, 2);
        Assert.Equal(0.0, voice.Pan, 2);
    }

    // Buses multiply, and master multiplies everything: a volume slider is one number in one place.
    [Xunit.Fact]
    public void BusesMultiplyAndMasterCoversThemAll()
    {
        var mixer = new AudioMixer();
        mixer.SetListener(Vector3.Zero, Quaternion.Identity);
        mixer.SetBusVolume(AudioBus.Sfx, 0.5f);
        mixer.SetBusVolume(AudioBus.Master, 0.5f);

        var handle = mixer.Play(Id("s"), Sound(far: 0f), Vector3.Zero, false);
        Assert.Equal(0.25, mixer.Find(handle)!.Gain, 3);

        mixer.SetBusVolume(AudioBus.Master, 0f);
        mixer.Update(1.0);
        Assert.Equal(0.0, mixer.Find(handle)!.Gain, 3);
    }

    // A cooldown is what stops two hits in the same tick sounding like one hit with a flanger.
    [Xunit.Fact]
    public void ACooldownRefusesTheSameSoundTwiceAtOnce()
    {
        var mixer = new AudioMixer();
        var sound = Sound(cooldown: 0.1f);

        mixer.Update(10.0);
        Assert.True(mixer.Play(Id("hit"), sound, Vector3.Zero, false).IsValid);
        Assert.False(mixer.Play(Id("hit"), sound, Vector3.Zero, false).IsValid);
        Assert.Equal(1, mixer.Refused);

        mixer.Update(10.2);
        Assert.True(mixer.Play(Id("hit"), sound, Vector3.Zero, false).IsValid);
    }

    // Five footsteps with a cap of four: the oldest goes, because it is the one furthest through its
    // own noise and least likely to be missed.
    [Xunit.Fact]
    public void TooManyOfOneSoundStealsTheOldest()
    {
        var mixer = new AudioMixer();
        var sound = Sound(max: 4);

        var first = mixer.Play(Id("step"), sound, Vector3.Zero, false);
        for (int i = 0; i < 3; i++) { mixer.Update(i * 0.01); mixer.Play(Id("step"), sound, Vector3.Zero, false); }

        mixer.Update(1.0);
        Assert.True(mixer.Play(Id("step"), sound, Vector3.Zero, false).IsValid);

        Assert.True(mixer.Find(first)!.Stopping, "the oldest one should have been taken");
        Assert.Equal(1, mixer.Stolen);
    }

    // At the global cap the quietest goes — but never something more important than the newcomer, so
    // a death rattle is not dropped for a footstep standing closer.
    [Xunit.Fact]
    public void AtTheCapTheQuietestGoesUnlessItMattersMore()
    {
        var mixer = new AudioMixer { MaxVoices = 2 };
        mixer.SetListener(Vector3.Zero, Quaternion.Identity);

        var near = mixer.Play(Id("a"), Sound(max: 1), new Vector3(0, 0, -3), true);     // gain 1.0
        var far = mixer.Play(Id("b"), Sound(max: 1), new Vector3(0, 0, -30), true);     // quieter
        Assert.True(mixer.Find(far)!.Gain < mixer.Find(near)!.Gain);

        Assert.True(mixer.Play(Id("c"), Sound(max: 1), Vector3.Zero, false).IsValid);
        Assert.True(mixer.Find(far)!.Stopping, "the quietest voice is the one stolen");
        Assert.False(mixer.Find(near)!.Stopping);

        // Now everything left outranks the newcomer, so nothing is stolen and it is refused instead.
        var mixer2 = new AudioMixer { MaxVoices = 1 };
        mixer2.Play(Id("important"), Sound(priority: 5), Vector3.Zero, false);
        Assert.False(mixer2.Play(Id("trivial"), Sound(priority: 0), Vector3.Zero, false).IsValid);
        Assert.Equal(1, mixer2.Refused);
    }

    // A sound record with no files is a content mistake, not a crash.
    [Xunit.Fact]
    public void NothingToPlayIsRefusedQuietly()
    {
        var mixer = new AudioMixer();

        Assert.False(mixer.Play(Id("empty"), new SoundRecord(), Vector3.Zero, false).IsValid);
        Assert.False(mixer.Play(Id("missing"), null, Vector3.Zero, false).IsValid);
        Assert.Equal(2, mixer.Refused);
        Assert.Equal(0, mixer.Playing);
    }

    // One of several files, so a flurry of blows is not one sample on repeat.
    [Xunit.Fact]
    public void VariationsArePickedBetween()
    {
        var mixer = new AudioMixer { MaxVoices = 64 };
        var sound = Sound(max: 64);
        sound.Variations.Add(AssetPath.Intern("audio/b.wav"));
        sound.Variations.Add(AssetPath.Intern("audio/c.wav"));

        var seen = new System.Collections.Generic.HashSet<AssetPath>();
        for (int i = 0; i < 40; i++)
        {
            var handle = mixer.Play(Id("hit"), sound, Vector3.Zero, false);
            if (handle.IsValid) seen.Add(mixer.Find(handle)!.Asset);
        }

        Assert.True(seen.Count > 1, "forty plays of a three-variation sound used one file every time");
    }

    // Sounds live in origin space like everything else, so a rebase moves them with the world (R6):
    // a waterfall a kilometre behind where it is pouring would be audible in the wrong place.
    [Xunit.Fact]
    public void SoundsMoveWithAnOriginRebase()
    {
        var mixer = new AudioMixer();
        mixer.SetListener(new Vector3(10, 0, 0), Quaternion.Identity);
        var handle = mixer.Play(Id("falls"), Sound(), new Vector3(20, 0, 0), positional: true, loop: true);
        float before = mixer.Find(handle)!.Gain;

        var offset = new Vector3(-2048, 0, 0);
        mixer.Rebase(offset);
        mixer.Update(1.0);

        Assert.Equal(new Vector3(-2028, 0, 0), mixer.Find(handle)!.Position);
        Assert.Equal(new Vector3(-2038, 0, 0), mixer.ListenerPosition);
        Assert.Equal(before, mixer.Find(handle)!.Gain, 3);      // the same distance, so the same sound
    }

    // Stopping is a request the backend carries out; the mixer only drops a voice when told it ended.
    [Xunit.Fact]
    public void StoppingIsRequestedThenConfirmed()
    {
        var mixer = new AudioMixer();
        var handle = mixer.Play(Id("s"), Sound(), Vector3.Zero, false);

        mixer.Stop(handle);
        Assert.True(mixer.Find(handle)!.Stopping);
        Assert.Equal(1, mixer.Playing);          // still there: the backend has not confirmed yet

        mixer.Remove(handle);
        Assert.Null(mixer.Find(handle));
        Assert.Equal(0, mixer.Playing);
    }
}
