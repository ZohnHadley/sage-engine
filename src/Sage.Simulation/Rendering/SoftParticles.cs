#nullable enable
using System;
using System.Numerics;

namespace Sage.Simulation;

// Soft particles (issue 4n-6, docs/design/06 §3.12): a particle fades where it meets opaque geometry, so a
// smoke quad that cuts the floor shows no hard line along the cut.
//
// The client draws them with the soft twin of the material's technique (`UnlitBlend` → `UnlitBlendSoft`,
// sprite.fx), which samples the scene's depth (`sage:depth`, the depth hook of issue #316) under the pixel
// and scales the particle's colour by `Fade`: nothing where it touches the surface, all of it `soft`
// metres in front. This is that arithmetic, headless, and the rules for when it applies; sprite.fx
// repeats it line for line (`SoftLinearDepth`, `PSUnlitBlendSoft`).
//
// When it applies: a sprite with `soft` > 0 drawn with a Transparent material (an alpha-tested one is in
// the depth it would read, and would fade against itself), in a screen view that is not depth-only (the
// depth covers the screen's views as the scene draws them), on a frame whose depth was drawn and with an
// effect that has the soft technique. Anywhere else it is drawn hard, as every particle was before.
internal static class SoftParticles
{
    // The soft twin of a technique: its name with `Soft` after it (its instanced twin is then
    // `Instancing.TechniqueFor` of that: `UnlitBlendSoftInstanced`).
    public const string Suffix = "Soft";

    public static string TechniqueFor(string technique) => technique + Suffix;

    // Whether a sprite of this softness, drawn in this pass, asks to be drawn soft (and so asks the frame
    // for the scene's depth).
    public static bool Applies(float soft, RenderPass pass) => soft > 0f && pass == RenderPass.Transparent;

    // Whether the frame draws `sage:depth`: a post effect (or the water) reads it, or a soft particle is
    // on screen.
    public static bool FrameNeedsDepth(bool postNeedsDepth, int softSprites) => postNeedsDepth || softSprites > 0;

    // How much of a particle is left with `gap` metres between it and the geometry behind it: 0 touching
    // (or behind it), 1 at `soft` metres or more; linear between. A soft distance of 0 is hard: all of it.
    public static float Fade(float gap, float soft) => soft <= 0f ? 1f : Math.Clamp(gap / soft, 0f, 1f);

    // The same from two depths as the depth hook stores them (z/w, Direct3D's 0..1) and the view's planes:
    // the scene's under the pixel and the particle's own (PostCurves.LinearDepth turns both to metres).
    public static float Fade(float sceneDepth, float particleDepth, float near, float far, bool orthographic, float soft) =>
        Fade(PostCurves.LinearDepth(sceneDepth, near, far, orthographic) - PostCurves.LinearDepth(particleDepth, near, far, orthographic), soft);

    // sprite.fx's `SoftParams`: x = 1 / the soft distance (multiplied, not divided, in the shader), y and z
    // the view's near and far planes, w = 1 when it is orthographic.
    public static Vector4 Params(float soft, float near, float far, bool orthographic) =>
        new(soft > 0f ? 1f / soft : 0f, near, far, orthographic ? 1f : 0f);

    // sprite.fx's `SoftRect`: where the view draws in `sage:depth`, as uv (x, y, width, height), from its
    // rectangle in pixels and the target's size.
    public static Vector4 Rect(int x, int y, int width, int height, int targetWidth, int targetHeight)
    {
        float w = Math.Max(1, targetWidth), h = Math.Max(1, targetHeight);
        return new Vector4(x / w, y / h, width / w, height / h);
    }

    // Where a pixel at `ndc` (the view's clip x/w, y/w: -1..1, y up) reads `sage:depth`: across the view's
    // rectangle, top to bottom as textures are (sprite.fx's `PSUnlitBlendSoft`).
    public static Vector2 DepthUv(Vector2 ndc, Vector4 rect) =>
        new(rect.X + (ndc.X * 0.5f + 0.5f) * rect.Z, rect.Y + (ndc.Y * -0.5f + 0.5f) * rect.W);
}
