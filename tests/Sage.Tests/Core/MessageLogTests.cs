#nullable enable
using sage_engine;

namespace sage_engine.Tests;

using Assert = Xunit.Assert;

// Player-facing messages (docs/design/13 §3). A world resource, so this is all headless: a server
// pushes into a log nobody draws.
public class MessageLogTests
{
    public MessageLogTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void MessagesAgeAndLeave()
    {
        var log = new MessageLog(new GameEvents());
        log.Add("Picked up a sword", MessageKind.Good, seconds: 2f);

        Assert.Equal(1, log.Count);
        Assert.Equal(1f, log[0].Fade, 3);

        log.Advance(1f);
        Assert.Equal(0.5f, log[0].Fade, 3);     // half gone: a HUD fades on this

        log.Advance(1.1f);
        Assert.Equal(0, log.Count);
    }

    [Fact]
    public void TheOldestGoesWhenTheLogIsFull()
    {
        var log = new MessageLog(new GameEvents());
        for (int i = 0; i < 40; i++) log.Add($"line {i}", MessageKind.Info, 60f);

        Assert.Equal(32, log.Count);
        Assert.Equal("line 8", log[0].Text);     // the first eight scrolled off
        Assert.Equal("line 39", log[log.Count - 1].Text);
    }

    [Fact]
    public void EveryWorldHasOneAndItAgesWithTheFrame()
    {
        using var world = new World("messages");
        world.Say("You died", MessageKind.Bad, 1f);

        // Saying something sends an event (04 §3.1); the log is the presentation system that picks
        // it up, so it goes on screen at the next frame rather than the instant a tick says it.
        Assert.Equal(0, world.Messages().Count);

        world.RunFrame(0.5f, 0f);
        Assert.Equal(1, world.Messages().Count);

        world.RunFrame(0.6f, 0f);
        Assert.Equal(1, world.Messages().Count);   // 0.4 s of its second left

        world.RunFrame(0.5f, 0f);
        Assert.Equal(0, world.Messages().Count);   // display time, not ticks
    }

    // A dedicated server ticks and never draws. The log still has to keep up, or its reader falls
    // behind ev_maxage and the bus reports the log itself as the thing that is lagging (04 §3.2).
    [Fact]
    public void AWorldThatNeverDrawsAFrameStillKeepsUp()
    {
        using var world = new World("headless");
        for (int i = 0; i < 40; i++)
        {
            world.Say($"tick {i}", MessageKind.Info, 600f);
            world.RunFixed(1f / 60f);
        }

        Assert.Equal(32, world.Messages().Count);            // the cap, not whatever survived ev_maxage
        Assert.Equal("tick 39", world.Messages()[31].Text);
    }

    [Fact]
    public void EmptyTextIsIgnored()
    {
        var log = new MessageLog(new GameEvents());
        log.Add("");
        Assert.Equal(0, log.Count);
    }
}
