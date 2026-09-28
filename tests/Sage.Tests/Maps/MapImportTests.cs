#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using sage_engine;

namespace sage_engine.Tests;

using Assert = Xunit.Assert;

// TrenchBroom `.map` import (docs/design/15 §3, TODO F16).
//
// A brush is planes, so every one of these asks the same question a different way: do the planes the
// mapper wrote come back as the solid they drew? Geometry that is *slightly* wrong here — a normal
// facing in, a winding reversed, a vertex unwelded — is invisible in a screenshot and fatal in physics.
public class MapImportTests
{
    public MapImportTests() { _ = TestEnv.UserRoot; }

    // The canonical Quake brush: 128 x 128 x 32 units, written the way every Quake tool has written one
    // since 1996. Three points per plane, clockwise seen from outside.
    private const string CubeMap = """
        {
        "classname" "worldspawn"
        {
        ( -64 -64 -16 ) ( -64 -63 -16 ) ( -64 -64 -15 ) stone 0 0 0 1 1
        ( -64 -64 -16 ) ( -64 -64 -15 ) ( -63 -64 -16 ) stone 0 0 0 1 1
        ( -64 -64 -16 ) ( -63 -64 -16 ) ( -64 -63 -16 ) stone 0 0 0 1 1
        ( 64 64 16 ) ( 64 65 16 ) ( 65 64 16 ) stone 0 0 0 1 1
        ( 64 64 16 ) ( 65 64 16 ) ( 64 64 17 ) stone 0 0 0 1 1
        ( 64 64 16 ) ( 64 64 17 ) ( 64 65 16 ) stone 0 0 0 1 1
        }
        }
        """;

    private static MapBrush ParseOneBrush(string text)
    {
        Assert.True(MapFile.TryParse(text, out var map, out string error), error);
        var world = map.Worldspawn;
        Assert.NotNull(world);
        return Assert.Single(world!.Brushes);
    }

    [Fact]
    public void ACubeOfSixPlanesBecomesSixFacesAndEightCorners()
    {
        var brush = ParseOneBrush(CubeMap);

        Assert.True(BrushGeometry.TryBuild(brush, new MapSpace(), out var built, out string error), error);

        Assert.Equal(6, built.Faces.Length);
        foreach (var face in built.Faces) Assert.Equal(4, face.Positions.Length);

        // Eight, not twenty-four: the corners where three planes meet are welded, which is what physics
        // needs to build a hull rather than a cloud.
        Assert.Equal(8, built.Hull.Length);
    }

    [Fact]
    public void TheCubeIsTheSizeItWasWrittenAtInMetres()
    {
        var brush = ParseOneBrush(CubeMap);
        Assert.True(BrushGeometry.TryBuild(brush, new MapSpace(), out var built, out _));

        // 128 x 32 x 128 units at 32 units to the metre: four metres square and one metre thick. The
        // thickness lands on Y because map space is Z-up and engine space is not.
        var size = built.Max - built.Min;
        Assert.Equal(4f, size.X, 3);
        Assert.Equal(1f, size.Y, 3);
        Assert.Equal(4f, size.Z, 3);
    }

    [Fact]
    public void EveryFaceFacesOutOfTheBrush()
    {
        var brush = ParseOneBrush(CubeMap);
        Assert.True(BrushGeometry.TryBuild(brush, new MapSpace(), out var built, out _));

        // A normal that points into the solid is the bug that makes a room you can see through from
        // inside and not from outside, and it is one sign flip away at all times.
        foreach (var face in built.Faces)
        {
            var toFace = face.Positions[0] - built.Centre;
            Assert.True(Vector3.Dot(face.Normal, toFace) > 0f,
                $"face {face.Texture} at {face.Positions[0]} has normal {face.Normal} pointing inwards");
        }
    }

    [Fact]
    public void AFacesWindingAgreesWithItsNormal()
    {
        var brush = ParseOneBrush(CubeMap);
        Assert.True(BrushGeometry.TryBuild(brush, new MapSpace(), out var built, out _));

        // The renderer takes the triangle's own winding as the truth and lights by the vertex normal;
        // if the two disagree the face is lit from behind or culled away entirely.
        foreach (var face in built.Faces)
        {
            var geometric = Vector3.Normalize(Vector3.Cross(face.Positions[1] - face.Positions[0],
                                                            face.Positions[2] - face.Positions[0]));
            Assert.True(Vector3.Dot(geometric, face.Normal) > 0.99f,
                $"face {face.Texture}: winding normal {geometric} against plane normal {face.Normal}");
        }
    }

    [Fact]
    public void TheSolidIsWhereEveryPlaneSaysItIs()
    {
        var brush = ParseOneBrush(CubeMap);
        Assert.True(BrushGeometry.TryBuild(brush, new MapSpace(), out var built, out _));

        // Every corner is on or behind every face's plane — the definition of the intersection of
        // half-spaces, checked rather than assumed.
        foreach (var face in built.Faces)
        {
            float plane = Vector3.Dot(face.Normal, face.Positions[0]);
            foreach (var corner in built.Hull)
                Assert.True(Vector3.Dot(face.Normal, corner) <= plane + 1e-3f,
                    $"corner {corner} is outside face {face.Normal}");
        }
    }

    [Fact]
    public void ARedundantPlaneContributesNoFace()
    {
        // A seventh plane well outside the cube, facing so that the cube is on its solid side: mappers
        // leave these behind whenever they carve a brush, and a plane that touches nothing must produce
        // no polygon rather than a degenerate one. (Written the other way round it would cut the whole
        // brush away, which is a different thing entirely and is covered below.)
        var text = CubeMap.Replace(
            "( 64 64 16 ) ( 64 64 17 ) ( 64 65 16 ) stone 0 0 0 1 1",
            "( 64 64 16 ) ( 64 64 17 ) ( 64 65 16 ) stone 0 0 0 1 1\n( 999 0 0 ) ( 999 0 1 ) ( 999 1 0 ) stone 0 0 0 1 1");

        var brush = ParseOneBrush(text);
        Assert.True(BrushGeometry.TryBuild(brush, new MapSpace(), out var built, out _));

        Assert.Equal(6, built.Faces.Length);
        Assert.Equal(8, built.Hull.Length);
    }

    [Fact]
    public void PlanesThatEncloseNothingAreRefusedRatherThanBuilt()
    {
        // Four planes all facing the same way enclose an infinite slab, not a solid. Physics must never
        // see this, so it is refused with the line it was written on.
        var text = """
            {
            "classname" "worldspawn"
            {
            ( 0 0 0 ) ( 0 1 0 ) ( 0 0 1 ) stone 0 0 0 1 1
            ( 8 0 0 ) ( 8 1 0 ) ( 8 0 1 ) stone 0 0 0 1 1
            ( 16 0 0 ) ( 16 1 0 ) ( 16 0 1 ) stone 0 0 0 1 1
            ( 24 0 0 ) ( 24 1 0 ) ( 24 0 1 ) stone 0 0 0 1 1
            }
            }
            """;

        var brush = ParseOneBrush(text);

        Assert.False(BrushGeometry.TryBuild(brush, new MapSpace(), out _, out string error));
        Assert.Contains("enclose nothing", error);
    }

    [Fact]
    public void TheValve220DialectIsReadAsWellAsTheStandardOne()
    {
        // TrenchBroom writes this one by default: the texture axes are written down instead of being
        // derived from the face normal.
        var text = """
            {
            "classname" "worldspawn"
            {
            ( -64 -64 -16 ) ( -64 -63 -16 ) ( -64 -64 -15 ) stone [ 0 1 0 0 ] [ 0 0 -1 0 ] 0 1 1
            ( -64 -64 -16 ) ( -64 -64 -15 ) ( -63 -64 -16 ) stone [ 1 0 0 0 ] [ 0 0 -1 0 ] 0 1 1
            ( -64 -64 -16 ) ( -63 -64 -16 ) ( -64 -63 -16 ) stone [ 1 0 0 0 ] [ 0 -1 0 0 ] 0 1 1
            ( 64 64 16 ) ( 64 65 16 ) ( 65 64 16 ) stone [ 1 0 0 0 ] [ 0 -1 0 0 ] 0 1 1
            ( 64 64 16 ) ( 65 64 16 ) ( 64 64 17 ) stone [ 1 0 0 0 ] [ 0 0 -1 0 ] 0 1 1
            ( 64 64 16 ) ( 64 64 17 ) ( 64 65 16 ) stone [ 0 1 0 0 ] [ 0 0 -1 0 ] 0 1 1
            }
            }
            """;

        var brush = ParseOneBrush(text);
        Assert.True(brush.Faces[0].HasAxes);
        Assert.True(BrushGeometry.TryBuild(brush, new MapSpace(), out var built, out string error), error);
        Assert.Equal(6, built.Faces.Length);

        // A 128-unit face with a 64-unit texture and no scaling covers the texture exactly twice.
        var wall = built.Faces.First(f => MathF.Abs(f.Normal.X) > 0.9f);
        float spread = wall.Uvs.Max(uv => uv.X) - wall.Uvs.Min(uv => uv.X);
        Assert.Equal(2f, spread, 3);
    }

    [Fact]
    public void TextureScaleStretchesTheTextureAcrossTheFace()
    {
        // Scale 2 means each texel covers two units, so the same wall shows half as many repeats. Off by
        // a reciprocal here and every wall in a level is wrong.
        var text = CubeMap.Replace("stone 0 0 0 1 1", "stone 0 0 0 2 2");

        var brush = ParseOneBrush(text);
        Assert.True(BrushGeometry.TryBuild(brush, new MapSpace(), out var built, out _));

        var wall = built.Faces.First(f => MathF.Abs(f.Normal.X) > 0.9f);
        float spread = wall.Uvs.Max(uv => uv.Y) - wall.Uvs.Min(uv => uv.Y);
        Assert.Equal(0.25f, spread, 3);      // 32 units / (2 scale x 64 texels)
    }

    [Theory]
    [InlineData(0, 4f, 2f)]
    [InlineData(90, 2f, 4f)]
    public void RotatingAFacesTextureTurnsItsAxes(int degrees, float expectedU, float expectedV)
    {
        // A brush 256 units across and 128 deep, so the two texture axes can be told apart by how far
        // they run: four tiles one way and two the other, and a quarter turn swaps them. Rotation is the
        // one part of the standard format's texture maths with a choice in it — Quake turns the *base*
        // axes about their own third axis rather than about the face normal, and the two are different
        // rotations on any face that is not axis-aligned.
        string r = degrees.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string face = " stone 0 0 " + r + " 1 1\n";
        string text = "{\n\"classname\" \"worldspawn\"\n{\n"
                    + "( -128 -64 -16 ) ( -128 -63 -16 ) ( -128 -64 -15 )" + face
                    + "( -128 -64 -16 ) ( -128 -64 -15 ) ( -127 -64 -16 )" + face
                    + "( -128 -64 -16 ) ( -127 -64 -16 ) ( -128 -63 -16 )" + face
                    + "( 128 64 16 ) ( 128 65 16 ) ( 129 64 16 )" + face
                    + "( 128 64 16 ) ( 129 64 16 ) ( 128 64 17 )" + face
                    + "( 128 64 16 ) ( 128 64 17 ) ( 128 65 16 )" + face
                    + "}\n}\n";

        var brush = ParseOneBrush(text);
        Assert.True(BrushGeometry.TryBuild(brush, new MapSpace(), out var built, out string error), error);

        var underside = built.Faces.First(f => f.Normal.Y < -0.9f);
        Assert.Equal(expectedU, underside.Uvs.Max(uv => uv.X) - underside.Uvs.Min(uv => uv.X), 3);
        Assert.Equal(expectedV, underside.Uvs.Max(uv => uv.Y) - underside.Uvs.Min(uv => uv.Y), 3);
    }

    [Fact]
    public void EntitiesKeepTheirKeysAndTheirPlaceInTheWorld()
    {
        var text = """
            {
            "classname" "worldspawn"
            "message" "A room"
            }
            {
            "classname" "info_player_start"
            "origin" "64 -32 16"
            "angle" "90"
            }
            """;

        Assert.True(MapFile.TryParse(text, out var map, out string error), error);
        Assert.Equal(2, map.Entities.Count);
        Assert.Equal("A room", map.Worldspawn!.Keys["message"]);

        var start = map.Entities[1];
        Assert.Equal("info_player_start", start.ClassName);
        Assert.True(start.TryGetVector("origin", out var origin));
        Assert.Equal(new Vector3(64, -32, 16), origin);
        Assert.Equal(90f, start.GetFloat("angle"));

        // Map space is Z-up: the entity is one metre up (16 units), not half a metre north.
        var engine = new MapSpace().ToEngine(origin);
        Assert.Equal(2f, engine.X, 3);
        Assert.Equal(0.5f, engine.Y, 3);
        Assert.Equal(1f, engine.Z, 3);
    }

    [Fact]
    public void CommentsAndLineBreaksInsideAFaceAreReadPast()
    {
        // TrenchBroom writes `// brush 0` comments and wraps long faces; a parser that reads lines
        // rather than tokens breaks on both.
        var text = """
            // Game: Quake
            // Format: Standard
            {
            "classname" "worldspawn"
            // brush 0
            {
            ( -64 -64 -16 ) ( -64 -63 -16 )
              ( -64 -64 -15 ) stone 0 0 0 1 1
            ( -64 -64 -16 ) ( -64 -64 -15 ) ( -63 -64 -16 ) stone 0 0 0 1 1
            ( -64 -64 -16 ) ( -63 -64 -16 ) ( -64 -63 -16 ) stone 0 0 0 1 1
            ( 64 64 16 ) ( 64 65 16 ) ( 65 64 16 ) stone 0 0 0 1 1
            ( 64 64 16 ) ( 65 64 16 ) ( 64 64 17 ) stone 0 0 0 1 1
            ( 64 64 16 ) ( 64 64 17 ) ( 64 65 16 ) stone 0 0 0 1 1
            }
            }
            """;

        var brush = ParseOneBrush(text);
        Assert.Equal(6, brush.Faces.Count);
    }

    [Fact]
    public void Quake2SurfaceFlagsAreReadPastRatherThanRefused()
    {
        // A map exported for a different profile has three extra numbers on every face. Nothing here
        // uses them, but refusing the file would be refusing a level over a field we ignore.
        var text = CubeMap.Replace("stone 0 0 0 1 1", "stone 0 0 0 1 1 0 0 0");

        var brush = ParseOneBrush(text);
        Assert.Equal(6, brush.Faces.Count);
        Assert.True(BrushGeometry.TryBuild(brush, new MapSpace(), out _, out string error), error);
    }

    [Theory]
    [InlineData("{ \"classname\" \"worldspawn\" ", "file ended inside an entity")]
    [InlineData("{\n\"classname\"\n}", "has no value")]
    [InlineData("{\n\"classname\" \"worldspawn\"\n{\n( 0 0 0 ) ( 0 1 0 ) ( 0 0 1 ) stone 0 0 0 1 1\n}\n}", "at least 4 faces")]
    public void AMalformedMapSaysWhatIsWrongAndWhere(string text, string expected)
    {
        Assert.False(MapFile.TryParse(text, out _, out string error));
        Assert.Contains(expected, error);
        Assert.Contains("line ", error);      // a mapper needs somewhere to look
    }
}
