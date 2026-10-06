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

## Content that is not in this repository

The Daggerfall importer (`tools/DaggerfallImport`) reads art from a copy of *The Elder Scrolls II:
Daggerfall* that you own and writes it to paths `.gitignore` keeps out of git. That art belongs to
Bethesda and is never redistributed here; this licence does not cover it.
