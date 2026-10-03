#nullable enable

namespace Sage.Simulation;

// Commands phase: the QuickSave and QuickLoad actions (F5 and F9 in engine content's input maps, which a
// game rebinds with its own `input_map`; plan decision 7) ask the SaveSystem for a quick-save or a
// quick-load, which runs when this tick ends (issue 4i-6). Read from the tick's PlayerCommand rather
// than a pawn's intent, so it works with no player in the world and while a screen has the keyboard.
//
// It runs in every pass, held ones included (RunCondition.Always, issue #285): a paused world still runs a
// held pass each real tick and is handed the player's command there, so F5 and F9 work while the game is
// paused, and the request runs at that pass's boundary. A slowed world's held pass keeps the last step's
// command, so a command is acted on once, by the tick it was sampled for.
[System(Id, Phase.Commands, Condition = RunCondition.Always)]
internal sealed class QuickSaveKeysSystem : ISystem
{
    public const string Id = "sage.saves.quick_keys";

    private readonly World _world;
    private readonly SaveSystem _saves;
    private readonly ActionId _save, _load;
    private long _handled = -1;   // the tick of the last command acted on

    public QuickSaveKeysSystem(World world, Engine engine)
    {
        _world = world;
        _saves = engine.Saves;
        _save = engine.Actions.Get(SaveSystem.QuickSaveAction);
        _load = engine.Actions.Get(SaveSystem.QuickLoadAction);
    }

    public void Run(in SystemContext ctx)
    {
        if (!_world.Resources.TryGet<PlayerInput>(out var input) || input is not { HasCommand: true }) return;
        long tick = input.Command.Tick;
        if (tick != 0 && tick == _handled) return;   // the same command again, in a held pass
        _handled = tick;
        var pressed = input.Command.Pressed;
        if (pressed.Has(_save)) _saves.QuickSave();
        if (pressed.Has(_load)) _saves.QuickLoad();
    }
}
