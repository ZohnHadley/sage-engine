# CLAUDE.md — Sage

A C#/MonoGame game engine, being redesigned into a base engine for game designers, developers and
modders: action RPGs first (Daggerfall, Half-Life, Morrowind, S.T.A.L.K.E.R.), 3D and 2D.

**Start here:** [`docs/history/handoff-2026-09-27.md`](docs/history/handoff-2026-09-27.md) (where things
stand and what is next), then [`docs/REDESIGN.md`](docs/REDESIGN.md) (the plan; §5 is the roadmap). Work is
tracked as GitHub issues: phases #2, #9, #15, #23, each with sub-issues.

## Commands
```bash
dotnet tool restore
dotnet build Sage.sln -c Debug                          # .NET 10 SDK (global.json); projects target net8.0
dotnet build Sage.sln -c Debug -p:SageSkipShaders=true  # Linux: mgfxc needs Wine, so compile without shaders
dotnet test tests/Sage.Tests -c Debug                   # also -c Development; not Shipping
python3 tools/check_docs.py --tests <count>             # docs against the code; --fix rewrites counts
tools/smoke_run.sh src/Sage.Host/bin/Development/net8.0 games/Sandbox 3 Shaders Audio   # real game, Xvfb
```

## Rules of the house
- **One branch and one PR per issue**; say "Closes #N" in the PR. CI must be green (Linux and Windows);
  the owner has asked for green PRs to be merged.
- **Warnings are errors.** Suppress only with a scoped `#pragma` and a reason.
- **Declare, don't register.** `[Record]` and `[SavedResource]` are registered by generated code
  (`src/Sage.Generators`) for the plugin that owns them (`Plugin = "id"` when an assembly has several).
  What is still registered by hand goes in a module's `Init`; registries are sealed later and throw.
  Every component and tag has a stable id, `[Component("ns:name")]` / `[Tag(...)]` (a build error
  without one); prefabs and saves use the id, never the C# name. Rename a saved field only with a
  `Version` bump and an `[Upgrade]` method; `tests/Sage.Tests/Content/Saves` holds golden saves that must load.
- **Boot through `SageApp`**, not by hand; a game's rules come from `IGameModule.CreateRules`; world
  resources go in with `Resources.Add` (or `Replace`, on purpose).
- **Tests are headless and parallel, and boot through `HeadlessApp`** (`tests/Sage.Testing`), never
  `new Engine(...)`. Tests that change process-wide state or measure time/allocation go in the
  `ProcessWideState` / `Measurements` collections (`tests/Sage.Tests/Core/TestSupport.cs`).
  `Sage.Engine` has no MonoGame; tests cannot reference `Sage.Client` — the smoke run checks the client.
- **Docs are checked.** An "As built" claim cites the test that proves it (the `test:` marker, see the README); quoted counts are verified.
  A new test changes the count: run `check_docs.py --fix`.
- Traps worth knowing are in the handoff's §5 (`git clean -fdx` deletes new untracked files; this clone
  may be shallow; the 8.0.1xx SDK miscompiles; `RollForward` hides .NET 8 runtime bugs).
