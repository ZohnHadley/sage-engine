#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Editing;

// What the editor has selected (issue #221): what the outliner highlights, the inspector shows, the
// gizmo moves and `ed_delete` takes away. One thing at a time; multi-select is later (F30).
//
// **A selection of something the document placed is a selection of its placement**, not of its entity.
// Every edit re-spawns the placement it touched (EditDocument, §10e), so the entity a click found is
// gone a frame after the first nudge; holding the placement, the selection's entity is always the one the
// document spawned for it last. `Respawned` is listened to only to say so (Changed) and to notice the
// placement leaving the document, which clears the selection, as does closing or opening a document.
// Anything else (a scene's own entity, the player in a dev run) is held as the entity, and is dropped
// when it dies.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class EditorSelection
{
    private Entity _entity;
    private Placement? _placement;

    public EditorSelection(EditDocument document)
    {
        Document = document;
        document.Respawned += OnRespawned;
        document.Changed += OnDocumentChanged;
    }

    public EditDocument Document { get; }
    public World World => Document.World;

    // The selected placement, when what is selected is one of the document's.
    public Placement? Placement => _placement;

    // The selected entity: the placement's current one, else the entity selected (null once it died).
    public Entity Entity => _placement != null ? Document.EntityOf(_placement)
        : World.IsAlive(_entity) ? _entity : default;

    public bool IsEmpty => _placement == null && Entity.IsNull;

    // The selection changed, or its entity was replaced by a re-spawn.
    public event Action? Changed;

    // Exactly this entity: the outliner's row. One the document placed selects its placement.
    public void Select(Entity entity)
    {
        var placement = Document.IsOpen ? Document.PlacementOf(entity) : null;
        Set(placement, placement == null ? entity : default);
    }

    // What a click in the viewport means: the placement the entity belongs to (a prefab's child, or the
    // collider of one, selects the placed thing), else the entity itself.
    public void SelectPlaced(Entity entity)
    {
        for (var e = entity; !e.IsNull && World.IsAlive(e); e = e.Parent)
            if (Document.IsOpen && Document.PlacementOf(e) is { } placement)
            {
                Set(placement, default);
                return;
            }
        Set(null, entity);
    }

    public void Select(Placement placement)
    {
        if (Document.IndexOf(placement) < 0) throw new ArgumentException("not a placement of the open document", nameof(placement));
        Set(placement, default);
    }

    public void Clear() => Set(null, default);

    public bool Is(Entity entity) => !entity.IsNull && Entity is { IsNull: false } selected && selected.Id == entity.Id;

    private void Set(Placement? placement, Entity entity)
    {
        if (ReferenceEquals(placement, _placement) && entity == _entity) return;
        _placement = placement;
        _entity = entity;
        Changed?.Invoke();
    }

    private void OnRespawned(Entity old, Entity now)
    {
        if (_placement == null) return;
        if (now.IsNull && Document.IndexOf(_placement) < 0) Clear();
        else if (!now.IsNull && ReferenceEquals(Document.PlacementOf(now), _placement)) Changed?.Invoke();
    }

    private void OnDocumentChanged()
    {
        if (_placement != null && (!Document.IsOpen || Document.IndexOf(_placement) < 0)) Clear();
    }
}
