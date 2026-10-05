using Microsoft.Xna.Framework;

namespace MyGame;

// MyGame's client half: what needs a screen. A plain IModule (a game module, if the game has one, is its
// simulation half's), loaded through game.json's `modules.add` (Client/bin/{config}/MyGame.Client.dll).
[Plugin("mygame.client", "0.1.0")]
public sealed class MyGameClientModule : IModule
{
    public IReadOnlyList<Type> Dependencies => new[] { typeof(ClientModule) };

    public void Init(ModuleContext ctx) { }

    public void OnWorldCreated(World world) => world.AddSystem(new MyGameHud(world));
}

// The HUD: the world's message log (what the rules `Say`), bottom left. The engine draws the world and
// the crosshair; what a HUD shows is the game's.
[System("mygame.hud", Phase.FrameUpdate)]
public sealed class MyGameHud : ISystem
{
    private readonly UiDraw _ui;
    private readonly MessageLog _messages;

    public MyGameHud(World world)
    {
        _ui = world.Resources.Get<UiDraw>();
        _messages = world.Messages();
    }

    public void Run(in SystemContext ctx)
    {
        if (_ui.Size.X < 1f) return;   // before the first frame sized the viewport
        var messages = _messages.Messages;
        float bottom = _ui.Size.Y - 40f;
        for (int i = messages.Length - 1, row = 0; i >= 0 && row < 6; i--, row++)
        {
            byte alpha = (byte)(255 * System.Math.Clamp(messages[i].Remaining, 0f, 1f));
            _ui.Text(24f, bottom - row * _ui.LineHeight, messages[i].Text, new Color((byte)255, (byte)255, (byte)255, alpha));
        }
    }
}
