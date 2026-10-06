#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sandbox;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Music (issue #325, docs/design/11 §3): a `music` record, the world's `Music` resource that says which track
// plays and which it is fading from, the data that changes it (inputs, actions, a scene's environment), and
// the MusicPlayer that makes it into streamed voices — crossfaded, layered, outside the voice cap and never
// stolen. All headless: the client's audio system only calls the player once a frame.
public class MusicTests
{
    public MusicTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    private static string AudioFolder => Path.Combine(TestEnv.FolderAbove("Sage.sln"), "tests", "Sage.Tests", "Content", "Audio");
    private static string Tone(string name) => Path.Combine(AudioFolder, name);
    private static RecordId Game(string name) => new("game", name);

    private const string Content = """
        [ { "type": "music", "id": "a", "track": "music/a.ogg", "fadeIn": 1, "fadeOut": 0.5,
            "layers": [ { "asset": "music/a_drums.ogg", "intensity": 0.5, "fade": 0.5 } ] },
          { "type": "music", "id": "b", "track": "music/b.ogg", "fadeIn": 2, "volume": 0.8 },
          { "type": "music", "id": "c", "track": "music/b.ogg", "fadeIn": 1 },
          { "type": "gate_rule", "id": "to_b", "then": [ { "play_music": "game:b", "fade": 1 } ] },
          { "type": "gate_rule", "id": "quiet", "then": [ { "stop_music": {} } ] },
          { "type": "gate_rule", "id": "tense", "then": [ { "music_intensity": 0.8 } ] },
          { "type": "scene", "id": "field", "environment": { "music": "game:a" } },
          { "type": "scene", "id": "cave", "environment": { "music": "game:b" } },
          { "type": "scene", "id": "hall" } ]
        """;

    private static HeadlessApp Boot(string content = Content)
    {
        var files = new MountFixture();
        files.Write("game", "data/music.json", content);
        Directory.CreateDirectory(Path.Combine(files.Dir("game"), "music"));
        File.Copy(Tone("tone_stereo.ogg"), Path.Combine(files.Dir("game"), "music", "a.ogg"));
        File.Copy(Tone("tone_stereo.ogg"), Path.Combine(files.Dir("game"), "music", "a_drums.ogg"));
        File.Copy(Tone("tone_mono.ogg"), Path.Combine(files.Dir("game"), "music", "b.ogg"));
        files.Mount("game", "game");
        var app = HeadlessApp.Gameplay().With(new LogicTestPlugin()).Mount(files)
            .OnRegistered(a => a.Records.Register<MusicRecord>())   // the client's record type
            .Boot();
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        return app;
    }

    private static void Do(HeadlessApp app, string rule) =>
        Conditions.Run(app.World, default, app.Records.Get<GateRule>(Game(rule)).Then);

    // One tick of the simulation (the fade moves) and one frame of the player (the voices follow).
    private static void Step(HeadlessApp app, MusicPlayer player, AudioMixer mixer, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++)
        {
            app.World.RunFixed(Dt);
            player.Update(MusicRules.Of(app.World), app.Records, mixer, Dt);
        }
    }

    private static Voice VoiceOf(MusicPlayer player, AudioMixer mixer, RecordId track, int stem = 0)
    {
        var deck = player.DeckOf(track);
        Assert.True(deck != null, $"no deck for {track}");
        var voice = mixer.Find(deck!.Voices[stem]);
        Assert.True(voice != null, $"{track}'s stem {stem} has no voice");
        return voice!;
    }

    // The done criterion: two tracks crossfade across ticks. `PlayMusic` on a wire switches to `b`, which
    // comes in over its own fadeIn (2 s) while `a` goes out: equal power all the way, half-way at one second,
    // and then `a`'s voices stop and only `b` is left. At no point are there more than two decks.
    [Fact]
    public void TwoTracksCrossfadeAcrossTicks_AndTheOneGoingOutStops()
    {
        using var app = Boot();
        var world = app.World;
        var mixer = new AudioMixer();
        var player = new MusicPlayer();
        var wire = world.Create(Transform.At(default), "jukebox");

        world.IO().FireInput(wire, MusicRules.PlayInput, "game:a 0");
        Step(app, player, mixer, 2);
        var music = MusicRules.Of(world);
        Assert.Equal((Game("a"), true), (music.Track, music.Settled));
        var a = VoiceOf(player, mixer, Game("a"));
        var drums = VoiceOf(player, mixer, Game("a"), stem: 1);
        Assert.Equal(1f, a.Gain, 3);
        Assert.Equal(0f, drums.Gain, 3);                                  // intensity 0: the drums are silent
        Assert.True(a.Music && a.Loop && !a.Positional);
        Assert.Equal((AudioBus.Music, true), (a.Record!.Bus, a.Record.Stream));
        Assert.True(PcmStreamer.Streams(a.Record, a.Asset));              // the backend streams it

        world.IO().FireInput(wire, MusicRules.PlayInput, "b");             // its own fadeIn: 2 s
        var falling = new List<float>();
        var rising = new List<float>();
        for (int tick = 0; tick < 130; tick++)
        {
            Step(app, player, mixer);
            Assert.InRange(player.Decks.Count, 1, 2);
            float ga = player.DeckOf(Game("a")) is { } deck && mixer.Find(deck.Voices[0]) is { Stopping: false } va ? va.Gain : 0f;
            float gb = VoiceOf(player, mixer, Game("b")).Gain / 0.8f;     // b's volume is 0.8
            falling.Add(ga);
            rising.Add(gb);
            Assert.InRange(ga * ga + gb * gb, 0.98f, 1.02f);              // equal power: as loud mid-change
        }

        for (int i = 1; i < falling.Count; i++)
        {
            Assert.True(falling[i] <= falling[i - 1] + 1e-5f, $"a rose at tick {i}");
            Assert.True(rising[i] >= rising[i - 1] - 1e-5f, $"b fell at tick {i}");
        }
        Assert.True(falling[5] > 0.9f && rising[5] < 0.3f, "the change is gradual, not a cut");
        Assert.InRange(falling[60], 0.65f, 0.76f);                        // half-way at one second: ~0.707 each
        Assert.InRange(rising[60], 0.65f, 0.76f);
        Assert.Equal(0f, falling[^1]);
        Assert.Equal(1f, rising[^1], 3);

        Assert.True(music.Settled);
        Assert.Equal((Game("b"), default(RecordId)), (music.Track, music.Previous));
        Assert.Single(player.Decks);
        Assert.True(a.Stopping && drums.Stopping);                        // the backend stops and drops them
        Assert.Equal(0.8f, VoiceOf(player, mixer, Game("b")).Gain, 3);
    }

    // Music is a second mixer path (11 §13): its voices do not count against the cap, and a crowd of sound
    // effects never steals them, even a silent layer that is the quietest voice there is.
    [Fact]
    public void MusicVoicesAreOutsideTheCap_AndNeverStolen()
    {
        using var app = Boot();
        var mixer = new AudioMixer { MaxVoices = 2 };
        var player = new MusicPlayer();
        MusicRules.Play(app.World, Game("a"), 0f);
        Step(app, player, mixer, 2);
        var music = player.DeckOf(Game("a"))!.Voices.Select(mixer.Find).ToList();
        Assert.Equal(2, music.Count);
        Assert.Equal(0f, music[1]!.Gain);                                  // the silent drum layer

        var hit = new SoundRecord { Variations = { AssetPath.Intern("audio/hit.wav") }, MaxInstances = 10, MaxDistance = 0f };
        int played = 0;
        for (int i = 0; i < 6; i++)
            if (mixer.Play(new RecordId("game", $"hit{i}"), hit, default, positional: false, volume: 0.1f + i * 0.1f).IsValid) played++;
        Assert.Equal(6, played);                                           // each newcomer stole an effect, not the music
        Assert.Equal(4, mixer.Stolen);
        Assert.All(music, v => Assert.False(v!.Stopping));
        Assert.Equal(2 + 6, mixer.Playing);
        Assert.Equal(2, mixer.Voices.Count(v => !v.Music && !v.Stopping));
    }

    // Layers (stems) play in step with the track and come in or go out over their own fade as the world's
    // intensity crosses theirs: `music_intensity` in a `then`, and the `SetMusicIntensity` input.
    [Fact]
    public void LayersFollowTheIntensity_OverTheirOwnFade()
    {
        using var app = Boot();
        var world = app.World;
        var mixer = new AudioMixer();
        var player = new MusicPlayer();
        MusicRules.Play(world, Game("a"), 0f);
        Step(app, player, mixer, 2);
        var drums = VoiceOf(player, mixer, Game("a"), stem: 1);
        Assert.Equal(0f, drums.Gain);

        Do(app, "tense");
        Assert.Equal(0.8f, MusicRules.Of(world).Intensity);
        Step(app, player, mixer, 15);                                      // a quarter of a second of 0.5
        Assert.InRange(drums.Gain, 0.45f, 0.55f);
        Step(app, player, mixer, 20);
        Assert.Equal(1f, drums.Gain, 3);
        Assert.Equal(1f, VoiceOf(player, mixer, Game("a")).Gain, 3);       // the track is untouched

        var wire = world.Create(Transform.At(default), "mood");
        world.IO().FireInput(wire, MusicRules.IntensityInput, "0.2");
        Step(app, player, mixer, 40);
        Assert.Equal(0.2f, MusicRules.Of(world).Intensity, 3);
        Assert.Equal(0f, drums.Gain, 3);
    }

    // The words: `play_music` with a fade of its own, `stop_music` over the track's fadeOut, and the
    // `StopMusic` input with seconds.
    [Fact]
    public void TheActionsAndInputs_PlayAndStopOverTheirFades()
    {
        using var app = Boot();
        var world = app.World;
        var music = MusicRules.Of(world);
        MusicRules.Play(world, Game("a"), 0f);
        app.World.RunFixed(Dt);

        Do(app, "to_b");                                                   // "fade": 1, not b's own 2
        Assert.Equal((Game("b"), Game("a"), 0f, 1f), (music.Track, music.Previous, music.Blend, music.BlendRate));

        MusicRules.Play(world, Game("a"), 0f);
        app.World.RunFixed(Dt);
        Do(app, "quiet");                                                  // a's fadeOut, half a second
        Assert.Equal((default(RecordId), Game("a"), 2f), (music.Track, music.Previous, music.BlendRate));
        for (int i = 0; i < 31; i++) world.RunFixed(Dt);
        Assert.True(music.Settled);
        Assert.Equal((default(RecordId), default(RecordId)), (music.Track, music.Previous));

        var wire = world.Create(Transform.At(default), "jukebox");
        world.IO().FireInput(wire, MusicRules.PlayInput, "game:b 0");
        world.RunFixed(Dt);
        world.IO().FireInput(wire, MusicRules.StopInput, "4");
        world.RunFixed(Dt);
        Assert.Equal((default(RecordId), Game("b")), (music.Track, music.Previous));
        Assert.Equal(0.25f, music.BlendRate, 4);
    }

    // A change mid-fade starts from where it stands: back to the track going out turns the fade round with
    // no jump in either gain, and a third track lets the quieter one go.
    [Fact]
    public void AChangeMidFade_StartsFromWhereItStands()
    {
        var music = new Music();
        RecordId a = Game("a"), b = Game("b"), c = Game("c");
        music.Play(a, 0f);
        music.Advance(Dt);
        music.Play(b, 2f);
        music.Advance(0.5f);
        float ga = music.GainOf(a), gb = music.GainOf(b);
        Assert.True(ga > gb);

        music.Play(a, 2f);                                                  // back again
        Assert.Equal((a, b), (music.Track, music.Previous));
        Assert.Equal(ga, music.GainOf(a), 4);
        Assert.Equal(gb, music.GainOf(b), 4);
        music.Advance(0.25f);
        Assert.True(music.GainOf(a) > ga);

        music.Play(c, 1f);                                                  // a is the louder: it is the one that fades
        Assert.Equal((c, a, 0f), (music.Track, music.Previous, music.Blend));
        Assert.Equal(0f, music.GainOf(b));

        music.Play(c, 1f);                                                  // asked again: nothing restarts
        Assert.Equal(0f, music.Blend);
        music.Stop(0f);
        music.Advance(Dt);
        Assert.Equal((default(RecordId), default(RecordId), true), (music.Track, music.Previous, music.Settled));
        music.Stop(1f);                                                     // silence to silence: settled at once
        Assert.True(music.Settled);
    }

    // A region with its own music: entering a scene whose environment names one fades to it; a scene that
    // names none leaves the music alone.
    [Fact]
    public void EnteringAScene_FadesToItsMusic()
    {
        using var app = Boot();
        var world = app.World;
        var music = MusicRules.Of(world);
        var scenes = app.Engine.Scenes;

        Assert.True(scenes.Load(world, Game("field")));
        Assert.Equal((Game("a"), 0f, 1f), (music.Track, music.Blend, music.BlendRate));   // a's fadeIn, 1 s
        for (int i = 0; i < 70; i++) world.RunFixed(Dt);
        Assert.True(music.Settled);

        Assert.True(scenes.Load(world, Game("cave")));
        Assert.Equal((Game("b"), Game("a"), 0.5f), (music.Track, music.Previous, music.BlendRate));
        for (int i = 0; i < 130; i++) world.RunFixed(Dt);

        Assert.True(scenes.Load(world, Game("hall")));
        Assert.Equal((Game("b"), true), (music.Track, music.Settled));
    }

    // The music is world state: a save keeps the track, the one it was fading from, how far, and the
    // intensity; a load puts them back, and the player follows.
    [Fact]
    public void TheMusicSurvivesASave()
    {
        using var app = Boot();
        var world = app.World;
        var music = MusicRules.Of(world);
        MusicRules.Play(world, Game("a"), 0f);
        world.RunFixed(Dt);
        MusicRules.Play(world, Game("b"), 2f);
        MusicRules.SetIntensity(world, 0.7f);
        for (int i = 0; i < 30; i++) world.RunFixed(Dt);
        float blend = music.Blend;
        Assert.InRange(blend, 0.2f, 0.3f);

        Assert.True(app.Engine.Saves.Save("music"));
        MusicRules.Play(world, Game("c"), 0f);
        MusicRules.SetIntensity(world, 0f);
        Assert.True(app.Engine.Saves.Load("music"));

        var loaded = MusicRules.Of(world);
        Assert.Equal((Game("b"), Game("a"), 0.5f, 0.7f), (loaded.Track, loaded.Previous, loaded.BlendRate, loaded.Intensity));
        Assert.Equal(blend, loaded.Blend, 4);

        var mixer = new AudioMixer();
        var player = new MusicPlayer();
        player.Update(loaded, app.Records, mixer, Dt);
        Assert.Equal(2, player.Decks.Count);                               // both sides of the fade, from the top
        Assert.Equal(1f, VoiceOf(player, mixer, Game("a"), stem: 1).Gain / VoiceOf(player, mixer, Game("a")).Gain, 3);
    }

    // Sound turned off silences the music and forgets it; on again, the world's track starts from the top. A
    // track with no record is silence, said once, not an error every frame.
    [Fact]
    public void SilenceStopsTheVoices_AndAMissingTrackIsQuiet()
    {
        using var app = Boot();
        var mixer = new AudioMixer();
        var player = new MusicPlayer();
        MusicRules.Play(app.World, Game("a"), 0f);
        Step(app, player, mixer, 2);
        var first = VoiceOf(player, mixer, Game("a"));

        player.Silence(mixer);
        Assert.True(first.Stopping);
        Assert.Empty(player.Decks);
        Step(app, player, mixer);
        Assert.NotEqual(first.Handle, VoiceOf(player, mixer, Game("a")).Handle);

        MusicRules.Play(app.World, Game("nothing"), 0f);
        Step(app, player, mixer, 2);
        var deck = player.DeckOf(Game("nothing"));
        Assert.True(deck != null && deck.Record == null && deck.Voices.Length == 0);
    }

    // What a music record must be: .ogg files (music streams), a loop that ends after it starts.
    [Fact]
    public void AMusicRecordsFilesAreOgg_AndItsLoopInOrder()
    {
        using var app = Boot("""
            [ { "type": "music", "id": "wav", "track": "music/a.wav" },
              { "type": "music", "id": "none" },
              { "type": "music", "id": "layer", "track": "music/a.ogg", "layers": [ { "asset": "music/a.wav" } ] },
              { "type": "music", "id": "loop", "track": "music/a.ogg", "loopStart": 5000, "loopEnd": 4000 },
              { "type": "music", "id": "fine", "track": "music/a.ogg", "loopStart": 1000, "loopEnd": 4000 } ]
            """);
        var errors = app.Records.LoadErrors;
        Assert.Contains(errors, e => e.Contains("music game:wav", StringComparison.Ordinal) && e.Contains("not an .ogg", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("music game:none", StringComparison.Ordinal) && e.Contains("needs a \"track\"", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("music game:layer", StringComparison.Ordinal) && e.Contains("'music/a.wav' is not an .ogg", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("music game:loop", StringComparison.Ordinal) && e.Contains("not after loopStart", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, e => e.Contains("music game:fine", StringComparison.Ordinal));
    }

    // ---- loop points (PcmStreamer) ----

    // A loop with points plays the intro once, then the body between LoopStart and LoopEnd again and again,
    // sample for sample against the file decoded whole, with the wrap inside a buffer.
    // A loop start nearer the file's start than NVorbis's seek is off by (VorbisPcmSource.Seek) is read up to.
    [Theory]
    [InlineData("tone_mono.ogg", -1)]
    [InlineData("tone_stereo.ogg", -1)]
    [InlineData("tone_mono.ogg", 50)]
    [InlineData("tone_stereo.ogg", 10)]
    public void ALoopWithPoints_PlaysTheIntroOnceAndTheBodyAgain(string file, int loopStart)
    {
        var decoded = OggVorbis.Decode(File.OpenRead(Tone(file)));
        int ch = decoded.Channels;
        byte[] whole = decoded.Samples;
        int frames = whole.Length / 2 / ch, start = loopStart >= 0 ? loopStart : frames / 5 + 3, end = frames * 4 / 5 + 7;   // not on a packet edge

        using var streamer = new PcmStreamer(new VorbisPcmSource(() => File.OpenRead(Tone(file))), loop: true,
                                             framesPerBuffer: 777, loopStart: start, loopEnd: end);
        var pcm = new MemoryStream();
        while (streamer.FramesStreamed < end + 3L * (end - start) && streamer.TryFill(out var buffer, out int bytes))
            pcm.Write(buffer, 0, bytes);
        byte[] got = pcm.ToArray();
        Assert.Equal(0, got.Length % (777 * 2 * ch));                      // every buffer full: no short one at a wrap
        Assert.InRange(streamer.Loops, 3, 4);

        int introBytes = end * ch * 2, bodyBytes = (end - start) * ch * 2;
        Assert.True(whole.AsSpan(0, introBytes).SequenceEqual(got.AsSpan(0, introBytes)), "the intro differs");
        for (int pass = 0; pass < 3; pass++)
            Assert.True(whole.AsSpan(start * ch * 2, bodyBytes).SequenceEqual(got.AsSpan(introBytes + pass * bodyBytes, bodyBytes)),
                        $"pass {pass} of the body differs");
    }

    [Fact]
    public void LoopPointsOutOfOrder_AreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PcmStreamer(new AudioStreamingTests.Generated(100, 1), true, loopStart: 50, loopEnd: 50));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PcmStreamer(new AudioStreamingTests.Generated(100, 1), true, loopStart: -1));
        using var streamer = new PcmStreamer(new AudioStreamingTests.Generated(100, 1), loop: false, framesPerBuffer: 64, loopStart: 10, loopEnd: 20);
        long frames = 0;
        while (streamer.TryFill(out _, out int bytes)) frames += bytes / 2;
        Assert.Equal(100, frames);                                         // not looping: the points are ignored
    }

    // ---- the Sandbox ----

    // The Sandbox changes its music with data alone: its start scene fades in `wander`, walking into the hut
    // yard's trigger switches to `hearth` through the trigger's `PlayMusic` output and walking out switches
    // back over four seconds, and the crypt (a scene of its own) has `crypt`. The tracks are real files that
    // stream, `wander` from its loop point.
    [Fact]
    public void TheSandbox_SwitchesMusicOnATriggerAndAScene()
    {
        using var app = HeadlessApp.ForGame(Path.Combine(TestEnv.FolderAbove("Sage.sln"), "games", "Sandbox"), new SandboxModule())
            .WithEngineContent()
            .OnRegistered(a => a.Records.Register<MusicRecord>())
            .Boot();
        Assert.True(app.Records.ErrorCount == 0, string.Join("\n", app.Records.LoadErrors));
        var world = app.World;
        var music = MusicRules.Of(world);
        RecordId wander = new("sandbox", "wander"), hearth = new("sandbox", "hearth"), crypt = new("sandbox", "crypt");
        Assert.Equal(wander, music.Track);

        var mixer = new AudioMixer();
        var player = new MusicPlayer();
        void Frames(int n)
        {
            for (int i = 0; i < n; i++)
            {
                world.RunFixed(Dt);
                world.RunFrame(Dt, 1f);
                player.Update(music, app.Records, mixer, Dt);
            }
        }
        Frames(200);
        Assert.True(music.Settled);
        var wanderVoice = VoiceOf(player, mixer, wander);
        Assert.Equal(22050, wanderVoice.LoopStart);
        Assert.Equal(2, player.DeckOf(wander)!.Voices.Length);             // the track and its drums

        var record = app.Records.Get<MusicRecord>(wander);
        foreach (var asset in record.Layers.Select(l => l.Asset).Prepend(record.Track))
        {
            using var stream = PcmStreamer.Open(app.Vfs, asset, loop: true, out string? error, loopStart: record.LoopStart);
            Assert.True(stream != null, error);
            Assert.True(stream!.TryFill(out _, out _));
        }

        // Into the hut yard: the trigger's output switches the music.
        var you = SandboxScreensTests.Player(world);
        var yard = Assert.Single(world.QueryAll().Entities.ToEntityList(), e => e.Name == "hut yard");
        var outside = world.Get<Transform>(you);
        var inYard = outside;
        inYard.LocalPosition = world.Get<Transform>(yard).LocalPosition;
        world.Teleport(you, inYard);
        Frames(10);
        Assert.Equal((hearth, wander), (music.Track, music.Previous));
        Frames(150);
        Assert.Equal((hearth, true), (music.Track, music.Settled));
        Assert.Null(player.DeckOf(wander));

        // And out again: back to wander over four seconds.
        world.Teleport(you, outside);
        Frames(10);
        Assert.Equal((wander, hearth), (music.Track, music.Previous));
        Assert.Equal(0.25f, music.BlendRate, 4);

        // The crypt has its own.
        Assert.True(app.Engine.Scenes.Load(world, crypt));
        Assert.Equal(crypt, music.Track);
    }
}

// Loop points cost nothing either: ten minutes of an intro and a body, and not a byte on the heap.
[Collection(MeasurementsCollection.Name)]
public class MusicAllocationTests
{
    [Fact]
    public void TenMinutesOfALoopWithPoints_AllocateNothing()
    {
        long tenMinutes = 600L * 44100;
        using var streamer = new PcmStreamer(new AudioStreamingTests.Generated(tenMinutes / 5, 2), loop: true,
                                             loopStart: 44100, loopEnd: tenMinutes / 7);
        int queued = 0;
        Action<byte[], int> submit = (_, _) => queued++;
        streamer.Pump(0, submit);   // warm

        long before = GC.GetAllocatedBytesForCurrentThread();
        while (streamer.FramesStreamed < tenMinutes)
        {
            queued--;
            streamer.Pump(queued, submit);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.InRange(streamer.Loops, 6, 7);
    }
}
