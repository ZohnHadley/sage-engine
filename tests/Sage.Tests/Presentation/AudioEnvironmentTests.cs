#nullable enable
using System;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Occlusion and reverb zones (issue #329): what the room does to a sound, decided headlessly by the
// mixer's `AudioEnvironment` and handed to the backend as numbers — a gain, a low-pass and a reverb blend.
public class AudioEnvironmentTests
{
    public AudioEnvironmentTests() { _ = TestEnv.UserRoot; }

    private static readonly RecordId Fire = new("sage", "fire");

    private static SoundRecord Sound() => new()
    {
        Variations = { AssetPath.Intern("audio/fire.wav") },
        MinDistance = 1f,
        MaxDistance = 40f,
        MaxInstances = 64,
    };

    // Quake units, 32 to the metre; map (x, y, z) is engine (x, z, -y). A floor, and a wall from map
    // x -64..64, y 32..96, z 0..128: engine x -2..2, height 0..4, z -1..-3.
    internal const string WallMap = """
        {
        "classname" "worldspawn"
        {
        ( -640 -640 -32 ) ( -640 -639 -32 ) ( -640 -640 -31 ) floor 0 0 0 1 1
        ( -640 -640 -32 ) ( -640 -640 -31 ) ( -639 -640 -32 ) floor 0 0 0 1 1
        ( -640 -640 -32 ) ( -639 -640 -32 ) ( -640 -639 -32 ) floor 0 0 0 1 1
        ( 640 640 0 ) ( 640 641 0 ) ( 641 640 0 ) floor 0 0 0 1 1
        ( 640 640 0 ) ( 641 640 0 ) ( 640 640 1 ) floor 0 0 0 1 1
        ( 640 640 0 ) ( 640 640 1 ) ( 640 641 0 ) floor 0 0 0 1 1
        }
        {
        ( -64 32 0 ) ( -64 33 0 ) ( -64 32 1 ) wall 0 0 0 1 1
        ( -64 32 0 ) ( -64 32 1 ) ( -63 32 0 ) wall 0 0 0 1 1
        ( -64 32 0 ) ( -63 32 0 ) ( -64 33 0 ) wall 0 0 0 1 1
        ( 64 96 128 ) ( 64 97 128 ) ( 65 96 128 ) wall 0 0 0 1 1
        ( 64 96 128 ) ( 65 96 128 ) ( 64 96 129 ) wall 0 0 0 1 1
        ( 64 96 128 ) ( 64 96 129 ) ( 64 97 128 ) wall 0 0 0 1 1
        }
        }
        {
        "classname" "echo"
        "trigger" "1"
        {
        ( 320 -128 0 ) ( 320 -127 0 ) ( 320 -128 1 ) clip 0 0 0 1 1
        ( 320 -128 0 ) ( 320 -128 1 ) ( 321 -128 0 ) clip 0 0 0 1 1
        ( 320 -128 0 ) ( 321 -128 0 ) ( 320 -127 0 ) clip 0 0 0 1 1
        ( 576 128 128 ) ( 576 129 128 ) ( 577 128 128 ) clip 0 0 0 1 1
        ( 576 128 128 ) ( 577 128 128 ) ( 576 128 129 ) clip 0 0 0 1 1
        ( 576 128 128 ) ( 576 128 129 ) ( 576 129 128 ) clip 0 0 0 1 1
        }
        }
        """;

    // A world from the map above: the wall, and an `echo` brush zone (map x 320..576, y -128..128, z 0..128:
    // engine x 10..18, z -4..4, height 0..4) with the engine's cave.
    private static (Engine engine, World world) Level()
    {
        var engine = HeadlessApp.Gameplay().WithEngineContent()
            .With(new MapModule())
            .File("maps/wall.map", WallMap, "sandbox")
            .File("data/level.json", """
                [
                  { "type": "map", "id": "wall", "file": "maps/wall.map" },
                  { "type": "prefab", "id": "echo", "name": "echo", "parts": { "reverb_zone": { "reverb": "sage:cave", "fade": 1 } } }
                ]
                """, "sandbox")
            .Build().Engine;
        var world = engine.CreateWorld("level");
        Assert.NotNull(MapLoader.Load(world, new RecordId("sandbox", "wall")));
        world.RunFixed(1f / 60f);   // the statics are in the space
        return (engine, world);
    }

    private static AudioMixer Mixer(World world, AudioSettings? settings = null)
    {
        settings ??= new AudioSettings();
        var mixer = new AudioMixer(settings);
        mixer.Environment = new AudioEnvironment(world, settings);
        return mixer;
    }

    // The issue's exit: a wall brush between the listener and a fire makes it quieter and duller than
    // the same fire at the same distance in the open.
    [Fact]
    public void AWallBrushBetweenListenerAndEmitterLowersTheGain()
    {
        var (engine, world) = Level();
        using var _ = engine;
        var mixer = Mixer(world);
        mixer.SetListener(new Vector3(0, 1.5f, 2f), Quaternion.Identity);
        mixer.Update(0.0);

        var behind = mixer.Find(mixer.Play(Fire, Sound(), new Vector3(0, 1.5f, -6f), positional: true))!;   // through the wall, 8 m
        var open = mixer.Find(mixer.Play(Fire, Sound(), new Vector3(0, 1.5f, 10f), positional: true))!;     // the open side, 8 m
        mixer.Update(0.016);

        Assert.Equal(1f, behind.Occlusion);
        Assert.Equal(0f, open.Occlusion);
        Assert.True(behind.Gain < open.Gain, $"behind the wall {behind.Gain:F3} should be quieter than in the open {open.Gain:F3}");
        Assert.True(behind.LowPass < 1f, "behind the wall should be filtered");
        Assert.Equal(1f, open.LowPass);
        Assert.Equal(open.Gain * mixer.Environment!.OccludedGain, behind.Gain, 3);
    }

    // The same without any physics: nothing to ask, nothing occluded, and the gain is the mixer's own.
    [Fact]
    public void WithoutPhysicsNothingIsOccluded()
    {
        var settings = new AudioSettings();
        var plain = new AudioMixer(settings);
        using var engine = HeadlessApp.Simulation().Build().Engine;
        var world = engine.CreateWorld("bare");
        var mixer = Mixer(world, settings);
        foreach (var m in new[] { plain, mixer }) { m.SetListener(Vector3.Zero, Quaternion.Identity); m.Update(0); }

        var a = plain.Find(plain.Play(Fire, Sound(), new Vector3(0, 0, -10), true))!;
        var b = mixer.Find(mixer.Play(Fire, Sound(), new Vector3(0, 0, -10), true))!;
        mixer.Update(0.1);
        plain.Update(0.1);
        Assert.Equal(a.Gain, b.Gain);
        Assert.Equal(1f, b.LowPass);
    }

    // At most `OcclusionRays` rays per update however many voices play, and round-robin reaches them all.
    [Fact]
    public void TheRayBudgetIsRespectedAndEveryVoiceIsReached()
    {
        var (engine, world) = Level();
        using var _ = engine;
        var settings = new AudioSettings { MaxVoices = 64 };
        settings.OcclusionRays = 3;
        var mixer = Mixer(world, settings);
        mixer.SetListener(new Vector3(0, 1.5f, 2f), Quaternion.Identity);
        mixer.Update(0.0);

        var voices = Enumerable.Range(0, 10)
            .Select(i => mixer.Find(mixer.Play(Fire, Sound(), new Vector3(i - 5, 1.5f, -6f), positional: true))!)
            .ToList();
        Assert.Equal(3, mixer.Environment!.RaysLastUpdate);   // the plays shared this update's budget
        Assert.Equal(3, voices.Count(v => v.OcclusionProbed));

        double now = 0;
        for (int i = 0; i < 6; i++)
        {
            mixer.Update(now += 0.016);
            Assert.InRange(mixer.Environment.RaysLastUpdate, 0, 3);
        }
        Assert.All(voices, v => Assert.True(v.OcclusionProbed));

        // And it keeps going round: 6 updates of 3 rays after the first 3 is 21 rays for 10 voices.
        Assert.Equal(21, mixer.Environment.RaysTotal);

        // Off is off: no rays, and every voice clears.
        settings.OcclusionRays = 0;
        for (int i = 0; i < 30; i++) mixer.Update(now += 0.016);
        Assert.Equal(0, mixer.Environment.RaysLastUpdate);
        Assert.All(voices, v => Assert.Equal(0f, v.Occlusion));
    }

    // A voice moving behind the wall does not click to silence: its occlusion moves over `Smoothing`.
    [Fact]
    public void OcclusionIsSmoothed()
    {
        var (engine, world) = Level();
        using var _ = engine;
        var mixer = Mixer(world);
        mixer.Environment!.Smoothing = 0.2f;
        mixer.SetListener(new Vector3(0, 1.5f, 2f), Quaternion.Identity);
        mixer.Update(0.0);

        var handle = mixer.Play(Fire, Sound(), new Vector3(6f, 1.5f, -6f), positional: true);   // past the wall's end: clear
        var voice = mixer.Find(handle)!;
        mixer.Update(0.05);
        Assert.Equal(0f, voice.Occlusion);
        float clear = voice.Gain;

        mixer.Move(handle, new Vector3(0, 1.5f, -8f));   // behind it now, as far away (10 m)
        mixer.Update(0.10);
        Assert.Equal(1f, voice.OcclusionTarget);
        Assert.Equal(0.25f, voice.Occlusion, 3);         // 0.05 s of 0.2
        Assert.True(voice.Gain < clear && voice.Gain > clear * mixer.Environment.OccludedGain);

        mixer.Update(0.20);
        mixer.Update(0.30);
        Assert.Equal(1f, voice.Occlusion);
        Assert.Equal(clear * mixer.Environment.OccludedGain, voice.Gain, 3);
    }

    // A sound that starts behind the wall starts muffled, rather than fading down from full volume.
    [Fact]
    public void ASoundStartingBehindAWallStartsMuffled()
    {
        var (engine, world) = Level();
        using var _ = engine;
        var mixer = Mixer(world);
        mixer.SetListener(new Vector3(0, 1.5f, 2f), Quaternion.Identity);
        mixer.Update(0.0);
        var voice = mixer.Find(mixer.Play(Fire, Sound(), new Vector3(0, 1.5f, -6f), positional: true))!;
        Assert.Equal(1f, voice.Occlusion);
        Assert.True(voice.LowPass < 1f);
    }

    // A map brush entity whose prefab has a `reverb_zone` is a zone the size of its brushes; walking into
    // it blends the reverb in over the zone's fade, and walking out blends it away.
    [Fact]
    public void EnteringAReverbZoneBlendsItsPresetIn()
    {
        var (engine, world) = Level();
        using var _ = engine;

        var zone = Assert.Single(world.Query<ReverbZone>().Entities.ToEntityList());
        var box = world.Get<ReverbZone>(zone);
        Assert.Equal(new Vector3(8, 4, 8), box.Size);
        Assert.True(engine.Records.TryGet(new RecordId("sage", "cave"), out ReverbRecord cave));

        var mixer = Mixer(world);
        mixer.SetListener(new Vector3(0, 1.5f, 6f), Quaternion.Identity);   // outside
        mixer.Update(0.0);
        Assert.True(mixer.Environment!.Zone.IsEmpty);
        Assert.Equal(0f, mixer.Environment.Reverb.Mix);

        mixer.SetListener(new Vector3(14, 1.5f, 0), Quaternion.Identity);   // inside, for half a second
        double now = Run(mixer, 0.0, 0.5);
        Assert.Equal(new RecordId("sage", "cave"), mixer.Environment.Zone);
        Assert.Equal(0.5f, mixer.Environment.Blend, 3);
        Assert.Equal(cave.Mix * 0.5f, mixer.Environment.Reverb.Mix, 3);           // half way over a 1 s fade
        Assert.InRange(mixer.Environment.Reverb.DecayTime, new ReverbRecord().DecayTime, cave.DecayTime);
        now = Run(mixer, now, 0.6);
        Assert.Equal(1f, mixer.Environment.Blend);
        Assert.Equal(cave.Mix, mixer.Environment.Reverb.Mix, 3);
        Assert.Equal(cave.DecayTime, mixer.Environment.Reverb.DecayTime, 3);

        mixer.SetListener(new Vector3(0, 1.5f, 6f), Quaternion.Identity);   // out again, for half a second
        now = Run(mixer, now, 0.5);
        Assert.True(mixer.Environment.Zone.IsEmpty);
        Assert.Equal(cave.Mix * 0.5f, mixer.Environment.Reverb.Mix, 3);
        Assert.Equal(cave.DecayTime, mixer.Environment.Reverb.DecayTime, 3);     // the tail keeps its character
        Run(mixer, now, 0.6);
        Assert.Equal(0f, mixer.Environment.Reverb.Mix);
    }

    // Frames of 50 ms from `from` for `seconds`; returns the time reached.
    private static double Run(AudioMixer mixer, double from, double seconds)
    {
        int frames = (int)Math.Round(seconds / 0.05);
        for (int i = 1; i <= frames; i++) mixer.Update(from + i * 0.05);
        return from + frames * 0.05;
    }

    // Where zones overlap, the higher priority wins, then the smaller box: a crypt inside a cave level.
    [Fact]
    public void OverlappingZonesPickByPriorityThenSize()
    {
        using var engine = HeadlessApp.Simulation().WithEngineContent().File("data/zones.json", """
            [
              { "type": "prefab", "id": "big", "name": "big", "parts": { "reverb_zone": { "reverb": "sage:outdoors", "size": [100, 50, 100] } } },
              { "type": "prefab", "id": "small", "name": "small", "parts": { "reverb_zone": { "reverb": "sage:room", "size": [10, 5, 10] } } },
              { "type": "prefab", "id": "loud", "name": "loud", "parts": { "reverb_zone": { "reverb": "sage:hall", "size": [100, 50, 100], "priority": 1, "offset": [200, 0, 0] } } }
            ]
            """).Build().Engine;
        var world = engine.CreateWorld("zones");
        world.Spawn(new RecordId("sage", "big"), Vector3.Zero);
        world.Spawn(new RecordId("sage", "small"), Vector3.Zero);
        world.Spawn(new RecordId("sage", "loud"), new Vector3(-200, 0, 0));   // offset back over the others
        var mixer = Mixer(world);

        mixer.SetListener(new Vector3(30, 0, 0), Quaternion.Identity);
        mixer.Update(0);
        Assert.Equal(new RecordId("sage", "hall"), mixer.Environment!.Zone);   // priority beats both

        world.Destroy(world.Query<ReverbZone>().Entities.ToEntityList().Single(e => world.Get<ReverbZone>(e).Priority == 1));
        mixer.Update(0.1);
        Assert.Equal(new RecordId("sage", "outdoors"), mixer.Environment.Zone);   // only the big one here
        mixer.SetListener(new Vector3(3, 0, 0), Quaternion.Identity);
        mixer.Update(0.2);
        Assert.Equal(new RecordId("sage", "room"), mixer.Environment.Zone);       // inside both: the smaller
    }

    // The four engine presets load, and a zone naming something that is not a reverb plays dry.
    [Fact]
    public void TheEnginePresetsLoadAndAnUnknownOnePlaysDry()
    {
        using var engine = HeadlessApp.Simulation().WithEngineContent().File("data/zones.json", """
            [ { "type": "prefab", "id": "odd", "name": "odd", "parts": { "reverb_zone": { "reverb": "sage:room", "size": [10, 5, 10] } } } ]
            """).Build().Engine;
        foreach (var name in new[] { "room", "hall", "cave", "outdoors" })
            Assert.True(engine.Records.TryGet(new RecordId("sage", name), out ReverbRecord _), name);

        var world = engine.CreateWorld("zones");
        var odd = world.Spawn(new RecordId("sage", "odd"), Vector3.Zero);
        world.Get<ReverbZone>(odd).Reverb = new RecordId("sage", "nowhere");
        var mixer = Mixer(world);
        mixer.SetListener(Vector3.Zero, Quaternion.Identity);
        mixer.Update(0);
        mixer.Update(2);
        Assert.Equal(new RecordId("sage", "nowhere"), mixer.Environment!.Zone);
        Assert.Equal(0f, mixer.Environment.Reverb.Mix);
    }
}

// What `log_level audio trace` says (issue #329's "snd_ trace shows the occlusion factor"). Process-wide,
// because it turns the audio category's level down for everybody.
[Xunit.Collection(ProcessWideStateCollection.Name)]
public class AudioEnvironmentTraceTests
{
    public AudioEnvironmentTraceTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void TheTraceNamesTheOcclusionFactorAndTheZone()
    {
        var engine = HeadlessApp.Gameplay().WithEngineContent()
            .With(new MapModule())
            .File("maps/wall.map", AudioEnvironmentTests.WallMap, "sandbox")
            .File("data/level.json", """
                [
                  { "type": "map", "id": "wall", "file": "maps/wall.map" },
                  { "type": "prefab", "id": "echo", "name": "echo", "parts": { "reverb_zone": { "reverb": "sage:cave" } } }
                ]
                """, "sandbox")
            .Build().Engine;
        using var _ = engine;
        var world = engine.CreateWorld("level");
        Assert.NotNull(MapLoader.Load(world, new RecordId("sandbox", "wall")));
        world.RunFixed(1f / 60f);

        var was = LogCat.Audio.MinLevel;
        LogCat.Audio.MinLevel = LogLevel.Trace;
        try
        {
            using var log = new CaptureSink();
            var settings = new AudioSettings();
            var mixer = new AudioMixer(settings) { Environment = new AudioEnvironment(world, settings) };
            var sound = new RecordId("sage", "trace" + Guid.NewGuid().ToString("N"));
            mixer.SetListener(new Vector3(0, 1.5f, 2f), Quaternion.Identity);
            mixer.Update(0);
            mixer.Play(sound, new SoundRecord { Variations = { AssetPath.Intern("audio/fire.wav") }, MinDistance = 1, MaxDistance = 40 },
                       new Vector3(0, 1.5f, -6f), positional: true);
            mixer.SetListener(new Vector3(14, 1.5f, 0), Quaternion.Identity);
            mixer.Update(0.1);

            var lines = log.Entries.Where(e => e.Category == LogCat.Audio).Select(e => e.Message).ToList();
            Assert.Contains(lines, l => l.Contains($"play {sound}") && l.Contains("occl 1.00"));
            Assert.Contains(lines, l => l.Contains($"occlusion {sound} blocked"));
            Assert.Contains(lines, l => l.StartsWith("reverb sage:cave"));
        }
        finally
        {
            LogCat.Audio.MinLevel = was;
        }
    }
}
