#nullable enable

namespace Sage.Client;

// Opening the window when somebody is talked to (docs/design/16 §3.5, 13 §3, F24).
//
// The simulation does not open windows: it says "this was used" and this decides that a thing with
// something to say means a conversation. Same shape as the audio system — a client-side reader of an
// event the engine would raise on a headless server and nobody would show.
[System("sage.client.dialogue", Phase.FrameUpdate)]
public sealed class DialogueSystem : ISystem
{
    private readonly EventReader<Used> _used;
    private readonly ScreenStack _screens;
    private readonly Conversation _conversation;
    private readonly DialogueScreen _screen = new();

    public DialogueSystem(World world)
    {
        // **Fixed, not Frame**: `Used` is sent from the gameplay tick, and an event belongs to the queue
        // of the schedule that sent it (04 §3.1). Reading the Frame queue finds an empty one for ever,
        // which is exactly how F4's audio shipped silent.
        _used = world.Events.Reader<Used>(this, Schedule.Fixed);
        _screens = world.Resources.Get<ScreenStack>();
        _conversation = world.Resources.Get<Conversation>();
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;

        foreach (ref readonly var used in _used.Read())
        {
            if (!world.IsAlive(used.Target) || !world.Has<Dialogue>(used.Target)) continue;
            if (DialogueRules.Start(world, used.Target, used.User))
                _screens.Show(_screen, world, used.User);
        }

        // A conversation that ended takes its window with it, so "that is all" and pressing Escape are
        // the same thing to a player.
        if (!_conversation.Running && ReferenceEquals(_screens.Top, _screen)) _screens.Close();
    }
}
