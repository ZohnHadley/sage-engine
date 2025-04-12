using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

class EditorManager {
    private Dictionary<String,bool> debugModes = new Dictionary<String,bool>();
    private GraphicsDeviceManager graphicDeviceManager;
    private int windoWidth;
    private int windowHeight;
    private Camera camera;

    private float current_fps;

    public EditorManager(Game _game, int _windowWidth, int _windowHeight){
        graphicDeviceManager = new GraphicsDeviceManager(_game);
        
        graphicDeviceManager.PreferredBackBufferWidth = _windowWidth;
        graphicDeviceManager.PreferredBackBufferHeight = _windowHeight;

        windoWidth = graphicDeviceManager.PreferredBackBufferWidth;
        windowHeight = graphicDeviceManager.PreferredBackBufferHeight;
        graphicDeviceManager.ApplyChanges();

        debugModes.Add("showFPS", true);
        debugModes.Add("showGrid", false);
        debugModes.Add("showBoundingBox", false);
        debugModes.Add("showCollision", false);
        debugModes.Add("showCameraInfo", false);

        camera = new Camera(graphicDeviceManager, graphicDeviceManager.GraphicsDevice.DisplayMode.AspectRatio, new Vector3(0,0, 0), new Vector3(0, 0, 0));
    }
 
    public Dictionary<String,bool> getDebugModes(){
        return debugModes;
    }

    public GraphicsDeviceManager getGraphicsDeviceManager(){
        return graphicDeviceManager;
    }

    public int getWindowWidth(){
        return windoWidth;
    }

    public int getWindowHeight(){
        return windowHeight;
    }

    public bool getDebugMode(String mode){
        if(debugModes.ContainsKey(mode)){
            return debugModes[mode];
        }else{
            return false;
        }
    }

    public float getCurrentFPS(){
        return current_fps;
    }
    public void setCurrentFPS(float fps){
        current_fps = fps;
    }

    public void setDebugMode(String mode, bool value){
        if(debugModes.ContainsKey(mode)){
            debugModes[mode] = value;
        }else{
            debugModes.Add(mode, value);
        }
    }

    
    public void setWindowWidth(int width){
        windoWidth = width;
        graphicDeviceManager.PreferredBackBufferWidth = width;
        graphicDeviceManager.ApplyChanges();
    }

    public void setWindowHeight(int height){
        windowHeight = height;
        graphicDeviceManager.PreferredBackBufferHeight = height;
        graphicDeviceManager.ApplyChanges();
    }

    public Camera getCamera(){
        return camera;
    }

    public void setCamera(Camera cam){
        camera = cam;
    }

}