using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace sage_engine;

internal class KeyboardInputSystem : IEnginSystem
{
    private static KeyboardInputSystem instance;
    private KeyboardListener _keyboardListener;
    private MouseListener _mouseListener;

    private bool idDebug = false;
    private Dictionary<String, Keys> key_binds = new Dictionary<string, Keys> {
        ["toggle_debug"] = Keys.OemTilde
    };

    private KeyboardInputSystem()
    {
        _keyboardListener = new KeyboardListener();
        _keyboardListener.OnKeyPressed += HandleKeyPressed;
        // _keyboardListener.OnKeyReleased += HandleKeyReleased;
        // _mouseListener = new MouseListener();
        // _mouseListener.OnMouseButtonPressed += HandledMouseButtonPressed;
    }

    public static KeyboardInputSystem getInstance()
    {
        if(instance == null)
        {
            instance = new KeyboardInputSystem();
        }
        return instance;
    }

    private void HandledMouseButtonPressed(MouseButton mousebtn)
    {
        Console.WriteLine(mousebtn);
    }

    private void HandleKeyPressed(Keys key)
    {
        if (key_binds["toggle_debug"].Equals(key))
        {
            idDebug =! idDebug;
            Console.WriteLine(idDebug);
        }
    }

    private void HandleKeyReleased(Keys key)
    {
        // Console.WriteLine(key + " UP");
    }

    public void update(GameTime deltaTime)
    {
        // _mouseListener.update(deltaTime);
        _keyboardListener.Update(deltaTime);
    }
}