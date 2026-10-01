#nullable enable
namespace Sage.Simulation;

// Play-in-editor (phase 10a, issue #226): a play world plays the editor's document **as it is in memory**,
// not as its file last said. An edit not yet saved, a placement never saved at all, a wire made a moment
// ago: all of it is what a designer means by "play", and none of it is in the record store until a save
// and a reload. So the play world carries the document's record as a resource, and wherever its scene
// would place that document from the store (Scenes.Place, a streamed scene's buckets, a hot reload's
// re-placing) it places this one instead. A document its scene does not name is placed with the scene
// all the same: it is what the editor shows beside it.
//
// The record is the editor's own copy; nothing here changes it, and the play world spawns from it the way
// any world spawns a placements record (SpawnPlacements), so what plays is what a saved file would load.
internal sealed class PlayedDocument
{
    public PlayedDocument(RecordId scene, RecordId id, PlacementsRecord record)
    {
        Scene = scene;
        Id = id;
        Record = record;
    }

    public RecordId Scene { get; }
    public RecordId Id { get; }
    public PlacementsRecord Record { get; }

    // The placements to place for `document` in `world`: the played copy when it is that document, else
    // the store's. False when neither has it.
    public static bool TryGet(Engine engine, World? world, RecordId document, out PlacementsRecord record)
    {
        if (world != null && world.Resources.TryGet<PlayedDocument>(out var played) && played != null && played.Id == document)
        {
            record = played.Record;
            return true;
        }
        return engine.Records.TryGet(document, out record);
    }

    // The played document when `scene` is the one it was played in and does not name it itself.
    public static PlayedDocument? Beside(World? world, RecordId scene, SceneRecord record)
    {
        if (world == null || !world.Resources.TryGet<PlayedDocument>(out var played) || played == null || played.Scene != scene) return null;
        foreach (var document in record.Placements)
            if (document.Id == played.Id) return null;
        return played;
    }
}
