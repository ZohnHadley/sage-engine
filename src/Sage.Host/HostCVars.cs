namespace Sage.Host;

// Main-loop cvars (docs/design/01 §5.2 and §9).
internal sealed class HostCVars
{
    public CVar<int> TickRate { get; }
    public CVar<float> MaxFrameTime { get; }
    public CVar<float> TimeScale { get; }
    public CVar<bool> VSync { get; }
    public CVar<int> MaxFps { get; }
    public CVar<float> ExitAfter { get; }
    public CVar<int> Width { get; }
    public CVar<int> Height { get; }
    public const int MinWidth = 320, MaxWidth = 7680, MinHeight = 200, MaxHeight = 4320;

    public HostCVars(CVarRegistry r)
    {
        TickRate = r.Register("sim_tickrate", 60, CVarFlags.None,
            "Simulation ticks per second (Fixed schedule). Rendering interpolates between ticks.", 1, 240);
        MaxFrameTime = r.Register("sim_maxframetime", 0.25f, CVarFlags.None,
            "Longest frame the simulation catches up on, in seconds (avoids the spiral of death after a hitch).", 0.02f, 2f);
        TimeScale = r.Register("host_timescale", 1f, CVarFlags.DevOnly | CVarFlags.Cheat,
            "Simulation speed multiplier, on top of each world's own (WorldTime.HostScale; 0.2 = slow motion).", 0f, 10f);
        VSync = r.Register("r_vsync", true, CVarFlags.Archive,
            "Wait for the display's vertical sync.");
        MaxFps = r.Register("host_maxfps", 0, CVarFlags.Archive,
            "Frame-rate cap in frames per second (0 = uncapped; with r_vsync on, the display paces frames anyway).", 0, 1000);
        ExitAfter = r.Register("host_exitafter", 0f, CVarFlags.DevOnly,
            "Quit after this many seconds of real time and log frame/tick counts (0 = off). For automated smoke runs.", 0f, 86400f);
        // The window's size in pixels (issue #81): saved in config.cfg, or given on the command line as
        // `+vid_width 1600 +vid_height 900`. Applied when it changes.
        Width = r.Register("vid_width", 800, CVarFlags.Archive, "Window width in pixels.", MinWidth, MaxWidth);
        Height = r.Register("vid_height", 410, CVarFlags.Archive, "Window height in pixels.", MinHeight, MaxHeight);
    }
}
