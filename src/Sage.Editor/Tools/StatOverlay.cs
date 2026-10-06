#nullable enable
using System;
using System.Numerics;
using ImGuiNET;

namespace Sage.Editor;

// `stat fps` / `stat mem` / `stat frame` / `stat render` / `stat assets` overlays (docs/design/02 §4.4,
// §4.6 and §9; issue #300). Dev builds only (ImGui dev tools). `stat frame` shows the profiler: ms per phase
// and per system, averaged. `stat render` is the renderer's last frame (draw calls, triangles, what it drew)
// with the GPU uploads and thread-pool jobs of that frame (WorkStats); `stat assets` what is loaded, what
// loading cost the frame and what is still loading.
internal sealed class StatOverlay
{
    private readonly CoreCVars _core;
    private bool _showFps;
    private bool _showMem;
    private bool _showFrame;
    private bool _showRender;
    private bool _showAssets;
    private readonly Func<Renderer?> _renderer;
    private readonly Func<World?> _world;

    // Frame timing, averaged over half a second so the numbers are readable.
    private double _accumSeconds;
    private int _accumFrames;
    private float _fps;
    private float _avgMs;
    private float _worstMs;
    private float _worstInWindowMs;

    // Allocations measured on the main thread between two EndFrame calls.
    private long _lastAllocated = -1;
    private long _allocThisFrame;
    private int _framesOverBudget;

    public StatOverlay(CVarRegistry cvars, CoreCVars core, Func<Renderer?> renderer, Func<World?> world)
    {
        _core = core;
        _renderer = renderer;
        _world = world;
        cvars.RegisterCommand("stat", CVarFlags.None, "stat <fps|mem|frame|render|assets|all|none>: toggle performance overlays.", a =>
        {
            string what = a.Count > 0 ? a[0].ToLowerInvariant() : "";
            switch (what)
            {
                case "fps": _showFps = !_showFps; break;
                case "mem": _showMem = !_showMem; break;
                case "frame": _showFrame = !_showFrame; break;
                case "render": _showRender = !_showRender; break;
                case "assets": _showAssets = !_showAssets; break;
                case "all": _showFps = _showMem = _showFrame = _showRender = _showAssets = true; break;
                case "none": _showFps = _showMem = _showFrame = _showRender = _showAssets = false; break;
                default: Log.Warn(LogCat.Console, "stat <fps|mem|frame|render|assets|all|none>"); break;
            }
        });
    }

    // Call once per frame, after drawing, with the frame's elapsed real time.
    public void EndFrame(float frameSeconds)
    {
        _accumSeconds += frameSeconds;
        _accumFrames++;
        _worstInWindowMs = MathF.Max(_worstInWindowMs, frameSeconds * 1000f);
        if (_accumSeconds >= 0.5)
        {
            _fps = (float)(_accumFrames / _accumSeconds);
            _avgMs = (float)(_accumSeconds * 1000.0 / _accumFrames);
            _worstMs = _worstInWindowMs;
            _accumSeconds = 0;
            _accumFrames = 0;
            _worstInWindowMs = 0;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread();
        if (_lastAllocated >= 0)
            _allocThisFrame = allocated - _lastAllocated;
        _lastAllocated = allocated;

        int budget = _core.MemWarnBytes.Value;
        if (budget > 0 && _allocThisFrame > budget)
        {
            if (++_framesOverBudget >= 60)
                Log.Every(LogCat.Core, LogLevel.Warn, "mem_warn", TimeSpan.FromSeconds(30),
                    $"Frames are allocating {_allocThisFrame} bytes each (mem_warn_bytes = {budget}) for {_framesOverBudget} frames in a row");
        }
        else
        {
            _framesOverBudget = 0;
        }
    }

    // Phases ("Fixed.Gameplay") and their systems ("Fixed.Gameplay/FaceCameraSystem"), in run order.
    // Fixed-phase numbers are per frame, so they include every tick that ran in the frame.
    private static void DrawProfiler()
    {
        if (!Profiler.Enabled)
        {
            ImGui.Text("profiler disabled (Shipping build)");
            return;
        }
        ImGui.Separator();
        foreach (var e in Profiler.All)
        {
            if (e.AverageMs < 0.0005 && e.LastCalls == 0) continue;
            int slash = e.Name.IndexOf('/');
            string label = slash < 0 ? e.Name : "    " + e.Name.Substring(slash + 1);
            ImGui.Text($"{e.AverageMs,7:F3} ms  {label}");
        }
    }

    // The renderer's last frame, and the uploads and jobs of the frame before this one (WorkStats).
    private void DrawRender()
    {
        ImGui.Separator();
        var work = WorkStats.LastFrame;
        if (_renderer() is { } renderer)
        {
            var r = renderer.LastFrame;
            ImGui.Text($"draw calls {r.DrawCalls,6}   triangles {r.Triangles,8}   material switches {r.MaterialSwitches,4}");
            ImGui.Text($"items {r.Items,6} (culled {r.Culled})   sprites {r.Sprites}   lights {r.Lights}   debug lines {r.DebugLines}");
            if (r.Instanced > 0 || r.InstancedSprites > 0)
                ImGui.Text($"instanced {r.Instanced,6} in {r.InstancedDraws} draws (saved {r.Instanced - r.InstancedDraws})   instanced sprites {r.InstancedSprites}");
        }
        else ImGui.Text("no renderer");
        ImGui.Text($"uploads {work.Uploads,4} ({work.UploadBytes / 1024,6} KB)   total {WorkStats.Uploads} ({WorkStats.UploadBytes / (1024 * 1024)} MB)");
        ImGui.Text($"jobs running {work.JobsRunning,3}   started {work.JobsStarted,3}   finished {work.JobsFinished,3}   total {WorkStats.JobsFinished}");
    }

    // What is loaded, and what loading cost the last frame.
    private void DrawAssets()
    {
        ImGui.Separator();
        var work = WorkStats.LastFrame;
        if (_renderer() is { } renderer)
        {
            var r = renderer.LastFrame;
            ImGui.Text($"meshes {r.Meshes,5}   textures {r.Textures,5}   materials {r.Materials,5}");
        }
        ImGui.Text($"loads {work.LoadsFinished,3} ({work.LoadMs,7:F2} ms)   pending {work.LoadsPending,3}   total {WorkStats.LoadsFinished}");
        if (_world() is { } world && world.Resources.TryGet<Terrain>(out var terrain) && terrain is { Generator: not null })
            ImGui.Text($"terrain sectors loaded {terrain.Sectors.Count,3}");
    }

    public void Draw()
    {
        if (!_showFps && !_showMem && !_showFrame && !_showRender && !_showAssets) return;

        var io = ImGui.GetIO();
        ImGui.SetNextWindowPos(new Vector2(io.DisplaySize.X - 10, 30), ImGuiCond.Always, new Vector2(1, 0));
        ImGui.SetNextWindowBgAlpha(0.6f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoInputs;
        if (ImGui.Begin("##stats", flags))
        {
            if (_showFps)
                ImGui.Text($"{_fps,6:F1} fps   {_avgMs,6:F2} ms avg   {_worstMs,6:F2} ms worst");
            if (_showMem)
            {
                var gc = GC.GetGCMemoryInfo();
                ImGui.Text($"alloc/frame {_allocThisFrame,8} B   heap {gc.HeapSizeBytes / 1024,8} KB");
                ImGui.Text($"GC  gen0 {GC.CollectionCount(0)}  gen1 {GC.CollectionCount(1)}  gen2 {GC.CollectionCount(2)}");
                if (Log.DroppedCount > 0)
                    ImGui.Text($"log entries dropped: {Log.DroppedCount}");
            }
            if (_showRender)
                DrawRender();
            if (_showAssets)
                DrawAssets();
            if (_showFrame)
                DrawProfiler();
        }
        ImGui.End();
    }
}
