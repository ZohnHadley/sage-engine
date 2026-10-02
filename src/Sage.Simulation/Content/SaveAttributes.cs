#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Simulation;

// What a prefab-spawned entity came from (F31), so a save can rebuild it: loading is
// `Spawn(prefab)` and then the saved components over the top, which restores the *parts* a prefab
// applied (a character's collider, controller and intent) without saving any of them.
//
// Also what the editor's "revert to prefab" (15) will read.
[Component("sage:from_prefab")]
public struct FromPrefab : IComponent
{
    public RecordId Prefab;

    // Its components as its prefab gave them, overrides included, when it was spawned (phase 4i issue
    // 4i-5): what a save diffs it against, so only what the game changed is written. Shared by every entity
    // spawned from the same prefab and overrides; null for one that was not spawned (a bare Create).
    internal SpawnBaseline? Baseline;

    // Where it was placed, and the sector that position is relative to (issue 4m-4): content's entity that
    // is still there is not written with a transform, so moving the placement in the content moves it.
    internal Transform Placed;
    internal SectorCoord PlacedFrame;
    internal bool HasPlaced;
}

// A runtime spawn no save names (issue 4m-4): its prefab says `"persist": false` (a spark, a cue's or an
// effect's prop). A load destroys every entity with it, since the save it is loading did not have it and
// the game it came from is gone. What the engine or the game makes for itself (a camera, a viewmodel, a
// level's brushes) is not tagged and is left alone; a game that makes a throwaway entity with a bare
// `Create` and wants a load to clear it adds this tag. Never written: the entity has no id to write it under.
[Tag("sage:unsaved")]
[Transient]
[Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // saves you can trust (phase 4i)
public struct Unsaved : ITag { }

// For a saved resource whose data implies something else that has to be rebuilt — a spellbook's
// drafts become `ability` records again (F21). Called once the world around it has finished loading.
public interface ISavedResource
{
    void AfterLoad(World world);
}

// An entity a save names whose prefab this game no longer has — a mod was removed, a prefab renamed
// (REDESIGN §4.5, issue 4i-2). Instead of being dropped, it loads as this: a Transform where it was, its
// persistent id, and everything the save said about it, kept as text. It is inert — disabled, so no
// query and no system sees it — and the next save writes `Saved` back unchanged, so putting the mod back
// brings the entity back as it was. What refers to it by id keeps referring to it.
[Transient]   // written from `Saved`, never as a component
[Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // saves you can trust (4i)
[Component("sage:save_placeholder")]
public struct SavePlaceholder : IComponent
{
    [Property(Tooltip = "The prefab the save named, which this game does not have")]
    public RecordId Prefab;

    [Property(Tooltip = "The saved entity as the save wrote it (JSON), written back unchanged by the next save")]
    public string Saved;
}

// What a save said about an entity that this game has no component or tag for — from a plugin that is
// not loaded now, or removed since (issue 4i-2). Kept as text and written back unchanged by the next
// save, so a save survives a session without a mod. Never read by anything else.
[Transient]   // merged back into the entity's components and tags, never written as a component itself
[Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // saves you can trust (4i)
[Component("sage:unknown_saved_data")]
public struct UnknownSavedData : IComponent
{
    [Property(Tooltip = "Components by id, each { \"version\", \"data\" } as saved (a JSON object)")]
    public string Components;

    [Property(Tooltip = "Tag ids as saved (a JSON array)")]
    public string Tags;
}
