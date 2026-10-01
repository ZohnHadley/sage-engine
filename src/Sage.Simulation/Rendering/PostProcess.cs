#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Simulation;

// Post-processing (issue 4h-6, REDESIGN §4.7, docs/design/06 "As built (post-processing)"): a chain of
// full-screen effects between the screen's views and the UI.
//
//   { "type": "post_effect", "id": "grade", "material": "sage:post_grade", "order": 100, "cvar": "r_post_grade" }
//
// An effect is a material (an effect file, a technique and its params, 07 §3.3) drawn over the whole
// picture, reading the step before it. Effects run by `order`, lowest first (ties by id); each can be
// switched by a cvar, and a mod removes one of the engine's like any record (`"patch": true,
// "disabled": true`). `r_post 0` turns the whole chain off.
[Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[Record("post_effect", Plugin = "sage.client")]
public sealed class PostEffectRecord
{
    [RecordRef("material")]
    [Property(Tooltip = "The material drawn over the whole picture: its effect reads the picture as Source")]
    public RecordId Material;

    [Property(Tooltip = "Where in the chain it runs: lower first; equal orders run by id")]
    public int Order;

    [Property(Tooltip = "A cvar that switches it: a bool, or a number where 0 is off. Empty: on whenever r_post is")]
    public string Cvar = "";
}

// Where a step of the chain reads from or writes to.
internal enum PostTarget
{
    Screen = -1,   // the back buffer: the last step writes here
    Scene = 0,     // `sage:scene`, what the screen's views drew into (colour and depth)
    Post0 = 1,     // `sage:post0` and `sage:post1`, the ping-pong pair between effects
    Post1 = 2,
}

// One full-screen draw: `Effect` (an index into the ordered effects; -1 for a plain copy, the render
// scale's upscale when no effect is on) reads `Source` and writes `Destination`.
internal readonly record struct PostStep(int Effect, PostTarget Source, PostTarget Destination);

// The planning half of post-processing: pure and headless, like RenderViewPlan, so the chain a frame
// draws is something a test can check. The client's renderer draws what it says.
internal static class PostChainPlan
{
    public const string SceneTarget = "sage:scene";
    public const string Post0Target = "sage:post0";
    public const string Post1Target = "sage:post1";

    // The smallest render scale: below a quarter the picture is not a picture.
    public const float MinScale = 0.25f;

    public static string TargetName(PostTarget target) => target switch
    {
        PostTarget.Scene => SceneTarget,
        PostTarget.Post0 => Post0Target,
        PostTarget.Post1 => Post1Target,
        _ => throw new ArgumentOutOfRangeException(nameof(target), "The screen is not a render target."),
    };

    // Whether a render scale draws the scene smaller than the screen (1, or anything above it, does not).
    public static bool IsScaled(float scale) => scale < 0.999f;

    // The size the scene (and the ping-pong pair) is drawn at: the screen's times the scale, at least
    // one pixel. The last step writes the screen at its own size, and the UI draws there after it.
    public static (int Width, int Height) ScaledSize(int width, int height, float scale)
    {
        if (!IsScaled(scale)) return (Math.Max(1, width), Math.Max(1, height));
        scale = Math.Max(scale, MinScale);
        return (Math.Max(1, (int)MathF.Round(width * scale)), Math.Max(1, (int)MathF.Round(height * scale)));
    }

    // The frame's chain, written to `steps` (one per effect at most, or one): the enabled effects in
    // order, the first reading the scene, each writing the ping-pong target the one before did not, and
    // the last writing the screen. Returns how many steps there are; 0 means the chain is off, and the
    // screen's views draw straight into the back buffer as they always have. With no effect on but a
    // render scale below 1 it is one copy, the scene stretched over the screen.
    public static int Plan(ReadOnlySpan<bool> enabled, float scale, Span<PostStep> steps)
    {
        int on = 0;
        for (int i = 0; i < enabled.Length; i++)
            if (enabled[i]) on++;

        if (on == 0)
        {
            if (!IsScaled(scale)) return 0;
            if (steps.Length < 1) throw new ArgumentException("steps needs room for the copy", nameof(steps));
            steps[0] = new PostStep(-1, PostTarget.Scene, PostTarget.Screen);
            return 1;
        }
        if (steps.Length < on) throw new ArgumentException("steps needs one entry per enabled effect", nameof(steps));

        var source = PostTarget.Scene;
        int n = 0;
        for (int i = 0; i < enabled.Length; i++)
        {
            if (!enabled[i]) continue;
            var destination = n == on - 1 ? PostTarget.Screen
                : source == PostTarget.Post0 ? PostTarget.Post1 : PostTarget.Post0;
            steps[n++] = new PostStep(i, source, destination);
            source = destination;
        }
        return n;
    }

    // The post effects in the order they run: by `order`, then by id. Built when the records load, not
    // per frame.
    public static List<(RecordId Id, PostEffectRecord Effect)> Ordered(RecordStore records)
    {
        var list = new List<(RecordId Id, PostEffectRecord Effect)>();
        if (records.TypeNameOf(typeof(PostEffectRecord)) == null) return list;
        foreach (var id in records.Ids("post_effect"))
            if (records.TryGet(id, out PostEffectRecord effect)) list.Add((id, effect));
        list.Sort((a, b) =>
        {
            int c = a.Effect.Order.CompareTo(b.Effect.Order);
            return c != 0 ? c : string.CompareOrdinal(a.Id.ToString(), b.Id.ToString());
        });
        return list;
    }

    // Whether an effect is on: its cvar (none: always) is not off. A bool cvar is its value; a number is
    // on unless 0; a string unless empty or "0".
    public static bool IsOn(CVar? cvar)
    {
        return cvar switch
        {
            null => true,
            CVar<bool> b => b.Value,
            CVar<int> i => i.Value != 0,
            CVar<float> f => f.Value != 0f,
            _ => cvar.ValueString.Length > 0 && cvar.ValueString != "0",
        };
    }

    // How much of the night tint the grade applies: 0 by day, 1 once the sun is well under the horizon,
    // fading across dusk and dawn with the sun's height. 0 with no sky (a world without a clock's sky
    // keeps the look it had).
    public static float Night(RecordStore records, WorldClock? clock)
    {
        if (SkyRules.Current(records, clock) is not { } sky) return 0f;
        var (_, elevation) = SkyRules.SunAt(sky, clock!.Hour);
        float x = Math.Clamp((elevation + NightFade) / (2f * NightFade), 0f, 1f);   // -NightFade → 1, +NightFade → 0
        return 1f - x * x * (3f - 2f * x);
    }

    // Half the band of sun heights (sin of elevation) over which dusk turns to night.
    private const float NightFade = 0.15f;
}
