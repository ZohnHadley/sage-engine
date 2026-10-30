# Third-party notices

Sage is MIT-licensed (`LICENSE`). It depends on the software below, each under its own licence. The
licences were read from each package's metadata (NuGet `nuspec`), not assumed; update this file when
`Directory.Packages.props` changes.

## Shipped with a game (runtime)

| Package | Version | Licence | Used by |
|---|---|---|---|
| [BepuPhysics](https://github.com/bepu/bepuphysics2) (+ BepuUtilities) | 2.4.0 | Apache-2.0 | `Sage.Physics3D` — physics |
| [Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS) | 3.6.0 | MIT | `Sage.Simulation` — ECS storage |
| ↳ Friflo.Json.Burst, Friflo.Json.Fliox, Friflo.Json.Fliox.Annotation (dependencies of the above) | 1.0.4 | **LGPL-3.0-only** | pulled in by Friflo.Engine.ECS |
| [MonoGame.Framework.DesktopGL](https://github.com/MonoGame/MonoGame) | 3.8.5.1 | MS-PL | `Sage.Client`, `Sage.Host` — window, graphics, input, audio |
| ↳ MonoGame.Library.SDL | 2.32.10.2 | zlib (SDL) | native windowing and input |
| ↳ MonoGame.Library.OpenAL | 1.24.3.4 | see the package's `LICENSE` (OpenAL Soft; parts BSD-3-Clause) | native audio |
| ↳ NVorbis | 0.10.4 | MIT | Ogg Vorbis decoding; also referenced by `Sage.Simulation`, which decodes a sound's `.ogg` to PCM (issue #302) |
| [StbImageSharp](https://github.com/StbSharp/StbImageSharp) | 2.30.16 | Unlicense OR MIT | `Sage.Simulation` — PNG/JPEG decoding for `sage cook` (issue #302) |
| [StbTrueTypeSharp](https://github.com/StbSharp/StbTrueTypeSharp) | 1.26.12 | Public Domain (the project's README; the nuspec names no licence) | `Sage.UI` — TrueType/OpenType fonts, measured and rasterised headlessly (issue #338) |
| [SharpGLTF.Core](https://github.com/vpenades/SharpGLTF) | 1.0.7 | MIT | `Sage.Simulation` — glTF skins and clips, read headlessly (issue #116); `Sage.Client` — glTF meshes, through it |

| Font | Version | Licence | Used by |
|---|---|---|---|
| [IBM Plex Sans](https://github.com/IBM/plex) Regular and SemiBold (`engine_content/fonts/plex-sans*.ttf`, unmodified) | 1.1.0 | SIL OFL 1.1 (`engine_content/fonts/plex-sans-OFL.txt`; Reserved Font Name "Plex") | the RPG kit's screens and the engine's dialogs (issue #597) |

**LGPL note.** The three Friflo.Json packages are LGPL-3.0-only. They are used as unmodified,
dynamically linked .NET assemblies, which the LGPL permits in a program under another licence, provided
a player can replace those assemblies with their own build (they ship as separate DLLs, which satisfies
that) and the LGPL text and a notice accompany the distribution. Anyone shipping a game on Sage should
include this file. Whether to keep this dependency is part of the ECS-ownership decision (REDESIGN §3.5,
§6 decision 4).

## Development only (not in a Shipping build)

| Package | Version | Licence | Used by |
|---|---|---|---|
| MonoGame.ImGuiNet (vendored, `third_party/MonoGame.ImGuiNet`) | 1.1.0 | MIT (`third_party/MonoGame.ImGuiNet/LICENSE`) | `Sage.Editor`, dev builds of `Sage.Host` |
| [ImGui.NET](https://github.com/ImGuiNET/ImGui.NET) | 1.90.1.1 | MIT (Dear ImGui: MIT) | the above |
| dotnet-mgfxc (build tool) | 3.8.5.1 | MS-PL | compiles shaders at build time |
| xunit, xunit.runner.visualstudio | 2.9.3, 3.1.4 | Apache-2.0 | tests |
| Microsoft.NET.Test.Sdk, coverlet.collector | 17.14.1, 6.0.4 | MIT | tests |

## Game content

The Sandbox's art and sound below come from [OpenGameArt.org](https://opengameart.org). Each licence was
read on the asset's own page. Every one is **CC0 1.0** (public domain dedication,
<https://creativecommons.org/publicdomain/zero/1.0/>), which asks for no attribution and no notice; the
authors are credited here anyway. The files were converted to the size and format the game's records expect
(resized, cropped, mono 22.05 kHz audio, short loops cut from longer recordings, peaks matched); where a
file was changed further, the row says how. Paths are under `games/Sandbox/content/`.

| File(s) | Asset | Author | Source | Licence |
|---|---|---|---|---|
| `textures/brick_wall.png`, `brick_wall_normal.png` (X and Y flipped, so the bricks stand out from the mortar), `brick_wall_spec.png` (derived from the colour map) | 50 free textures-4 + normalmaps, texture 154 | rubberduck | <https://opengameart.org/content/50-free-textures-4-normalmaps> | CC0 |
| `textures/hut_wall.png` (stone wall 3), `hut_floor.png` (wood floor 1), `hut_roof.png` (thatch 1), `hut_door.png` (planks), `terrain_rock.png` (rock 6) | Wall, grass, rock, stone, wood and dirt (480) | West (submitted by qubodup) | <https://opengameart.org/content/wall-grass-rock-stone-wood-and-dirt-480> | CC0 |
| `textures/terrain_detail.png` (Super Perlin 1), `clouds.png` (Super Perlin 12); greyscale, levels matched to the maps they replace | 700+ Noise Textures | Screaming Brain Studios | <https://opengameart.org/content/700-noise-textures> | CC0 |
| `textures/moon.png` | Full Moon | Fupi | <https://opengameart.org/content/full-moon-1> | CC0 |
| `textures/tree.png` | Horse Chestnut Tree (offered under several licences; used under CC0) | Onsemeliot | <https://opengameart.org/content/horse-chestnut-tree> | CC0 |
| `textures/sword.png` | short sword [64x64] (steel) | LordNeo | <https://opengameart.org/content/short-sword-64x64> | CC0 |
| `textures/fireball.png` (two of the four frames, turned flame-up) | Firebal 32x32 | MSavioti | <https://opengameart.org/content/firebal-32x32> | CC0 |
| `textures/ui_frame.png` (panel-border-008, halved) | Fantasy UI Borders | Kenney | <https://opengameart.org/content/fantasy-ui-borders> | CC0 |
| `audio/step_grass_a.wav`, `step_grass_b.wav`, `step_grass_c.wav`, `land_grass.wav` | Fantozzi's Footsteps (Grass/Sand & Stone) | Fantozzi (submitted by qubodup) | <https://opengameart.org/content/fantozzis-footsteps-grasssand-stone> | CC0 |
| `audio/step_wood_a.wav`, `step_wood_b.wav`, `step_wood_c.wav`, `land_wood.wav`, `thunder.wav`, `ui_move.wav` | 100 CC0 SFX #2 | rubberduck | <https://opengameart.org/content/100-cc0-sfx-2> | CC0 |
| `audio/jump.wav`, `pickup.wav`, `swing.wav`, `ui_select.wav` | RPG Sound Pack | artisticdude | <https://opengameart.org/content/rpg-sound-pack> | CC0 |
| `audio/hit_flesh_a.wav`, `hit_flesh_b.wav` | 37 hits/punches | Independent.nu (submitted by qubodup) | <https://opengameart.org/content/37-hitspunches> | CC0 |
| `audio/cast_fire.wav` | Fireball | Julien Matthey (submitted by diligentcircle) | <https://opengameart.org/content/fireball-1> | CC0 |
| `audio/boom_fire.wav` | 25 CC0 bang / firework SFX (cannon_01) | rubberduck | <https://opengameart.org/content/25-cc0-bang-firework-sfx> | CC0 |
| `audio/fire_loop.wav` | Fireplace Sound loop | PagDev | <https://opengameart.org/content/fireplace-sound-loop> | CC0 |
| `audio/rain_loop.wav` | Rain (loopable) | Ylmir | <https://opengameart.org/content/rain-loopable> | CC0 |
| `music/hearth.ogg` (the loop version) | Medieval: The Old Tower Inn | RandomMind | <https://opengameart.org/content/medieval-the-old-tower-inn> | CC0 |
| `music/wander.ogg` | Medieval: Exploration | RandomMind | <https://opengameart.org/content/medieval-exploration> | CC0 |
| `music/wander_drums.ogg` | Cinematic percussion loop | Marwan Antonios | <https://opengameart.org/content/cinematic-percussion-loop> | CC0 |
| `music/crypt.ogg` | Dungeon Ambience | yd | <https://opengameart.org/content/dungeon-ambience> | CC0 |

The graphics showcase's (`games/Showcase/content/`) pictures from OpenGameArt are CC0 1.0 too, each licence read
on the asset's own page; its other textures are made by `tools/texgen.py` (`games/Showcase/tools/textures.json`),
its models by `games/Showcase/tools/make_models.py` (Blender) and its maps by `games/Showcase/tools/make_maps.py`.

| File(s) | Asset | Author | Source | Licence |
|---|---|---|---|---|
| `textures/sky_day.png` (Panorama_Sky_05), `sky_night.png` (Panorama_Sky_12), `sky_red.png` (Panorama_Sky_19); unchanged 2048x1024 equirectangular panoramas | Cloudy Skyboxes (panorama set) | Screaming Brain Studios | <https://opengameart.org/content/cloudy-skyboxes-0> | CC0 |
| `textures/smoke_sheet.png` (smoke_01, 02, 04, 05), `flame_sheet.png` (flame_01 to 04), `spark.png` (spark_01), `scorch.png` (scorch_01), `scorch_b.png` (scorch_02), `rune.png` (magic_03); scaled down, made white with the picture in the alpha (the rune: in the colour), frames packed 2x2 (`games/Showcase/tools/make_sprites.py`) | Particle Pack (80+ sprites) | Kenney | <https://opengameart.org/content/particle-pack-80-sprites> | CC0 |
| `textures/sky_clouds_px.png`, `_nx`, `_py`, `_ny`, `_pz`, `_nz` (east, west, up, down, south, north of clouds1); 1024x1024 BMP scaled to 512x512 PNG | clouds skybox 1 | Luke.RUSTLTD | <https://opengameart.org/content/clouds-skybox-1> | CC0 |
| `textures/grass.png` (grass1), `rock.png` (rock 6), `sand.png` (sand 1); unchanged 480x480 | Wall, grass, rock, stone, wood and dirt (480) | West (submitted by qubodup) | <https://opengameart.org/content/wall-grass-rock-stone-wood-and-dirt-480> | CC0 |
| `textures/moon.png`, `clouds.png`, `terrain_detail.png` | copies of the Sandbox's (rows above: Full Moon; 700+ Noise Textures) | Fupi; Screaming Brain Studios | as above | CC0 |

`games/Showcase/content/models/mannequin.glb` is a copy of the engine's own test model
(`games/Sandbox/tools/make_mannequin.py`).

Everything else under `games/` and `tests/games/` (the models, the sprites `creature.png`, `hands.png` and
`sword_fp.png`, `map_main.png`) is still made by the scripts in `games/Sandbox/tools/`.

## Content that is not in this repository

The Daggerfall importer (`tools/DaggerfallImport`) reads art from a copy of *The Elder Scrolls II:
Daggerfall* that you own and writes it to paths `.gitignore` keeps out of git. That art belongs to
Bethesda and is never redistributed here; this licence does not cover it.
