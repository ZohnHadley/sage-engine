namespace Sage.Host;

// Main-loop cvars (docs/design/01 §5.2 and §9).
internal sealed class HostCVars
{
    public CVar<int> TickRate { get; }
    public CVar<float> MaxFrameTime { get; }
    public CVar<float> TimeScale { get; }
    public CVar<bool> VSync { get; }
    public CVar<float> ExitAfter { get; }
    public CVar<int> Width { get; }
    public CVar<int> Height { get; }

    public HostCVars(CVarRegistry r)
    {
        TickRate = r.Register("sim_tickrate", 60, CVarFlags.None,
            "Simulation ticks per second (Fixed schedule). Rendering interpolates between ticks.", 1, 240);
        MaxFrameTime = r.Register("sim_maxframetime", 0.25f, CVarFlags.None,
            "Longest frame the simulation catches up on, in seconds (avoids the spiral of death after a hitch).", 0.02f, 2f);
        TimeScale = r.Register("host_timescale", 1f, CVarFlags.DevOnly | CVarFlags.Cheat,
            "Simulation speed multiplier (0.2 = slow motion).", 0f, 10f);
        VSync = r.Register("r_vsync", true, CVarFlags.Archive,
            "Wait for the display's vertical sync.");
        ExitAfter = r.Register("host_exitafter", 0f, CVarFlags.DevOnly,
            "Quit after this many seconds of real time and log frame/tick counts (0 = off). For automated smoke runs.", 0f, 86400f);
        // The window's size in pixels (issue #81): saved in config.cfg, or given on the command line as
        // `+vid_width 1600 +vid_height 900`. Applied when it changes.
        Width = r.Register("vid_width", 800, CVarFlags.Archive, "Window width in pixels.", 320, 7680);
        Height = r.Register("vid_height", 410, CVarFlags.Archive, "Window height in pixels.", 200, 4320);
    }
}
