# Sage engine specification

The structured reference for what the engine must do and how it is built. Start with the SRS, then the
TDD; use the API contract when writing game or kit code, and a subsystem sheet when working on one part.

| Document | Answers |
|---|---|
| [SRS.md](SRS.md) | **Software Requirements Specification.** Goals, users, platforms, performance targets, quality attributes, the functional scope, open decisions. |
| [TDD.md](TDD.md) | **Technical Design Document.** The architecture: assemblies and layers, boot and loop, ECS, communication, content, rendering hand-off, threading, memory, persistence, testing, extension points, risks. |
| [API.md](API.md) | **API and interface contract.** How game code and kits talk to the engine, what is stable, how it is versioned. |
| [subsystems/](subsystems/README.md) | **Subsystem spec sheets**, one per part, each with numbered requirements, their status and the open issues. |

How this relates to the other documents:
- [`../../ARCHITECTURE.md`](../../ARCHITECTURE.md) is the older overview; the TDD supersedes it where they differ.
- [`../design/`](../design/00-index.md) holds the long design docs and their history of what was built; the sheets link into them.
- [`../REDESIGN.md`](../REDESIGN.md) §5 is the roadmap; the work is tracked as GitHub milestones and issues.
- [`../MAKING_A_GAME.md`](../MAKING_A_GAME.md), [`../EDITOR.md`](../EDITOR.md) and [`../MODDING.md`](../MODDING.md) are the guides for game makers, designers and modders.
