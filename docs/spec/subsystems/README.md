# Subsystem spec sheets

One sheet per subsystem, all in the same shape: purpose and scope, responsibilities, placement and
dependencies, interfaces, data model, lifecycle, threading and performance, diagnostics, numbered
requirements with their status, and the open GitHub issues. The [SRS](../SRS.md) holds the engine-wide
requirements and points here for the detail; the long design rationale stays in [`../../design/`](../../design/00-index.md).

| # | Sheet | Code | Main milestones |
|---|---|---|---|
| 01 | [Platform layer](01-platform.md) | PLAT | R1, 4o |
| 02 | [Core services](02-core-services.md) | CORE | R1 |
| 03 | [App and game loop](03-app-and-loop.md) | LOOP | 4m, R1 |
| 04 | [ECS, scenes and prefabs](04-ecs-and-scenes.md) | ECS | 4m |
| 05 | [Events and logic](05-events-and-logic.md) | LOGIC | 4m |
| 06 | [Assets and content](06-assets-and-content.md) | ASSET | 4n, R1, 9 |
| 07 | [Rendering](07-rendering.md) | REND | 4n |
| 08 | [Physics and movement](08-physics.md) | PHYS | 4k, 4l |
| 09 | [Navigation and AI](09-navigation-and-ai.md) | AI | 4l, 4r |
| 10 | [Animation](10-animation.md) | ANIM | 4k, 4p |
| 11 | [Audio](11-audio.md) | AUD | 4o |
| 12 | [Input](12-input.md) | INP | 4o |
| 13 | [Game UI](13-ui.md) | UI | 4q |
| 14 | [World, streaming and time](14-world-and-streaming.md) | WORLD | 4m |
| 15 | [Saves](15-saves.md) | SAVE | 4m |
| 16 | [Gameplay framework](16-gameplay.md) | GAME | 4f, 4r |
| 17 | [RPG kit](17-rpg-kit.md) | RPG | 4f |
| 18 | [Editor](18-editor.md) | EDIT | 10b |
| 19 | [Modding](19-modding.md) | MOD | 9 |
| 20 | [Tooling and release](20-tooling-and-release.md) | TOOL | R1 |
| 21 | [Voxel worlds](21-voxels.md) | VOX | 4s |

**Keeping them true.** When a PR finishes an issue, it flips the matching requirement's status to Done
and cites the file or the test that proves it (test citations are checked by `tools/check_docs.py`). A new gap gets an
issue and a row.
