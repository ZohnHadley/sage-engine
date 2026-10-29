namespace MyMod;

// MyMod's module: a code mod's modules are plugins named for the mod (`mymod`, or `mymod.<name>`), so what
// they register is the mod's. Register in Init only, as any module does.
[Plugin(Id, "0.1.0")]
[RequiresPlugin("sage", ">=0.1")]
public sealed class MyModModule : IModule
{
    public const string Id = "mymod";

    public void Init(ModuleContext ctx)
    {
        var engine = ctx.Engine;
        engine.CVars.RegisterCommand("mymod_hello", CVarFlags.None, "mymod_hello: say which notes this mod has.", _ =>
            Log.Info(LogCat.Console, "MyMod's notes: " + string.Join(", ", engine.Records.Ids("mymod_note"))));
    }
}

// A record type of the mod's own: `{ "type": "mymod_note", ... }` in its data/.
[Record("mymod_note", Plugin = MyModModule.Id)]
public sealed class MyModNote
{
    [Property(Tooltip = "What the note says")]
    public string Text = "";
}
