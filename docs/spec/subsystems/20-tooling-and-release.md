# 20 · Tooling and release

> Status: partly built. The CLI, SDK, templates, analyzers, CI, smoke runs, docs checker, public API declaration and git-tag versioning all work, and `sage package` writes a shippable game folder (#293). A public package feed and the first release tag are not done (R1). Owning assemblies and folders: `Sage.Cli`, `Sage.Generators`, `sdk/` (`Sage.Sdk`, `Sage.Player`, `Sage.Templates`), `build/`, `tools/`, `.github/workflows/`. Design doc: none of its own; the plan is [REDESIGN.md](../../REDESIGN.md) §3.6 and §5, and the rules are in [RELEASING.md](../../RELEASING.md).

## 1. Purpose and scope

This sheet covers everything that makes the engine buildable, checkable and shippable by people other than
its authors: the `sage` command-line tool, the MSBuild SDK that games build with, the packages and
templates, the Roslyn analyzers and generators that turn declarations into registrations and mistakes into
build errors, CI, the real-executable smoke runs, the documentation checker, the declared public API, the
version scheme and the release procedure.

It does not cover the game loop or registration at runtime ([03](03-app-and-loop.md)), content loading
([06](06-assets-and-content.md)), or modding rules ([19](19-modding.md)), though `sage mods` is a mod
tool that lives here.

## 2. Responsibilities

- Run the whole content load headlessly (`sage validate`), write JSON Schemas (`sage schema`) and report mods (`sage mods`).
- Provide `Sage.Sdk`, an MSBuild SDK that references the engine, runs the analyzers, compiles shaders, checks `game.json` against the build and makes `dotnet run` start the game.
- Provide `Sage.Player` (the host and the `sage` tool in Debug, Development and Shipping) and `Sage.Templates` (`sage-game`, `sage-game-data`, `sage-mod-data`).
- Enforce architecture and declarations at build time with the SAGE analyzers.
- Keep the public API of released assemblies declared, and version from git tags.
- Run CI on Linux and Windows, and smoke-run real games in a virtual display.
- Check the docs against the registry dump and the tests.
- Package a game for players and cut releases (planned).

Not responsible for: game design, the engine's runtime behaviour, or hosting a feed (an owner decision, SRS §10).

## 3. Placement and dependencies

| Part | Location | Notes |
|---|---|---|
| CLI | `src/Sage.Cli` (assembly `sage`) | Boots a game headlessly through `SageApp`; references the simulation layers, never MonoGame |
| Analyzers and generators | `src/Sage.Generators` (netstandard2.0) | Referenced by every engine project and packed into `Sage.Sdk` |
| SDK | `sdk/Sage.Sdk` (`Sdk/Sdk.props`, `Sdk/Sdk.targets`) | `games/Hello` imports the same files by path; the Sandbox still uses `games/Directory.Build.props` |
| Player | `sdk/Sage.Player` | Host and CLI per configuration, plus engine assemblies a game compiles against |
| Templates | `sdk/Sage.Templates/content/` | `sage-game` (simulation and client halves), `sage-game-data` (no C#), `sage-mod-data` |
| Build props | `build/` | `Sage.Version.props` (MinVer), `Sage.Configurations.props`, `Sage.EngineContent.targets`, `Sage.Kits.targets` |
| Scripts | `tools/` | `smoke_run.sh`, `check_docs.py`, `pack_sdk.sh`, `migrate_ecs_api.py`, `DaggerfallImport` |
| CI | `.github/workflows/ci.yml` | Linux and Windows jobs |

The configurations are Debug, Development and Shipping. Tests run in Debug and Development, not Shipping.
Projects target `net8.0` and build with the .NET 10 SDK pinned in `global.json`.

## 4. Interfaces

**CLI.** `sage validate <game> [--mods dir ...] [--game-mods] [--mounts dir[=ns] ...] [--engine-content dir]`,
`sage schema <game> [<game> ...] [--out dir] ...` (also `--client <Sage.Client.dll>`), and `sage mods <game>
[--mods dir ...]`, and `sage package <game> --out <dir> [--host <dir>] [--config Shipping] [--no-validate]` (#293:
the Shipping host with the game beside it in `game/`, then the folder validated). Exit codes: 0 clean (warnings
allowed), 1 content errors, 2 a usage mistake. `sage new` and `run` are planned (#297).

**SDK switches.** `SageSimulationOnly`, `SageDataOnly`, `SageStrictSaves`, `SageGenerators`, `SageSkipShaders`,
`SageGameDirectory`, `SagePackageDirectory` (default `bin/package/<config>`), and the `<SageKit Include="id" />`
item. Tool targets: `SageValidate`, `SagePackage` (`dotnet msbuild -t:SagePackage -p:Configuration=Shipping`, #293),
`SageCheckHost`, `SageCheckGameManifest`, `SageCompileGameShaders`. `-c Release` is Shipping: it writes
`bin/Shipping`, so `{config}` and the packed Player's Shipping host agree (#294).

**Host and tools.** `Sage.Host -game <folder> [-edit] [-dump-registry file] [-mods dirs] [-nomods] [+cmd ...]`.
`tools/smoke_run.sh <host dir> <game dir> [seconds] [allowed category ...]` and its `--dotnet-run` and
`--packaged <package dir>` (the package's host with no `-game`, #293) forms.
`python3 tools/check_docs.py --tests <count> [--fix]`. `tools/pack_sdk.sh <feed> [--build]`.

**SAGE diagnostic ranges** (the full table is MAKING_A_GAME §10a):

| Range | Concern |
|---|---|
| SAGE0001 to SAGE0007 | Record, resource, component and tag declarations, upgraders |
| SAGE0010 to SAGE0013 | Prefab parts and systems |
| SAGE0020 to SAGE0022 | Registration stage, record and resource names, upgraders nothing runs |
| SAGE0023 | Strict saves |
| SAGE0024, SAGE0025, SAGE0050 | Architecture: no MonoGame in simulation, no kit in the base, Friflo only in `Sage.Simulation` |
| SAGE0040 to SAGE0042 | Field metadata (`[Property]`, `[RecordRef]`, `[AssetKind]`) |
| SAGE0100 to SAGE0104 | Vocabulary entries |
| SAGE0110 to SAGE0114 | `Sage.Sdk`: game manifest, host, shaders, kits |
| SAGE0120 and up | `[Experimental]` areas (MAKING_A_GAME §10b) |
| RS0016, RS0017 | Undeclared or changed public API |

## 5. Data model

Formats owned here: the JSON Schemas written to `schemas/` (generated, never hand-edited, diffed by CI), the
registry dump (`user/registry.json`) that the docs checker reads, `PublicAPI.Shipped.txt` and
`PublicAPI.Unshipped.txt` beside each declared assembly (Core, Simulation, Physics3D, Gameplay, UI, Client,
the RPG kit and its client), and `AnalyzerReleases.*.md` for the diagnostics. The version is a SemVer string
from git tags (`BuildInfo.EngineVersion`).

## 6. Lifecycle and data flow

A game build: restore `Sage.Sdk` and `Sage.Player` for the version, run the generators (registrations and
metadata), run the analyzers, compile shaders with `mgfxc` (Wine on Linux, or `SageSkipShaders`), then check
that `game.json` names the dlls the build wrote. `dotnet run` starts the host with `-game` on that folder.

CI order on Linux: build all three configurations, test Debug and Development, probe that an undeclared
public type fails with RS0016, dump the registry under Xvfb, run `check_docs.py`, validate every game with
`sage validate`, check that `schemas/` is current, smoke-run the games, pack the SDK and templates into a
local feed, run a template game outside the repository, then build it in Shipping, package it with
`SagePackage`, check the folder has no developer files and run it from there under Xvfb (#293). Windows builds
all three configurations, tests Debug, packs, builds a template game with a shader and packages it, without
running it.

Release (RELEASING §4): move Unshipped API to Shipped on a branch, merge, tag `v0.1.0`, pack from the tag.

## 7. Threading, memory and performance

Build tools only. Parallel builds in this repository must be serialised
(`MSBUILDDISABLENODEREUSE=1 flock -o`). CI uses `fetch-depth: 0` so MinVer sees tags; a shallow clone builds
with a wrong commit count. The Linux smoke run allows `Shaders` and `Audio` warnings, since the runner has
no Wine-built shaders or sound device.

## 8. Errors and diagnostics

Build errors come from the analyzers with a fix in the message. Content errors carry file and line and make
`sage validate` exit 1. The smoke run fails on a non-zero exit, a crash report, a missing shutdown line, or
any WARN, ERROR or FATAL in a category not allowed. `check_docs.py` fails on a broken relative link, a `test:`
citation naming no test, a name missing from the registry dump, or a stale quoted count. `sage package`
refuses a host with the editor or ImGui in it (Debug, Development), a game not built in the configuration, and
an output folder in the way, writing nothing (test: AHostWithTheEditorOrImGuiInItIsRefused,
AGameNotBuiltInTheConfigurationOrAnOutputFolderInTheWayIsAnErrorThatWritesNothing).

## 9. Requirements

| ID | Requirement | Priority | Status | Evidence or issue |
|---|---|---|---|---|
| REQ-TOOL-01 | `sage validate` shall run every content check headlessly and exit 1 on errors. | Must | Done | test: ARefusedMod_IsReportedWithItsReason_AndTheGameStillValidates |
| REQ-TOOL-02 | `sage schema` shall generate JSON Schemas that CI diffs against `schemas/`. | Must | Done | test: TheModSchema_AcceptsTheTemplatesModJson_AndRefusesAnUnknownKey |
| REQ-TOOL-03 | Architecture rules (no MonoGame in simulation, no kit in the base) shall be build errors. | Must | Done | test: ABaseAssemblyThatUsesAKitIsABuildError |
| REQ-TOOL-04 | The public API of released assemblies shall be declared and a change shall fail the build. | Must | Done | test: EveryAssemblyGamesCompileAgainstDeclaresItsPublicApi |
| REQ-TOOL-05 | The version shall come from git tags as SemVer, and plugins shall declare the engine range they need. | Must | Done | test: TheEngineVersionIsASemVerFromGitNotTheDefaultAssemblyVersion |
| REQ-TOOL-06 | A game shall build and run outside the repository from `Sage.Sdk`, `Sage.Player` and the templates. | Must | Done (local feed) | `tools/pack_sdk.sh`; CI template smoke test |
| REQ-TOOL-07 | CI shall build and test on Linux and Windows and smoke-run real games. | Must | Done | `.github/workflows/ci.yml` |
| REQ-TOOL-08 | Documentation claims shall be checked against the registry dump and the tests. | Must | Partial | `tools/check_docs.py`; blind spot in #298 |
| REQ-TOOL-09 | `sage package` shall produce a folder that runs on a machine without the SDK. | Must | Done (#293): mounts are copied as folders (zip mounts are #397); CI runs the package on Linux | test: APackageIsTheShippingHostWithTheGameBesideItAndNothingElse, AHostWithTheEditorOrImGuiInItIsRefused, AGameNotBuiltInTheConfigurationOrAnOutputFolderInTheWayIsAnErrorThatWritesNothing, ThePackagedHelloAndSceneOnlyGamesLoadAndValidateFromTheOutputFolder |
| REQ-TOOL-10 | The `{config}` path in `game.json` shall resolve for Shipping. | Must | Done (#294): `-c Release` is Shipping and writes `bin/Shipping` | test: AReleaseBuildIsShippingAndWritesTheShippingFolder, OtherConfigurationsKeepTheirOwnFolder, TheEnginesConfigurationNameIsOneOfTheFoldersABuildWrites |
| REQ-TOOL-11 | A first release shall be tagged and cut. | Must | Done | #295 |
| REQ-TOOL-12 | The packages shall be published to a feed and `sage` shipped as a dotnet tool. | Should | Not started | #296 |
| REQ-TOOL-13 | `sage` shall have `new`, `run` and `package` and every template shall exist. | Should | Partial (`package`, #293) | #297 |
| REQ-TOOL-14 | Tooling shall have test and CI coverage. | Should | Partial | #298 |
| REQ-TOOL-15 | The dev console shall have history, Tab completion, a frame cap and autoexec. | Could | Done (#299) | test: History_WalksBackAndForthAndKeepsTheDraft, History_IsBounded, Tab_CompletesCommandsAndCVars, Tab_CompletesCVarValuesAndRecordIds, FrameLimiter_WaitsOutTheRestOfTheFrame, Autoexec_RunsAfterConfigAndWinsOverIt, Autoexec_MissingIsNotAnError |
| REQ-TOOL-16 | Diagnostics shall include a visual logger, render stats, load and job stats and a trace dump. | Could | Not started | #300 |
| REQ-TOOL-17 | Shipping content shall be cooked into engine formats. | Could | Not started | #302 |

## 10. Open work

**Milestone 3, tooling and the first release (epic #292)**

- #295 R1-3 Push the v0.1.0 tag and cut a first real release (P1)
- #296 R1-4 Publish a package feed and ship `sage` as a dotnet tool (P2)
- #297 R1-5 Complete the `sage` CLI: new, run, package, missing templates (P2)
- #298 R1-6 Tooling test and CI coverage, and the `check_docs` blind spot (P2)
- #300 R1-8 Diagnostics gaps (P3)
- #302 R1-10 Cooked asset formats and a cook step for Shipping (P3)
- #303 R1-11 Docs: refresh stale status markers (P3)

**Existing issues kept**

- #49 Per-app log, user folder and crash reporter (P2)

Closed: #16 (generated registration), #2 (phase 0) and #6 (the publish smoke test, now CI's package-and-run step, #293).

## 11. References

- [SRS](../SRS.md) §4 (REQ-SYS-04 to REQ-SYS-06), §10; sheets [03 App and loop](03-app-and-loop.md), [06 Assets](06-assets-and-content.md), [18 Editor](18-editor.md), [19 Modding](19-modding.md).
- [RELEASING.md](../../RELEASING.md): §1 versions, §2 public API, §3 engine ranges, §4 tagging.
- [MAKING_A_GAME.md](../../MAKING_A_GAME.md): §2 the shape of a game, §9 checking without playing, §10a SAGE diagnostics, §10b experimental API.
- [REDESIGN.md](../../REDESIGN.md) §3.6 and §5 (phase R1), §6 (owner decisions on feed and licence).
