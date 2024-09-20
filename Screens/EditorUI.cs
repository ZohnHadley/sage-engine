using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Myra;
using Myra.Graphics2D.Brushes;
using Myra.Graphics2D.UI;
namespace sage_engine;
class EditorUI
{
    private static Desktop _desktop;
    static bool isHovered = false;
    private static HorizontalMenu top_menu;
    private static MenuItem menuItem_scene;

    public static void load(GraphicsDevice graphicsDevice)
    {
        //add background to  panel
        _desktop = new Desktop();
        
      //  var stackPanelLayout = new StackPanelLayout( Orientation.Vertical);
         
        top_menu = new HorizontalMenu(); 
        menuItem_scene = new MenuItem { Text = "Scene" };
        menuItem_scene.Items.Add(new MenuItem { Text = "Save" });
        menuItem_scene.Items.Add(new MenuItem { Text = "Open" });
        menuItem_scene.Items.Add(new MenuItem { Text = "New" });
        top_menu.Items.Add(menuItem_scene); 

        _desktop.Root = top_menu;
    }

    public static void update(GameTime gameTime)
    {
        MouseState mouseState = Mouse.GetState();
       //check if mouse is hovering over the menu
        if (top_menu.Bounds.Contains(mouseState.Position))
        {
            //if mouse is hovering over the menu
            isHovered = true; 
        }
        else
        {
            //if mouse is not hovering over the menu 
            isHovered = false;
        }
    }

    public static void draw()
    {
        _desktop.Render();
    }

    public static bool getIsHovered()
    {
        return isHovered;
    }
}