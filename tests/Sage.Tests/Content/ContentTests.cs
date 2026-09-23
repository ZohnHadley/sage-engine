#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using sage_engine;

namespace sage_engine.Tests;

using Assert = Xunit.Assert;

[Record("test_item")]
public sealed class TestItem
{
    public string Name = "";
    public int Value;
    public Vector3 Offset;
    public List<string> Tags = new();
    public TestStats Stats = new();
    public RecordId Spell;
}

public sealed class TestStats
{
    public int Strength;
    public int Agility;
}

[Record("test_spell")]
public sealed class TestSpell
{
    public float Cost;
}

// Temp folders as VFS mounts: `Write("game", "data/items.json", json)`, then `Mount("game", "sandbox")`.
// Disk path -> virtual path, which is what an asset watcher has to do: the operating system tells it
// about a file, and everything else in the engine speaks virtual paths (05 §3.6, F32).
public class VfsReverseLookupTests
{
    public VfsReverseLookupTests() { _ = TestEnv.UserRoot; }

    [Xunit.Fact]
    public void AFileUnderAMountResolvesToItsVirtualPath()
    {
        var fx = new MountFixture();
        fx.Write("game", "textures/creature.png", "x");
        fx.Mount("game", "sandbox");

        var path = fx.Vfs.VirtualPathOf(Path.Combine(fx.Dir("game"), "textures", "creature.png"));
        Assert.NotNull(path);
        Assert.Equal("textures/creature.png", path!.Value.ToString());
    }

    [Xunit.Fact]
    public void AFileOutsideEveryMountResolvesToNothing()
    {
        var fx = new MountFixture();
        fx.Mount("game", "sandbox");
        Assert.Null(fx.Vfs.VirtualPathOf(Path.Combine(TestEnv.NewTempDir(), "stray.png")));
    }

    // Mount roots are not a prefix test: "…/game2/x.png" must not resolve against the "…/game" mount,
    // or a sibling folder would quietly shadow another mount's assets.
    [Xunit.Fact]
    public void ASiblingFolderWithASharedPrefixIsNotAMatch()
    {
        var fx = new MountFixture();
        fx.Mount("game", "sandbox");
        Directory.CreateDirectory(fx.Dir("game2"));
        Assert.Null(fx.Vfs.VirtualPathOf(Path.Combine(fx.Dir("game2"), "x.png")));
    }

    // Where one mount is inside another, the deeper root is the one the file belongs to: that is the
    // mount whose name anything asking for the file would have used.
    [Xunit.Fact]
    public void TheDeepestMountWins()
    {
        var fx = new MountFixture();
        fx.Mount("game", "sandbox");
        Directory.CreateDirectory(Path.Combine(fx.Dir("game"), "mods", "extra"));
        fx.Vfs.Mount(new FolderMount("extra", Path.Combine(fx.Dir("game"), "mods", "extra"), "extra"));

        var path = fx.Vfs.VirtualPathOf(Path.Combine(fx.Dir("game"), "mods", "extra", "textures", "a.png"));
        Assert.Equal("textures/a.png", path!.Value.ToString());
    }

    [Xunit.Fact]
    public void TheMountRootItselfIsNotAFile()
    {
        var fx = new MountFixture();
        fx.Mount("game", "sandbox");
        Assert.Null(fx.Vfs.VirtualPathOf(fx.Dir("game")));
    }
}

internal sealed class MountFixture
{
    public readonly string Root = TestEnv.NewTempDir();
    public readonly VirtualFileSystem Vfs = new();

    public string Dir(string mount) => Path.Combine(Root, mount);

    public void Write(string mount, string relative, string text)
    {
        string file = Path.Combine(Dir(mount), relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    public FolderMount Mount(string mount, string ns)
    {
        Directory.CreateDirectory(Dir(mount));
        var m = new FolderMount(mount, Dir(mount), ns);
        Vfs.Mount(m);
        return m;
    }

    public RecordStore Load()
    {
        var store = new RecordStore();
        store.Register<TestItem>();
        store.Register<TestSpell>();
        store.Load(Vfs);
        return store;
    }
}

// The VFS (docs/design/05 §3.1).
public class VfsTests
{
    public VfsTests() { _ = TestEnv.UserRoot; }

    [Theory]
    [InlineData(@"Textures\Wood.PNG", "textures/wood.png")]
    [InlineData("/data//items.json/", "data/items.json")]
    public void VirtualPath_Normalizes(string input, string expected) =>
        Assert.Equal(expected, VirtualPath.Parse(input).Value);

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("data/./x.json")]
    [InlineData("c:/windows/x")]
    [InlineData("  ")]
    public void VirtualPath_RejectsEscapes(string input) =>
        Assert.Throws<ArgumentException>(() => VirtualPath.Parse(input));

    [Fact]
    public void LaterMount_ShadowsEarlier_AndEnumerateIsInMountOrder()
    {
        var fx = new MountFixture();
        fx.Write("engine", "textures/wood.png", "engine");
        fx.Write("engine", "textures/stone.png", "engine");
        fx.Write("game", "textures/wood.png", "game");
        var engine = fx.Mount("engine", "sage");
        var game = fx.Mount("game", "sandbox");

        var wood = VirtualPath.Parse("textures/wood.png");
        Assert.Same(game, fx.Vfs.Which(wood));
        Assert.Same(engine, fx.Vfs.Which(VirtualPath.Parse("textures/stone.png")));
        using (var reader = new StreamReader(fx.Vfs.Open(wood)))
            Assert.Equal("game", reader.ReadToEnd());
        Assert.Null(fx.Vfs.Which(VirtualPath.Parse("textures/missing.png")));
        Assert.Throws<FileNotFoundException>(() => fx.Vfs.Open(VirtualPath.Parse("textures/missing.png")));

        var all = fx.Vfs.Enumerate(VirtualPath.Parse("textures"), "*.png").Select(x => $"{x.Mount.Name}:{x.Path}").ToList();
        Assert.Equal(new[] { "engine:textures/stone.png", "engine:textures/wood.png", "game:textures/wood.png" }, all);
    }

    [Fact]
    public void Lookup_IsCaseInsensitive()
    {
        var fx = new MountFixture();
        fx.Write("game", "Models/Bunny.XNB", "x");
        fx.Mount("game", "sandbox");

        var path = VirtualPath.Parse("models/bunny.xnb");
        Assert.True(fx.Vfs.Exists(path));
        Assert.True(File.Exists(fx.Vfs.Which(path)!.PhysicalPath(path)));   // Linux: the case-insensitive walk finds Models/Bunny.XNB
        Assert.Single(fx.Vfs.Enumerate(VirtualPath.Parse("MODELS"), "*"));
    }

    [Fact]
    public void MissingDirectory_EnumeratesNothing()
    {
        var fx = new MountFixture();
        fx.Mount("game", "sandbox");
        Assert.Empty(fx.Vfs.Enumerate(VirtualPath.Parse("data"), "*.json"));
    }
}

// The data-record pipeline (docs/design/05 §3.5).
public class RecordStoreTests
{
    public RecordStoreTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void RecordId_Parse()
    {
        Assert.Equal(new RecordId("sandbox", "bunny"), RecordId.Parse("bunny", "sandbox"));
        Assert.Equal(new RecordId("sage", "lit_default"), RecordId.Parse("sage:lit_default", "sandbox"));
        Assert.Equal("sage:x", new RecordId("sage", "x").ToString());
        Assert.Throws<FormatException>(() => RecordId.Parse("Bunny", "sandbox"));
        Assert.Throws<FormatException>(() => RecordId.Parse("a:b:c", "sandbox"));
        Assert.Throws<FormatException>(() => RecordId.Parse(":x", "sandbox"));
    }

    [Fact]
    public void Records_GetTheNamespaceOfTheirMount_AndDeserialize()
    {
        var fx = new MountFixture();
        fx.Write("engine", "data/spells.json", """[{ "type": "test_spell", "id": "fireball", "cost": 5 }]""");
        fx.Write("game", "data/sub/items.json", """
            // comments and trailing commas are allowed
            [{ "type": "test_item", "id": "sword", "name": "Sword", "value": 10, "offset": [1, 2, 3],
               "tags": ["weapon"], "stats": { "strength": 2 }, "spell": "sage:fireball", },]
            """);
        fx.Mount("engine", "sage");
        fx.Mount("game", "sandbox");
        var store = fx.Load();

        Assert.Equal(0, store.ErrorCount);
        Assert.True(store.TryGet(new RecordId("sage", "fireball"), out TestSpell spell));
        Assert.Equal(5f, spell.Cost);
        var sword = store.Get<TestItem>(new RecordId("sandbox", "sword"));
        Assert.Equal("Sword", sword.Name);
        Assert.Equal(new Vector3(1, 2, 3), sword.Offset);
        Assert.Equal(new[] { "weapon" }, sword.Tags);
        Assert.Equal(2, sword.Stats.Strength);
        Assert.Equal(new RecordId("sage", "fireball"), sword.Spell);
        Assert.True(store.Exists(new RecordId("sandbox", "sword")));
        Assert.False(store.TryGet(new RecordId("sage", "sword"), out TestItem _));
    }

    [Fact]
    public void BareReference_ResolvesInTheRecordsNamespace()
    {
        var fx = new MountFixture();
        fx.Write("game", "data/a.json", """
            [{ "type": "test_spell", "id": "zap" },
             { "type": "test_item", "id": "wand", "spell": "zap" }]
            """);
        fx.Mount("game", "sandbox");
        var store = fx.Load();

        Assert.Equal(0, store.ErrorCount);
        Assert.Equal(new RecordId("sandbox", "zap"), store.Get<TestItem>(new RecordId("sandbox", "wand")).Spell);
    }

    [Fact]
    public void Patch_MergesFieldByField()
    {
        var fx = new MountFixture();
        fx.Write("game", "data/items.json", """
            [{ "type": "test_item", "id": "sword", "name": "Sword", "value": 10,
               "tags": ["weapon", "iron", "old"], "stats": { "strength": 2, "agility": 1 } }]
            """);
        fx.Write("mod", "data/patch.json", """
            [{ "type": "test_item", "id": "sandbox:sword", "patch": true, "value": 99,
               "tags+": ["magic"], "tags-": ["old"], "stats": { "agility": 5 } }]
            """);
        fx.Mount("game", "sandbox");
        fx.Mount("mod", "mymod");
        var store = fx.Load();

        var sword = store.Get<TestItem>(new RecordId("sandbox", "sword"));
        Assert.Equal(0, store.ErrorCount);
        Assert.Equal("Sword", sword.Name);                               // untouched scalar kept
        Assert.Equal(99, sword.Value);                                   // scalar replaced
        Assert.Equal(new[] { "weapon", "iron", "magic" }, sword.Tags);   // list ops
        Assert.Equal(2, sword.Stats.Strength);                           // objects merge recursively
        Assert.Equal(5, sword.Stats.Agility);

        string described = store.Describe("test_item", new RecordId("sandbox", "sword"));
        Assert.Contains("value <- mod:data/patch.json", described);
        Assert.Contains("name <- game:data/items.json", described);
    }

    [Fact]
    public void WithinOneMount_DefinitionsLoadBeforePatches_WhateverTheFileNames()
    {
        var fx = new MountFixture();
        fx.Write("game", "data/a_patch.json", """[{ "type": "test_item", "id": "sword", "patch": true, "value": 7 }]""");
        fx.Write("game", "data/z_items.json", """[{ "type": "test_item", "id": "sword", "value": 1 }]""");
        fx.Mount("game", "sandbox");
        var store = fx.Load();

        Assert.Equal(0, store.ErrorCount);
        Assert.Equal(7, store.Get<TestItem>(new RecordId("sandbox", "sword")).Value);
    }

    [Fact]
    public void Redefinition_WithoutPatchFlag_IsAnErrorButApplied()
    {
        var fx = new MountFixture();
        fx.Write("game", "data/items.json", """[{ "type": "test_item", "id": "sword", "name": "Sword", "value": 10 }]""");
        fx.Write("mod", "data/items.json", """[{ "type": "test_item", "id": "sandbox:sword", "value": 20 }]""");
        fx.Mount("game", "sandbox");
        fx.Mount("mod", "mymod");
        var store = fx.Load();

        Assert.Equal(1, store.ErrorCount);
        var sword = store.Get<TestItem>(new RecordId("sandbox", "sword"));
        Assert.Equal("Sword", sword.Name);
        Assert.Equal(20, sword.Value);
    }

    [Fact]
    public void Disabled_RemovesTheRecord_AndPatchOfUnknownIsSkipped()
    {
        var fx = new MountFixture();
        fx.Write("game", "data/items.json", """[{ "type": "test_item", "id": "sword" }, { "type": "test_item", "id": "axe" }]""");
        fx.Write("mod", "data/items.json", """
            [{ "type": "test_item", "id": "sandbox:sword", "patch": true, "disabled": true },
             { "type": "test_item", "id": "sandbox:bow", "patch": true, "value": 1 }]
            """);
        fx.Mount("game", "sandbox");
        fx.Mount("mod", "mymod");
        var store = fx.Load();

        Assert.Equal(new[] { new RecordId("sandbox", "axe") }, store.Ids("test_item"));
        Assert.Equal(0, store.ErrorCount);
    }

    // A base writes its ids in its own terms: "firebolt" in an engine record means sage:firebolt, even
    // when a game's record inherits it. Getting this wrong made every engine record with a RecordId
    // field un-inheritable by a game (review #56).
    [Fact]
    public void Base_InheritedIdsKeepTheBasesNamespace()
    {
        var fx = new MountFixture();
        fx.Write("engine", "data/spells.json", """
            [{ "type": "test_item", "id": "firebolt", "name": "Firebolt" },
             { "type": "test_item", "id": "wand_base", "abstract": true, "spell": "firebolt", "value": 3 }]
            """);
        fx.Write("game", "data/wands.json", """
            [{ "type": "test_item", "id": "firebolt", "name": "the game's own firebolt" },
             { "type": "test_item", "id": "apprentice_wand", "base": "sage:wand_base", "name": "Apprentice wand" },
             { "type": "test_item", "id": "master_wand", "base": "sage:wand_base", "name": "Master wand", "spell": "firebolt" }]
            """);
        fx.Mount("engine", "sage");
        fx.Mount("game", "sandbox");
        var store = fx.Load();

        Assert.True(store.TryGet(new RecordId("sandbox", "apprentice_wand"), out TestItem apprentice));
        Assert.Equal(new RecordId("sage", "firebolt"), apprentice.Spell);   // the base's id, as the base meant it
        Assert.Equal(3, apprentice.Value);

        // A record that writes the field itself still means its own namespace.
        Assert.True(store.TryGet(new RecordId("sandbox", "master_wand"), out TestItem master));
        Assert.Equal(new RecordId("sandbox", "firebolt"), master.Spell);
        Assert.Equal(0, store.ErrorCount);
    }

    [Fact]
    public void Base_Inherits_AbstractIsNotBuilt_AndBrokenBasesAreErrors()
    {
        var fx = new MountFixture();
        fx.Write("game", "data/items.json", """
            [{ "type": "test_item", "id": "weapon_base", "abstract": true, "value": 5, "tags": ["weapon"], "stats": { "strength": 1 } },
             { "type": "test_item", "id": "sword", "base": "weapon_base", "name": "Sword", "stats": { "agility": 3 } },
             { "type": "test_item", "id": "a", "base": "b" },
             { "type": "test_item", "id": "b", "base": "a" },
             { "type": "test_item", "id": "orphan", "base": "nothing" }]
            """);
        fx.Mount("game", "sandbox");
        using var capture = new CaptureSink();
        var store = fx.Load();

        Assert.False(store.TryGet(new RecordId("sandbox", "weapon_base"), out TestItem _));
        var sword = store.Get<TestItem>(new RecordId("sandbox", "sword"));
        Assert.Equal(5, sword.Value);
        Assert.Equal(new[] { "weapon" }, sword.Tags);
        Assert.Equal(1, sword.Stats.Strength);
        Assert.Equal(3, sword.Stats.Agility);
        string described = store.Describe("test_item", new RecordId("sandbox", "sword"));
        Assert.Contains("value <- game:data/items.json (via base sandbox:weapon_base)", described);
        Assert.DoesNotContain("base <-", described);
        var messages = capture.Entries.Select(e => e.Message).ToList();
        Assert.Contains(messages, m => m.Contains("\"base\" cycle"));
        Assert.Contains(messages, m => m.Contains("base sandbox:nothing not found"));
    }

    [Fact]
    public void UnknownField_Warns_BadValue_SkipsTheRecord_MissingReference_IsAnError()
    {
        var fx = new MountFixture();
        fx.Write("game", "data/items.json", """
            [{ "type": "test_item", "id": "typo", "vaule": 3 },
             { "type": "test_item", "id": "bad", "value": "lots" },
             { "type": "test_item", "id": "dangling", "spell": "no_such_spell" },
             { "type": "no_such_type", "id": "x" }]
            """);
        fx.Mount("game", "sandbox");
        using var capture = new CaptureSink();
        var store = fx.Load();

        var messages = capture.Entries.Select(e => e.Message).ToList();
        Assert.Contains(messages, m => m.Contains("unknown field 'vaule'"));
        Assert.Contains(messages, m => m.Contains("unknown record type 'no_such_type'"));
        Assert.True(store.TryGet(new RecordId("sandbox", "typo"), out TestItem _));
        Assert.False(store.TryGet(new RecordId("sandbox", "bad"), out TestItem _));
        Assert.Contains(messages, m => m.Contains("refers to sandbox:no_such_spell"));
        Assert.Equal(2, store.ErrorCount);   // bad value, dangling reference (warnings don't count)
    }

    [Fact]
    public void InvalidJson_IsAnError_OtherFilesStillLoad()
    {
        var fx = new MountFixture();
        fx.Write("game", "data/a.json", "[{ oops");
        fx.Write("game", "data/b.json", """{ "type": "test_item", "id": "single" }""");
        fx.Mount("game", "sandbox");
        var store = fx.Load();

        Assert.Equal(1, store.ErrorCount);
        Assert.True(store.TryGet(new RecordId("sandbox", "single"), out TestItem _));
    }

    [Fact]
    public void Reload_UpdatesInstancesInPlace()
    {
        var fx = new MountFixture();
        fx.Write("game", "data/items.json", """[{ "type": "test_item", "id": "sword", "value": 1 }, { "type": "test_item", "id": "axe" }]""");
        fx.Mount("game", "sandbox");
        var store = fx.Load();
        var sword = store.Get<TestItem>(new RecordId("sandbox", "sword"));
        int reloads = 0;
        store.Reloaded += () => reloads++;

        fx.Write("game", "data/items.json", """[{ "type": "test_item", "id": "sword", "value": 2 }, { "type": "test_item", "id": "bow" }]""");
        store.Reload();

        Assert.Equal(1, reloads);
        Assert.Same(sword, store.Get<TestItem>(new RecordId("sandbox", "sword")));
        Assert.Equal(2, sword.Value);
        Assert.Equal(new[] { new RecordId("sandbox", "bow"), new RecordId("sandbox", "sword") }, store.Ids("test_item"));
    }

    [Fact]
    public void Register_RequiresTheAttribute()
    {
        var store = new RecordStore();
        store.Register<TestItem>();
        store.Register<TestItem>();   // idempotent
        Assert.Throws<InvalidOperationException>(() => store.Register<TestStats>());
        Assert.Throws<InvalidOperationException>(() => store.All<TestStats>());
    }
}
