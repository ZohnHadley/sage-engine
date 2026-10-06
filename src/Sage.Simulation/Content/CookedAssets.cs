#nullable enable
using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Sage.Simulation;

// Cooked assets (docs/design/05 §7, issue #302): binary forms of a model and a texture, written by
// `sage cook` (which `sage package` runs) *beside* the loose file they were cooked from, under the loose
// file's path plus an extension:
//
//   models/bunny.glb     → models/bunny.glb.sgmesh     vertex and index arrays as the GPU takes them
//   textures/wall.png    → textures/wall.png.sgtex     premultiplied RGBA, or BC1/BC3 (DXT1/DXT5) blocks,
//                                                     with the whole mip chain (issue #317); .jpg and .tga too
//
// The client asks for the loose path as always, and reads the cooked file instead when `Find` says one
// stands in for it. Nothing names a cooked file: records, prefabs and maps keep naming the `.glb` and the
// `.png`, the loose file stays in the package (the simulation reads skeletons and clips from the `.glb`,
// and `sage validate` checks the paths), and a missing, unreadable or out-of-date cooked file falls back to
// the loose one with a warning (out of date: cooked from a file of another length, where the mount is a
// folder that can say so). The point is load time and memory: no glTF parse and no PNG inflate at
// load, no premultiply pass, and a compressed texture is a quarter (BC3) or an eighth (BC1) of RGBA.
internal static class CookedAssets
{
    public const string MeshExtension = ".sgmesh";
    public const string TextureExtension = ".sgtex";

    // What gets cooked, by the loose file's extension.
    public static bool IsCookableMesh(string path) => path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase);

    public static bool IsCookableTexture(string path) =>
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tga", StringComparison.OrdinalIgnoreCase);

    // The cooked file to read in place of `source`, or null for the loose file. A cooked file stands in only
    // when its mount is at least as high as the loose file's: a mod (a later mount) that replaces
    // `wall.png` with its own loose file must win over the game's cooked `wall.png.sgtex`, which was made
    // from the file the mod replaced (05 §3.2: the last mount wins, cooked or not).
    public static VirtualPath? Find(VirtualFileSystem vfs, VirtualPath source, string extension)
    {
        var cooked = VirtualPath.Parse(source.Value + extension);
        int cookedMount = WinningMount(vfs, cooked);
        if (cookedMount < 0) return null;
        return WinningMount(vfs, source) > cookedMount ? null : cooked;
    }

    // The cooked mesh standing in for `source`, or null to read the loose file. A cooked file that cannot be
    // read, or that was cooked from a file of another length (the loose file changed since, and the mount
    // can say so: a folder), is warned about once and passed over: the loose file is always there to fall
    // back to.
    public static MeshGeometry? LoadMesh(VirtualFileSystem vfs, VirtualPath source) =>
        Load(vfs, source, MeshExtension, "mesh", stream => (CookedMesh.Read(stream, out var stamp), stamp));

    public static CookedTextureData? LoadTexture(VirtualFileSystem vfs, VirtualPath source) =>
        Load(vfs, source, TextureExtension, "texture", stream => (CookedTexture.Read(stream, out var stamp), stamp));

    // A loose image (.png, .jpg, .tga) from `mount`, as the client uploads it when no cooked file stands in
    // (issue #317): decoded, premultiplied and mipmapped, uncompressed. Throws IOException when the file
    // cannot be read and InvalidDataException when it is not an image this engine decodes.
    public static CookedTextureData ReadLooseTexture(IMount mount, VirtualPath source)
    {
        using var stream = mount.Open(source);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return CookedTexture.Cook(memory.ToArray(), compress: false);
    }

    private static T? Load<T>(VirtualFileSystem vfs, VirtualPath source, string extension, string kind, Func<Stream, (T, SourceStamp)> read)
        where T : class
    {
        if (Find(vfs, source, extension) is not { } cooked) return null;
        var mount = vfs.Which(cooked)!;
        try
        {
            using var stream = mount.Open(cooked);
            var (value, stamp) = read(stream);
            if (vfs.Which(source) is { } sourceMount && sourceMount.PhysicalPath(source) is { } file
                && new FileInfo(file).Length != stamp.Length)
            {
                Log.Once(LogCat.Assets, LogLevel.Warn, $"cooked-stale:{cooked}",
                         $"Cooked {kind} '{cooked}' ({mount.Name}) was made from another '{source}' (cook again); reading the loose file");
                return null;
            }
            return value;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OverflowException)
        {
            Log.Once(LogCat.Assets, LogLevel.Warn, $"cooked-bad:{cooked}",
                     $"Cooked {kind} '{cooked}' ({mount.Name}) cannot be read ({ex.Message}); reading the loose file");
            return null;
        }
    }

    private static int WinningMount(VirtualFileSystem vfs, VirtualPath path)
    {
        var mounts = vfs.Mounts;
        for (int i = mounts.Count - 1; i >= 0; i--)
            if (mounts[i].Exists(path)) return i;
        return -1;
    }

    // The header both formats share: four magic bytes, a format version, and the loose file's length and
    // FNV-1a hash, which say which file it was cooked from (`sage cook` skips one that is up to date).
    internal static void WriteHeader(BinaryWriter writer, ReadOnlySpan<byte> magic, int version, in SourceStamp stamp)
    {
        writer.Write(magic);
        writer.Write(version);
        writer.Write(stamp.Length);
        writer.Write(stamp.Hash);
    }

    internal static SourceStamp ReadHeader(BinaryReader reader, ReadOnlySpan<byte> magic, int version, string kind)
    {
        Span<byte> read = stackalloc byte[4];
        if (reader.Read(read) != 4 || !read.SequenceEqual(magic)) throw new InvalidDataException($"not a {kind} file");
        int fileVersion = reader.ReadInt32();
        if (fileVersion != version) throw new InvalidDataException($"{kind} version {fileVersion}; this engine reads version {version} (cook again)");
        return new SourceStamp(reader.ReadInt64(), reader.ReadUInt64());
    }

    internal static byte[] ReadArray(BinaryReader reader, int bytes)
    {
        var data = reader.ReadBytes(bytes);
        if (data.Length != bytes) throw new EndOfStreamException("the file ends early");
        return data;
    }
}

// Which loose file a cooked one was made from: its length and its FNV-1a 64-bit hash.
internal readonly record struct SourceStamp(long Length, ulong Hash)
{
    public static SourceStamp Of(ReadOnlySpan<byte> bytes)
    {
        ulong hash = 14695981039346656037UL;
        foreach (byte b in bytes)
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }
        return new SourceStamp(bytes.Length, hash);
    }
}

// `.sgmesh`: a MeshGeometry as it is in memory. Little-endian throughout:
//   "SGMS" int32 version  int64 sourceLength  uint64 sourceHash
//   vec3 boundsCentre  float boundsRadius
//   int32 joints (0 = no skin); per joint: string name, mat4 inverseBind, mat4 rest; then int32 logicalIndex
//   int32 parts; per part: byte kind (0 rigid, 1 skinned), int32 vertices, int32 indices,
//                          vertices × 52 or 72 bytes (MeshVertex, SkinnedMeshVertex), indices × int32
// Version 3 (issue #321) follows each part by a byte (1 = a second UV set) and then vertices × vec2.
// Version 4 (issue #363) adds morph targets: after the skin, int32 targets; per target: string name, float
// rest weight. After each part's UV set, int32 morphs; per morph: int32 target, vertices × vec3 position
// deltas, byte (1 = normals) and then vertices × vec3 normal deltas.
// Version 2 (issue #410): each vertex gained a tangent and a colour. A version-1 file is refused like any
// file this engine does not read, so the client falls back to the loose `.glb` (with a warning) until it is
// cooked again.
internal static class CookedMesh
{
    public const int Version = 4;   // 3: a second UV set per part (issue #321); 4: morph targets (issue #363)
    private static ReadOnlySpan<byte> Magic => "SGMS"u8;

    public static void Write(Stream stream, MeshGeometry mesh, in SourceStamp stamp)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        CookedAssets.WriteHeader(writer, Magic, Version, stamp);
        WriteVector(writer, mesh.BoundsCentre);
        writer.Write(mesh.BoundsRadius);

        var skin = mesh.Skin;
        writer.Write(skin?.JointNames.Length ?? 0);
        if (skin != null)
        {
            for (int i = 0; i < skin.JointNames.Length; i++)
            {
                writer.Write(skin.JointNames[i]);
                writer.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<Matrix4x4>(in skin.InverseBind[i])));
                writer.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<Matrix4x4>(in skin.RestJoints[i])));
            }
            writer.Write(skin.LogicalIndex);
        }

        writer.Write(mesh.MorphTargets.Length);
        for (int i = 0; i < mesh.MorphTargets.Length; i++)
        {
            writer.Write(mesh.MorphTargets[i]);
            writer.Write(i < mesh.RestMorphWeights.Length ? mesh.RestMorphWeights[i] : 0f);
        }

        writer.Write(mesh.Parts.Count);
        foreach (var part in mesh.Parts)
        {
            writer.Write((byte)(part.Skinned != null ? 1 : 0));
            writer.Write(part.VertexCount);
            writer.Write(part.Indices.Length);
            if (part.Skinned != null) writer.Write(MemoryMarshal.AsBytes(part.Skinned.AsSpan()));
            else writer.Write(MemoryMarshal.AsBytes(part.Rigid.AsSpan()));
            writer.Write(MemoryMarshal.AsBytes(part.Indices.AsSpan()));
            writer.Write((byte)(part.Uv1 != null ? 1 : 0));
            if (part.Uv1 != null) writer.Write(MemoryMarshal.AsBytes(part.Uv1.AsSpan()));
            var morphs = part.Morphs ?? Array.Empty<MeshMorph>();
            writer.Write(morphs.Length);
            foreach (var morph in morphs)
            {
                writer.Write(morph.Target);
                writer.Write(MemoryMarshal.AsBytes(morph.Positions.AsSpan()));
                writer.Write((byte)(morph.Normals != null ? 1 : 0));
                if (morph.Normals != null) writer.Write(MemoryMarshal.AsBytes(morph.Normals.AsSpan()));
            }
        }
    }

    // Throws InvalidDataException (or EndOfStreamException) on a file that is not one, or is cut short.
    public static MeshGeometry Read(Stream stream, out SourceStamp stamp)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        stamp = CookedAssets.ReadHeader(reader, Magic, Version, "cooked mesh");
        var mesh = new MeshGeometry { BoundsCentre = ReadVector(reader), BoundsRadius = reader.ReadSingle() };

        int joints = reader.ReadInt32();
        if (joints < 0 || joints > 65535) throw new InvalidDataException($"{joints} joints");
        if (joints > 0)
        {
            var skin = new MeshSkin
            {
                InverseBind = new Matrix4x4[joints], RestJoints = new Matrix4x4[joints], JointNames = new string[joints], LogicalIndex = 0,
            };
            for (int i = 0; i < joints; i++)
            {
                skin.JointNames[i] = reader.ReadString();
                skin.InverseBind[i] = ReadMatrix(reader);
                skin.RestJoints[i] = ReadMatrix(reader);
            }
            skin.LogicalIndex = reader.ReadInt32();
            mesh.Skin = skin;
        }

        int targets = reader.ReadInt32();
        if (targets < 0 || targets > 65535) throw new InvalidDataException($"{targets} morph targets");
        if (targets > 0)
        {
            mesh.MorphTargets = new string[targets];
            mesh.RestMorphWeights = new float[targets];
            for (int i = 0; i < targets; i++)
            {
                mesh.MorphTargets[i] = reader.ReadString();
                mesh.RestMorphWeights[i] = reader.ReadSingle();
            }
        }

        int parts = reader.ReadInt32();
        if (parts < 0 || parts > 1 << 16) throw new InvalidDataException($"{parts} parts");
        for (int p = 0; p < parts; p++)
        {
            byte kind = reader.ReadByte();
            int vertices = reader.ReadInt32(), indices = reader.ReadInt32();
            if (kind > 1 || vertices < 0 || indices < 0 || indices % 3 != 0) throw new InvalidDataException($"part {p} is malformed");
            int stride = kind == 1 ? SkinnedMeshVertex.Size : MeshVertex.Size;
            var vertexBytes = CookedAssets.ReadArray(reader, checked(vertices * stride));
            var indexBytes = CookedAssets.ReadArray(reader, checked(indices * sizeof(int)));
            var part = new MeshGeometryPart { Indices = MemoryMarshal.Cast<byte, int>(indexBytes).ToArray() };
            if (kind == 1) part.Skinned = MemoryMarshal.Cast<byte, SkinnedMeshVertex>(vertexBytes).ToArray();
            else part.Rigid = MemoryMarshal.Cast<byte, MeshVertex>(vertexBytes).ToArray();
            foreach (int index in part.Indices)
                if ((uint)index >= (uint)vertices) throw new InvalidDataException($"part {p} has an index past its {vertices} vertices");
            byte hasUv1 = reader.ReadByte();
            if (hasUv1 > 1) throw new InvalidDataException($"part {p} is malformed");
            if (hasUv1 == 1) part.Uv1 = MemoryMarshal.Cast<byte, Vector2>(CookedAssets.ReadArray(reader, checked(vertices * 8))).ToArray();
            int morphs = reader.ReadInt32();
            if (morphs < 0 || morphs > targets || (morphs > 0 && kind != 1)) throw new InvalidDataException($"part {p} has {morphs} morph targets");
            if (morphs > 0)
            {
                part.Morphs = new MeshMorph[morphs];
                for (int m = 0; m < morphs; m++)
                {
                    int target = reader.ReadInt32();
                    if ((uint)target >= (uint)targets) throw new InvalidDataException($"part {p} morphs target {target} of {targets}");
                    var morph = new MeshMorph
                    {
                        Target = target,
                        Positions = MemoryMarshal.Cast<byte, Vector3>(CookedAssets.ReadArray(reader, checked(vertices * 12))).ToArray(),
                    };
                    byte hasNormals = reader.ReadByte();
                    if (hasNormals > 1) throw new InvalidDataException($"part {p} is malformed");
                    if (hasNormals == 1) morph.Normals = MemoryMarshal.Cast<byte, Vector3>(CookedAssets.ReadArray(reader, checked(vertices * 12))).ToArray();
                    part.Morphs[m] = morph;
                }
            }
            mesh.Parts.Add(part);
        }
        return mesh;
    }

    private static void WriteVector(BinaryWriter writer, Vector3 v)
    {
        writer.Write(v.X);
        writer.Write(v.Y);
        writer.Write(v.Z);
    }

    private static Vector3 ReadVector(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static Matrix4x4 ReadMatrix(BinaryReader reader) =>
        MemoryMarshal.Read<Matrix4x4>(CookedAssets.ReadArray(reader, 64));
}

// How a cooked texture's pixels are stored.
internal enum CookedTextureFormat : byte
{
    Rgba = 0,   // premultiplied RGBA8, 4 bytes a pixel: lossless (pixel art, UI, fonts, odd sizes)
    Bc1 = 1,    // DXT1: 8 bytes per 4×4 block, opaque
    Bc3 = 2,    // DXT5: 16 bytes per 4×4 block, with alpha
}

// A texture's pixels, as cooked: always premultiplied (07 §13), with its whole mip chain (issue #317) or
// just the one level. A loose file loaded by the client goes through the same shape (CookedAssets.ReadLoose),
// uncompressed, so both kinds reach the GPU mipmapped.
internal sealed class CookedTextureData
{
    public required int Width;
    public required int Height;
    public required CookedTextureFormat Format;

    // Level 0 (the full size) first, then each half the size of the one before down to 1×1 (TextureMips).
    // A block format's level is whole blocks: one smaller than 4×4 still fills one, as the GPU takes it.
    public required byte[][] Levels;

    public byte[] Data => Levels[0];
    public int LevelCount => Levels.Length;
    public bool HasMips => Levels.Length > 1;

    // What the texture takes on the GPU, every level.
    public long GpuBytes
    {
        get
        {
            long bytes = 0;
            foreach (var level in Levels) bytes += level.LongLength;
            return bytes;
        }
    }

    // A level's pixels as premultiplied RGBA8, decoding blocks: for a device without S3TC, and for tests.
    public byte[] ToRgba(int level = 0)
    {
        var (w, h) = TextureMips.Size(Width, Height, level);
        if (Format == CookedTextureFormat.Rgba) return Levels[level];
        int pw = TextureMips.BlockAligned(w), ph = TextureMips.BlockAligned(h);
        var padded = Format == CookedTextureFormat.Bc1 ? BlockCompression.DecodeBc1(Levels[level], pw, ph) : BlockCompression.DecodeBc3(Levels[level], pw, ph);
        return pw == w && ph == h ? padded : TextureMips.Crop(padded, pw, w, h);
    }
}

// Mip chains, made on the CPU (issue #317): each level a box filter of the one before, on premultiplied RGBA8
// (premultiplied, so a transparent texel's colour does not bleed into its neighbours). The chain is the
// GPU's: levels down to 1×1, a side halving (rounded down, never below 1) at each, so a 64×16 texture has
// seven levels (64×16, 32×8, 16×4, 8×2, 4×1, 2×1, 1×1). Headless, so a test reads the levels it makes.
internal static class TextureMips
{
    // Levels in the full chain of a `width`×`height` texture: 1 + log2 of the longer side, rounded down.
    public static int LevelCount(int width, int height)
    {
        int levels = 1;
        for (int size = Math.Max(width, height); size > 1; size >>= 1) levels++;
        return levels;
    }

    public static (int Width, int Height) Size(int width, int height, int level) => (Math.Max(1, width >> level), Math.Max(1, height >> level));

    // A side rounded up to whole 4×4 blocks.
    public static int BlockAligned(int side) => (side + 3) & ~3;

    // The full chain of premultiplied RGBA8 `level0` (which becomes level 0 itself, not a copy).
    public static byte[][] Generate(byte[] level0, int width, int height)
    {
        var levels = new byte[LevelCount(width, height)][];
        levels[0] = level0;
        for (int i = 1; i < levels.Length; i++)
        {
            var (sw, sh) = Size(width, height, i - 1);
            var (dw, dh) = Size(width, height, i);
            levels[i] = Downsample(levels[i - 1], sw, sh, dw, dh);
        }
        return levels;
    }

    // A box filter from `sw`×`sh` to `dw`×`dh` (no larger): each texel is the rounded mean of the source
    // texels its footprint covers. Halving an even side averages pairs; an odd side's extra row or column
    // goes to the last texel's footprint, so every source texel counts once.
    public static byte[] Downsample(byte[] source, int sw, int sh, int dw, int dh)
    {
        var output = new byte[dw * dh * 4];
        for (int y = 0; y < dh; y++)
        {
            int y0 = y * sh / dh, y1 = (y + 1) * sh / dh;
            for (int x = 0; x < dw; x++)
            {
                int x0 = x * sw / dw, x1 = (x + 1) * sw / dw;
                int r = 0, g = 0, b = 0, a = 0;
                for (int sy = y0; sy < y1; sy++)
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int p = (sy * sw + sx) * 4;
                        r += source[p];
                        g += source[p + 1];
                        b += source[p + 2];
                        a += source[p + 3];
                    }
                int n = (x1 - x0) * (y1 - y0), half = n / 2, o = (y * dw + x) * 4;
                output[o] = (byte)((r + half) / n);
                output[o + 1] = (byte)((g + half) / n);
                output[o + 2] = (byte)((b + half) / n);
                output[o + 3] = (byte)((a + half) / n);
            }
        }
        return output;
    }

    // `rgba` (w×h) grown to `pw`×`ph` by repeating its last column and row: a level smaller than a block,
    // or not a multiple of four, filled out before it is block-compressed.
    public static byte[] Pad(byte[] rgba, int w, int h, int pw, int ph)
    {
        if (pw == w && ph == h) return rgba;
        var output = new byte[pw * ph * 4];
        for (int y = 0; y < ph; y++)
            for (int x = 0; x < pw; x++)
                Array.Copy(rgba, (Math.Min(y, h - 1) * w + Math.Min(x, w - 1)) * 4, output, (y * pw + x) * 4, 4);
        return output;
    }

    // The top-left `w`×`h` of a `pw`-wide RGBA8 image.
    public static byte[] Crop(byte[] rgba, int pw, int w, int h)
    {
        var output = new byte[w * h * 4];
        for (int y = 0; y < h; y++) Array.Copy(rgba, y * pw * 4, output, y * w * 4, w * 4);
        return output;
    }
}

// `.sgtex`, version 2 (issue #317 added the mip chain; a version 1 file is read as out of date and the loose
// file loads in its place, with a warning, until the game is cooked again):
//   "SGTX" int32 version  int64 sourceLength  uint64 sourceHash
//   byte format  int32 width  int32 height  int32 levels (1, or the full chain)
//   per level: int32 bytes  data
internal static class CookedTexture
{
    public const int Version = 2;
    private static ReadOnlySpan<byte> Magic => "SGTX"u8;

    // Premultiplies straight-alpha RGBA8 in place: what the client did to every loose texture at load.
    public static void Premultiply(Span<byte> rgba)
    {
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            int a = rgba[i + 3];
            if (a == 255) continue;
            rgba[i] = (byte)(rgba[i] * a / 255);
            rgba[i + 1] = (byte)(rgba[i + 1] * a / 255);
            rgba[i + 2] = (byte)(rgba[i + 2] * a / 255);
        }
    }

    // Decodes a PNG, JPEG or TGA (Daggerfall- and Quake-era mod art, issue #317) to straight-alpha RGBA8
    // headlessly, with StbImageSharp, which is what MonoGame's own Texture2D.FromStream used. Throws
    // InvalidDataException on a file it cannot decode.
    public static byte[] DecodeImage(byte[] file, out int width, out int height)
    {
        StbImageSharp.ImageResult image;
        try { image = StbImageSharp.ImageResult.FromMemory(file, StbImageSharp.ColorComponents.RedGreenBlueAlpha); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or NotSupportedException or OverflowException)
        {
            throw new InvalidDataException($"not an image this engine reads ({ex.Message})", ex);
        }
        if (image == null || image.Data == null || image.Width <= 0 || image.Height <= 0)
            throw new InvalidDataException("not an image this engine reads");
        width = image.Width;
        height = image.Height;
        return image.Data;
    }

    // A loose image file, cooked: decoded, premultiplied, mipmapped unless `mips` is off, compressed when
    // asked and possible.
    public static CookedTextureData Cook(byte[] file, bool compress, bool mips = true)
    {
        var rgba = DecodeImage(file, out int width, out int height);
        Premultiply(rgba);
        return FromRgba(rgba, width, height, compress, mips);
    }

    // Cooks premultiplied RGBA8: BC1 when every pixel is opaque, BC3 when not, RGBA when `compress` is off
    // or a side is not a multiple of four (a block format's unit). With `mips`, the whole chain, each level
    // made from the RGBA of the one before and compressed on its own.
    public static CookedTextureData FromRgba(byte[] premultiplied, int width, int height, bool compress, bool mips = true)
    {
        if (premultiplied.Length != width * height * 4) throw new ArgumentException($"{width}x{height} RGBA is {width * height * 4} bytes, not {premultiplied.Length}");
        var levels = mips && width > 0 && height > 0 ? TextureMips.Generate(premultiplied, width, height) : new[] { premultiplied };
        if (!compress || width % 4 != 0 || height % 4 != 0 || width == 0 || height == 0)
            return new CookedTextureData { Width = width, Height = height, Format = CookedTextureFormat.Rgba, Levels = levels };
        bool opaque = true;
        for (int i = 3; i < premultiplied.Length; i += 4)
            if (premultiplied[i] != 255) { opaque = false; break; }
        var format = opaque ? CookedTextureFormat.Bc1 : CookedTextureFormat.Bc3;
        var blocks = new byte[levels.Length][];
        for (int i = 0; i < levels.Length; i++)
        {
            var (w, h) = TextureMips.Size(width, height, i);
            int pw = TextureMips.BlockAligned(w), ph = TextureMips.BlockAligned(h);
            var padded = TextureMips.Pad(levels[i], w, h, pw, ph);
            blocks[i] = opaque ? BlockCompression.EncodeBc1(padded, pw, ph) : BlockCompression.EncodeBc3(padded, pw, ph);
        }
        return new CookedTextureData { Width = width, Height = height, Format = format, Levels = blocks };
    }

    // The bytes a level of a texture holds: RGBA's four a pixel, a block format's whole blocks.
    public static long LevelBytes(CookedTextureFormat format, int width, int height, int level)
    {
        var (w, h) = TextureMips.Size(width, height, level);
        long blocks = (long)(TextureMips.BlockAligned(w) / 4) * (TextureMips.BlockAligned(h) / 4);
        return format switch
        {
            CookedTextureFormat.Rgba => (long)w * h * 4,
            CookedTextureFormat.Bc1 => blocks * 8,
            CookedTextureFormat.Bc3 => blocks * 16,
            _ => throw new InvalidDataException($"texture format {(byte)format}"),
        };
    }

    public static void Write(Stream stream, CookedTextureData texture, in SourceStamp stamp)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        CookedAssets.WriteHeader(writer, Magic, Version, stamp);
        writer.Write((byte)texture.Format);
        writer.Write(texture.Width);
        writer.Write(texture.Height);
        writer.Write(texture.Levels.Length);
        foreach (var level in texture.Levels)
        {
            writer.Write(level.Length);
            writer.Write(level);
        }
    }

    public static CookedTextureData Read(Stream stream, out SourceStamp stamp)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        stamp = CookedAssets.ReadHeader(reader, Magic, Version, "cooked texture");
        var format = (CookedTextureFormat)reader.ReadByte();
        int width = reader.ReadInt32(), height = reader.ReadInt32(), count = reader.ReadInt32();
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384) throw new InvalidDataException($"a {width}x{height} texture");
        if (format > CookedTextureFormat.Bc3) throw new InvalidDataException($"texture format {(byte)format}");
        if (format != CookedTextureFormat.Rgba && (width % 4 != 0 || height % 4 != 0)) throw new InvalidDataException($"a {width}x{height} block texture");
        int full = TextureMips.LevelCount(width, height);
        if (count != 1 && count != full) throw new InvalidDataException($"{count} mip levels; a {width}x{height} texture has 1 or {full}");
        var levels = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            int bytes = reader.ReadInt32();
            long expected = LevelBytes(format, width, height, i);
            if (bytes != expected) throw new InvalidDataException($"{bytes} bytes in level {i}; a {width}x{height} {format} texture has {expected}");
            levels[i] = CookedAssets.ReadArray(reader, bytes);
        }
        return new CookedTextureData { Width = width, Height = height, Format = format, Levels = levels };
    }
}
