# CLAUDE.md — Sage

A C#/MonoGame game engine, being redesigned into a base engine for game designers, developers and
modders: action RPGs first (Daggerfall, Half-Life, Morrowind, S.T.A.L.K.E.R.), 3D and 2D.

**Start here:** [`docs/history/handoff-2026-10-06-4p.md`](docs/history/handoff-2026-10-06-4p.md) (where things
stand and what is next), then [`docs/REDESIGN.md`](docs/REDESIGN.md) (the plan; §5 is the roadmap). Work is
tracked as GitHub issues: phases #2, #9, #15, #23, then Stage B #75 (4a), #87 (4b), #88 (4c), #115 (4d), #132 (4e, done; next 4f), #152 (4h, done), #153 (4i, done), #182 (4g, done), #200 (4j, done), #215 (10a, the editor, done; guide docs/EDITOR.md), each with sub-issues.
Since the 2026-10-02 replan (REDESIGN §5 "Stage B, part 2") every phase is a GitHub milestone with a parent issue,
and issues carry `priority:`, `type:`, `area:` and `size:` labels: 4k #130 (in progress), 4l #258, 4m #274, R1 #292,
4n #304, 4o #324, 4q #337 (done), 4p #356 (done), 10b #365, 4f #376, 4r #385, 9 #395. What "complete" means
for each system is in [`docs/spec/`](docs/spec/README.md) (SRS, TDD, API contract, a sheet per subsystem); when a PR
finishes an issue, flip its requirement row in the sheet to Done.

## Commands
```bash
dotnet tool restore
dotnet build Sage.sln -c Debug                          # .NET 10 SDK (global.json); projects target net8.0
dotnet build Sage.sln -c Debug -p:SageSkipShaders=true  # Linux: mgfxc needs Wine, so compile without shaders
dotnet test tests/Sage.Tests -c Debug                   # also -c Development; not Shipping
dotnet build Sage.sln -c Development -p:SageSkipShaders=true   # then the registry dump check_docs reads:
(cd src/Sage.Host/bin/Development/net8.0 && xvfb-run -a ./Sage.Host -game ../../../../../games/Sandbox \
    -dump-registry ../../../../../user/registry.json)   # what check_docs reads; Windows: no xvfb-run
python3 tools/check_docs.py --tests <count>             # docs against the registry dump; --fix rewrites counts
src/Sage.Cli/bin/Debug/net8.0/sage validate games/Sandbox # content checks, headless; exits 1 on errors (--mounts dir[=ns] ...)
src/Sage.Cli/bin/Debug/net8.0/sage schema games/Sandbox games/Hello tests/games/scene-only tests/games/camera-cut \
    tests/games/scripted-sequence tests/games/topics tests/games/skeletal tests/games/weapons tests/games/saves \
    tests/games/open-world tests/games/mods tests/games/editor tests/games/render-check tests/games/kit-screens \
    --out schemas                                       # JSON Schemas for VS Code; rerun
                                                        # after adding a field/component/part/record (CI diffs schemas/)
tools/smoke_run.sh src/Sage.Host/bin/Development/net8.0 games/Sandbox 3 Shaders Audio   # real game, Xvfb
tools/kit_screens_check.sh src/Sage.Host/bin/Development/net8.0 [--update]   # every kit screen drawn, vs its golden
SAGE_UPDATE_GOLDENS=1 dotnet test tests/Sage.Tests -c Debug --filter KitScreenGoldenTests   # rewrite plan goldens
dotnet run --project games/Hello -c Development         # a Sage.Sdk game runs itself (host -game <folder>);
                                                        # the host never guesses a game without -game
tools/pack_sdk.sh /tmp/feed                             # Sage.Sdk + Sage.Player + templates -> local feed (after
                                                        # building all three configs); then, outside the repo:
dotnet new install /tmp/feed/Sage.Templates.*.nupkg && dotnet new sage-game -o /tmp/g --feed /tmp/feed
tools/smoke_run.sh --dotnet-run /tmp/g 3 Shaders Audio  # `dotnet run` a template game under Xvfb
```

## Rules of the house
- **One branch and one PR per issue**; say "Closes #N" in the PR. CI must be green (Linux and Windows);
  the owner has asked for green PRs to be merged.
- **Warnings are errors.** Suppress only with a scoped `#pragma` and a reason.
- **Declare, don't register.** `[Record]`, `[SavedResource]` and `[PrefabPart]` are registered by
  generated code (`src/Sage.Generators`) for the plugin that owns them (`Plugin = "id"` when an assembly
  has several). A system is `[System("id", Phase.X, After = ...)]`, ordered by id and added with
  `world.AddSystem(new X(...))`; its event readers take `this` as owner so removal releases them.
  What is still registered by hand goes in a module's `Init`; registries are sealed later and throw, and
  a registration written in `Start`, `OnWorldCreated`, `CreateRules` or a system is a build error
  (SAGE0020; every SAGE id is in `docs/MAKING_A_GAME.md` §10a). A simulation project sets
  `<SageSimulationOnly>true</SageSimulationOnly>`, which makes a MonoGame type a build error.
  Games build with `Sage.Sdk` (`sdk/Sage.Sdk`; `games/Hello` imports it by path, the Sandbox still
  uses `games/Directory.Build.props`); a game adds a kit with `<SageKit Include="sage.kits.rpg" />`; its build diagnostics are SAGE0110–0114.
  Every component and tag has a stable id, `[Component("ns:name")]` / `[Tag(...)]` (a build error
  without one); prefabs and saves use the id, never the C# name. Rename a saved field only with a
  `Version` bump and an `[Upgrade]` method; `tests/Sage.Tests/Content/Saves` holds golden saves that must load.
- **Describe fields** with `[Property(Min, Max, Unit, Tooltip, Category)]`, `[RecordRef("type")]` on a
  RecordId and `[AssetKind("kind")]` on an AssetPath: the generated metadata table (`Metadata`) feeds the
  inspector, `ent_dump`, the FGD's per-entity keys, the registry dump (issue #18) and the JSON Schemas in
  `schemas/` (issue #21; generated by `sage schema`, never edited by hand; a custom JSON converter
  describes its shape with `[SchemaShape]`).
- **Public API is declared** (issue #31, `docs/RELEASING.md`): Core, Simulation, Physics3D, Gameplay,
  UI, Client and the kit keep `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt`, and a public change the
  files lack is RS0016/RS0017, a build error. Add the lines to Unshipped (`dotnet format analyzers
  src/X/X.csproj --diagnostics RS0016 --severity warn`); prefer `internal` for what games and kits don't
  need. `[Experimental("SAGE012x")]` marks unstable areas (MAKING_A_GAME §10b). The version comes from
  git tags (MinVer, `build/Sage.Version.props`; this clone may be shallow, so the count is off, never
  the build); `[RequiresPlugin("sage", range)]` and game.json's `"sage"` are checked against it.
- **Boot through `SageApp`**, not by hand; a game's rules come from `IGameModule.CreateRules`; world
  resources go in with `Resources.Add` (or `Replace`, on purpose).
- **Tests are headless and parallel, and boot through `HeadlessApp`** (`tests/Sage.Testing`), never
  `new Engine(...)`. Tests that change process-wide state or measure time/allocation go in the
  `ProcessWideState` / `Measurements` collections (`tests/Sage.Tests/Core/TestSupport.cs`).
  The base engine (`Sage.Core` ← `Sage.Simulation` ← `Sage.Physics3D` ← `Sage.Gameplay`, plus `Sage.UI` on
  the simulation — retained game-UI widgets, headless, #95; and `Sage.Editing`, the editor's headless
  model, #216; one namespace each) has no MonoGame and never references a kit (SAGE0025); tests cannot reference `Sage.Client` —
  the smoke run checks the client.
- **Docs are checked.** An "As built" claim cites the test that proves it (the `test:` marker, see the README); quoted counts are verified.
  **Docs go in once per pack** (owner, 2026-10-02): an issue's PR is code, tests, `PublicAPI` and `schemas/` only; spec sheets,
  design docs, guides, README/ARCHITECTURE counts, TODO and the handoff are updated in one docs PR (a branch named `*docs*`)
  after a pack of issues has merged. CI fixes counts in place on other branches and holds a docs branch to them; run `check_docs.py --fix` there.
- Traps worth knowing are in the handoffs' §5 (2026-09-27: `git clean -fdx` deletes new untracked files;
  this clone may be shallow; the 8.0.1xx SDK miscompiles; `RollForward` hides .NET 8 runtime bugs.
  2026-09-28: merge `main` into a branch and re-run everything before merging it; allocation is
  measured per thread; content mistakes are load errors now. 2026-09-30: build the solution before running
  the host — it doesn't build the game; the log's repeat note comes from any thread; serialise parallel
  builds with `MSBUILDDISABLENODEREUSE=1 flock -o`).
- **Parallel work goes in git worktrees**, one branch per issue, each agent with its own SAGE id range;
  the lead merges `main` into each branch and re-verifies before opening and merging its PR.

## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.
graphify-out/ is git-ignored and nothing installs Graphify automatically: in a fresh clone or cloud session, run
`uv tool install graphifyy` (PyPI `graphifyy`, double y) then `graphify update .` (about 30 s, local, no API calls),
or type `/graphify` and the skill in `.claude/skills/graphify/` does both.

Rules:
- For codebase questions, first run `graphify query "<question>"` when graphify-out/graph.json exists. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).
