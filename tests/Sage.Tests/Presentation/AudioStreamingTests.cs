#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Streamed sound (issue #326, docs/design/11 §3): an Ogg Vorbis file decoded as it plays, a few buffers
// at a time, for music, ambience beds and dialogue. The decoding, the loop and the buffer bound are
// PcmStreamer's, headless; the client's backend only hands its buffers to a DynamicSoundEffectInstance
// (the smoke run exercises that).
public class AudioStreamingTests
{
    public AudioStreamingTests() { _ = TestEnv.UserRoot; }

    private static string AudioFolder => Path.Combine(TestEnv.FolderAbove("Sage.sln"), "tests", "Sage.Tests", "Content", "Audio");
    private static string Tone(string name) => Path.Combine(AudioFolder, name);

    private static PcmStreamer Stream(string file, bool loop, int frames = PcmStreamer.DefaultFramesPerBuffer, int buffers = PcmStreamer.DefaultMaxBuffers) =>
        new(new VorbisPcmSource(() => File.OpenRead(Tone(file))), loop, frames, buffers);

    // Every buffer the streamer gives, back to back, until `frames` frames have come out or it ends.
    private static byte[] Drain(PcmStreamer streamer, long frames, List<byte[]>? seen = null)
    {
        var all = new MemoryStream();
        while (streamer.FramesStreamed < frames && streamer.TryFill(out var buffer, out int bytes))
        {
            seen?.Add(buffer);
            all.Write(buffer, 0, bytes);
        }
        return all.ToArray();
    }

    private static short Sample(byte[] pcm, int index) => (short)(pcm[index * 2] | pcm[index * 2 + 1] << 8);

    // Played through once, the stream is the file decoded whole, sample for sample: the last buffer is
    // short, and then it has ended.
    [Theory]
    [InlineData("tone_mono.ogg")]
    [InlineData("tone_stereo.ogg")]
    public void AStreamPlayedOnce_IsTheWholeFile_AndThenEnds(string file)
    {
        byte[] whole;
        using (var f = File.OpenRead(Tone(file))) whole = OggVorbis.Decode(f).Samples;

        using var streamer = Stream(file, loop: false, frames: 1000);
        var pcm = Drain(streamer, long.MaxValue);
        Assert.Equal(whole, pcm);
        Assert.True(streamer.Ended);
        Assert.False(streamer.TryFill(out _, out int none));
        Assert.Equal(0, none);
        Assert.Equal(0, streamer.Loops);
        Assert.True(streamer.Finished(queued: 0));
        Assert.False(streamer.Finished(queued: 1));   // the device has not played the last of it yet
    }

    // Looped, the file follows itself with nothing between: the stream is the decoded file repeated, with
    // the wrap in the middle of a buffer (1000 frames does not divide the file), and the samples either side
    // of the loop point are the file's last and first.
    [Theory]
    [InlineData("tone_mono.ogg")]
    [InlineData("tone_stereo.ogg")]
    public void ALoopWrapsWithoutAGap_TheFileRepeatedSampleForSample(string file)
    {
        var decoded = OggVorbis.Decode(File.OpenRead(Tone(file)));
        byte[] whole = decoded.Samples;
        int frames = whole.Length / 2 / decoded.Channels;
        Assert.NotEqual(0, frames % 1000);

        using var streamer = Stream(file, loop: true, frames: 1000);
        var pcm = Drain(streamer, frames * 3L + frames / 2);
        Assert.False(streamer.Ended);
        Assert.Equal(3, streamer.Loops);
        Assert.Equal(0, pcm.Length % (1000 * 2 * decoded.Channels));   // every buffer full: no short one at a wrap

        for (int pass = 0; pass < 3; pass++)
            Assert.True(whole.AsSpan().SequenceEqual(pcm.AsSpan(pass * whole.Length, whole.Length)), $"pass {pass} differs from the file");
        int tail = pcm.Length - 3 * whole.Length;
        Assert.True(whole.AsSpan(0, tail).SequenceEqual(pcm.AsSpan(3 * whole.Length, tail)));

        // Across the loop point: the file's last frame, then its first, adjacent in the stream.
        int samples = whole.Length / 2, at = samples;   // the first sample of the second pass
        for (int c = 0; c < decoded.Channels; c++)
        {
            Assert.Equal(Sample(whole, samples - decoded.Channels + c), Sample(pcm, at - decoded.Channels + c));
            Assert.Equal(Sample(whole, c), Sample(pcm, at + c));
        }
    }

    // A stream that cannot seek (an archive's entry, one day) is held compressed and loops the same way.
    [Fact]
    public void AStreamThatCannotSeek_StillLoopsSeamlessly()
    {
        byte[] whole = OggVorbis.Decode(File.OpenRead(Tone("tone_mono.ogg"))).Samples;
        int opened = 0;
        var source = new VorbisPcmSource(() => { opened++; return new ForwardOnly(File.OpenRead(Tone("tone_mono.ogg"))); });
        using var streamer = new PcmStreamer(source, loop: true, framesPerBuffer: 777);
        var pcm = Drain(streamer, whole.Length / 2 * 2L);
        Assert.True(whole.AsSpan().SequenceEqual(pcm.AsSpan(0, whole.Length)));
        Assert.True(whole.AsSpan().SequenceEqual(pcm.AsSpan(whole.Length, whole.Length)));
        Assert.InRange(streamer.Loops, 1, 2);
        Assert.Equal(1, opened);   // opened once: the loop seeks in the copy
    }

    // Several minutes of music cost what a jingle does: the streamer fills the same MaxBuffers buffers in
    // turn, whatever the length, and its memory never changes. Three minutes of the tone, looped 720 times.
    [Fact]
    public void ThreeMinutesOfOgg_StreamThroughTheSameFewBuffers()
    {
        using var streamer = Stream("tone_mono.ogg", loop: true);
        int held = streamer.BufferedBytes;
        Assert.Equal(PcmStreamer.DefaultMaxBuffers * PcmStreamer.DefaultFramesPerBuffer * 2, held);   // 32 KB of mono

        var seen = new List<byte[]>();
        long threeMinutes = 180L * streamer.SampleRate;
        Drain(streamer, threeMinutes, seen);
        Assert.True(streamer.FramesStreamed >= threeMinutes);
        Assert.True(streamer.Loops >= 700, $"{streamer.Loops} loops");
        Assert.Equal(PcmStreamer.DefaultMaxBuffers, seen.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Equal(held, streamer.BufferedBytes);
        // In turn: a buffer comes round again only after the others have each been handed out.
        for (int i = PcmStreamer.DefaultMaxBuffers; i < seen.Count; i++) Assert.Same(seen[i - PcmStreamer.DefaultMaxBuffers], seen[i]);
    }

    // The device's queue is topped up to MaxBuffers and no further, so the buffer about to be refilled has
    // always been played.
    [Fact]
    public void PumpTopsTheQueueUpToMaxBuffers_AndNoFurther()
    {
        using var streamer = Stream("tone_stereo.ogg", loop: true, frames: 512, buffers: 3);
        var queue = new Queue<byte[]>();
        void Submit(byte[] buffer, int bytes) { Assert.DoesNotContain(buffer, queue); queue.Enqueue(buffer); }

        Assert.Equal(3, streamer.Pump(queued: 0, Submit));
        Assert.Equal(0, streamer.Pump(queue.Count, Submit));
        for (int frame = 0; frame < 200; frame++)
        {
            queue.Dequeue();   // the device played one
            Assert.Equal(1, streamer.Pump(queue.Count, Submit));
            Assert.Equal(3, queue.Count);
        }
    }

    // A source with nothing in it ends rather than rewinding for ever.
    [Fact]
    public void AnEmptyLoopEnds_RatherThanSpinning()
    {
        using var streamer = new PcmStreamer(new Generated(0, 1), loop: true, framesPerBuffer: 64);
        Assert.False(streamer.TryFill(out _, out _));
        Assert.True(streamer.Ended);
        Assert.Equal(0, streamer.Pump(0, (_, _) => throw new InvalidOperationException("nothing to submit")));
    }

    // ---- through the VFS, and the record ----

    private static HeadlessApp Boot(string records)
    {
        var files = new MountFixture();
        files.Write("game", "data/sounds.json", records);
        Directory.CreateDirectory(Path.Combine(files.Dir("game"), "music"));
        File.Copy(Tone("tone_stereo.ogg"), Path.Combine(files.Dir("game"), "music", "theme.ogg"));
        files.Write("game", "audio/hit.wav", "RIFF");
        files.Write("game", "audio/hit.mp3", "ID3");
        files.Mount("game", "game");
        return HeadlessApp.Simulation().Mount(files)
            .OnRegistered(a => a.Records.Register<SoundRecord>())   // the client's record type
            .Boot();
    }

    // A sound says `"stream": true` and names an .ogg: it loads clean, and the streamer opens the file from
    // the mount that has it and plays it.
    [Fact]
    public void AStreamedSound_LoadsAndOpensThroughTheVfs()
    {
        using var app = Boot("""[ { "type": "sound", "id": "theme", "variations": ["music/theme.ogg"], "stream": true, "bus": "Music" } ]""");
        Assert.True(app.Records.ErrorCount == 0, string.Join("\n", app.Records.LoadErrors));
        var theme = app.Records.Get<SoundRecord>(new RecordId("game", "theme"));
        Assert.True(theme.Stream);
        Assert.True(PcmStreamer.Streams(theme, theme.Variations[0]));
        Assert.False(PcmStreamer.Streams(new SoundRecord(), theme.Variations[0]));             // not asked to stream
        Assert.False(PcmStreamer.Streams(theme, AssetPath.Intern("audio/hit.wav")));           // a .wav plays whole

        using var streamer = PcmStreamer.Open(app.Vfs, theme.Variations[0], loop: true, out string? error);
        Assert.True(streamer != null, error);
        Assert.Equal((2, 44100), (streamer!.Channels, streamer.SampleRate));
        Assert.True(streamer.TryFill(out _, out int bytes) && bytes == PcmStreamer.DefaultFramesPerBuffer * 4);

        Assert.Null(PcmStreamer.Open(app.Vfs, AssetPath.Intern("music/missing.ogg"), false, out error));
        Assert.Contains("not found", error);
        Assert.Null(PcmStreamer.Open(app.Vfs, AssetPath.Intern("audio/hit.wav"), false, out error));
        Assert.Contains("Ogg Vorbis", error);
    }

    // A sound's files are what the engine plays — a .wav or an .ogg — and only an .ogg streams: load
    // errors, at the variation.
    [Fact]
    public void ASoundOfAnotherKind_OrAStreamedWav_IsALoadError()
    {
        using var app = Boot("""
        [ { "type": "sound", "id": "hit", "variations": ["audio/hit.wav"] },
          { "type": "sound", "id": "mp3", "variations": ["audio/hit.mp3"] },
          { "type": "sound", "id": "wav_stream", "variations": ["music/theme.ogg", "audio/hit.wav"], "stream": true } ]
        """);
        var errors = app.Records.LoadErrors;
        Assert.Equal(2, app.Records.ErrorCount);
        Assert.Contains(errors, e => e.Contains("sound game:mp3", StringComparison.Ordinal) && e.Contains("neither a .wav nor an .ogg", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("sound game:wav_stream", StringComparison.Ordinal) && e.Contains("only Ogg Vorbis streams", StringComparison.Ordinal));
    }

    // ---- helpers ----

    // A tone made on the spot: `frames` frames, or for ever.
    internal sealed class Generated : IPcmSource
    {
        private readonly long _frames;
        private long _at;

        public Generated(long frames, int channels, int rate = 44100) { _frames = frames; Channels = channels; SampleRate = rate; }

        public int Channels { get; }
        public int SampleRate { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int n = (int)Math.Min(count / Channels, _frames - _at) * Channels;
            for (int i = 0; i < n; i++) buffer[offset + i] = MathF.Sin((_at + i / Channels) * 0.0627f) * 0.5f;
            _at += n / Channels;
            return n;
        }

        public void Rewind() => _at = 0;
        public void Dispose() { }
    }

    private sealed class ForwardOnly(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}

// Streaming allocates nothing once it has started: ten minutes of sound through the same buffers, and not
// a byte more on the heap.
[Collection(MeasurementsCollection.Name)]
public class AudioStreamingAllocationTests
{
    [Fact]
    public void TenMinutesStreamed_AllocateNothing()
    {
        long tenMinutes = 600L * 44100;
        using var streamer = new PcmStreamer(new AudioStreamingTests.Generated(tenMinutes / 7, 2), loop: true);
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
        Assert.Equal(PcmStreamer.DefaultMaxBuffers * PcmStreamer.DefaultFramesPerBuffer * 4, streamer.BufferedBytes);
    }
}
