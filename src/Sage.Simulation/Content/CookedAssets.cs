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
//   textures/wall.png    → textures/wall.png.sgtex     premultiplied RGBA, or BC1/BC3 (DXT1/DXT5) blocks
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
        || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

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
//                          vertices × 32 or 52 bytes (MeshVertex, SkinnedMeshVertex), indices × int32
internal static class CookedMesh
{
    public const int Version = 1;
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

        writer.Write(mesh.Parts.Count);
        foreach (var part in mesh.Parts)
        {
            writer.Write((byte)(part.Skinned != null ? 1 : 0));
            writer.Write(part.VertexCount);
            writer.Write(part.Indices.Length);
            if (part.Skinned != null) writer.Write(MemoryMarshal.AsBytes(part.Skinned.AsSpan()));
            else writer.Write(MemoryMarshal.AsBytes(part.Rigid.AsSpan()));
            writer.Write(MemoryMarshal.AsBytes(part.Indices.AsSpan()));
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

// A texture's pixels, as cooked: always premultiplied (07 §13), one mip level (as Texture2D.FromStream).
internal sealed class CookedTextureData
{
    public required int Width;
    public required int Height;
    public required CookedTextureFormat Format;
    public required byte[] Data;

    // What the texture takes on the GPU.
    public long GpuBytes => Data.LongLength;

    // The pixels as premultiplied RGBA8, decoding blocks: for a device without S3TC, and for tests.
    public byte[] ToRgba() => Format switch
    {
        CookedTextureFormat.Rgba => Data,
        CookedTextureFormat.Bc1 => BlockCompression.DecodeBc1(Data, Width, Height),
        _ => BlockCompression.DecodeBc3(Data, Width, Height),
    };
}

// `.sgtex`:
//   "SGTX" int32 version  int64 sourceLength  uint64 sourceHash
//   byte format  int32 width  int32 height  int32 bytes  data
internal static class CookedTexture
{
    public const int Version = 1;
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

    // Decodes a PNG or JPEG to straight-alpha RGBA8 headlessly, as the client's Texture2D.FromStream does
    // (both are StbImageSharp). Throws InvalidDataException on a file it cannot decode.
    public static byte[] DecodeImage(byte[] file, out int width, out int height)
    {
        StbImageSharp.ImageResult image;
        try { image = StbImageSharp.ImageResult.FromMemory(file, StbImageSharp.ColorComponents.RedGreenBlueAlpha); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or NotSupportedException or OverflowException)
        {
            throw new InvalidDataException($"not an image this engine reads ({ex.Message})", ex);
        }
        width = image.Width;
        height = image.Height;
        return image.Data;
    }

    // A loose image file, cooked: decoded, premultiplied, compressed when asked and possible.
    public static CookedTextureData Cook(byte[] file, bool compress)
    {
        var rgba = DecodeImage(file, out int width, out int height);
        Premultiply(rgba);
        return FromRgba(rgba, width, height, compress);
    }

    // Cooks premultiplied RGBA8: BC1 when every pixel is opaque, BC3 when not, RGBA when `compress` is off
    // or a side is not a multiple of four (a block format's unit).
    public static CookedTextureData FromRgba(byte[] premultiplied, int width, int height, bool compress)
    {
        if (premultiplied.Length != width * height * 4) throw new ArgumentException($"{width}x{height} RGBA is {width * height * 4} bytes, not {premultiplied.Length}");
        if (!compress || width % 4 != 0 || height % 4 != 0 || width == 0 || height == 0)
            return new CookedTextureData { Width = width, Height = height, Format = CookedTextureFormat.Rgba, Data = premultiplied };
        bool opaque = true;
        for (int i = 3; i < premultiplied.Length; i += 4)
            if (premultiplied[i] != 255) { opaque = false; break; }
        return opaque
            ? new CookedTextureData { Width = width, Height = height, Format = CookedTextureFormat.Bc1, Data = BlockCompression.EncodeBc1(premultiplied, width, height) }
            : new CookedTextureData { Width = width, Height = height, Format = CookedTextureFormat.Bc3, Data = BlockCompression.EncodeBc3(premultiplied, width, height) };
    }

    public static void Write(Stream stream, CookedTextureData texture, in SourceStamp stamp)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        CookedAssets.WriteHeader(writer, Magic, Version, stamp);
        writer.Write((byte)texture.Format);
        writer.Write(texture.Width);
        writer.Write(texture.Height);
        writer.Write(texture.Data.Length);
        writer.Write(texture.Data);
    }

    public static CookedTextureData Read(Stream stream, out SourceStamp stamp)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        stamp = CookedAssets.ReadHeader(reader, Magic, Version, "cooked texture");
        var format = (CookedTextureFormat)reader.ReadByte();
        int width = reader.ReadInt32(), height = reader.ReadInt32(), bytes = reader.ReadInt32();
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384) throw new InvalidDataException($"a {width}x{height} texture");
        long expected = format switch
        {
            CookedTextureFormat.Rgba => (long)width * height * 4,
            CookedTextureFormat.Bc1 => (long)width * height / 2,
            CookedTextureFormat.Bc3 => (long)width * height,
            _ => throw new InvalidDataException($"texture format {(byte)format}"),
        };
        if (format != CookedTextureFormat.Rgba && (width % 4 != 0 || height % 4 != 0)) throw new InvalidDataException($"a {width}x{height} block texture");
        if (bytes != expected) throw new InvalidDataException($"{bytes} bytes of pixels; a {width}x{height} {format} texture has {expected}");
        return new CookedTextureData { Width = width, Height = height, Format = format, Data = CookedAssets.ReadArray(reader, bytes) };
    }
}
