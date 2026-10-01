#nullable enable

namespace Sage.Simulation;

// Commands phase: the QuickSave and QuickLoad actions (F5 and F9 in engine content's input maps, which a
// game rebinds with its own `input_map`; plan decision 7) ask the SaveSystem for a quick-save or a
// quick-load, which runs when this tick ends (issue 4i-6). Read from the tick's PlayerCommand rather
// than a pawn's intent, so it works with no player in the world and while a screen has the keyboard.
[System(Id, Phase.Commands)]
internal sealed class QuickSaveKeysSystem : ISystem
{
    public const string Id = "sage.saves.quick_keys";

    private readonly World _world;
    private readonly SaveSystem _saves;
    private readonly ActionId _save, _load;

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
        var pressed = input.Command.Pressed;
        if (pressed.Has(_save)) _saves.QuickSave();
        if (pressed.Has(_load)) _saves.QuickLoad();
    }
}
