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
internal sealed class PixelCheckCapture
{
    private readonly Engine engine;
    private string? pending;
    private bool requireShaders;

    public PixelCheckCapture(Engine engine) => this.engine = engine;

    public void Register(CVarRegistry cvars) =>
        cvars.RegisterCommand("r_pixelcheck", CVarFlags.DevOnly,
            "r_pixelcheck <spec.json> [shaders]: at the end of this frame, read it back and check the spec's regions " +
            "(sky colour, shadow darkness, fog, the post grade); a failed check is an error. `shaders`: fail if the " +
            "engine's shaders are missing, instead of skipping the checks that need them.", a =>
            {
                if (a.Count == 0) { Log.Warn(LogCat.Console, "usage: r_pixelcheck <spec.json> [shaders]"); return; }
                pending = a[0];
                requireShaders = a.Count > 1 && string.Equals(a[1], "shaders", StringComparison.OrdinalIgnoreCase);
            });

    // Called after the frame is drawn, before Present.
    public void AfterDraw(GraphicsDevice device)
    {
        if (pending == null) return;
        string specPath = pending;
        pending = null;
        try { Run(device, specPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            Log.Error(LogCat.Render, $"Pixel check {specPath}: {ex.Message}");
        }
    }

    private void Run(GraphicsDevice device, string specPath)
    {
        var spec = PixelCheckSpec.Parse(ReadSpec(specPath));
        bool shaders = engine.Vfs.Exists(VirtualPath.Parse("shaders/lit.mgfxo"));
        if (requireShaders && !shaders)
        {
            Log.Error(LogCat.Render, $"Pixel check {specPath}: the engine's shaders are not in Content/shaders, and `shaders` says they must be");
            return;
        }

        int w = device.PresentationParameters.BackBufferWidth, h = device.PresentationParameters.BackBufferHeight;
        var pixels = new Color[w * h];
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
        var frame = new PixelFrame(w, h, rgba);
        string shot = Save(device, pixels, w, h, specPath);

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

    private string ReadSpec(string path)
    {
        if (File.Exists(path)) return File.ReadAllText(path);
        var virtualPath = VirtualPath.Parse(path);
        if (!engine.Vfs.Exists(virtualPath)) throw new FileNotFoundException($"no file or mounted asset '{path}'");
        using var stream = engine.Vfs.Open(virtualPath);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Save(GraphicsDevice device, Color[] pixels, int w, int h, string specPath)
    {
        Directory.CreateDirectory(UserPaths.Screenshots);
        string file = Path.Combine(UserPaths.Screenshots, $"pixelcheck-{Path.GetFileNameWithoutExtension(specPath)}.png");
        using var texture = new Texture2D(device, w, h);
        texture.SetData(pixels);
        using var stream = File.Create(file);
        texture.SaveAsPng(stream, w, h);
        return file;
    }
}
