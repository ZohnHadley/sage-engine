#nullable enable
using System;
using System.Numerics;
using ImGuiNET;
using Sage.Editing;

namespace Sage.Editor;

// The Records panel's preview of what a record draws with (issue #366): each asset slot of the open record
// (AssetPicking.Slots: a material's albedo, normal, specular, emissive and environment maps, its shader) as
// a thumbnail, and each a drop target for an asset dragged from the Assets panel. A drop is a SetRecordValue
// like any other edit of the form, and with the record browser live (RecordEditor.Live) the world shows the
// new texture before anything is saved.
internal static class RecordAssetStrip
{
    private const float Edge = 56f;

    public static void Draw(RecordDocument record, Thumbnails thumbnails)
    {
        var slots = AssetPicking.Slots(record);
        if (slots.Count == 0) return;
        if (!ImGui.CollapsingHeader(record.Type == "material" ? "Material preview" : "Assets", ImGuiTreeNodeFlags.DefaultOpen)) return;

        thumbnails.BeginFrame();
        float x = 0f, width = ImGui.GetContentRegionAvail().X;
        foreach (var slot in slots)
        {
            if (x > 0f && x + Edge + 8f <= width) ImGui.SameLine();
            else x = 0f;
            x += Edge + 8f;

            ImGui.PushID(slot.Path);
            ImGui.BeginGroup();
            if (slot.Current is { } current) thumbnails.Draw(current, Edge, slot.Kind ?? "asset");
            else ImGui.Button("drop\nhere", new Vector2(Edge, Edge));
            ImGui.TextDisabled(Clip(slot.Label, 9));
            ImGui.EndGroup();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{slot.Path}: {slot.Current?.Value ?? "(none)"}\ndrag a{(slot.Kind is { } k ? " " + k : "n asset")} here from the Assets panel");
            if (AssetDrag.Accept(slot.Kind) is { } dropped && !AssetPicking.ToRecord(record, slot.Path, dropped, out string error))
                Log.Warn(LogCat.Editor, error);
            ImGui.PopID();
        }
        if (record.Live && record.PreviewError.Length > 0) ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f), "not shown in the game: " + record.PreviewError);
    }

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..(length - 2)] + "..";
}
