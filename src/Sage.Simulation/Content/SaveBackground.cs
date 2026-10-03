#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace Sage.Simulation;

// Saves a game takes while it plays (issue #285): **snapshot on the tick, write in the background.**
//
// A save is two halves. The *snapshot* reads the worlds: every persistent entity, resource, tombstone and
// dormant cell becomes a JSON tree that shares nothing with the world (SaveSnapshot). It has to happen at
// a tick boundary, on the simulation's thread, and it is what a save costs the frame. The *write* turns
// those trees into bytes, compresses them (`save_compress`), encodes the thumbnail, writes the files into
// `slot.writing` and moves the folder into place. It touches nothing the simulation owns, so it runs on a
// thread pool thread, one save after another in the order they were taken.
//
// **The snapshot serialises as little as it can.** A component whose type needs nothing but its own value
// to be written (values and strings all the way down, none of a plugin's converters, which may read the
// world) is kept as a boxed copy and serialised by the writer (SaveSerializer.ResolveDeferred); one still
// bit for bit as its prefab spawned it is left out without serialising anything (SpawnBaseline keeps the
// spawn's values). What is left on the tick is walking the entities and what needs the world: entity
// references, gameplay's attributes and tags by name. For 10k spawned entities that took the tick from
// about 190 ms to about 50 (Debug build), the rest moving to the writer (test:
// ATenThousandEntitySaveDoesNotStallAFrame).
//
// Which saves go to the background (`save_background`, default on): the ones a game takes while it plays —
// a request made during a tick (F5, a system, a trigger), an autosave, `quicksave`, `RequestSave` between
// ticks. `Save(slot)` between ticks is still written before it returns, because its caller (a menu's
// Overwrite, a tool, a test) asks for its result.
//
// **Nothing reads a slot half-written.** Load, Exists, Delete, List and Slots wait for the writes still in
// flight before they look at the folder, so a quick-load straight after a quick-save loads it, and an
// autosave's rotation sees the slot the last one is writing. The engine waits for them when it is disposed.
//
// **The thumbnail** (SaveSystem.Thumbnail) is the client's hook: called in the snapshot, on the thread that
// owns the graphics device, it hands over the frame's pixels (RGBA, top row first); the writer encodes
// them as `thumbnail.png` in the slot, the header names it ("thumbnail"), and SaveSlot.ThumbnailPath gives
// a load menu its full path. With no hook, or a hook that returns null, a save has none.
public sealed partial class SaveSystem
{
    // The writes queued so far, chained so they run one at a time in order; completed when none is due.
    private readonly object _writerGate = new();
    private Task _writer = Task.CompletedTask;
    private int _writesInFlight;

    // A background write finished: the next read of Slots scans the folder again.
    private volatile bool _slotsStale;

    // Held shut by a test to prove the tick does not wait for the write: the writer waits on it first.
    internal ManualResetEventSlim? WriterGate;

    private CVar<bool>? _compress;
    private CVar<bool>? _background;

    // The client's thumbnail of the frame a save is taken on (issue #285); null: saves have none. Called on
    // the simulation's thread when the save is snapshotted.
    [Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public Func<SaveThumbnail?>? Thumbnail { get; set; }

    // A save is being written in the background.
    [Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public bool IsWriting => Volatile.Read(ref _writesInFlight) > 0;

    // Waits until every save taken so far is on disk (a quit, a test). Reading a slot does it already.
    [Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public void WaitForWrites()
    {
        Task pending;
        lock (_writerGate) pending = _writer;
        if (!pending.IsCompleted) pending.Wait();
        if (_slotsStale) { _slotsStale = false; _slots = null; }
    }

    // A save the game took while it plays: snapshotted now, written in the background (or now, with
    // `save_background 0`). False when the snapshot failed; a write that fails later says so in the log.
    private bool SaveWhilePlaying(string slot, SaveKind kind, string? title)
    {
        if (_background is { Value: false }) return Save(slot, kind, title);
        var snapshot = Snapshot(slot, kind, title);
        if (snapshot == null) return false;
        Interlocked.Increment(ref _writesInFlight);
        lock (_writerGate)
        {
            _writer = _writer.ContinueWith(_ =>
            {
                try
                {
                    WriterGate?.Wait();
                    Write(snapshot);
                }
                finally { Interlocked.Decrement(ref _writesInFlight); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
        return true;
    }

    private void RegisterBackgroundCommands(CVarRegistry cvars)
    {
        _compress = cvars.Register("save_compress", false, CVarFlags.Archive,
            "Write world files gzip-compressed (world_<name>.json.gz). Off: indented JSON. Either loads.");
        _background = cvars.Register("save_background", true, CVarFlags.Archive,
            "Write quick-saves, autosaves and saves asked for in a tick on a background thread; the tick pays only for the snapshot.");
    }
}

// The pixels of a save's thumbnail (issue #285), as a client hands them to SaveSystem.Thumbnail: `Width`
// by `Height`, four bytes a pixel (R, G, B, A), the top row first. The writer encodes them as a PNG.
[Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public sealed class SaveThumbnail
{
    public SaveThumbnail(int width, int height, byte[] rgba)
    {
        Width = width;
        Height = height;
        Rgba = rgba;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Rgba { get; }

    internal bool IsValid => Width > 0 && Height > 0 && Width <= 4096 && Height <= 4096
                             && Rgba != null && Rgba.Length == Width * Height * 4;

    // A smaller copy, at most `maxWidth` wide, by nearest neighbour: what a client keeps of a full frame.
    public SaveThumbnail Shrink(int maxWidth)
    {
        if (maxWidth <= 0 || Width <= maxWidth) return this;
        int w = maxWidth, h = Math.Max(1, (int)((long)Height * maxWidth / Width));
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int sy = (int)((long)y * Height / h);
            for (int x = 0; x < w; x++)
            {
                int sx = (int)((long)x * Width / w);
                Buffer.BlockCopy(Rgba, (sy * Width + sx) * 4, pixels, (y * w + x) * 4, 4);
            }
        }
        return new SaveThumbnail(w, h, pixels);
    }
}

// A PNG writer for thumbnails (issue #285): 8-bit RGBA, no filtering, one zlib stream. The simulation has no
// image library, and this is all a thumbnail needs.
internal static class Png
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static byte[] Encode(int width, int height, byte[] rgba)
    {
        using var output = new MemoryStream();
        output.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        WriteBigEndian(ihdr, (uint)width);
        WriteBigEndian(ihdr[4..], (uint)height);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 6;    // colour type: RGBA
        ihdr[10] = 0;   // compression
        ihdr[11] = 0;   // filter method
        ihdr[12] = 0;   // no interlace
        Chunk(output, "IHDR", ihdr);

        using (var raw = new MemoryStream())
        {
            using (var zlib = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
            {
                int stride = width * 4;
                for (int y = 0; y < height; y++)
                {
                    zlib.WriteByte(0);   // filter: none
                    zlib.Write(rgba, y * stride, stride);
                }
            }
            Chunk(output, "IDAT", raw.ToArray());
        }
        Chunk(output, "IEND", ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        WriteBigEndian(word, (uint)data.Length);
        output.Write(word);
        Span<byte> name = stackalloc byte[4];
        for (int i = 0; i < 4; i++) name[i] = (byte)type[i];
        output.Write(name);
        output.Write(data);
        uint crc = Crc32.Update(Crc32.Update(0xFFFFFFFFu, name), data) ^ 0xFFFFFFFFu;
        WriteBigEndian(word, crc);
        output.Write(word);
    }

    private static void WriteBigEndian(Span<byte> to, uint value)
    {
        to[0] = (byte)(value >> 24);
        to[1] = (byte)(value >> 16);
        to[2] = (byte)(value >> 8);
        to[3] = (byte)value;
    }

    private static class Crc32
    {
        private static readonly uint[] Table = Make();

        private static uint[] Make()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
