using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;


namespace sage_engine;
internal class EditorManager {
    // private Dictionary<String,bool> debugModes = new Dictionary<String,bool>();
    private int _windowWidth = 0;
    private int _windowHeight = 0;
    public int windowWidth
    {
        get { return _windowWidth; } set
        {
            if(value > 0)   
                _windowWidth = value;
            // graphicDeviceManager.PreferredBackBufferHeight = value;
            // graphicDeviceManager.ApplyChanges();
        }
    }
    public int WindowHeight
    {
        get { return _windowHeight; }
        set
        {
            if(value > 0) 
                _windowHeight = value;
            // graphicDeviceManager.PreferredBackBufferHeight = value;
            // graphicDeviceManager.ApplyChanges();
        }
    }
    
    private DevCamera camera;

    public EditorManager(){ 
    } 

    public void setGraphicsDeviceManager(GraphicsDeviceManager gdm, int width, int height){
        windowWidth = width;
        WindowHeight = height;
        gdm.PreferredBackBufferWidth = windowWidth;
        gdm.PreferredBackBufferHeight = WindowHeight;
        gdm.ApplyChanges();
    } 
 
    public DevCamera getCamera(){
        return camera;
    }

    public void setCamera(DevCamera cam){
        camera = cam;
    }

}