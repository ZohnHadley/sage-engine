#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Lightmaps for brush levels (issue #313): every worldspawn face unwrapped into one atlas, lit on the CPU by
// the level's baked lamps with shadow rays against the brushes, the sky each texel sees in alpha; baked at
// load and cached by a hash of what it read. The drawing (lit.fx `Lightmapped`, the renderer leaving baked
// lamps out of a lightmapped draw's four) is the client's and is checked by the smoke run.
public class LightmapTests
{
    public LightmapTests() { _ = TestEnv.UserRoot; }

    // A closed room, eight metres square and four high inside (map units: -128..128 and 0..128), with a
    // partition down the middle that stops short of the north wall, so a lamp west of it shadows the floor
    // east of it.
    private static string Room(bool partition = true, string entities = "")
    {
        var map = new StringBuilder("{\n\"classname\" \"worldspawn\"\n");
        Box(map, -144, -144, -16, 144, 144, 0, "floor");
        Box(map, -144, -144, 128, 144, 144, 144, "ceiling");
        Box(map, -144, -144, 0, -128, 144, 128, "wall");
        Box(map, 128, -144, 0, 144, 144, 128, "wall");
        Box(map, -128, -144, 0, 128, -128, 128, "wall");
        Box(map, -128, 128, 0, 128, 144, 128, "wall");
        if (partition) Box(map, -8, -128, 0, 8, 64, 128, "wall");
        map.Append("}\n").Append(entities);
        return map.ToString();
    }

    private static void Box(StringBuilder map, int x0, int y0, int z0, int x1, int y1, int z1, string texture)
    {
        map.Append("{\n");
        map.Append($"( {x0} {y0} {z0} ) ( {x0} {y0 + 1} {z0} ) ( {x0} {y0} {z0 + 1} ) {texture} 0 0 0 1 1\n");
        map.Append($"( {x0} {y0} {z0} ) ( {x0} {y0} {z0 + 1} ) ( {x0 + 1} {y0} {z0} ) {texture} 0 0 0 1 1\n");
        map.Append($"( {x0} {y0} {z0} ) ( {x0 + 1} {y0} {z0} ) ( {x0} {y0 + 1} {z0} ) {texture} 0 0 0 1 1\n");
        map.Append($"( {x1} {y1} {z1} ) ( {x1} {y1 + 1} {z1} ) ( {x1 + 1} {y1} {z1} ) {texture} 0 0 0 1 1\n");
        map.Append($"( {x1} {y1} {z1} ) ( {x1 + 1} {y1} {z1} ) ( {x1} {y1} {z1 + 1} ) {texture} 0 0 0 1 1\n");
        map.Append($"( {x1} {y1} {z1} ) ( {x1} {y1} {z1 + 1} ) ( {x1} {y1 + 1} {z1} ) {texture} 0 0 0 1 1\n");
        map.Append("}\n");
    }

    private static List<LevelBrush> Build(string text)
    {
        Assert.True(MapFile.TryParse(text, out var file, out string error), error);
        var brushes = BrushGeometry.Build(file.Worldspawn!.Brushes, new MapSpace(), out int skipped);
        Assert.Equal(0, skipped);
        return brushes;
    }

    // The face of `brushes` facing `normal` that `point` lies on.
    private static LevelFace FaceAt(IEnumerable<LevelBrush> brushes, Vector3 point, Vector3 normal)
    {
        foreach (var brush in brushes)
            foreach (var face in brush.Faces)
            {
                if (Vector3.Dot(face.Normal, normal) < 0.99f) continue;
                if (MathF.Abs(Vector3.Dot(face.Normal, point - face.Positions[0])) > 1e-3f) continue;
                var min = face.Positions.Aggregate(Vector3.Min);
                var max = face.Positions.Aggregate(Vector3.Max);
                if (point.X >= min.X - 1e-3f && point.X <= max.X + 1e-3f && point.Z >= min.Z - 1e-3f && point.Z <= max.Z + 1e-3f
                    && point.Y >= min.Y - 1e-3f && point.Y <= max.Y + 1e-3f) return face;
            }
        throw new InvalidOperationException($"no face facing {normal} at {point}");
    }

    private static Vector4 At(LevelLightmap lightmap, IEnumerable<LevelBrush> brushes, Vector3 point, Vector3 normal) =>
        lightmap.Sample(lightmap.UvAt(FaceAt(brushes, point, normal), point));

    // What common.fxh's PointLights gives a floor point under a white lamp: (1 - d/range)² times Lambert.
    private static float Direct(Vector3 lamp, float range, Vector3 point)
    {
        float d = Vector3.Distance(lamp, point);
        float falloff = 1f - d / range;
        return falloff * falloff * (lamp.Y - point.Y) / d;
    }

    private static readonly Vector3 Up = Vector3.UnitY;

    // Every face gets a chart of its own, inside the atlas, and no two overlap — borders included — so
    // filtering one face's light never reads another's.
    [Fact]
    public void EveryFaceHasItsOwnChartInTheAtlas()
    {
        var brushes = Build(Room());
        var lightmap = LightmapBaker.Bake(brushes, ReadOnlySpan<LightSample>.Empty, LightmapSettings.For(4f) with { SkyRays = 0 });

        var rects = new List<(float X0, float Y0, float X1, float Y1)>();
        foreach (var face in brushes.SelectMany(b => b.Faces))
        {
            Assert.True(lightmap.Uvs.TryGetValue(face, out var uvs), "a face has no lightmap coordinates");
            Assert.Equal(face.Positions.Length, uvs!.Length);
            foreach (var uv in uvs)
            {
                Assert.InRange(uv.X, 0f, 1f);
                Assert.InRange(uv.Y, 0f, 1f);
            }
            // In texels, out to the edge of the border texel round the corners' texel centres.
            float x0 = uvs.Min(u => u.X) * lightmap.Width - 1.5f, x1 = uvs.Max(u => u.X) * lightmap.Width + 1.5f;
            float y0 = uvs.Min(u => u.Y) * lightmap.Height - 1.5f, y1 = uvs.Max(u => u.Y) * lightmap.Height + 1.5f;
            Assert.True(x0 >= -0.01f && y0 >= -0.01f && x1 <= lightmap.Width + 0.01f && y1 <= lightmap.Height + 0.01f);
            rects.Add((x0, y0, x1, y1));
        }
        for (int i = 0; i < rects.Count; i++)
            for (int j = i + 1; j < rects.Count; j++)
            {
                var a = rects[i];
                var b = rects[j];
                bool overlap = a.X0 < b.X1 - 0.01f && b.X0 < a.X1 - 0.01f && a.Y0 < b.Y1 - 0.01f && b.Y0 < a.Y1 - 0.01f;
                Assert.False(overlap, $"charts {i} and {j} overlap");
            }

        // Four texels a metre: the 9 m floor (under the walls too) is 37 texel centres across.
        var floor = FaceAt(brushes, Vector3.Zero, Up);
        var floorUvs = lightmap.Uvs[floor];
        Assert.Equal(36f, (floorUvs.Max(u => u.X) - floorUvs.Min(u => u.X)) * lightmap.Width, 2);
    }

    // The baked light is the shader's own point-light sum where the lamp can see the floor, and nothing
    // where the partition stands between them: direct light, with shadows.
    [Fact]
    public void ALampLightsWhatItCanSee_AndThePartitionShadowsTheRest()
    {
        var brushes = Build(Room());
        var lamp = new Vector3(-2f, 2f, 0f);   // west of the partition (x = 0), half way up
        var lights = new[] { new LightSample(lamp, Vector3.One, 10f) };
        var lightmap = LightmapBaker.Bake(brushes, lights, LightmapSettings.For(4f) with { SkyRays = 0 });

        // On the lamp's side: what PointLights computes, to within a texel and the 8-bit steps.
        foreach (var point in new[] { new Vector3(-2f, 0f, 3f), new Vector3(-3f, 0f, -1f), new Vector3(-2f, 0f, 0f) })
        {
            var sample = At(lightmap, brushes, point, Up);
            Assert.Equal(Direct(lamp, 10f, point), sample.X, 0.06f);
            Assert.Equal(sample.X, sample.Y, 0.001f);   // white light: grey
        }

        // Behind the partition: dark, though in range and facing the lamp.
        var shadowed = new Vector3(2f, 0f, 0f);
        Assert.True(Direct(lamp, 10f, shadowed) > 0.1f);
        Assert.Equal(0f, At(lightmap, brushes, shadowed, Up).X, 0.02f);

        // And round the end of it, where the gap to the north wall lets the lamp through, lit again.
        var round = new Vector3(1f, 0f, -3.8f);
        Assert.True(At(lightmap, brushes, round, Up).X > 0.03f);

        // A face turned away from the lamp gets none of it: the partition's west face is lit, its east not.
        Assert.True(At(lightmap, brushes, new Vector3(-0.25f, 1.5f, 0f), -Vector3.UnitX).X > 0.2f);
        Assert.Equal(0f, At(lightmap, brushes, new Vector3(0.25f, 1.5f, 0f), Vector3.UnitX).X, 0.02f);
    }

    // The sky term: a closed room sees none of it (the floor keeps the floor the settings give it), and a
    // slab in the open sees all of it.
    [Fact]
    public void TheSkyTermIsWhatATexelCanSeeOfTheSky()
    {
        var settings = LightmapSettings.For(2f);
        var room = Build(Room(partition: false));
        var closed = LightmapBaker.Bake(room, ReadOnlySpan<LightSample>.Empty, settings);
        Assert.Equal(settings.MinSky, At(closed, room, Vector3.Zero, Up).W, 0.01f);

        var slabText = new StringBuilder("{\n\"classname\" \"worldspawn\"\n");
        Box(slabText, -64, -64, -16, 64, 64, 0, "floor");
        slabText.Append("}\n");
        var slab = Build(slabText.ToString());
        var open = LightmapBaker.Bake(slab, ReadOnlySpan<LightSample>.Empty, settings);
        Assert.Equal(1f, At(open, slab, Vector3.Zero, Up).W, 0.01f);

        // A wall standing on it hides half the sky from the floor at its foot.
        var walled = new StringBuilder("{\n\"classname\" \"worldspawn\"\n");
        Box(walled, -64, -64, -16, 64, 64, 0, "floor");
        Box(walled, -64, -8, 0, 64, 8, 256, "wall");
        walled.Append("}\n");
        var wall = Build(walled.ToString());
        var half = LightmapBaker.Bake(wall, ReadOnlySpan<LightSample>.Empty, settings);
        Assert.InRange(At(half, wall, new Vector3(0f, 0f, 0.5f), Up).W, 0.3f, 0.8f);
    }

    // The same inputs bake the same bytes (which is what lets a bake be cached), the hash names exactly
    // those inputs, and the cache's file reads back as what was written.
    [Fact]
    public void ABakeIsDeterministic_HashedByItsInputs_AndReadsBackFromItsFile()
    {
        var brushes = Build(Room());
        var lights = new[] { new LightSample(new Vector3(-2f, 2f, 0f), new Vector3(1f, 0.8f, 0.6f), 8f) };
        var settings = LightmapSettings.For(2f);

        var a = LightmapBaker.Bake(brushes, lights, settings);
        var b = LightmapBaker.Bake(brushes, lights, settings);
        Assert.Equal(a.Rgba, b.Rgba);

        ulong hash = LightmapBaker.Hash(brushes, lights, settings);
        Assert.Equal(hash, LightmapBaker.Hash(brushes, lights, settings));
        var moved = new[] { lights[0] with { Position = new Vector3(-2f, 2f, 1f) } };
        Assert.NotEqual(hash, LightmapBaker.Hash(brushes, moved, settings));
        Assert.NotEqual(hash, LightmapBaker.Hash(brushes, lights, settings with { Density = 3f }));
        Assert.NotEqual(hash, LightmapBaker.Hash(Build(Room(partition: false)), lights, settings));

        var stream = new MemoryStream();
        LightmapCache.Write(stream, a, hash, brushes);
        stream.Position = 0;
        var read = LightmapCache.Read(stream, hash, brushes);
        Assert.NotNull(read);
        Assert.True(read!.FromCache);
        Assert.Equal((a.Width, a.Height, a.Lights), (read.Width, read.Height, read.Lights));
        Assert.Equal(a.Rgba, read.Rgba);
        foreach (var face in brushes.SelectMany(x => x.Faces)) Assert.Equal(a.Uvs[face], read.Uvs[face]);

        stream.Position = 0;
        Assert.Null(LightmapCache.Read(stream, hash + 1, brushes));                      // another bake's file
        stream.Position = 0;
        Assert.Null(LightmapCache.Read(stream, hash, Build(Room(partition: false))));    // other faces
    }

    // A face too big for a chart at the density asked for gets fewer texels, and a level too big for the
    // atlas a lower density, rather than failing to bake.
    [Fact]
    public void ALevelTooBigForTheAtlasBakesCoarser()
    {
        var brushes = Build(Room());
        var lightmap = LightmapBaker.Bake(brushes, ReadOnlySpan<LightSample>.Empty,
                                          LightmapSettings.For(16f) with { SkyRays = 0, MaxAtlas = 128, MaxChart = 64 });
        Assert.InRange(lightmap.Width, 1, 128);
        Assert.InRange(lightmap.Height, 1, 128);
        Assert.Equal(brushes.Sum(b => b.Faces.Length), lightmap.Uvs.Count);
    }

    private const string Records = """
        [
          { "type": "map", "id": "baked_room", "file": "maps/baked_room.map", "lightmap": 4 },
          { "type": "map", "id": "plain_room", "file": "maps/baked_room.map" },
          { "type": "prefab", "id": "lamp", "parts": { "light": { "range": 10, "intensity": 1, "baked": true } } },
          { "type": "prefab", "id": "torch", "parts": { "light": { "range": 6, "intensity": 3 } } }
        ]
        """;

    // The done criterion's simulation half: a level whose record asks for a lightmap bakes one at load
    // from the lamps it placed that are baked — a lamp that is not stays out of it, to light the level
    // dynamically on top — and loading it again reads the bake back from the cache. A level that asks
    // for none has none.
    [Fact]
    public void ALevelThatAsksForALightmapBakesItsBakedLampsAtLoad_AndReadsItBackNextTime()
    {
        var entities = """
            {
            "classname" "lamp"
            "origin" "-64 0 64"
            }
            {
            "classname" "torch"
            "origin" "64 96 64"
            }
            """;
        var fixture = new MountFixture();
        fixture.Write("game", "maps/baked_room.map", Room(entities: entities));
        fixture.Write("game", "data/level.json", Records);
        fixture.Mount("game", "test");
        using var app = HeadlessApp.Gameplay().With(new MapModule()).Mount(fixture).Build();

        var world = app.CreateWorld("first");
        Assert.NotNull(MapLoader.Load(world, new RecordId("test", "baked_room")));
        world.RunFixed(1f / 60f);
        var level = world.Resources.Get<MapLevels>().Loaded.Single();
        Assert.False(level.LightmapPending);
        var lightmap = level.Lightmap;
        Assert.NotNull(lightmap);
        Assert.False(lightmap!.FromCache);
        Assert.Equal(1, lightmap.Lights);   // the lamp; the torch lights dynamically

        // The lamp is marked so the renderer leaves it out of a lightmapped draw's lights; the torch is not.
        var lamps = world.Query<PointLight>().Entities.ToEntityList().Select(e => world.Get<PointLight>(e)).ToList();
        Assert.Equal(new[] { false, true }, lamps.Select(l => l.Baked).OrderBy(b => b).ToArray());

        // In the level's own frame, the lamp is at (-2, 2, 0): the floor beneath it is lit as PointLights
        // would light it, and the floor east of the partition is not.
        var lamp = new Vector3(-2f, 2f, 0f);
        Assert.Equal(Direct(lamp, 10f, new Vector3(-2f, 0f, 2f)), At(lightmap, level.Brushes, new Vector3(-2f, 0f, 2f), Up).X, 0.06f);
        Assert.Equal(0f, At(lightmap, level.Brushes, new Vector3(2f, 0f, 0f), Up).X, 0.02f);

        // The cache: the same level in another world is read back, texel for texel.
        var second = app.CreateWorld("second");
        Assert.NotNull(MapLoader.Load(second, new RecordId("test", "baked_room")));
        second.RunFixed(1f / 60f);
        var again = second.Resources.Get<MapLevels>().Loaded.Single().Lightmap;
        Assert.NotNull(again);
        Assert.True(again!.FromCache);
        Assert.Equal(lightmap.Rgba, again.Rgba);

        // No "lightmap" on the record: nothing to wait for, nothing baked.
        var third = app.CreateWorld("third");
        Assert.NotNull(MapLoader.Load(third, new RecordId("test", "plain_room")));
        third.RunFixed(1f / 60f);
        var plain = third.Resources.Get<MapLevels>().Loaded.Single();
        Assert.False(plain.LightmapPending);
        Assert.Null(plain.Lightmap);
    }

    // A baked lamp is in the texture steady and all round, so a flicker or a cone on one is said when it is
    // placed: they show only on what is not lightmapped.
    [Fact]
    public void ABakedLampWithAFlickerOrAConeIsWarnedAbout()
    {
        var fixture = new MountFixture();
        fixture.Write("game", "data/lamps.json", """
            [
              { "type": "prefab", "id": "flicker_lamp", "parts": { "light": { "baked": true, "pattern": "torch" } } },
              { "type": "prefab", "id": "spot_lamp", "parts": { "light": { "baked": true, "cone": 30 } } },
              { "type": "prefab", "id": "steady_lamp", "parts": { "light": { "baked": true } } }
            ]
            """);
        fixture.Mount("game", "test");
        using var capture = new CaptureSink();
        using var app = HeadlessApp.Gameplay().Mount(fixture).Build();
        var world = app.CreateWorld("lamps");
        foreach (var name in new[] { "flicker_lamp", "spot_lamp", "steady_lamp" })
            world.Spawn(new RecordId("test", name), Vector3.Zero);

        var warnings = capture.Entries.Where(e => e.Level == LogLevel.Warn).Select(e => e.Message).ToList();
        Assert.Contains(warnings, w => w.Contains("flicker_lamp") && w.Contains("\"pattern\": \"torch\""));
        Assert.Contains(warnings, w => w.Contains("spot_lamp") && w.Contains("\"cone\""));
        Assert.DoesNotContain(warnings, w => w.Contains("steady_lamp"));
    }
}
