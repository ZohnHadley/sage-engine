#nullable enable
using System;
using System.IO;

namespace Sage.Simulation;

// Where streamed sound comes from (issue #326): float samples, channel-interleaved, read a piece at a
// time, and wound back to the start for a loop. The Ogg Vorbis file is the one there is; a test gives a
// generated one, and a later source (a voice-over pack, a procedural bed) is another implementation.
internal interface IPcmSource : IDisposable
{
    int Channels { get; }
    int SampleRate { get; }

    // Up to `count` samples (all channels, so a multiple of Channels) into `buffer` at `offset`; fewer only
    // at the end, and 0 once there are none left. Throws InvalidDataException on a file that breaks off.
    int Read(float[] buffer, int offset, int count);

    // To frame `frame` (per channel, from the first), for a loop: the next Read gives exactly what a read
    // from the start would have given there. Seek(0) winds back to the start; a loop point (issue #325)
    // seeks into the middle. Throws InvalidDataException when the source cannot get there.
    void Seek(long frame);
}

// An Ogg Vorbis file read as it plays (issue #326, docs/design/11 §3): the decoder holds a page or two of
// the file, never the whole of it, so a ten-minute track costs what a ten-second one does.
internal sealed class VorbisPcmSource : IPcmSource
{
    private readonly Stream _stream;
    private readonly NVorbis.VorbisReader _reader;

    // `open` gives the file from its start. A stream that cannot seek (none of the mounts' can't, but an
    // archive's entry might) is read into memory first, still compressed — about a tenth of its PCM —
    // because NVorbis' forward-only reader cannot be trusted and a loop has to wind back. Throws
    // InvalidDataException on a file that is not Ogg Vorbis or has more than two channels.
    public VorbisPcmSource(Func<Stream> open)
    {
        var stream = open();
        if (!stream.CanSeek)
        {
            var copy = new MemoryStream();
            using (stream) stream.CopyTo(copy);
            copy.Position = 0;
            stream = copy;
        }
        _stream = stream;
        try { _reader = new NVorbis.VorbisReader(stream, closeOnDispose: false); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or IOException or NotSupportedException)
        {
            stream.Dispose();
            throw new InvalidDataException($"not Ogg Vorbis ({ex.Message})", ex);
        }
        if (_reader.Channels is < 1 or > 2 || _reader.SampleRate <= 0)
        {
            string what = _reader.SampleRate <= 0 ? $"a sample rate of {_reader.SampleRate}" : $"{_reader.Channels} channels; a sound is mono or stereo";
            Dispose();
            throw new InvalidDataException(what);
        }
        Channels = _reader.Channels;
        SampleRate = _reader.SampleRate;
    }

    public int Channels { get; }
    public int SampleRate { get; }

    // Per channel; an estimate on a file without a granule position on its last page.
    public long TotalFrames => _reader.TotalSamples;

    public int Read(float[] buffer, int offset, int count)
    {
        try { return Math.Max(0, _reader.ReadSamples(buffer, offset, count)); }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException or IndexOutOfRangeException)
        {
            throw new InvalidDataException($"cannot be decoded ({ex.Message})", ex);
        }
    }

    // **NVorbis lands past its target** (0.10.4): a seek to any frame but the first lands a fixed number of
    // frames later than reading from the start would have got there — 30 in one file, 119 in another, the same
    // for every target in a file. A loop point has to be exact (issue #325), so the shift is measured once, the
    // first time it matters, and taken off every seek after; a target nearer the start than that is read up to
    // from the start instead.
    private long _seekShift = -1;
    private float[]? _skip;

    public void Seek(long frame)
    {
        try
        {
            if (frame <= 0) { _reader.SeekTo(0L); return; }
            if (_seekShift < 0) _seekShift = MeasureSeekShift();
            long target = frame - _seekShift;
            if (target >= 1) { _reader.SeekTo(target); return; }
            _reader.SeekTo(0L);
            _skip ??= new float[1024 * Channels];
            Skip(frame, _skip);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException or IndexOutOfRangeException or IOException)
        {
            throw new InvalidDataException($"cannot seek to frame {frame} ({ex.Message})", ex);
        }
    }

    // How many frames past its target NVorbis's seek lands: a window just after the sound starts (silence
    // matches anywhere), sought to, then found in the same stretch read from the start. Decodes a few
    // thousand frames past wherever the sound starts, once per source; 0 when it cannot tell (a file silent
    // for its first half minute, or too short to probe).
    private long MeasureSeekShift()
    {
        const int Window = 256, MaxShift = 8192, Chunk = 4096;
        int ch = Channels;
        var scratch = new float[(MaxShift + Window) * ch];

        _reader.SeekTo(0L);
        long start = -1, at = 0, cap = 30L * SampleRate;
        while (start < 0 && at < cap)
        {
            int n = _reader.ReadSamples(scratch, 0, Chunk * ch);
            if (n <= 0) break;
            for (int i = 0; i < n; i++)
                if (MathF.Abs(scratch[i]) > 1e-4f) { start = at + i / ch; break; }
            at += n / ch;
        }
        if (start < 0) return 0;

        long probeAt = Math.Max(1, start);
        var probe = new float[Window * ch];
        if (probeAt + Window + MaxShift / 2 >= TotalFrames) return 0;
        _reader.SeekTo(probeAt);
        if (ReadFully(probe, probe.Length) < probe.Length) return 0;

        _reader.SeekTo(0L);
        if (!Skip(probeAt, scratch)) return 0;
        int have = ReadFully(scratch, scratch.Length);
        for (int k = 0; (k + Window) * ch <= have; k++)
        {
            bool same = true;
            for (int i = 0; i < probe.Length && same; i++) same = MathF.Abs(scratch[k * ch + i] - probe[i]) <= 1e-6f;
            if (same) return k;
        }
        return 0;
    }

    private bool Skip(long frames, float[] scratch)
    {
        while (frames > 0)
        {
            int n = _reader.ReadSamples(scratch, 0, (int)Math.Min(frames, scratch.Length / Channels) * Channels);
            if (n <= 0) return false;
            frames -= n / Channels;
        }
        return true;
    }

    private int ReadFully(float[] buffer, int count)
    {
        int got = 0;
        while (got < count)
        {
            int n = _reader.ReadSamples(buffer, got, count - got);
            if (n <= 0) break;
            got += n;
        }
        return got;
    }

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }
}

// Sound decoded as it plays (issue #326, docs/design/11 §3): music, ambience beds and dialogue, which are
// minutes long and would be tens of megabytes each as whole PCM. The client's backend hands the buffers
// this fills to a `DynamicSoundEffectInstance`; everything that decides anything is here, headless.
//
// **Bounded memory.** It owns `MaxBuffers` buffers of `FramesPerBuffer` frames each, made once, and fills
// them in turn; nothing grows with the length of the file. The backend submits a buffer only while fewer
// than MaxBuffers are queued in the device, so the one about to be refilled has always been played — which
// also makes it safe for a backend that reads the buffer in place rather than copying it.
//
// **Seamless loops.** A buffer that reaches the end of the source rewinds it and goes on filling from the
// first sample, so the last sample of one pass and the first of the next sit side by side in one buffer:
// no partial buffer, no gap, no click from a buffer boundary.
//
// **Loop points** (issue #325): a loop may wind back to `LoopStart` rather than the first frame, and wrap at
// `LoopEnd` rather than the end of the file, both in frames (samples per channel). Music with an intro plays
// it once and then repeats the body: the frame before LoopEnd and the frame at LoopStart sit side by side in
// one buffer, as the file's last and first do without loop points.
internal sealed class PcmStreamer : IDisposable
{
    // About 93 ms of 44.1 kHz per buffer, four of them: a third of a second queued, which survives a slow
    // frame or two, for 64 KB of stereo — whatever the length of the file.
    public const int DefaultFramesPerBuffer = 4096;
    public const int DefaultMaxBuffers = 4;

    private readonly IPcmSource _source;
    private readonly byte[][] _buffers;
    private readonly float[] _decode;
    private int _next;
    private long _position;   // the source's next frame

    // `loopStart` and `loopEnd` are frames: where a loop winds back to, and where it wraps (0: the end of
    // the source). Ignored when it does not loop.
    public PcmStreamer(IPcmSource source, bool loop, int framesPerBuffer = DefaultFramesPerBuffer, int maxBuffers = DefaultMaxBuffers,
                       long loopStart = 0, long loopEnd = 0)
    {
        if (framesPerBuffer < 1) throw new ArgumentOutOfRangeException(nameof(framesPerBuffer), framesPerBuffer, "at least one frame");
        if (maxBuffers < 2) throw new ArgumentOutOfRangeException(nameof(maxBuffers), maxBuffers, "at least two, one playing and one filling");
        if (loopStart < 0) throw new ArgumentOutOfRangeException(nameof(loopStart), loopStart, "a frame, from 0");
        if (loopEnd != 0 && loopEnd <= loopStart) throw new ArgumentOutOfRangeException(nameof(loopEnd), loopEnd, "after loopStart, or 0 for the end");
        _source = source;
        Loop = loop;
        LoopStart = loopStart;
        LoopEnd = loopEnd;
        FramesPerBuffer = framesPerBuffer;
        _decode = new float[framesPerBuffer * source.Channels];
        _buffers = new byte[maxBuffers][];
        for (int i = 0; i < maxBuffers; i++) _buffers[i] = new byte[_decode.Length * 2];
    }

    public int Channels => _source.Channels;
    public int SampleRate => _source.SampleRate;
    public bool Loop { get; }
    public long LoopStart { get; }
    public long LoopEnd { get; }
    public int FramesPerBuffer { get; }
    public int MaxBuffers => _buffers.Length;

    // What it holds, in PCM: the same for a jingle as for an hour of music.
    public int BufferedBytes => _buffers.Length * _buffers[0].Length;

    // Every source sample has been handed out (never, while it loops).
    public bool Ended { get; private set; }

    // Frames handed out, and how many times the source wound back to its start.
    public long FramesStreamed { get; private set; }
    public int Loops { get; private set; }

    // The next buffer of 16-bit little-endian PCM, channel-interleaved, with `bytes` of it filled: a whole
    // buffer except the last of a sound that does not loop. False once the sound has ended.
    public bool TryFill(out byte[] buffer, out int bytes)
    {
        buffer = _buffers[_next];
        bytes = 0;
        if (Ended) return false;

        int want = _decode.Length, got = 0;
        bool rewoundEmpty = false;
        while (got < want)
        {
            int room = want - got;
            if (Loop && LoopEnd > 0)
            {
                // At the loop's end: back to its start, in this same buffer.
                long left = (LoopEnd - _position) * Channels;
                if (left <= 0) { Wrap(); continue; }
                if (left < room) room = (int)left;
            }
            int read = _source.Read(_decode, got, room);
            if (read > 0) { got += read; _position += read / Channels; rewoundEmpty = false; continue; }
            // The end of the source. A loop goes back to its start and carries on in this same buffer;
            // a source with nothing in it (or one that gives nothing after a rewind) ends rather than
            // spinning here for ever.
            if (!Loop || rewoundEmpty) { Ended = true; break; }
            Wrap();
            rewoundEmpty = true;
        }
        if (got == 0) return false;

        for (int i = 0; i < got; i++)
        {
            short s = (short)Math.Clamp((int)MathF.Round(_decode[i] * 32767f), short.MinValue, short.MaxValue);
            buffer[i * 2] = (byte)s;
            buffer[i * 2 + 1] = (byte)(s >> 8);
        }
        bytes = got * 2;
        FramesStreamed += got / Channels;
        _next = (_next + 1) % _buffers.Length;
        return true;
    }

    private void Wrap()
    {
        _source.Seek(LoopStart);
        _position = LoopStart;
        Loops++;
    }

    // Tops the device's queue up to MaxBuffers: `queued` is how many it still holds, `submit` takes each
    // new buffer and its length. Returns how many went in.
    public int Pump(int queued, Action<byte[], int> submit)
    {
        int submitted = 0;
        while (queued + submitted < _buffers.Length && TryFill(out var buffer, out int bytes))
        {
            submit(buffer, bytes);
            submitted++;
        }
        return submitted;
    }

    // Done playing: nothing left to give and nothing left in the device.
    public bool Finished(int queued) => Ended && queued == 0;

    public void Dispose() => _source.Dispose();

    // ---- Where streamed sounds come from ---------------------------------------------------------------

    // Whether a voice of `record` playing `asset` is streamed rather than loaded whole: the record says
    // `"stream": true` and the file is Ogg Vorbis (a `.wav` is played whole either way; the record check
    // says so at load).
    public static bool Streams(SoundRecord? record, AssetPath asset) => record is { Stream: true } && IsOgg(asset);

    public static bool IsOgg(AssetPath asset) => asset.Path.Value.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase);

    // A streamer over `path` through the VFS (05 §3.1), from the highest mount that has it. Null with `error` set when it is in no mount
    // or cannot be read as Ogg Vorbis.
    public static PcmStreamer? Open(VirtualFileSystem vfs, AssetPath path, bool loop, out string? error,
                                    int framesPerBuffer = DefaultFramesPerBuffer, int maxBuffers = DefaultMaxBuffers,
                                    long loopStart = 0, long loopEnd = 0)
    {
        error = null;
        if (vfs.Which(path.Path) is not { } mount)
        {
            error = "not found in any mount";
            return null;
        }
        if (!IsOgg(path))
        {
            error = "only Ogg Vorbis (.ogg) is streamed";
            return null;
        }
        try
        {
            var source = new VorbisPcmSource(() => mount.Open(path.Path));
            try { return new PcmStreamer(source, loop, framesPerBuffer, maxBuffers, loopStart, loopEnd); }
            catch (ArgumentOutOfRangeException ex) { source.Dispose(); error = ex.Message; return null; }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return null;
        }
    }
}
