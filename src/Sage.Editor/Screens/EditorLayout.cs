#nullable enable
using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace Sage.Editor;

// The editor's docked layout (`-edit`, issue #219, 15 §3):
//
//   +---------------------------------------------------------------+
//   | menu bar                                                      |
//   +-----------+---------------------------------------+-----------+
//   | Outliner  |                                       | Inspector |
//   |           |   the screen: the edit world under    |           |
//   |           |   the free camera (nothing drawn)     |           |
//   +-----------+---------------------------------------+-----------+
//   | Log | Console                                                 |
//   +---------------------------------------------------------------+
//   | status bar: document, saved or not, selection                 |
//   +---------------------------------------------------------------+
//
// **The viewport is the screen itself**, not an ImGui window holding a render target: the dock space's
// central node is a pass-through, so the world the renderer drew on the back buffer shows in the middle,
// and the mouse over it is the free camera's (ImGui does not capture it there). That is the simplest
// thing that cannot go wrong: no second camera, no texture binding, no size to keep in step with a
// window. The panels cover the edges of the picture rather than squeezing it; `ed_viewport`'s window is
// still there for a second view. Panels can be dragged anywhere and re-docked (ImGui's docking);
// `ed_layout` puts them back.
//
// ImGui.NET (1.90) is built from ImGui's docking branch, so `DockSpace` and the docking flags are there,
// but the DockBuilder that lays out a default arrangement is ImGui's internal API and has no C# binding.
// Its functions are exported by the same native library ImGui.NET loads (cimgui), so the six this needs
// are declared below.
internal sealed class EditorLayout
{
    public const string OutlinerTitle = "Outliner";
    public const string InspectorTitle = "Inspector";
    public const string ConsoleTitle = "Console";

    private const string HostTitle = "##sage_editor_dock_host";
    private bool _buildLayout = true;   // on the first frame, and after `ed_layout`

    // The dock space's height is the display's minus the menu bar and the status bar.
    public float StatusBarHeight => ImGui.GetFrameHeight() + 2;

    public void Reset() => _buildLayout = true;

    // Docking on, then the host window that fills the screen between the menu bar and the status bar,
    // and the dock space in it. Call after the menu bar (it takes the work area's top) and before the
    // panels, which dock into it by title.
    public void BeginFrame()
    {
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;

        var viewport = ImGui.GetMainViewport();
        var size = new Vector2(viewport.WorkSize.X, viewport.WorkSize.Y - StatusBarHeight);
        ImGui.SetNextWindowPos(viewport.WorkPos);
        ImGui.SetNextWindowSize(size);
        ImGui.SetNextWindowViewport(viewport.ID);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        const ImGuiWindowFlags hostFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBringToFrontOnFocus
            | ImGuiWindowFlags.NoNavFocus | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings;
        ImGui.Begin(HostTitle, hostFlags);
        ImGui.PopStyleVar(3);

        uint dock = ImGui.GetID("sage_editor_dock");
        if (_buildLayout)
        {
            _buildLayout = false;
            BuildDefault(dock, size);
        }
        ImGui.DockSpace(dock, Vector2.Zero, ImGuiDockNodeFlags.PassthruCentralNode);
        ImGui.End();
    }

    // The status bar along the bottom of the screen: whatever `text` says, on one line.
    public void DrawStatusBar(string text)
    {
        var viewport = ImGui.GetMainViewport();
        float height = StatusBarHeight;
        ImGui.SetNextWindowPos(new Vector2(viewport.WorkPos.X, viewport.WorkPos.Y + viewport.WorkSize.Y - height));
        ImGui.SetNextWindowSize(new Vector2(viewport.WorkSize.X, height));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 2));
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoDocking
            | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;
        if (ImGui.Begin("##sage_editor_status", flags)) ImGui.TextUnformatted(text);
        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    // Outliner on the left, inspector on the right, the log and the console as tabs along the bottom,
    // and the middle left empty for the world.
    private static void BuildDefault(uint dock, Vector2 size)
    {
        Native.igDockBuilderRemoveNode(dock);
        Native.igDockBuilderAddNode(dock, (int)ImGuiDockNodeFlags.PassthruCentralNode | Native.DockSpaceFlag);
        Native.igDockBuilderSetNodeSize(dock, size);

        Native.igDockBuilderSplitNode(dock, ImGuiDir.Down, 0.28f, out uint bottom, out uint top);
        Native.igDockBuilderSplitNode(top, ImGuiDir.Left, 0.22f, out uint left, out uint rest);
        Native.igDockBuilderSplitNode(rest, ImGuiDir.Right, 0.28f, out uint right, out _);

        Native.igDockBuilderDockWindow(OutlinerTitle, left);
        Native.igDockBuilderDockWindow(InspectorTitle, right);
        Native.igDockBuilderDockWindow(LogPanel.Title, bottom);
        Native.igDockBuilderDockWindow(ConsoleTitle, bottom);
        Native.igDockBuilderFinish(dock);
    }

    // imgui_internal.h's DockBuilder, from the cimgui library ImGui.NET itself loads (same name, same
    // calling convention as ImGuiNET.ImGuiNative).
    private static class Native
    {
        private const string Library = "cimgui";

        // ImGuiDockNodeFlags_DockSpace (imgui_internal.h, ImGuiDockNodeFlagsPrivate_): a node that lives
        // in a window of ours rather than floating in its own.
        public const int DockSpaceFlag = 1 << 10;

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint igDockBuilderAddNode(uint nodeId, int flags);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern void igDockBuilderRemoveNode(uint nodeId);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern void igDockBuilderSetNodeSize(uint nodeId, Vector2 size);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint igDockBuilderSplitNode(uint nodeId, ImGuiDir splitDir, float sizeRatioForNodeAtDir,
                                                         out uint outIdAtDir, out uint outIdAtOppositeDir);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern void igDockBuilderDockWindow([MarshalAs(UnmanagedType.LPUTF8Str)] string windowName, uint nodeId);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern void igDockBuilderFinish(uint nodeId);
    }
}
