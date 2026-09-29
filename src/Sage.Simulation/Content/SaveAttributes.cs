#nullable enable
using System;

namespace Sage.Simulation;

// What a prefab-spawned entity came from (F31), so a save can rebuild it: loading is
// `Spawn(prefab)` and then the saved components over the top, which restores the *parts* a prefab
// applied (a character's collider, controller and intent) without saving any of them.
//
// Also what the editor's "revert to prefab" (15) will read.
[Component("sage:from_prefab")]
public struct FromPrefab : Friflo.Engine.ECS.IComponent
{
    public RecordId Prefab;
}

// For a saved resource whose data implies something else that has to be rebuilt — a spellbook's
// drafts become `ability` records again (F21). Called once the world around it has finished loading.
public interface ISavedResource
{
    void AfterLoad(World world);
}
