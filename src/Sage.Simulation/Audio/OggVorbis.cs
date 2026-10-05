#nullable enable
using System;
using System.IO;

namespace Sage.Simulation;

// Sound as 16-bit PCM, interleaved: what the client hands to a SoundEffect.
internal sealed class PcmSound
{
    public required byte[] Samples;      // little-endian int16, channel-interleaved
    public required int SampleRate;
    public required int Channels;

    public double Seconds => Samples.Length / 2.0 / Channels / SampleRate;
}

// Ogg Vorbis, decoded headlessly (issue #302, docs/design/11 §3): a sound's `asset` may be a `.ogg` as well as a
// `.wav`. MonoGame's SoundEffect takes PCM, so the whole file is decoded to it when it loads (NVorbis, the
// decoder MonoGame itself depends on); a `.ogg` is smaller on disk and in a package, and the same in memory
// once loaded. Streaming long music from the file as it plays is the audio area's next step, not this.
internal static class OggVorbis
{
    // The longest sound decoded whole: ten minutes of 48 kHz stereo is 110 MB of PCM, past which a file is
    // music that wants streaming, not a sound effect.
    public const double MaxSeconds = 600;

    // Throws InvalidDataException on a file that is not Ogg Vorbis, has more than two channels (a SoundEffect
    // takes mono or stereo), or is longer than MaxSeconds.
    public static PcmSound Decode(Stream stream)
    {
        NVorbis.VorbisReader reader;
        try { reader = new NVorbis.VorbisReader(stream, closeOnDispose: false); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or IOException or NotSupportedException)
        {
            throw new InvalidDataException($"not Ogg Vorbis ({ex.Message})", ex);
        }

        using (reader)
        {
            int channels = reader.Channels, rate = reader.SampleRate;
            if (channels is < 1 or > 2) throw new InvalidDataException($"{channels} channels; a sound is mono or stereo");
            if (rate <= 0) throw new InvalidDataException($"a sample rate of {rate}");
            long total = reader.TotalSamples;   // per channel; an estimate on a file without a granule at its end
            if (total > MaxSeconds * rate) throw new InvalidDataException($"{total / (double)rate:F0} s long; a sound is decoded whole, up to {MaxSeconds:F0} s");

            var output = new MemoryStream((int)Math.Max(0, Math.Min(total * channels * 2, int.MaxValue / 2)));
            var buffer = new float[4096 * channels];
            var bytes = new byte[buffer.Length * 2];
            long written = 0;
            while (true)
            {
                int read;
                try { read = reader.ReadSamples(buffer, 0, buffer.Length); }
                catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException or IndexOutOfRangeException)
                {
                    throw new InvalidDataException($"cannot be decoded ({ex.Message})", ex);
                }
                if (read <= 0) break;
                for (int i = 0; i < read; i++)
                {
                    short s = (short)Math.Clamp((int)MathF.Round(buffer[i] * 32767f), short.MinValue, short.MaxValue);
                    bytes[i * 2] = (byte)s;
                    bytes[i * 2 + 1] = (byte)(s >> 8);
                }
                output.Write(bytes, 0, read * 2);
                written += read;
                if (written > MaxSeconds * rate * channels) throw new InvalidDataException($"longer than {MaxSeconds:F0} s; a sound is decoded whole");
            }
            if (written == 0) throw new InvalidDataException("no samples");
            return new PcmSound { Samples = output.ToArray(), SampleRate = rate, Channels = channels };
        }
    }
}
