using System;
using System.ComponentModel.Design.Serialization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Myra;
using Myra.Graphics2D.Brushes;
using Myra.Graphics2D.UI;
using Myra.Graphics2D.UI.Properties;
namespace sage_engine;
class EditorUI
{
    private static Desktop _desktop;
    private static EditorManager editorManager;
    static bool isHovered = false;
    static Label label_fps;
    public static void load(GraphicsDevice graphicsDevice)
    {
        _desktop = new Desktop();
        //add background to  panel
        
        var root = new Panel
        {
            Width = graphicsDevice.Viewport.Width,
            Height = graphicsDevice.Viewport.Height,
        };

        var verticalStack = new VerticalStackPanel
        {
        
            Spacing = 2,
          
        };

        /*var topMenu = new HorizontalMenu();
        topMenu.Items.Add(new MenuItem { Text = "File" });
        topMenu.Items.Add(new MenuItem { Text = "Edit" });
        

        var horizontalSplit = new HorizontalSplitPane
        {
            Width = graphicsDevice.Viewport.Width,
            Height = graphicsDevice.Viewport.Height,
        };
        StackPanel.SetProportionType(horizontalSplit, Myra.Graphics2D.UI.ProportionType.Fill);

        var rightPanel = new VerticalStackPanel
        { 
            Height = graphicsDevice.Viewport.Height, 
        };

        var leftPanel_inspectionPanel = new VerticalStackPanel
        {
            Spacing = 2,
 
            Height = graphicsDevice.Viewport.Height,

            Background = new SolidBrush(Color.Gray),
            ShowGridLines = true,
            GridLinesColor = Color.White,
        };

        var label_leftPanel = new Label
        {
            Text = "Inspection Panel",
          
        };

        var label_leftPanel_objectName = new Label
        {
            Text = "name : N/A",
          
        };
 
        var bottomPanel = new VerticalStackPanel
        {
            Height = graphicsDevice.Viewport.Height / 2,
            Background = new SolidBrush(Color.Gray),
            ShowGridLines = true,
            GridLinesColor = Color.White,
        };

        leftPanel_inspectionPanel.Widgets.Add(label_leftPanel);
        leftPanel_inspectionPanel.Widgets.Add(label_leftPanel_objectName);
        


        horizontalSplit.Widgets.Add(leftPanel_inspectionPanel);
        horizontalSplit.Widgets.Add(rightPanel); 
        horizontalSplit.SetSplitterPosition(0, 0.25f);


        var verticalSplit = new VerticalSplitPane
        {
            Width = graphicsDevice.Viewport.Width, 
        };
        
        verticalSplit.Widgets.Add(horizontalSplit);
        verticalSplit.Widgets.Add(bottomPanel);

        verticalStack.Widgets.Add(topMenu);*/
        label_fps = new Label
        {
            Text =  editorManager.getCurrentFPS().ToString(),
            TextColor = Color.Red,
        };
        verticalStack.Widgets.Add(label_fps);
        //verticalStack.Widgets.Add(verticalSplit);

        root.Widgets.Add(verticalStack);
        //add buttons to grid 
 
        _desktop.Root = root;
    }

    public static void update(GameTime gameTime)
    {
 
        MouseState mouseState = Mouse.GetState();
        //check if mouse is hovering over the menu
        if (_desktop.IsMouseOverGUI)
        {
            //if mouse is hovering over the menu
            isHovered = true;
        }
        else
        {
            //if mouse is not hovering over the menu 
            isHovered = false;
        }
        
        //Console.WriteLine(isHovered);
    }

    public static void draw(GameTime gameTime)
    {
        editorManager.setCurrentFPS( 1 / (float)gameTime.ElapsedGameTime.TotalSeconds);
        label_fps.Text = editorManager.getCurrentFPS().ToString();
        _desktop.Render();
    }

    public static bool getIsHovered()
    {
        return isHovered;
    }

    public static void setEditorManager(EditorManager _editorManager)
    {
        editorManager = _editorManager;
    }
}

