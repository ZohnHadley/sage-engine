#nullable enable
using System.Diagnostics;
using System.Numerics;
using Sage.Cli;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Cooked assets (issue #302, docs/design/05 §7): `sage cook` writes a `.sgmesh` beside each `.glb` and a `.sgtex`
// beside each `.png`/`.jpg`, `sage package` runs it, and the client reads them in place of the loose files,
// falling back to the loose file when one is missing, unreadable or out of date, or when a later mount has its
// own loose copy. The client's own reading (ContentService, Renderer) is a thin layer over CookedAssets and
// MeshGeometry, which are tested here headless; the smoke run boots the real client.
public class CookedAssetTests
{
    public CookedAssetTests() { _ = TestEnv.UserRoot; }

    internal static string Repo => TestEnv.FolderAbove("Sage.sln");
    internal static string SandboxContent => Path.Combine(Repo, "games", "Sandbox", "content");
    private static string Skeletal => Path.Combine(Repo, "tests", "games", "skeletal", "content", "models", "mannequin.glb");
    private static string AudioFolder => Path.Combine(Repo, "tests", "Sage.Tests", "Content", "Audio");

    // ---- Meshes ----

    public static TheoryData<string> Models() => new()
    {
        Path.Combine("games", "Sandbox", "content", "models", "bunny.glb"),
        Path.Combine("games", "Sandbox", "content", "models", "arms.glb"),
        Path.Combine("games", "Sandbox", "content", "models", "viewmodel_crossbow.glb"),
        Path.Combine("tests", "games", "skeletal", "content", "models", "mannequin.glb"),
        Path.Combine("games", "Sandbox", "content", "models", "brick_wall.glb"),
    };

    [Theory]
    [MemberData(nameof(Models))]
    public void ACookedMeshReadsBackAsTheGeometryOfItsGlb(string model)
    {
        byte[] glb = File.ReadAllBytes(Path.Combine(Repo, model));
        var loose = MeshGeometry.ReadGlb(new MemoryStream(glb), model)!;
        Assert.NotNull(loose);

        var file = new MemoryStream();
        CookedMesh.Write(file, loose, SourceStamp.Of(glb));
        file.Position = 0;
        var cooked = CookedMesh.Read(file, out var stamp);

        Assert.Equal(SourceStamp.Of(glb), stamp);
        Assert.Equal(loose.BoundsCentre, cooked.BoundsCentre);
        Assert.Equal(loose.BoundsRadius, cooked.BoundsRadius);
        Assert.Equal(loose.Parts.Count, cooked.Parts.Count);
        for (int p = 0; p < loose.Parts.Count; p++)
        {
            Assert.Equal(loose.Parts[p].Indices, cooked.Parts[p].Indices);
            Assert.Equal(loose.Parts[p].Rigid, cooked.Parts[p].Rigid);
            Assert.Equal(loose.Parts[p].Skinned, cooked.Parts[p].Skinned);
        }
        Assert.Equal(loose.Skin == null, cooked.Skin == null);
        if (loose.Skin != null)
        {
            Assert.Equal(loose.Skin.JointNames, cooked.Skin!.JointNames);
            Assert.Equal(loose.Skin.InverseBind, cooked.Skin.InverseBind);
            Assert.Equal(loose.Skin.RestJoints, cooked.Skin.RestJoints);
            Assert.Equal(loose.Skin.LogicalIndex, cooked.Skin.LogicalIndex);
        }
    }

    [Fact]
    public void TheSkinnedMannequinCooksWithItsSkinAndSkinnedVertices()
    {
        var mesh = MeshGeometry.ReadGlb(File.OpenRead(Skeletal), "mannequin")!;
        Assert.NotNull(mesh.Skin);
        Assert.Contains(mesh.Parts, p => p.Skinned != null);
        // Weights normalised, joints inside the skin: what the loader always did (#117), now headless.
        foreach (var v in mesh.Parts.Where(p => p.Skinned != null).SelectMany(p => p.Skinned!))
        {
            Assert.Equal(1f, v.Weights.X + v.Weights.Y + v.Weights.Z + v.Weights.W, 3);
            Assert.True(Math.Max(Math.Max(v.Joint0, v.Joint1), Math.Max(v.Joint2, v.Joint3)) < mesh.Skin!.JointNames.Length);
        }
    }

    [Fact]
    public void TheVertexStructsHaveTheLayoutTheClientsVertexBuffersTake()
    {
        // VertexMesh is 52 bytes (issue #410: TANGENT at 32, COLOR at 48), VertexSkinned 72 (BLENDINDICES at 32,
        // BLENDWEIGHT at 36, TANGENT at 52, COLOR at 68).
        Assert.Equal(52, MeshVertex.Size);
        Assert.Equal(72, SkinnedMeshVertex.Size);
        Assert.Equal(MeshVertex.Size, System.Runtime.InteropServices.Marshal.SizeOf<MeshVertex>());
        Assert.Equal(SkinnedMeshVertex.Size, System.Runtime.InteropServices.Marshal.SizeOf<SkinnedMeshVertex>());
        Assert.Equal(32, (int)System.Runtime.InteropServices.Marshal.OffsetOf<MeshVertex>(nameof(MeshVertex.Tangent)));
        Assert.Equal(48, (int)System.Runtime.InteropServices.Marshal.OffsetOf<MeshVertex>(nameof(MeshVertex.Colour)));
        Assert.Equal(32, (int)System.Runtime.InteropServices.Marshal.OffsetOf<SkinnedMeshVertex>(nameof(SkinnedMeshVertex.Joint0)));
        Assert.Equal(36, (int)System.Runtime.InteropServices.Marshal.OffsetOf<SkinnedMeshVertex>(nameof(SkinnedMeshVertex.Weights)));
        Assert.Equal(52, (int)System.Runtime.InteropServices.Marshal.OffsetOf<SkinnedMeshVertex>(nameof(SkinnedMeshVertex.Tangent)));
        Assert.Equal(68, (int)System.Runtime.InteropServices.Marshal.OffsetOf<SkinnedMeshVertex>(nameof(SkinnedMeshVertex.Colour)));
    }

    [Fact]
    public void ACutShortOrForeignCookedMeshIsRefused()
    {
        byte[] glb = File.ReadAllBytes(Path.Combine(SandboxContent, "models", "bunny.glb"));
        var file = new MemoryStream();
        CookedMesh.Write(file, MeshGeometry.ReadGlb(new MemoryStream(glb), "bunny")!, SourceStamp.Of(glb));
        byte[] bytes = file.ToArray();

        Assert.ThrowsAny<IOException>(() => CookedMesh.Read(new MemoryStream(bytes[..(bytes.Length / 2)]), out _));
        Assert.Throws<InvalidDataException>(() => CookedMesh.Read(new MemoryStream(glb), out _));
        bytes[4] = 99;   // a format version this engine does not read
        Assert.Throws<InvalidDataException>(() => CookedMesh.Read(new MemoryStream(bytes), out _));
        bytes[4] = 1;    // nor one cooked before vertices had tangents and colours (issue #410): it is cooked again
        Assert.Contains("cook again", Assert.Throws<InvalidDataException>(() => CookedMesh.Read(new MemoryStream(bytes), out _)).Message);
    }

    // ---- Textures ----

    [Fact]
    public void BlockCompressionIsExactOnFlatBlocksAndCloseOnGradients()
    {
        const int w = 16, h = 8;
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = (y * w + x) * 4;
                bool flat = x < 4;   // the first column of blocks is one colour
                rgba[p] = (byte)(flat ? 200 : x * 16);
                rgba[p + 1] = (byte)(flat ? 40 : x * 8 + y);   // a gradient along one line through colour space
                rgba[p + 2] = (byte)(flat ? 8 : 255 - x * 16);
                rgba[p + 3] = 255;
            }

        var bc1 = BlockCompression.DecodeBc1(BlockCompression.EncodeBc1(rgba, w, h), w, h);
        Assert.Equal(w * h / 2, BlockCompression.EncodeBc1(rgba, w, h).Length);
        double error = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 4; c++)
                {
                    int p = (y * w + x) * 4 + c;
                    if (x < 4) Assert.InRange(Math.Abs(bc1[p] - rgba[p]), 0, 4);   // 565 rounding only
                    error += Math.Abs(bc1[p] - rgba[p]);
                }
        Assert.True(error / rgba.Length < 3, $"mean BC1 error {error / rgba.Length:F2}");

        // BC3 keeps alpha: a ramp from transparent to opaque comes back within one alpha step.
        for (int i = 0; i < w * h; i++) rgba[i * 4 + 3] = (byte)(i % w * 17);
        var bc3 = BlockCompression.DecodeBc3(BlockCompression.EncodeBc3(rgba, w, h), w, h);
        for (int i = 0; i < w * h; i++) Assert.InRange(Math.Abs(bc3[i * 4 + 3] - rgba[i * 4 + 3]), 0, 9);
    }

    [Fact]
    public void ATextureCooksToBlocksWhenItCanAndToExactPremultipliedPixelsWhenItCannot()
    {
        byte[] wall = File.ReadAllBytes(Path.Combine(SandboxContent, "textures", "hut_wall.png"));     // 64x64, opaque
        byte[] sprite = File.ReadAllBytes(Path.Combine(SandboxContent, "textures", "creature.png"));   // 512x192, alpha
        byte[] sword = File.ReadAllBytes(Path.Combine(SandboxContent, "textures", "sword.png"));       // 48x64, alpha

        var opaque = CookedTexture.Cook(wall, compress: true);
        Assert.Equal(CookedTextureFormat.Bc1, opaque.Format);
        Assert.Equal(64 * 64 / 2, opaque.Data.Length);   // an eighth of RGBA (level 0; the mips are below)

        var alpha = CookedTexture.Cook(sprite, compress: true);
        Assert.Equal(CookedTextureFormat.Bc3, alpha.Format);
        Assert.Equal(512 * 192, alpha.Data.Length);      // a quarter

        // Uncompressed: exactly what the client made of the PNG at load (decode, then premultiply).
        var exact = CookedTexture.Cook(sword, compress: false);
        Assert.Equal(CookedTextureFormat.Rgba, exact.Format);
        var expected = CookedTexture.DecodeImage(sword, out int width, out int height);
        CookedTexture.Premultiply(expected);
        Assert.Equal((48, 64), (width, height));
        Assert.Equal(expected, exact.ToRgba());

        // Round trip through the file.
        var file = new MemoryStream();
        CookedTexture.Write(file, alpha, SourceStamp.Of(sprite));
        file.Position = 0;
        var read = CookedTexture.Read(file, out var stamp);
        Assert.Equal(SourceStamp.Of(sprite), stamp);
        Assert.Equal((alpha.Width, alpha.Height, alpha.Format), (read.Width, read.Height, read.Format));
        Assert.Equal(alpha.Levels, read.Levels);

        // A side that is not a multiple of four is kept as RGBA rather than padded.
        var odd = CookedTexture.FromRgba(new byte[6 * 4 * 4], 6, 4, compress: true);
        Assert.Equal(CookedTextureFormat.Rgba, odd.Format);
    }

    [Fact]
    public void PremultiplyIsTheClientsFormula()
    {
        var pixels = new byte[] { 255, 128, 0, 128, 10, 20, 30, 255, 200, 200, 200, 0 };
        CookedTexture.Premultiply(pixels);
        Assert.Equal(new byte[] { 128, 64, 0, 128, 10, 20, 30, 255, 0, 0, 0, 0 }, pixels);
    }

    [Fact]
    public void AFileThatIsNotAnImageIsRefusedWithInvalidData()
    {
        Assert.Throws<InvalidDataException>(() => CookedTexture.DecodeImage("not a png"u8.ToArray(), out _, out _));
    }

    // ---- Mips and TGA (issue #317) ----

    private static byte[] Pixels(params byte[] rgba) => rgba;

    [Fact]
    public void AMipChainHalvesEachSideDownToOneTexelAndBoxFiltersPremultipliedPixels()
    {
        Assert.Equal(7, TextureMips.LevelCount(64, 16));
        Assert.Equal(new[] { (64, 16), (32, 8), (16, 4), (8, 2), (4, 1), (2, 1), (1, 1) },
                     Enumerable.Range(0, 7).Select(l => TextureMips.Size(64, 16, l)));
        Assert.Equal(1, TextureMips.LevelCount(1, 1));
        Assert.Equal(3, TextureMips.LevelCount(5, 3));   // 5x3, 2x1, 1x1

        // 4x2: opaque red beside transparent (premultiplied: zero colour), then opaque white beside opaque black.
        var level0 = Pixels(
            255, 0, 0, 255,  0, 0, 0, 0,        255, 255, 255, 255,  0, 0, 0, 255,
            255, 0, 0, 255,  0, 0, 0, 0,        255, 255, 255, 255,  0, 0, 0, 255);
        var chain = TextureMips.Generate(level0, 4, 2);
        Assert.Equal(3, chain.Length);
        Assert.Same(level0, chain[0]);
        // Level 1 (2x1): half-covered red, which is pure red again once divided by its alpha (no dark fringe),
        // and mid grey.
        Assert.Equal(Pixels(128, 0, 0, 128,  128, 128, 128, 255), chain[1]);
        // Level 2 (1x1): the mean of all eight.
        Assert.Equal(Pixels(128, 64, 64, 192), chain[2]);

        // An odd side: every source texel counts once, so a flat colour stays exactly that colour.
        var flat = Enumerable.Repeat(new byte[] { 10, 20, 30, 40 }, 5 * 3).SelectMany(p => p).ToArray();
        foreach (var level in TextureMips.Generate(flat, 5, 3))
            for (int i = 0; i < level.Length; i += 4) Assert.Equal(new byte[] { 10, 20, 30, 40 }, level[i..(i + 4)]);
    }

    [Fact]
    public void ACookedTextureStoresItsMipsUncompressedOrAsBlocksAndReadsThemBack()
    {
        byte[] wall = File.ReadAllBytes(Path.Combine(SandboxContent, "textures", "hut_wall.png"));   // 64x64, opaque
        var rgba = CookedTexture.DecodeImage(wall, out int width, out int height);
        CookedTexture.Premultiply(rgba);
        var expected = TextureMips.Generate((byte[])rgba.Clone(), width, height);

        // Uncompressed: exactly the box-filtered chain, 64x64 down to 1x1.
        var exact = CookedTexture.Cook(wall, compress: false);
        Assert.Equal(7, exact.LevelCount);
        for (int level = 0; level < exact.LevelCount; level++) Assert.Equal(expected[level], exact.ToRgba(level));
        Assert.Equal(expected.Sum(l => (long)l.Length), exact.GpuBytes);

        // BC1: every level is blocks, the ones under 4x4 a whole block each, and decodes close to the RGBA chain.
        var bc1 = CookedTexture.Cook(wall, compress: true);
        Assert.Equal((CookedTextureFormat.Bc1, 7), (bc1.Format, bc1.LevelCount));
        Assert.Equal(new[] { 2048, 512, 128, 32, 8, 8, 8 }, bc1.Levels.Select(l => l.Length));
        for (int level = 0; level < bc1.LevelCount; level++)
        {
            var decoded = bc1.ToRgba(level);
            Assert.Equal(expected[level].Length, decoded.Length);
            double error = 0;
            for (int i = 0; i < decoded.Length; i++) error += Math.Abs(decoded[i] - expected[level][i]);
            Assert.True(error / decoded.Length < 8, $"level {level}: mean BC1 error {error / decoded.Length:F2}");
        }

        // A block texture whose small levels are not multiples of four (12x4: 6x2, 3x1, 1x1) pads them.
        var alpha = new byte[12 * 4 * 4];
        for (int i = 0; i < alpha.Length; i += 4) alpha[i] = alpha[i + 3] = (byte)(i % 3 == 0 ? 255 : 128);
        var bc3 = CookedTexture.FromRgba(alpha, 12, 4, compress: true);
        Assert.Equal((CookedTextureFormat.Bc3, 4), (bc3.Format, bc3.LevelCount));
        Assert.Equal(new[] { 48, 32, 16, 16 }, bc3.Levels.Select(l => l.Length));
        Assert.Equal(6 * 2 * 4, bc3.ToRgba(1).Length);

        // Both round-trip through the file, level for level.
        foreach (var texture in new[] { exact, bc1, bc3 })
        {
            var file = new MemoryStream();
            CookedTexture.Write(file, texture, SourceStamp.Of(wall));
            file.Position = 0;
            var read = CookedTexture.Read(file, out _);
            Assert.Equal((texture.Width, texture.Height, texture.Format), (read.Width, read.Height, read.Format));
            Assert.Equal(texture.Levels, read.Levels);
        }

        // Without mips (a caller that asks for none): one level.
        Assert.Equal(1, CookedTexture.Cook(wall, compress: true, mips: false).LevelCount);
    }

    // A `.sgtex` as #302 wrote it (version 1: one level, no mips).
    private static byte[] Version1Texture(byte[] png)
    {
        var data = CookedTexture.Cook(png, compress: false, mips: false);
        var file = new MemoryStream();
        using (var writer = new BinaryWriter(file, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            var stamp = SourceStamp.Of(png);
            writer.Write("SGTX"u8);
            writer.Write(1);
            writer.Write(stamp.Length);
            writer.Write(stamp.Hash);
            writer.Write((byte)data.Format);
            writer.Write(data.Width);
            writer.Write(data.Height);
            writer.Write(data.Data.Length);
            writer.Write(data.Data);
        }
        return file.ToArray();
    }

    [Fact]
    public void ACookedTextureFromBeforeMipsIsStaleAndTheLooseFileLoadsInsteadWithAWarning()
    {
        byte[] png = File.ReadAllBytes(Path.Combine(SandboxContent, "textures", "hut_wall.png"));
        var fixture = new MountFixture();
        string unique = Guid.NewGuid().ToString("N");
        WriteBytes(fixture, "game", $"t{unique}/wall.png", png);
        WriteBytes(fixture, "game", $"t{unique}/wall.png.sgtex", Version1Texture(png));
        fixture.Mount("game", "game");
        var path = VirtualPath.Parse($"t{unique}/wall.png");

        using var log = new CaptureSink();
        Assert.Null(CookedAssets.LoadTexture(fixture.Vfs, path));
        Log.Flush();
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains(unique, StringComparison.Ordinal)
                                          && e.Message.Contains("version 1", StringComparison.Ordinal) && e.Message.Contains("cook again", StringComparison.Ordinal));

        // The loose file, read as the client reads it, has its mips.
        var loose = CookedAssets.ReadLooseTexture(fixture.Vfs.Which(path)!, path);
        Assert.Equal((CookedTextureFormat.Rgba, 7), (loose.Format, loose.LevelCount));

        // And `sage cook` sees the old file as out of date and writes the new one.
        string game = TestEnv.NewTempDir();
        File.WriteAllText(Path.Combine(game, "game.json"), """{ "id": "cooktest", "mounts": ["content"] }""");
        Directory.CreateDirectory(Path.Combine(game, "content"));
        File.WriteAllBytes(Path.Combine(game, "content", "wall.png"), png);
        File.WriteAllBytes(Path.Combine(game, "content", "wall.png.sgtex"), Version1Texture(png));
        var cooked = GameCook.Run(game);
        Assert.Equal(new[] { "wall.png" }, cooked.Cooked.Select(c => c.Path));
        using var stream = File.OpenRead(Path.Combine(game, "content", "wall.png.sgtex"));
        Assert.Equal(7, CookedTexture.Read(stream, out _).LevelCount);
    }

    // A 32-bit uncompressed TGA (image type 2), rows stored bottom-up as the format's default origin is, BGRA.
    // `rows` are top-down RGBA.
    internal static byte[] Tga(int width, int height, byte[] rows)
    {
        var file = new MemoryStream();
        file.Write(new byte[] { 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 32, 8 });
        for (int y = height - 1; y >= 0; y--)
            for (int x = 0; x < width; x++)
            {
                int p = (y * width + x) * 4;
                file.Write(new[] { rows[p + 2], rows[p + 1], rows[p], rows[p + 3] });
            }
        return file.ToArray();
    }

    [Fact]
    public void ATgaFromAModFolderLoadsPremultipliedWithItsMipsAndIsCooked()
    {
        // 2x2: red and half-transparent green on top, blue and white below.
        var rows = Pixels(255, 0, 0, 255,  0, 255, 0, 128,
                          0, 0, 255, 255,  255, 255, 255, 255);
        byte[] tga = Tga(2, 2, rows);

        var fixture = new MountFixture();
        WriteBytes(fixture, "game", "textures/sign.png", File.ReadAllBytes(Path.Combine(SandboxContent, "textures", "hut_wall.png")));
        fixture.Mount("game", "game");
        WriteBytes(fixture, "mod", "textures/daggerfall/sign.tga", tga);
        var mod = fixture.Mount("mod", "mymod");
        var path = VirtualPath.Parse("textures/daggerfall/sign.tga");
        Assert.Same(mod, fixture.Vfs.Which(path));
        Assert.Null(CookedAssets.LoadTexture(fixture.Vfs, path));   // nothing cooked: the loose file is read

        var texture = CookedAssets.ReadLooseTexture(fixture.Vfs.Which(path)!, path);
        Assert.Equal((2, 2, CookedTextureFormat.Rgba, 2), (texture.Width, texture.Height, texture.Format, texture.LevelCount));
        Assert.Equal(Pixels(255, 0, 0, 255,  0, 128, 0, 128,      // top row first, green premultiplied
                            0, 0, 255, 255,  255, 255, 255, 255), texture.ToRgba(0));
        Assert.Equal(Pixels(128, 96, 128, 223), texture.ToRgba(1));    // the 1x1 level: the mean of the four

        // `sage cook` cooks a .tga as it does a .png.
        Assert.True(CookedAssets.IsCookableTexture("textures/sign.TGA"));
        string game = TestEnv.NewTempDir();
        File.WriteAllText(Path.Combine(game, "game.json"), """{ "id": "cooktest", "mounts": ["content"] }""");
        Directory.CreateDirectory(Path.Combine(game, "content"));
        File.WriteAllBytes(Path.Combine(game, "content", "sign.tga"), tga);
        var cooked = GameCook.Run(game);
        Assert.True(cooked.Ok, string.Join("\n", cooked.Errors));
        Assert.Equal(new[] { "sign.tga" }, cooked.Cooked.Select(c => c.Path));
        Assert.True(File.Exists(Path.Combine(game, "content", "sign.tga.sgtex")));
    }

    // ---- Which file the client reads ----

    private static void WriteBytes(MountFixture fixture, string mount, string relative, byte[] bytes)
    {
        string file = Path.Combine(fixture.Dir(mount), relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, bytes);
    }

    private static byte[] Cooked(byte[] png, bool compress = true)
    {
        var file = new MemoryStream();
        CookedTexture.Write(file, CookedTexture.Cook(png, compress), SourceStamp.Of(png));
        return file.ToArray();
    }

    [Fact]
    public void TheCookedFileStandsInForTheLooseOneInItsMountAndALaterMountsLooseFileWins()
    {
        byte[] png = File.ReadAllBytes(Path.Combine(SandboxContent, "textures", "hut_wall.png"));
        var fixture = new MountFixture();
        WriteBytes(fixture, "game", "textures/wall.png", png);
        WriteBytes(fixture, "game", "textures/wall.png.sgtex", Cooked(png));
        fixture.Mount("game", "game");
        var path = VirtualPath.Parse("textures/wall.png");

        Assert.Equal(VirtualPath.Parse("textures/wall.png.sgtex"), CookedAssets.Find(fixture.Vfs, path, CookedAssets.TextureExtension));
        Assert.Equal(CookedTextureFormat.Bc1, CookedAssets.LoadTexture(fixture.Vfs, path)!.Format);
        Assert.Null(CookedAssets.Find(fixture.Vfs, VirtualPath.Parse("textures/other.png"), CookedAssets.TextureExtension));

        // A mod, mounted after the game, ships its own loose wall.png: the game's cooked one was made from the
        // file the mod replaced, so the mod's loose file is read.
        WriteBytes(fixture, "mod", "textures/wall.png", png);
        fixture.Mount("mod", "mod");
        Assert.Null(CookedAssets.Find(fixture.Vfs, path, CookedAssets.TextureExtension));
        Assert.Null(CookedAssets.LoadTexture(fixture.Vfs, path));

        // A mod that ships its own cooked copy too is read cooked again.
        WriteBytes(fixture, "mod", "textures/wall.png.sgtex", Cooked(png, compress: false));
        Assert.Equal(CookedTextureFormat.Rgba, CookedAssets.LoadTexture(fixture.Vfs, path)!.Format);
    }

    [Fact]
    public void AnUnreadableOrOutOfDateCookedFileFallsBackToTheLooseOneWithAWarning()
    {
        byte[] png = File.ReadAllBytes(Path.Combine(SandboxContent, "textures", "hut_wall.png"));
        byte[] glb = File.ReadAllBytes(Path.Combine(SandboxContent, "models", "bunny.glb"));
        var fixture = new MountFixture();
        string unique = Guid.NewGuid().ToString("N");
        WriteBytes(fixture, "game", $"t{unique}/broken.png", png);
        WriteBytes(fixture, "game", $"t{unique}/broken.png.sgtex", "SGTX garbage"u8.ToArray());
        WriteBytes(fixture, "game", $"t{unique}/edited.png", png.Concat(new byte[] { 0 }).ToArray());   // changed after the cook
        WriteBytes(fixture, "game", $"t{unique}/edited.png.sgtex", Cooked(png));
        WriteBytes(fixture, "game", $"m{unique}/bunny.glb", glb);
        WriteBytes(fixture, "game", $"m{unique}/bunny.glb.sgmesh", new byte[] { 1, 2, 3 });
        fixture.Mount("game", "game");

        using var log = new CaptureSink();
        Assert.Null(CookedAssets.LoadTexture(fixture.Vfs, VirtualPath.Parse($"t{unique}/broken.png")));
        Assert.Null(CookedAssets.LoadTexture(fixture.Vfs, VirtualPath.Parse($"t{unique}/edited.png")));
        Assert.Null(CookedAssets.LoadMesh(fixture.Vfs, VirtualPath.Parse($"m{unique}/bunny.glb")));
        Log.Flush();
        var warnings = log.Entries.Where(e => e.Level == LogLevel.Warn && e.Message.Contains(unique, StringComparison.Ordinal)).Select(e => e.Message).ToList();
        Assert.Contains(warnings, m => m.Contains("broken.png.sgtex", StringComparison.Ordinal) && m.Contains("cannot be read", StringComparison.Ordinal));
        Assert.Contains(warnings, m => m.Contains("edited.png.sgtex", StringComparison.Ordinal) && m.Contains("cook again", StringComparison.Ordinal));
        Assert.Contains(warnings, m => m.Contains("bunny.glb.sgmesh", StringComparison.Ordinal) && m.Contains("reading the loose file", StringComparison.Ordinal));
    }

    // ---- Ogg Vorbis ----

    [Fact]
    public void AnOggFileDecodesToPcmMonoOrStereo()
    {
        using (var mono = File.OpenRead(Path.Combine(AudioFolder, "tone_mono.ogg")))
        {
            var pcm = OggVorbis.Decode(mono);
            Assert.Equal((1, 22050), (pcm.Channels, pcm.SampleRate));
            Assert.InRange(pcm.Seconds, 0.2, 0.3);
            // A 440 Hz tone, not silence: the peak is well above the noise floor.
            int peak = 0;
            for (int i = 0; i + 1 < pcm.Samples.Length; i += 2) peak = Math.Max(peak, Math.Abs((int)(short)(pcm.Samples[i] | pcm.Samples[i + 1] << 8)));
            Assert.True(peak > 3000, $"peak {peak}");
        }
        using (var stereo = File.OpenRead(Path.Combine(AudioFolder, "tone_stereo.ogg")))
        {
            var pcm = OggVorbis.Decode(stereo);
            Assert.Equal((2, 44100), (pcm.Channels, pcm.SampleRate));
            Assert.InRange(pcm.Seconds, 0.45, 0.55);
        }
    }

    [Fact]
    public void AFileThatIsNotOggVorbisIsRefusedWithInvalidData()
    {
        Assert.Throws<InvalidDataException>(() => OggVorbis.Decode(File.OpenRead(Path.Combine(SandboxContent, "audio", "pickup.wav"))));
        Assert.Throws<InvalidDataException>(() => OggVorbis.Decode(new MemoryStream("OggS but not really"u8.ToArray())));
    }

    // ---- The cook ----

    private static string CopyGame(string uncompressed = "")
    {
        string game = TestEnv.NewTempDir();
        File.WriteAllText(Path.Combine(game, "game.json"),
            $$"""{ "id": "cooktest", "mounts": ["content"] {{(uncompressed.Length > 0 ? $", \"cook\": {{ \"uncompressed\": [\"{uncompressed}\"] }}" : "")}} }""");
        foreach (string file in new[] { "models/bunny.glb", "models/arms.glb", "textures/hut_wall.png", "textures/creature.png", "textures/sword.png", "audio/pickup.wav" })
        {
            string target = Path.Combine(game, "content", file);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(SandboxContent, file), target);
        }
        return game;
    }

    [Fact]
    public void CookWritesACookedFileBesideEveryModelAndTextureAndSkipsWhatIsUpToDate()
    {
        string game = CopyGame(uncompressed: "textures/sw*.png");
        File.WriteAllText(Path.Combine(game, "content", "textures", "broken.png"), "not a png");

        var first = GameCook.Run(game);
        Assert.True(first.Ok, string.Join("\n", first.Errors));
        foreach (string file in new[] { "models/bunny.glb.sgmesh", "models/arms.glb.sgmesh", "textures/hut_wall.png.sgtex", "textures/creature.png.sgtex", "textures/sword.png.sgtex" })
            Assert.True(File.Exists(Path.Combine(game, "content", file)), file);
        Assert.False(File.Exists(Path.Combine(game, "content", "audio", "pickup.wav.sgtex")));
        Assert.Equal("Bc1", first.Cooked.Single(c => c.Path == "textures/hut_wall.png").Format);
        Assert.Equal("Bc3", first.Cooked.Single(c => c.Path == "textures/creature.png").Format);
        Assert.Equal("Rgba", first.Cooked.Single(c => c.Path == "textures/sword.png").Format);   // game.json "cook": "uncompressed"
        // A file that will not decode is a warning (it loads loose, and fails there the same way), not cooked.
        Assert.Contains(first.Warnings, w => w.Contains("broken.png", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(game, "content", "textures", "broken.png.sgtex")));

        // Again: everything is up to date. Edit one file and only it is cooked again; --force cooks all.
        var again = GameCook.Run(game);
        Assert.Empty(again.Cooked);
        Assert.Equal(5, again.UpToDate);
        File.Copy(Path.Combine(SandboxContent, "textures", "hut_roof.png"), Path.Combine(game, "content", "textures", "hut_wall.png"), overwrite: true);
        Assert.Equal(new[] { "textures/hut_wall.png" }, GameCook.Run(game).Cooked.Select(c => c.Path));
        Assert.Equal(5, GameCook.Run(game, force: true).Cooked.Count);

        // --clean takes them all away again.
        var clean = GameCook.Run(game, clean: true);
        Assert.Equal(5, clean.Removed);
        Assert.Empty(Directory.EnumerateFiles(game, "*.sg*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("textures/ui/**", "textures/ui/frame.png", true)]
    [InlineData("textures/ui/**", "textures/ui/deep/frame.png", true)]
    [InlineData("textures/ui/**", "textures/frame.png", false)]
    [InlineData("*.png", "frame.png", true)]
    [InlineData("*.png", "textures/frame.png", false)]
    [InlineData("**/*.png", "textures/frame.png", true)]
    [InlineData("**/*.png", "frame.png", true)]
    [InlineData("textures/sprite_?.png", "textures/sprite_a.png", true)]
    public void UncompressedPatternsAreGlobsInsideAMount(string pattern, string path, bool matches)
    {
        Assert.Equal(matches, GameCook.Glob(pattern).IsMatch(path));
    }

    [Fact]
    public void GameJsonCookSettingsAreReadAndAMisspeltKeyIsRefused()
    {
        string game = TestEnv.NewTempDir();
        File.WriteAllText(Path.Combine(game, "game.json"), """{ "id": "g", "cook": { "compress": false, "uncompressed": ["ui/**"] } }""");
        var manifest = GameManifest.Load(game);
        Assert.False(manifest.Cook.Compress);
        Assert.Equal(new[] { "ui/**" }, manifest.Cook.Uncompressed);

        File.WriteAllText(Path.Combine(game, "game.json"), """{ "id": "g", "cook": { "compresss": false } }""");
        Assert.Throws<InvalidDataException>(() => GameManifest.Load(game));
    }
}

// What cooking buys, measured (issue #302's done criterion): the CPU time and allocation of loading the Sandbox's
// models and textures loose — the glTF parse, the image decode, premultiply and mips the client does — against
// reading their cooked files, and the texture memory before and after. Disk reads are left out of both (the
// bytes are in memory), so this is the decode the cook saves. Alone in the Measurements collection, since it
// times things.
[Collection(MeasurementsCollection.Name)]
public class CookedLoadMeasurementTests
{
    public CookedLoadMeasurementTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void CookedAssetsLoadFasterWithLessAllocationAndTexturesTakeLessMemory()
    {
        var models = Directory.GetFiles(Path.Combine(CookedAssetTests.SandboxContent, "models"), "*.glb").Select(File.ReadAllBytes).ToList();
        var textures = Directory.GetFiles(Path.Combine(CookedAssetTests.SandboxContent, "textures"), "*.png").Select(File.ReadAllBytes).ToList();
        Assert.NotEmpty(models);
        Assert.NotEmpty(textures);

        var cookedModels = models.Select(glb =>
        {
            var file = new MemoryStream();
            CookedMesh.Write(file, MeshGeometry.ReadGlb(new MemoryStream(glb), "m")!, SourceStamp.Of(glb));
            return file.ToArray();
        }).ToList();
        var cookedTextures = textures.Select(png =>
        {
            var file = new MemoryStream();
            CookedTexture.Write(file, CookedTexture.Cook(png, compress: true), SourceStamp.Of(png));
            return file.ToArray();
        }).ToList();

        void LoadLoose()
        {
            foreach (var glb in models) MeshGeometry.ReadGlb(new MemoryStream(glb), "m");
            foreach (var png in textures)
            {
                var rgba = CookedTexture.DecodeImage(png, out int w, out int h);
                CookedTexture.Premultiply(rgba);
                TextureMips.Generate(rgba, w, h);   // a loose texture gets its mips at load (issue #317)
            }
        }
        void LoadCooked()
        {
            foreach (var sgmesh in cookedModels) CookedMesh.Read(new MemoryStream(sgmesh), out _);
            foreach (var sgtex in cookedTextures) CookedTexture.Read(new MemoryStream(sgtex), out _);
        }

        var (looseMs, looseBytes) = Measure(LoadLoose);
        var (cookedMs, cookedBytes) = Measure(LoadCooked);

        long looseMemory = textures.Sum(png => CookedTexture.Cook(png, compress: false).GpuBytes);   // RGBA, mips and all
        long cookedMemory = cookedTextures.Sum(sgtex => CookedTexture.Read(new MemoryStream(sgtex), out _).GpuBytes);

        string report = $"Sandbox, {models.Count} models and {textures.Count} textures: load {looseMs:F2} ms loose, {cookedMs:F2} ms cooked; " +
                        $"allocated {looseBytes / 1024} KB loose, {cookedBytes / 1024} KB cooked; texture memory {looseMemory / 1024} KB loose, {cookedMemory / 1024} KB cooked";
        Console.WriteLine(report);

        Assert.True(cookedMs * 2 < looseMs, report);              // at least twice as fast (it is far more)
        Assert.True(cookedBytes * 2 < looseBytes, report);        // at least half the allocation
        Assert.True(cookedMemory * 3 < looseMemory, report);      // BC1 is an eighth of RGBA and BC3 a quarter
    }

    // The best of five runs after a warm-up (JIT), in milliseconds, and the allocation of one run.
    private static (double Ms, long Bytes) Measure(Action load)
    {
        load();
        double best = double.MaxValue;
        for (int i = 0; i < 5; i++)
        {
            var clock = Stopwatch.StartNew();
            load();
            best = Math.Min(best, clock.Elapsed.TotalMilliseconds);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        load();
        return (best, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
