#nullable enable

namespace Sage.Simulation;

// Schedules and phases (docs/design/03 §3.5, glossary). Fixed phases run once per simulation tick
// (sim_tickrate, default 60 Hz); Frame phases once per rendered frame. Structural changes recorded
// in ctx.Commands are applied at the end of every phase.
public enum Phase
{
    // Schedule.Fixed
    Commands, PrePhysics, Physics, PostPhysics, Gameplay, AI, Animation, EntityIO, Late,
    // Schedule.Frame
    FrameUpdate, Extract, Render, Overlay,
}

public enum Schedule { Fixed, Frame }

public enum RunCondition
{
    Default,        // WhenNotPaused for Fixed phases, Always for Frame phases
    WhenNotPaused,
    Always,
    DevOnly,        // dev builds only (not run in Shipping)
}

public static class PhaseInfo
{
    public const Phase FirstFrame = Phase.FrameUpdate;
    public const int Count = (int)Phase.Overlay + 1;

    public static Schedule ScheduleOf(Phase phase) => phase < FirstFrame ? Schedule.Fixed : Schedule.Frame;
}

public interface ISystem
{
    void Run(in SystemContext ctx);
}

public readonly ref struct SystemContext
{
    public SystemContext(World world, Phase phase, in TickTime tick, in FrameTime frame)
    {
        World = world;
        Phase = phase;
        Tick = tick;
        Frame = frame;
    }

    public World World { get; }
    public Phase Phase { get; }
    public TickTime Tick { get; }       // valid in Fixed phases (and the last tick in Frame phases)
    public FrameTime Frame { get; }     // valid in Frame phases
    public EntityCommands Commands => World.Commands;   // deferred structural changes
}
