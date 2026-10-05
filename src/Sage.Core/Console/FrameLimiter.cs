#nullable enable
using System;

namespace Sage.Core;

// `host_maxfps` (issue #299): how long a frame that began at `frameStart` still has to wait to last
// 1/maxFps seconds. Pure arithmetic so a test can drive it; the host sleeps for the answer.
public static class FrameLimiter
{
    // Seconds to wait; 0 when uncapped (maxFps <= 0) or the frame already took long enough.
    public static double Remaining(int maxFps, double frameStart, double now)
    {
        if (maxFps <= 0) return 0;
        double left = frameStart + 1.0 / maxFps - now;
        return left > 0 ? left : 0;
    }
}
