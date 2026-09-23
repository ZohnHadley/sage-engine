using Microsoft.Xna.Framework;

namespace sage_engine;

// The window the editor host owns. Everything else this used to hold moved into the engine's own
// services; what is left is the back-buffer size (15 §3 gives it documents and a command log).
internal class EditorManager
{
    private int _windowWidth = 0;
    private int _windowHeight = 0;

    public int WindowWidth
    {
        get => _windowWidth;
        set { if (value > 0) _windowWidth = value; }
    }

    public int WindowHeight
    {
        get => _windowHeight;
        set { if (value > 0) _windowHeight = value; }
    }

    public void SetGraphicsDeviceManager(GraphicsDeviceManager gdm, int width, int height)
    {
        WindowWidth = width;
        WindowHeight = height;
        gdm.PreferredBackBufferWidth = WindowWidth;
        gdm.PreferredBackBufferHeight = WindowHeight;
        gdm.ApplyChanges();
    }
}
