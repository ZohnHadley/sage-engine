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

    // Back to the first sample, for a loop: the next Read gives exactly what the first one gave.
    void Rewind();
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

    public void Rewind() => _reader.SeekTo(0L);

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

    public PcmStreamer(IPcmSource source, bool loop, int framesPerBuffer = DefaultFramesPerBuffer, int maxBuffers = DefaultMaxBuffers)
    {
        if (framesPerBuffer < 1) throw new ArgumentOutOfRangeException(nameof(framesPerBuffer), framesPerBuffer, "at least one frame");
        if (maxBuffers < 2) throw new ArgumentOutOfRangeException(nameof(maxBuffers), maxBuffers, "at least two, one playing and one filling");
        _source = source;
        Loop = loop;
        FramesPerBuffer = framesPerBuffer;
        _decode = new float[framesPerBuffer * source.Channels];
        _buffers = new byte[maxBuffers][];
        for (int i = 0; i < maxBuffers; i++) _buffers[i] = new byte[_decode.Length * 2];
    }

    public int Channels => _source.Channels;
    public int SampleRate => _source.SampleRate;
    public bool Loop { get; }
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
            int read = _source.Read(_decode, got, want - got);
            if (read > 0) { got += read; rewoundEmpty = false; continue; }
            // The end of the source. A loop goes back to the start and carries on in this same buffer;
            // a source with nothing in it (or one that gives nothing after a rewind) ends rather than
            // spinning here for ever.
            if (!Loop || rewoundEmpty) { Ended = true; break; }
            _source.Rewind();
            Loops++;
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
                                    int framesPerBuffer = DefaultFramesPerBuffer, int maxBuffers = DefaultMaxBuffers)
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
            return new PcmStreamer(source, loop, framesPerBuffer, maxBuffers);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return null;
        }
    }
}
