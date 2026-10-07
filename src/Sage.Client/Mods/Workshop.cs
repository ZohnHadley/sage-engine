#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Sage.Client;

// A mod-sharing service, behind an interface (issue #401; docs/spec/subsystems/19-modding.md REQ-MOD-16):
// Steam Workshop, mod.io or a studio's own, written by a game or a platform package. Sage ships no service
// and no implementation: this is the shape one takes, so the mods screen and `sage` tooling can be wired to
// one later without the engine knowing which.
//
// **It never downloads anything itself, and nothing it lists is loaded because it listed it.** The
// service's own client (Steam, the mod.io app) installs what the player subscribed to, into folders it
// owns; an adapter only says where they are (Installed). Those folders are then mod folders like any
// other: read by ModManifest, ordered and refused by ModLoadOrder, switched by the player's list. A code
// mod among them is still only loaded when the player has trusted it (#396) — a subscription is never
// consent to run code.
[Experimental("SAGE0132", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // mods (phase 9): may change before 1.0
public interface IWorkshop
{
    // "Steam Workshop": what the mods screen calls it.
    string Name { get; }

    // The service can be reached now (its client runs, the player is signed in).
    bool Available { get; }

    // What the player subscribed to that the service has already installed, each a mod folder or a
    // `.sagemod` file. Reads what is on disk; never fetches.
    IReadOnlyList<WorkshopItem> Installed();

    // Uploads a mod the author points at (an author's tool, never the game on its own): the folder or
    // `.sagemod` in `upload.Path`, as a new item or a new version of `upload.ItemId`.
    Task<WorkshopPublishResult> PublishAsync(WorkshopUpload upload, CancellationToken cancel = default);
}

// One subscribed item the service has installed.
[Experimental("SAGE0132", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // mods (phase 9): may change before 1.0
public sealed class WorkshopItem
{
    public WorkshopItem(string itemId, string title, string path, DateTimeOffset updated)
    {
        ItemId = itemId; Title = title; Path = path; Updated = updated;
    }

    // The service's id for it (not the mod's id, which its mod.json says).
    public string ItemId { get; }
    public string Title { get; }

    // The installed mod folder, or `.sagemod` file, on disk.
    public string Path { get; }
    public DateTimeOffset Updated { get; }
}

// What an author publishes: a mod folder or `.sagemod`, and how the service shows it.
[Experimental("SAGE0132", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // mods (phase 9): may change before 1.0
public sealed class WorkshopUpload
{
    public WorkshopUpload(string path) => Path = path;

    public string Path { get; }

    // Empty for a new item; the service's id to publish a new version of one.
    public string ItemId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string ChangeNote { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
}

// What a publish did: the item's id, or why it failed, as the service said it.
[Experimental("SAGE0132", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // mods (phase 9): may change before 1.0
public sealed class WorkshopPublishResult
{
    public WorkshopPublishResult(bool succeeded, string itemId, string message)
    {
        Succeeded = succeeded; ItemId = itemId; Message = message;
    }

    public bool Succeeded { get; }
    public string ItemId { get; }
    public string Message { get; }
}
