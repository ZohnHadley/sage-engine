#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Sage.Editing;

// What the editor has selected (issue #221): what the outliner highlights, the inspector shows, the
// gizmo moves and `ed_delete` takes away. Since issue #367 it may be several of the document's
// placements (Ctrl+click, a box, `ed_select_all`): `Placements`, in the order they were picked, and the
// last of them is `Placement`, the one the inspector shows and the gizmo stands on.
//
// **A selection of something the document placed is a selection of its placement**, not of its entity.
// Every edit re-spawns the placement it touched (EditDocument, §10e), so the entity a click found is
// gone a frame after the first nudge; holding the placement, the selection's entity is always the one the
// document spawned for it last. `Respawned` is listened to only to say so (Changed) and to notice the
// placement leaving the document, which takes it out of the selection, as closing or opening a document
// clears it. Anything else (a scene's own entity, the player in a dev run) is held as the entity, alone,
// and is dropped when it dies.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class EditorSelection
{
    private Entity _entity;
    private readonly List<Placement> _placements = new();

    public EditorSelection(EditDocument document)
    {
        Document = document;
        document.Respawned += OnRespawned;
        document.Changed += OnDocumentChanged;
    }

    public EditDocument Document { get; }
    public World World => Document.World;

    // The selected placement (the last picked, when there are several), when what is selected is the document's.
    public Placement? Placement => _placements.Count > 0 ? _placements[^1] : null;

    // Every selected placement, in the order picked: the last is `Placement`.
    public IReadOnlyList<Placement> Placements => _placements;

    // The selected entity: the placement's current one, else the entity selected (null once it died).
    public Entity Entity => Placement is { } placement ? Document.EntityOf(placement)
        : World.IsAlive(_entity) ? _entity : default;

    public bool IsEmpty => _placements.Count == 0 && Entity.IsNull;

    // The selection changed, or its entity was replaced by a re-spawn.
    public event Action? Changed;

    // Exactly this entity: the outliner's row. One the document placed selects its placement.
    public void Select(Entity entity)
    {
        var placement = Document.IsOpen ? Document.PlacementOf(entity) : null;
        if (placement != null) Set(new[] { placement }, default);
        else Set(Array.Empty<Placement>(), entity);
    }

    // What a click in the viewport means: the placement the entity belongs to (a prefab's child, or the
    // collider of one, selects the placed thing), else the entity itself.
    public void SelectPlaced(Entity entity) => SelectPlaced(entity, toggle: false);

    // The same; with `toggle` (Ctrl+click) the placement is added to the selection, or taken out of it if
    // it was in it. An entity the document did not place is never one of several: it is selected alone.
    public void SelectPlaced(Entity entity, bool toggle)
    {
        if (PlacedOf(entity) is { } placement)
        {
            if (toggle) Toggle(placement);
            else Set(new[] { placement }, default);
            return;
        }
        if (!toggle) Set(Array.Empty<Placement>(), entity);
    }

    // The placement an entity (or the prefab child or collider of one) belongs to, if it is the document's.
    public Placement? PlacedOf(Entity entity)
    {
        for (var e = entity; !e.IsNull && World.IsAlive(e); e = e.Parent)
            if (Document.IsOpen && Document.PlacementOf(e) is { } placement)
                return placement;
        return null;
    }

    public void Select(Placement placement)
    {
        Check(placement);
        Set(new[] { placement }, default);
    }

    // Exactly these placements (the last is the gizmo's); none clears the selection.
    public void Select(IEnumerable<Placement> placements)
    {
        var list = new List<Placement>();
        foreach (var placement in placements)
        {
            Check(placement);
            if (!list.Contains(placement, ReferenceEqualityComparer.Instance)) list.Add(placement);
        }
        Set(list, default);
    }

    // `placement` added to the selection (as the last, the gizmo's); one already in it moves to the end.
    public void Add(Placement placement)
    {
        Check(placement);
        var list = _placements.Where(p => !ReferenceEquals(p, placement)).Append(placement).ToList();
        Set(list, default);
    }

    // Taken out of the selection if it is in it, else added.
    public void Toggle(Placement placement)
    {
        if (Contains(placement)) Set(_placements.Where(p => !ReferenceEquals(p, placement)).ToList(), default);
        else Add(placement);
    }

    public bool Contains(Placement placement) => _placements.Contains(placement, ReferenceEqualityComparer.Instance);

    public void Clear() => Set(Array.Empty<Placement>(), default);

    // Whether this entity is selected: the selected entity, or the entity of any selected placement.
    public bool Is(Entity entity)
    {
        if (entity.IsNull) return false;
        if (_placements.Count > 0) return Document.PlacementOf(entity) is { } placement && Contains(placement);
        return Entity is { IsNull: false } selected && selected.Id == entity.Id;
    }

    private void Check(Placement placement)
    {
        if (Document.IndexOf(placement) < 0) throw new ArgumentException("not a placement of the open document", nameof(placement));
    }

    private void Set(IReadOnlyList<Placement> placements, Entity entity)
    {
        if (entity == _entity && placements.Count == _placements.Count
            && placements.Select((p, i) => ReferenceEquals(p, _placements[i])).All(same => same)) return;
        var copy = placements.ToList();
        _placements.Clear();
        _placements.AddRange(copy);
        _entity = entity;
        Changed?.Invoke();
    }

    private void OnRespawned(Entity old, Entity now)
    {
        if (_placements.Count == 0) return;
        if (now.IsNull) Prune();
        else if (Document.PlacementOf(now) is { } placement && Contains(placement)) Changed?.Invoke();
    }

    private void OnDocumentChanged() => Prune();

    // What left the document (or the document itself closing) leaves the selection.
    private void Prune()
    {
        if (_placements.Count == 0) return;
        if (!Document.IsOpen) { Clear(); return; }
        if (_placements.Any(p => Document.IndexOf(p) < 0)) Set(_placements.Where(p => Document.IndexOf(p) >= 0).ToList(), default);
    }
}
