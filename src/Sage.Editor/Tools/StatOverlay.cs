#nullable enable
using System;
using System.Numerics;
using ImGuiNET;

namespace sage_engine;

// `stat fps` / `stat mem` / `stat frame` overlays (docs/design/02 §4.4, §4.6 and §9). Dev builds only
// (ImGui dev tools). `stat frame` shows the profiler: ms per phase and per system, averaged.
internal sealed class StatOverlay
{
    private readonly CoreCVars _core;
    private bool _showFps;
    private bool _showMem;
    private bool _showFrame;

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

    public StatOverlay(CVarRegistry cvars, CoreCVars core)
    {
        _core = core;
        cvars.RegisterCommand("stat", CVarFlags.None, "stat <fps|mem|frame|all|none>: toggle performance overlays.", a =>
        {
            string what = a.Count > 0 ? a[0].ToLowerInvariant() : "";
            switch (what)
            {
                case "fps": _showFps = !_showFps; break;
                case "mem": _showMem = !_showMem; break;
                case "frame": _showFrame = !_showFrame; break;
                case "all": _showFps = _showMem = _showFrame = true; break;
                case "none": _showFps = _showMem = _showFrame = false; break;
                default: Log.Warn(LogCat.Console, "stat <fps|mem|frame|all|none>"); break;
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

    public void Draw()
    {
        if (!_showFps && !_showMem && !_showFrame) return;

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
            if (_showFrame)
                DrawProfiler();
        }
        ImGui.End();
    }
}
