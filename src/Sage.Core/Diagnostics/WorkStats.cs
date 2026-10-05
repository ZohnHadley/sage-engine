#nullable enable
using System.Diagnostics;
using System.Threading;

namespace Sage.Core;

// **Work the frame did not see** (docs/design/02 §4.4–4.5, issue #300): jobs on the thread pool, loads
// (synchronous or not) and uploads to the GPU, counted process-wide so `stat render` and `stat assets` can
// show them. The profiler times what runs inside the frame; these are the things that make a frame hitch
// without appearing in it — a texture decoded on the main thread, a sector still generating on a worker,
// forty megabytes going to the card in one frame.
//
// Every counter is an Interlocked add, so any thread may report and nothing allocates. The host calls
// EndFrame once a frame, which turns the running totals into that frame's numbers (`LastFrame`).
public static class WorkStats
{
    // One frame's work, and what was still under way at its end.
    public readonly record struct Frame(
        int JobsStarted, int JobsFinished, int JobsRunning,
        int LoadsFinished, double LoadMs, int LoadsPending,
        int Uploads, long UploadBytes);

    private static long _jobsStarted, _jobsFinished;
    private static long _loadsStarted, _loadsFinished, _loadTicks;
    private static long _uploads, _uploadBytes;

    // The totals at the last EndFrame, to take this frame's from.
    private static long _lastJobsStarted, _lastJobsFinished, _lastLoadsFinished, _lastLoadTicks, _lastUploads, _lastUploadBytes;

    // Work handed to another thread (a terrain sector, a save's write, a shader rebuild): call JobStarted
    // where it is queued and JobFinished when it is done, whatever the outcome.
    public static void JobStarted() => Interlocked.Increment(ref _jobsStarted);
    public static void JobFinished() => Interlocked.Increment(ref _jobsFinished);

    // A load: content read and built (a texture, a mesh, a terrain sector), on whatever thread. LoadStarted
    // when it is asked for, LoadFinished with the Stopwatch ticks it took when it is there.
    public static void LoadStarted() => Interlocked.Increment(ref _loadsStarted);
    public static void LoadFinished(long elapsedTicks)
    {
        Interlocked.Increment(ref _loadsFinished);
        Interlocked.Add(ref _loadTicks, elapsedTicks);
    }

    // A buffer or texture written to the GPU: how many bytes.
    public static void Uploaded(long bytes)
    {
        Interlocked.Increment(ref _uploads);
        Interlocked.Add(ref _uploadBytes, bytes);
    }

    public static long JobsStarted => Interlocked.Read(ref _jobsStarted);
    public static long JobsFinished => Interlocked.Read(ref _jobsFinished);
    public static int JobsRunning => (int)(JobsStarted - JobsFinished);
    public static long LoadsFinished => Interlocked.Read(ref _loadsFinished);
    public static int LoadsPending => (int)(Interlocked.Read(ref _loadsStarted) - LoadsFinished);
    public static long Uploads => Interlocked.Read(ref _uploads);
    public static long UploadBytes => Interlocked.Read(ref _uploadBytes);

    // The last frame's numbers, from EndFrame.
    public static Frame LastFrame { get; private set; }

    // Once a frame, on the main thread, after it has drawn.
    public static void EndFrame()
    {
        long jobsStarted = JobsStarted, jobsFinished = JobsFinished, loads = LoadsFinished;
        long loadTicks = Interlocked.Read(ref _loadTicks), uploads = Uploads, bytes = UploadBytes;
        LastFrame = new Frame(
            (int)(jobsStarted - _lastJobsStarted), (int)(jobsFinished - _lastJobsFinished), (int)(jobsStarted - jobsFinished),
            (int)(loads - _lastLoadsFinished), (loadTicks - _lastLoadTicks) * 1000.0 / Stopwatch.Frequency, LoadsPending,
            (int)(uploads - _lastUploads), bytes - _lastUploadBytes);
        _lastJobsStarted = jobsStarted;
        _lastJobsFinished = jobsFinished;
        _lastLoadsFinished = loads;
        _lastLoadTicks = loadTicks;
        _lastUploads = uploads;
        _lastUploadBytes = bytes;
    }
}
