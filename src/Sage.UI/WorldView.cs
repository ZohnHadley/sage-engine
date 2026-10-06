#nullable enable
namespace Sage.UI;

// A view-model every game has (issue #347), so a screen made of records alone — a mod's — has something
// to bind without C#: the world's variables (#89), by name, read and written. `vars[gold]` shows one, a
// slider bound to `vars[volume]` sets one, and a button's `set_var`/`add_var` changes what it shows.
//
//   { "type": "screen", "id": "mymod:tally", "layout": "mymod:tally", "viewModel": "ui_world" }
//   "count": { "widget": "label", "text": "Bells: {n}", "args": { "n": "vars[bells]" } }
[ViewModel("ui_world")]
internal sealed class WorldView : IViewModel
{
    public WorldVars Vars { get; } = new();

    public void Refresh(in UiBindContext context) => Vars.World = context.World;
}

// The world's variables by name, as a binding path indexes them: unset reads as 0.
internal sealed class WorldVars
{
    internal World? World;

    public double this[string name]
    {
        get => World == null ? 0 : Vars.ValueOf(World, name);
        set { if (World != null) Vars.Of(World).Set(name, value); }
    }
}
