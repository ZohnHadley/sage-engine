#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using Microsoft.Xna.Framework.Graphics;
using MonoGame.ImGuiNet;
using Sage.Editing;

namespace Sage.Editor;

// The drag of an asset out of the browser (issue #366): which asset, while the mouse is held. A field that
// takes assets calls `Accept` after drawing its widget; the viewport takes a drop that lands on no window
// (AssetsPanel.HandleViewport). ImGui's own drag and drop carries the gesture; the path rides here rather
// than in its payload buffer, as only one drag can be in the air.
internal static class AssetDrag
{
    public const string Payload = "SAGE_ASSET";

    public static VirtualPath? Dragging { get; private set; }

    // Call right after the item that is dragged: it starts (and keeps) the drag of `entry`.
    public static void Source(AssetEntry entry, Action drawPreview)
    {
        if (!ImGui.BeginDragDropSource()) return;
        Dragging = entry.Path;
        ImGui.SetDragDropPayload(Payload, IntPtr.Zero, 0);
        drawPreview();
        ImGui.EndDragDropSource();
    }

    // Call right after a field's widget: the asset dropped on it this frame when it fits a field of
    // `kind` (null: any asset), else null. A drag that does not fit is not offered to the field at all.
    public static VirtualPath? Accept(string? kind)
    {
        if (Dragging is not { } path || !AssetKinds.Fits(kind, path) || !ImGui.BeginDragDropTarget()) return null;
        bool dropped;
        unsafe { dropped = ImGui.AcceptDragDropPayload(Payload).NativePtr != null; }
        ImGui.EndDragDropTarget();
        return dropped ? path : null;
    }

    // The mouse came up: whatever was dropped has been taken by now (end of the frame).
    public static void EndFrame()
    {
        if (Dragging != null && !ImGui.IsMouseDown(ImGuiMouseButton.Left)) Dragging = null;
    }
}

// Textures as ImGui images, by path: what the browser's thumbnails and the material preview draw. Loaded
// through the client's content service (so a texture the game also draws is loaded once), a few a frame
// so opening a folder of hundreds does not stall, and dropped when the content service reloads one.
internal sealed class Thumbnails : IDisposable
{
    private const int LoadsPerFrame = 4;

    private readonly ImGuiRenderer _gui;
    private readonly Func<ContentService?> _content;
    private readonly Dictionary<AssetPath, (IntPtr Id, Texture2D Texture)> _bound = new();
    private readonly HashSet<AssetPath> _failed = new();
    private ContentService? _watched;
    private int _loadsLeft;

    public Thumbnails(ImGuiRenderer gui, Func<ContentService?> content)
    {
        _gui = gui;
        _content = content;
    }

    public void BeginFrame() => _loadsLeft = LoadsPerFrame;

    // The texture's ImGui id and its size, or false while it is not loaded (yet, or at all).
    public bool TryGet(VirtualPath path, out IntPtr id, out Vector2 size)
    {
        id = IntPtr.Zero;
        size = Vector2.Zero;
        var asset = AssetPath.Intern(path);
        if (!_bound.TryGetValue(asset, out var bound))
        {
            if (_failed.Contains(asset) || _loadsLeft <= 0 || Content() is not { } content) return false;
            _loadsLeft--;
            if (content.LoadTexture(asset) is not { } texture) { _failed.Add(asset); return false; }
            _bound[asset] = bound = (_gui.BindTexture(texture), texture);
        }
        id = bound.Id;
        size = new Vector2(bound.Texture.Width, bound.Texture.Height);
        return true;
    }

    // An image `edge` pixels on its longer side, or a box saying what it is when there is none.
    public void Draw(VirtualPath path, float edge, string fallback)
    {
        if (AssetKinds.Of(path) == AssetKinds.Texture && TryGet(path, out var id, out var size) && size.X > 0 && size.Y > 0)
        {
            float scale = edge / MathF.Max(size.X, size.Y);
            ImGui.Image(id, new Vector2(size.X * scale, size.Y * scale));
        }
        else
        {
            // Drawn, not a widget: it sits over the browser's selectable and must not take its clicks.
            var at = ImGui.GetCursorScreenPos();
            var draw = ImGui.GetWindowDrawList();
            draw.AddRect(at, at + new Vector2(edge, edge), ImGui.GetColorU32(ImGuiCol.Border));
            draw.AddText(at + new Vector2(4, edge * 0.5f - ImGui.GetTextLineHeight() * 0.5f), ImGui.GetColorU32(ImGuiCol.TextDisabled), fallback);
            ImGui.Dummy(new Vector2(edge, edge));
        }
    }

    private ContentService? Content()
    {
        var content = _content();
        if (content != _watched)
        {
            if (_watched != null) _watched.Reloaded -= Forget;
            if (content != null) content.Reloaded += Forget;
            _watched = content;
        }
        return content;
    }

    private void Forget(AssetPath path)
    {
        _failed.Remove(path);
        if (_bound.Remove(path, out var bound)) _gui.UnbindTexture(bound.Id);
    }

    public void Dispose()
    {
        if (_watched != null) _watched.Reloaded -= Forget;
        foreach (var (id, _) in _bound.Values) _gui.UnbindTexture(id);
        _bound.Clear();
    }
}

// The asset browser (issue #366, docs/design/15 §3 and §11): the VFS's models, textures, sounds, maps,
// fonts and shaders by kind, by mount (a mod by its id) and by search, with thumbnails; the selected one's
// mount and what it shadows, where it is named, and a rename that rewrites those. Drag one onto a field of
// the Inspector or the Records panel (a material's slots too) to set it, or into the viewport to place it.
//
// **This only draws.** What is listed, what a drop sets, what a drop in the viewport places and what a
// rename changes are Sage.Editing's (AssetBrowser, AssetPicking, AssetRename), tested headlessly and pressed
// by the `ed_asset*` commands as well.
internal sealed class AssetsPanel
{
    public const string Title = "Assets";
    private const float Thumb = 48f;

    private readonly AssetBrowser _browser;
    private readonly Thumbnails _thumbnails;
    private readonly Func<EditDocument?> _document;
    private readonly RecordEditor _records;
    private readonly Func<ContentService?> _content;
    private readonly Func<Vector2, Vector2, EditorRay?> _rayThrough;
    private readonly Action<Entity> _onPlaced;
    private string _search = "";
    private string _renameTo = "";
    private IReadOnlyList<AssetReference>? _references;   // the selected asset's, when asked for
    private string _message = "";

    public AssetsPanel(AssetBrowser browser, Thumbnails thumbnails, Func<EditDocument?> document, RecordEditor records,
                       Func<ContentService?> content, Func<Vector2, Vector2, EditorRay?> rayThrough, Action<Entity> onPlaced)
    {
        _browser = browser;
        _thumbnails = thumbnails;
        _document = document;
        _records = records;
        _content = content;
        _rayThrough = rayThrough;
        _onPlaced = onPlaced;
    }

    public void Draw()
    {
        if (!ImGui.Begin(Title)) { ImGui.End(); return; }
        DrawFilters();
        ImGui.Separator();

        float detail = MathF.Min(260f, ImGui.GetContentRegionAvail().X * 0.4f);
        if (ImGui.BeginChild("##assetlist", new Vector2(-detail, 0), ImGuiChildFlags.Border)) DrawList();
        ImGui.EndChild();
        ImGui.SameLine();
        if (ImGui.BeginChild("##assetdetail", Vector2.Zero, ImGuiChildFlags.Border)) DrawSelected();
        ImGui.EndChild();
        ImGui.End();
    }

    private void DrawFilters()
    {
        ImGui.SetNextItemWidth(110);
        if (ImGui.BeginCombo("##kind", _browser.Kind.Length == 0 ? "every kind" : _browser.Kind))
        {
            if (ImGui.Selectable("every kind", _browser.Kind.Length == 0)) _browser.Kind = "";
            foreach (var (kind, count) in _browser.Kinds())
                if (ImGui.Selectable($"{kind} ({count})###{kind}", _browser.Kind == kind)) _browser.Kind = kind;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(130);
        if (ImGui.BeginCombo("##mount", _browser.Mount.Length == 0 ? "every mount" : _browser.Mount))
        {
            if (ImGui.Selectable("every mount", _browser.Mount.Length == 0)) _browser.Mount = "";
            foreach (string mount in _browser.Mounts())
                if (ImGui.Selectable(mount, string.Equals(_browser.Mount, mount, StringComparison.OrdinalIgnoreCase))) _browser.Mount = mount;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("refresh")) _browser.Refresh();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##assetsearch", "search", ref _search, 128)) _browser.Search = _search;
    }

    private void DrawList()
    {
        _thumbnails.BeginFrame();
        var entries = _browser.Filtered();
        float width = ImGui.GetContentRegionAvail().X;
        int columns = Math.Max(1, (int)(width / (Thumb + 72f)));
        if (!ImGui.BeginTable("##assets", columns)) return;
        foreach (var entry in entries)
        {
            ImGui.TableNextColumn();
            ImGui.PushID(entry.Path.Value);
            bool selected = _browser.Selected?.Path == entry.Path;
            var start = ImGui.GetCursorPos();
            if (ImGui.Selectable("##pick", selected, ImGuiSelectableFlags.None, new Vector2(0, Thumb + ImGui.GetTextLineHeightWithSpacing())))
                Select(entry);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(entry.ToString());
            AssetDrag.Source(entry, () => { _thumbnails.Draw(entry.Path, Thumb, entry.Kind); ImGui.TextUnformatted(entry.Path.Value); });
            ImGui.SetCursorPos(start);
            _thumbnails.Draw(entry.Path, Thumb, entry.Kind);
            ImGui.TextUnformatted(entry.FromMod ? $"{entry.Name} ({entry.Origin})" : entry.Name);
            ImGui.PopID();
        }
        ImGui.EndTable();
        if (entries.Count == 0) ImGui.TextDisabled("no asset matches");
    }

    private void Select(AssetEntry entry)
    {
        _browser.Select(entry.Path);
        _references = null;
        _renameTo = entry.Path.Value;
        _message = "";
    }

    private void DrawSelected()
    {
        if (_browser.Selected is not { } entry) { ImGui.TextDisabled("Pick an asset: drag it onto a field, or into the viewport to place it."); return; }

        ImGui.TextWrapped(entry.Path.Value);
        ImGui.TextDisabled($"{entry.Kind}, from {entry.Origin}" + (entry.Size >= 0 ? $", {entry.Size / 1024.0:0.#} KB" : ""));
        foreach (var shadowed in entry.Shadows) ImGui.TextDisabled($"  replaces {shadowed.Name}'s");

        if (entry.Kind == AssetKinds.Texture && _thumbnails.TryGet(entry.Path, out var id, out var size) && size.X > 0)
        {
            float edge = MathF.Min(ImGui.GetContentRegionAvail().X, 192f);
            float scale = edge / MathF.Max(size.X, size.Y);
            ImGui.Image(id, new Vector2(size.X * scale, size.Y * scale));
            ImGui.TextDisabled($"{size.X} x {size.Y}");
        }
        else if (entry.Kind == AssetKinds.Sound && ImGui.Button("Play")) Play(entry.Path);

        if (ImGui.Button("Place in the document"))
        {
            var placed = _document() is { } document ? AssetPicking.Place(document, entry.Path, System.Numerics.Vector3.Zero, 0f, out _message) : null;
            if (placed != null && _document()!.EntityOf(placed) is { IsNull: false } entity) { _onPlaced(entity); _message = $"placed {placed.Name}"; }
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("At the origin; or drag it into the viewport");

        ImGui.Separator();
        if (ImGui.Button("Find references")) _references = AssetReferences.Find(_browser.Engine.Vfs, entry.Path);
        if (_references != null)
        {
            if (_references.Count == 0) ImGui.TextDisabled("nothing names it");
            foreach (var reference in _references) ImGui.TextUnformatted(reference.ToString());
        }

        ImGui.Separator();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##renameto", ref _renameTo, 256);
        if (ImGui.Button("Rename") && AssetBrowserParse(_renameTo, out var to))
        {
            var plan = AssetRename.Plan(_browser.Engine, entry.Path, to, _document(), _records);
            if (plan.Apply())
            {
                _browser.Select(to);
                _references = null;
                _message = $"renamed; {plan.Rewrites.Count} file(s) rewritten";
            }
            else _message = string.Join("\n", plan.Problems);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Moves the file in the game's folder and rewrites every file of the game's that names it");
        if (_message.Length > 0) ImGui.TextWrapped(_message);
    }

    private static bool AssetBrowserParse(string text, out VirtualPath path)
    {
        try { path = VirtualPath.Parse(text); return true; }
        catch (ArgumentException) { path = default; return false; }
    }

    private void Play(VirtualPath path)
    {
        try { _content()?.LoadSound(AssetPath.Intern(path))?.Play(); }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Xna.Framework.Audio.NoAudioHardwareException)
        {
            _message = $"cannot play: {ex.Message}";
        }
    }

    // After the panels are drawn: a dragged asset let go over no window lands in the viewport, where the
    // pointer's ray meets the world.
    public void HandleViewport()
    {
        if (AssetDrag.Dragging is { } path && ImGui.IsMouseReleased(ImGuiMouseButton.Left)
            && !ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow) && _document() is { IsOpen: true } document)
        {
            var io = ImGui.GetIO();
            if (_rayThrough(io.MousePos, io.DisplaySize) is { } ray)
            {
                if (AssetPicking.PlaceAt(document, path, ray, out string error) is { } placed && document.EntityOf(placed) is { IsNull: false } entity)
                    _onPlaced(entity);
                else if (error.Length > 0) Log.Warn(LogCat.Editor, error);
            }
        }
        AssetDrag.EndFrame();
    }
}
