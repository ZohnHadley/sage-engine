using Myra;
using Myra.Graphics2D.UI;
namespace sage_engine;
class EditorUI
{
    private static Desktop _desktop;

    public static void load()
    {
        var horizontalStackPanel = new HorizontalStackPanel
        {
            Spacing = 8,
            GridRow = 0,
            GridColumn = 0
        };

        var textBlock1 = new Label();
        textBlock1.Text = "First Text";
        horizontalStackPanel.Widgets.Add(textBlock1);

        var textButton1 = new TextButton();
        textButton1.Text = "Second Button";
        horizontalStackPanel.Widgets.Add(textButton1);

        var checkStackPanel1 = new CheckBox();
        checkStackPanel1.Text = "Third Checkbox";

        horizontalStackPanel.Widgets.Add(checkStackPanel1);
        _desktop = new Desktop();
        _desktop.Root = horizontalStackPanel;
    }


    public static void draw()
    {
        _desktop.Render();
    }
}