using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Sage.Host;

// `r_pixelcheck <spec> [shaders]` (issue #318): at the end of the frame, read the back buffer and run the
// spec's pixel checks on it (PixelCheck, headless and unit-tested in Sage.Simulation). Each check is a line
// in the log; a failed one is an ERROR in Render, so tools/smoke_run.sh fails the run. The frame is saved
// beside the screenshots as pixelcheck-<spec>.png, for a person (or a CI artifact) to look at.
//
// The spec is a VFS path (a game's `checks/drawing.json`) or a file. Whether the frame was drawn with the
// engine's compiled effects is whether `shaders/lit.mgfxo` is in a mount: a host built with
// SageSkipShaders draws only clear colours, so its checks marked `shaders` are skipped and `noShaders`
// ones run. `shaders` after the spec says the frame must have been drawn with them: CI's drawing job
// passes it, so a missing shader cannot turn every real check into a skip.
//
// `r_framecheck <golden.json> [update]` (issue #355) reads the same frame back and compares it, a grid of
// cell colours (FrameGrid, headless and unit-tested), with a golden taken before; `update` writes the
// golden instead. tools/kit_screens_check.sh opens each of the RPG kit's screens and runs it, so a screen
// that draws differently fails CI. A golden taken with the engine's shaders is skipped by a host without
// them, and the other way round: the world behind the screen is another picture.
internal sealed class PixelCheckCapture
{
    private readonly Engine engine;
    private string? pending;
    private bool requireShaders;
    private string? pendingGolden;
    private bool updateGolden;

    public PixelCheckCapture(Engine engine) => this.engine = engine;

    public void Register(CVarRegistry cvars)
    {
        cvars.RegisterCommand("r_pixelcheck", CVarFlags.DevOnly,
            "r_pixelcheck <spec.json> [shaders]: at the end of this frame, read it back and check the spec's regions " +
            "(sky colour, shadow darkness, fog, the post grade); a failed check is an error. `shaders`: fail if the " +
            "engine's shaders are missing, instead of skipping the checks that need them.", a =>
            {
                if (a.Count == 0) { Log.Warn(LogCat.Console, "usage: r_pixelcheck <spec.json> [shaders]"); return; }
                pending = a[0];
                requireShaders = a.Count > 1 && string.Equals(a[1], "shaders", StringComparison.OrdinalIgnoreCase);
            });
        cvars.RegisterCommand("r_framecheck", CVarFlags.DevOnly,
            "r_framecheck <golden.json> [update]: at the end of this frame, read it back and compare its grid of cell colours " +
            "with the golden (a screen that draws differently is an error); `update` writes the golden from this frame instead.", a =>
            {
                if (a.Count == 0) { Log.Warn(LogCat.Console, "usage: r_framecheck <golden.json> [update]"); return; }
                pendingGolden = a[0];
                updateGolden = a.Count > 1 && string.Equals(a[1], "update", StringComparison.OrdinalIgnoreCase);
            });
    }

    // Called after the frame is drawn, before Present.
    public void AfterDraw(GraphicsDevice device)
    {
        if (pending != null)
        {
            string specPath = pending;
            pending = null;
            try { Run(device, specPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
            {
                Log.Error(LogCat.Render, $"Pixel check {specPath}: {ex.Message}");
            }
        }
        if (pendingGolden != null)
        {
            string goldenPath = pendingGolden;
            pendingGolden = null;
            try { RunGolden(device, goldenPath, updateGolden); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
            {
                Log.Error(LogCat.Render, $"Frame check {goldenPath}: {ex.Message}");
            }
        }
    }

    private bool Shaders => engine.Vfs.Exists(VirtualPath.Parse("shaders/lit.mgfxo"));

    private static PixelFrame ReadBack(GraphicsDevice device, out Color[] pixels)
    {
        int w = device.PresentationParameters.BackBufferWidth, h = device.PresentationParameters.BackBufferHeight;
        pixels = new Color[w * h];
        device.GetBackBufferData(pixels);
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            var c = pixels[i];
            rgba[i * 4] = c.R;
            rgba[i * 4 + 1] = c.G;
            rgba[i * 4 + 2] = c.B;
            rgba[i * 4 + 3] = 255;
        }
        return new PixelFrame(w, h, rgba);
    }

    private void Run(GraphicsDevice device, string specPath)
    {
        var spec = PixelCheckSpec.Parse(ReadSpec(specPath));
        bool shaders = Shaders;
        if (requireShaders && !shaders)
        {
            Log.Error(LogCat.Render, $"Pixel check {specPath}: the engine's shaders are not in Content/shaders, and `shaders` says they must be");
            return;
        }

        var frame = ReadBack(device, out var pixels);
        int w = frame.Width, h = frame.Height;
        string shot = Save(device, pixels, w, h, "pixelcheck", specPath);

        foreach (var region in spec.Regions)
            Log.Info(LogCat.Render, $"Pixel check region {region.Name}: {PixelCheck.Format(frame.Mean(region))}");

        List<PixelCheckResult> results = PixelCheck.Run(spec, frame, shaders);
        int passed = 0, failed = 0, skipped = 0;
        foreach (var r in results)
        {
            switch (r.Outcome)
            {
                case PixelCheckOutcome.Passed: passed++; Log.Info(LogCat.Render, $"Pixel check passed: {r.Name}: {r.Detail}"); break;
                case PixelCheckOutcome.Skipped: skipped++; Log.Info(LogCat.Render, $"Pixel check skipped: {r.Name} ({r.Detail})"); break;
                default: failed++; Log.Error(LogCat.Render, $"Pixel check FAILED: {r.Name}: {r.Detail}"); break;
            }
        }
        string summary = $"Pixel check {specPath} on a {w}x{h} frame {(shaders ? "with" : "without")} shaders: " +
                         $"{passed} passed, {failed} failed, {skipped} skipped; the frame is {shot}";
        if (failed > 0) Log.Error(LogCat.Render, summary);
        else Log.Info(LogCat.Render, summary);
    }

    // A frame against its golden (FrameGrid), or the golden written from it. The frame is saved beside the
    // screenshots as framecheck-<golden>.png either way, for a person or a CI artifact to look at.
    private void RunGolden(GraphicsDevice device, string goldenPath, bool update)
    {
        bool shaders = Shaders;
        var frame = ReadBack(device, out var pixels);
        string shot = Save(device, pixels, frame.Width, frame.Height, "framecheck", goldenPath);
        string name = Path.GetFileNameWithoutExtension(goldenPath);

        if (update)
        {
            // Keep what a person tuned in the old golden (its grid, tolerance and allowance).
            FrameGrid? old = File.Exists(goldenPath) ? FrameGrid.Parse(File.ReadAllText(goldenPath)) : null;
            var grid = FrameGrid.Of(frame, old?.Columns ?? FrameGrid.DefaultColumns, old?.Rows ?? FrameGrid.DefaultRows, shaders)
                .With(old?.Tolerance ?? FrameGrid.DefaultTolerance, old?.MaxCells ?? 0);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(goldenPath))!);
            File.WriteAllText(goldenPath, grid.ToJson(GoldenComment(name, grid)));
            Log.Info(LogCat.Render, $"Frame check {name}: golden written from a {frame.Width}x{frame.Height} frame " +
                                    $"{(shaders ? "with" : "without")} shaders to {goldenPath}; the frame is {shot}");
            return;
        }

        if (!File.Exists(goldenPath) && (Path.IsPathRooted(goldenPath) || !engine.Vfs.Exists(VirtualPath.Parse(goldenPath))))
            throw new FileNotFoundException($"no golden at '{goldenPath}': write it with `r_framecheck {goldenPath} update` (tools/kit_screens_check.sh --update)");
        var golden = FrameGrid.Parse(ReadSpec(goldenPath));
        if (golden.Shaders != shaders)
        {
            Log.Info(LogCat.Render, $"Frame check {name}: skipped, the golden was drawn {(golden.Shaders ? "with" : "without")} the engine's shaders " +
                                    $"and this host has {(shaders ? "them" : "none")}; the frame is {shot}");
            return;
        }
        if (frame.Width != golden.Width || frame.Height != golden.Height)
        {
            Log.Error(LogCat.Render, $"Frame check {name} FAILED: the frame is {frame.Width}x{frame.Height}, the golden's is {golden.Width}x{golden.Height} (set vid_width / vid_height)");
            return;
        }
        var drawn = FrameGrid.Of(frame, golden.Columns, golden.Rows, shaders).With(golden.Tolerance, golden.MaxCells);
        var comparison = golden.Compare(drawn);
        string worst = comparison.Worst.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        if (comparison.Passed(golden))
        {
            Log.Info(LogCat.Render, $"Frame check {name}: passed, {comparison.Differences.Count} of {comparison.Cells} cells past {golden.Tolerance} " +
                                    $"(at most {golden.MaxCells}), the worst off by {worst}; the frame is {shot}");
            File.Delete(Path.ChangeExtension(shot, ".json"));   // an earlier failure's grid is not this frame's
            return;
        }
        // What the golden would be from this frame, beside it: when the change is meant (or CI draws a hair
        // differently), that file is the new golden, without running the host again.
        string actual = Path.ChangeExtension(shot, ".json");
        File.WriteAllText(actual, drawn.ToJson(GoldenComment(name, drawn)));
        Log.Error(LogCat.Render, $"Frame check {name} FAILED: {comparison.Differences.Count} of {comparison.Cells} cells moved past {golden.Tolerance} " +
                                 $"(at most {golden.MaxCells}): {comparison.Describe()}; the frame is {shot}, its grid {actual}");
    }

    private static string GoldenComment(string name, FrameGrid grid) =>
        $"The frame golden of '{name}' (issue #355): r_framecheck compares each drawn frame's {grid.Columns}x{grid.Rows} cell colours with these.\n" +
        "Written by `r_framecheck <this file> update` (tools/kit_screens_check.sh --update); look at the frame it was taken from before committing it.";

    private string ReadSpec(string path)
    {
        if (File.Exists(path)) return File.ReadAllText(path);
        var virtualPath = VirtualPath.Parse(path);
        if (!engine.Vfs.Exists(virtualPath)) throw new FileNotFoundException($"no file or mounted asset '{path}'");
        using var stream = engine.Vfs.Open(virtualPath);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Save(GraphicsDevice device, Color[] pixels, int w, int h, string prefix, string specPath)
    {
        Directory.CreateDirectory(UserPaths.Screenshots);
        string file = Path.Combine(UserPaths.Screenshots, $"{prefix}-{Path.GetFileNameWithoutExtension(specPath)}.png");
        using var texture = new Texture2D(device, w, h);
        texture.SetData(pixels);
        using var stream = File.Create(file);
        texture.SaveAsPng(stream, w, h);
        return file;
    }
}
