#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Friflo.Engine.ECS;

namespace sage_engine;

// Billboard sprites (docs/design/06 §3.8, 12): the Daggerfall-style creatures, NPCs and trees.
// Simulation-side data only; the client picks the direction and frame at extract.

public enum BillboardMode
{
    Cylindrical,   // rotates about Y only: characters, trees (they stay upright when you look up)
    Spherical,     // faces the camera fully: particles, effects, item pickups
}

// Draws a frame of a sprite sheet as a camera-facing quad at the entity's GlobalTransform.
// An empty Material means the sheet's material (or sage:sprite_default).
public struct SpriteRenderer : IComponent
{
    public RecordId Sheet;
    public RecordId Material;
    public Vector2 Size;     // metres (width, height); 0 = the sheet's default size
    public BillboardMode Mode;
    public byte Layer;
}

// One frame in the sheet's texture: the atlas rect in pixels, and the pivot inside that rect (also
// in pixels, from the top-left) that sits at the entity's position — usually the feet.
public sealed class SpriteFrame
{
    public int[] Rect = Array.Empty<int>();    // x, y, w, h
    public int[] Pivot = Array.Empty<int>();   // px, py; empty = bottom centre
}

// An animation: frames per direction group, played at Fps (12 §3).
public sealed class SpriteAnimation
{
    public float Fps = 8f;
    public bool Loop = true;
    public List<List<int>> Dirs = new();       // [direction][frame] → index into SpriteSheetRecord.Frames
}

// A sprite sheet (12 §3). Deviation from 05 §3.4/12: it is a *record* (data/**/*.json), not a
// `*.sheet.json` asset, because records already give hot reload, mod patches and validation, and the
// AssetServer doesn't exist yet (05 §3.5 lists sprite animation sets as records). It moves to an
// asset with R12 if that turns out better.
[Record("sprite_sheet")]
public sealed class SpriteSheetRecord
{
    public AssetPath Texture;
    public RecordId Material;                  // empty = sage:sprite_default
    public int Directions = 1;                 // 1 (always the same view), 5 (mirrored) or 8
    public Vector2 Size = new(1f, 1f);         // default world size in metres
    public BillboardMode Mode = BillboardMode.Cylindrical;
    public List<SpriteFrame> Frames = new();
    public Dictionary<string, SpriteAnimation> Animations = new(StringComparer.OrdinalIgnoreCase);

    public static readonly RecordId DefaultMaterial = new("sage", "sprite_default");

    private string[]? _names;

    // Clip names in a stable order (sorted), so SpriteAnimator.Clip can be an index: components hold
    // no strings, and gameplay looks an index up once with ClipIndex("walk").
    public IReadOnlyList<string> ClipNames => _names ??= Animations.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();

    public SpriteAnimation? Clip(int index) =>
        index >= 0 && index < ClipNames.Count && Animations.TryGetValue(ClipNames[index], out var a) ? a : null;

    public int ClipIndex(string name)
    {
        for (int i = 0; i < ClipNames.Count; i++)
            if (string.Equals(ClipNames[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}

// The pure parts of sprite rendering, in the engine so they can be tested headlessly.
public static class SpriteMath
{
    // Which of the sheet's direction groups faces the camera (06 §3.8): the angle from the sprite to
    // the camera, relative to the entity's own yaw, rounded to the nearest 45°.
    //   0 = seen from the front, 2 = its left side, 4 = from behind, 6 = its right side.
    // With 5 directions the sheet stores front..back (0..4) and the other three mirror them (Doom and
    // Daggerfall did this), so `flipU` is set and the quad's U runs backwards.
    public static int DirectionIndex(Vector3 spritePosition, Vector3 cameraPosition, float entityYaw, int directions, out bool flipU)
    {
        flipU = false;
        if (directions <= 1) return 0;

        Vector3 toCamera = cameraPosition - spritePosition;
        float viewAngle = MathF.Atan2(toCamera.X, toCamera.Z);      // 0 = camera along +Z, growing clockwise seen from above
        int index = (int)MathF.Round(WrapTau(viewAngle - entityYaw) / (MathF.PI / 4f)) & 7;
        if (directions >= 8 || index <= 4) return index;

        flipU = true;                                               // 5, 6, 7 mirror 3, 2, 1
        return 8 - index;
    }

    // The entity's yaw (rotation about Y) from its rotation quaternion.
    public static float Yaw(Quaternion rotation)
    {
        Vector3 forward = Vector3.Transform(new Vector3(0, 0, 1), rotation);
        return MathF.Atan2(forward.X, forward.Z);
    }

    // The frame of `clip` at `time` seconds, or -1 when the clip has no frames for this direction.
    // A non-looping clip holds its last frame.
    public static int FrameAt(SpriteAnimation clip, int direction, float time)
    {
        if (clip.Dirs.Count == 0) return -1;
        var frames = clip.Dirs[Math.Clamp(direction, 0, clip.Dirs.Count - 1)];
        if (frames.Count == 0) return -1;
        float fps = MathF.Max(clip.Fps, 0.0001f);
        int step = (int)MathF.Floor(MathF.Max(time, 0f) * fps);
        int index = clip.Loop ? step % frames.Count : Math.Min(step, frames.Count - 1);
        return frames[index];
    }

    private static float WrapTau(float a)
    {
        a %= MathF.Tau;
        return a < 0 ? a + MathF.Tau : a;
    }
}
