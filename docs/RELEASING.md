# Releasing Sage: versions, public API and the `sage` range

How the engine is versioned, what counts as its public API, and what to do when either changes
(issue #31, REDESIGN §3.6). Short on purpose: the build enforces most of it.

## 1. The version comes from git tags

There is one version, and a git tag is where it is written down. [`build/Sage.Version.props`](../build/Sage.Version.props)
runs [MinVer](https://github.com/adamralph/minver) in every engine project and in the three packages
(`Sage.Sdk`, `Sage.Player`, `Sage.Templates`), so they all agree:

| The commit | Its version |
|---|---|
| tagged `v0.1.0` | `0.1.0` |
| 5 commits after `v0.1.0` | `0.1.1-alpha.0.5` (the next patch, pre-release) |
| 37 commits before any tag | `0.1.0-alpha.0.37` (the first release is 0.1.0) |
| a source zip, no git | `0.1.0-alpha.0` (MinVer warns) |

`BuildInfo.EngineVersion` is that version with the commit after a `+` — `0.1.0-alpha.0.37+1a2b3c4` — and
is what the host logs at boot, what a save and the registry dump record, and what the `version` console
command prints. `BuildInfo.EngineSemVersion` is its `major.minor.patch`, the number ranges are checked
against (a pre-release counts as the release it leads to).

The count after `alpha.0` is the number of commits git can see. A shallow clone sees one and no tags, so
CI checks out with `fetch-depth: 0`; a local shallow clone (`git clone --depth`) builds, with a wrong
count. `dotnet msbuild sdk/Sage.Sdk/Sage.Sdk.csproj -restore -t:SageVersion -getProperty:SageVersion`
prints the version a build would use; `tools/pack_sdk.sh` uses exactly that, and the templates are
stamped with it, so `dotnet new sage-game` names the `Sage.Sdk/<version>` it was packed with.

A game's own version (its `[Plugin("mygame", "0.3.0")]`, a mod's `mod.json`) is its own business: MinVer
runs only in the engine.

### SemVer, in the 0.x series

Before 1.0 the *minor* is the breaking number: 0.2.0 may break what 0.1.x games compile against, and a
0.1.x patch may not. From 1.0 it is the major, as usual. "Breaking" means the declared public API (§2)
lost or changed something, or a record, component or save format changed without an upgrader.

## 2. The public API is declared

Every assembly a game, kit or mod compiles against — `Sage.Core`, `Sage.Simulation`, `Sage.Physics3D`,
`Sage.Gameplay`, `Sage.Client`, `Sage.Kits.Rpg`, `Sage.Kits.Rpg.Client` — keeps its public API in two
files beside its `.csproj`:

- **`PublicAPI.Shipped.txt`**: what a released version had.
- **`PublicAPI.Unshipped.txt`**: what changed since: added lines, and `*REMOVED*`-prefixed lines for
  shipped symbols taken away.

[Microsoft.CodeAnalysis.PublicApiAnalyzers](https://github.com/dotnet/roslyn-analyzers/blob/main/src/PublicApiAnalyzers/PublicApiAnalyzers.Help.md)
compares the code with the files on every build (`SagePublicApi` in `src/Directory.Build.props`), and
warnings are errors, so on both CI jobs and in every configuration:

- a new public type or member not in the files is **RS0016**;
- a declared one that is gone or changed is **RS0017** (a change is a removal plus an addition);
- a missing file stops the build too.

**When your change touches public API**, the build tells you which lines. Add them to
`PublicAPI.Unshipped.txt` (the IDE's fix for RS0016 does it, or from the command line:
`dotnet format analyzers src/Sage.X/Sage.X.csproj --diagnostics RS0016 --severity warn`), and
for a removal delete the line from Unshipped, or, for a shipped one, write it into Unshipped prefixed
with `*REMOVED*`. The diff of those files is the API review: a reviewer reads it, not the code, to see
what games gain and lose. (test: ANewPublicMemberWithoutTheFilesIsABuildError)
(test: ARemovedOrChangedPublicMemberWithoutTheFilesIsABuildError)

The host, the `sage` CLI, the editor and the generators have no files: nothing compiles against them.

### What is public, and why

- **Public** is what a game or kit uses: the ECS vocabulary, `World`, records, components, tags, events,
  prefab parts, modules and plugins, the vocabularies' contracts, the extension methods, the client's
  drawing and screen APIs.
- **Internal** is how the engine does it: its own systems, the built-in vocabulary entries (content names
  them by id, not by type), the renderer's snapshot, the audio backends, the `.map` reader and FGD
  export, the console registrars.
- **Public because another engine assembly needs it.** The engine is several assemblies and
  `InternalsVisibleTo` goes only to `Sage.Tests` (REDESIGN §3.1), so what the next layer uses must be
  public: the record JSON helpers (`JsonMembers`, `RecordParseContext`, the vector converters),
  `Upgraders`, `RegistrationLedger.Owner`, `Screen.Index`'s setter, `MovementProfileRecord.Fallback`,
  the client's keyboard and mouse listeners (the editor's camera reads them). They are declared like
  everything else; moving one behind a narrower API is an ordinary API change.

### Experimental API: `[Experimental("SAGE01xx")]`

An area that is new, or known to be about to change, is marked
`[System.Diagnostics.CodeAnalysis.Experimental("SAGE01xx")]`. It is in the files like the rest, but
naming it outside the assembly that declares it is an error until the project opts in, so nobody builds
on it by accident:

```xml
<NoWarn>$(NoWarn);SAGE0120</NoWarn>   <!-- I use the open vocabularies and will follow their changes -->
```

The ids and areas are in [MAKING_A_GAME §10b](MAKING_A_GAME.md#10b-experimental-api). An area leaves
experimental by deleting the attribute (not an API change: the symbols stay), and ids are never reused.
New ids come from SAGE0120–0129.

## 3. Plugins and games say which engines they need

The plugin id **`sage`** is the engine. A plugin, kit or game module says which engine versions it was
built for with the same attribute it names other plugins with, and a game can say it in `game.json`:

```csharp
[Plugin("mygame", "0.1.0")]
[RequiresPlugin("sage", "^0.1")]     // 0.1.x: in the 0.x series, ^ keeps the minor
public sealed class MyGameModule : IGameModule { … }
```
```jsonc
{ "id": "mygame", "sage": "^0.1", … }
```

Ranges are `VersionRange`: `*`, `0.1.0`, `>=0.1 <0.3`, `^0.1`, `~1.2`. When a plugin loads (module
sorting) or `game.json` is read, a range the engine is outside is a load error that names the plugin,
the range and this engine's version (test: APluginForAnotherEngineIsALoadErrorThatSaysBothVersions)
(test: AGameMadeForAnotherEngineIsALoadErrorThatSaysBothVersions). No plugin may use the id `sage`.
The RPG kit, the Sandbox, Hello and the `sage-game` template declare `>=0.1`.

## 4. Tagging a release

1. `main` is green, and `version` on a build of it prints the pre-release of the version you mean to tag
   (`0.1.0-alpha.0.N`).
2. On a branch: **move the public API to Shipped.** For each `src/*/PublicAPI.Unshipped.txt`, append its
   lines (not the `#nullable enable` header) to `PublicAPI.Shipped.txt`, drop the `*REMOVED*` lines
   together with the Shipped lines they name, and leave Unshipped with only `#nullable enable`. Sort
   Shipped if you like; the analyzer does not care. Build: nothing else changes. (For 0.1.0 this step
   also retired the test that held Shipped empty before the first release.)
3. Review `docs/` for the version (MAKING_A_GAME's install line names the templates' file), merge.
4. Tag the merge commit and push the tag: `git tag -a v0.1.0 -m "Sage 0.1.0" && git push origin v0.1.0`.
   Every build of that commit is now `0.1.0`; the next commit is `0.1.1-alpha.0.1`.
5. Pack from the tag (`tools/pack_sdk.sh <feed> --build`); nothing is published yet (REDESIGN §6 item 7).

To start a minor rather than a patch after a release (0.2.0 rather than 0.1.1), nothing needs doing
until the tag: the pre-releases say 0.1.1-alpha, and tagging `v0.2.0` is what makes it 0.2.0. A branch
that must build as 0.2.0-alpha already can set `MinVerMinimumMajorMinor` to 0.2 in
`build/Sage.Version.props`.
