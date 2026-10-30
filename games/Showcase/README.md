# Sage graphics showcase

A walkable 3D tech demo of what the engine draws, made of data only (no C#, no kit): records, five
`.map` brush levels, textures and a few models. Use it to look at the renderer and to improve it.

```bash
dotnet build Sage.sln -c Development            # with shaders (Windows); then, from the host's folder:
cd src/Sage.Host/bin/Development/net8.0
./Sage.Host -game ../../../../../games/Showcase -notitle
```

You start on the hub's cobbled plaza looking north at the materials gallery. Every zone has a signpost
and a sign: walk in and up to three subtitle lines say what it shows (each zone's trigger barks them).
The fog field and the weather court also change the sky and the weather as you walk in and out. Coordinates below are metres from the
scene's origin (absolute 512, 0, 512), x east and z south; `warp x z` (a cheat) takes absolute metres, so
`warp 564 474` is the lights hall at (52, -38).

At start the hub turns on `sv_cheats 1`, the post chain (`r_post 1`, `r_bloom 1`, `r_hdr 1`,
`r_aa fxaa`) and passes two weeks, so the moon is full (a data game cannot name a calendar yet). The
`r_*` settings are archived in `user/showcase/config.cfg`; the game sets them each time it starts.

## Zones

| Zone | Where | What it shows |
|---|---|---|
| Hub | (10, -38) | `plaza.map` on flattened ground; four torch lamps; the post chain: the engine's grade and vignette, bloom, HDR tonemap, FXAA, and two of the game's own `post_effect`s, a depth haze (`sage:post_depth_haze`, reads the depth hook) and a warm grade |
| Materials gallery | x -5..28, z -53 | seven plinths: normal + specular maps (brick), a specular map (brushed metal), an emissive map (rune), an environment map (chrome ball reflecting the day panorama), vertex colours (monkey), emissive alone (glowing torus, blooms), a normal-mapped moss ball; a brick brush wall behind |
| Lights hall | (52, -38) | `hall.map`, lightmapped (the sky term darkens its inside); a ceiling spot with a shadow map, a cold point lamp with a cube shadow map that moves 5 m and back (a `tween`, started by the hub's `logic_auto`), flickering torches (`pattern: torch`) on the east wall and by the door, a pulsing pink spot; cascaded sun shadows outside |
| Fog field | x -22..-78, z -41 | standing stones down the slope; the east half sets a linear-fog sky (`showcase:linear`), the west half the Exp² day sky, both under the `fog` weather; leaving resets the sky and clears the weather |
| Yard | (22, -16) | `yard.map`; a skinned mannequin walking a lane (walk/run blend space), one drilling (upper-body aim layer, strike, reload); 48 crystals of one mesh and material (`r_stats` shows them instanced; `r_instancing 0` to compare) |
| Campfire | (-12, -14) | soft particles (flames and smoke from 2x2 sheets), embers, a torch-flicker lamp with a cube shadow map, scorch decals laid at start by cues |
| Weather court | (40, 5) | `court.map`, lightmapped; walking in rolls rain in (`weather rain 4`), the paving wets and puddles gather, the shelter in the north-east corner keeps the rain off; leaving clears it |
| Pool | (37, 8), in the court | a raised pool: a water surface with no shore fade over a tiled floor |
| Pond | (17.5, 62) | the basin south of the court holds water 2 m deep: ripples, reflection, refraction, shore fade; walk in and dive for the underwater view |
| Terrain terrace | (-30, 10) | `deck.map` flattens the hillside under it; the ridge to the south-west shows the splat layers: grass, sand below 9.5 m by height, rock over 14° by slope, a detail texture over all |
| Knoll | (60, -80) | the high ground to watch the sky: procedural gradient, clouds, moon, turning stars; painted panoramas with `sky_set` |

## Console commands (open with `~`)

| Command | What it does |
|---|---|
| `time_set 21` / `time_set 6.5` / `time_scale 600` | night / dawn / a day in about two and a half minutes |
| `sky_set showcase:painted` | painted day and night panoramas (`skybox`, `skyboxNight`, `skyboxTint`) |
| `sky_set showcase:cubed` | a painted sky of six faces (`skyboxFaces`) |
| `sky_set showcase:alien`, `sky_set showcase:day` | a red panorama; back to the procedural sky |
| `sky_set showcase:linear` | linear fog instead of Exp² |
| `weather rain 4`, `weather storm 4`, `weather fog 4`, `weather clear 6` | the weathers, blended over that many seconds |
| `r_post 0/1`, `r_bloom`, `r_hdr`, `r_aa off/fxaa/msaa4`, `r_post_vignette 0`, `r_post_grade 0` | the post chain |
| `r_instancing 0`, `r_stats` | instancing off, and what the frame cost |
| `r_shadows`, `r_shadow_cascades 1..3`, `r_shadow_lamps 0..4`, `r_shadow_distance` | sun and lamp shadows |
| `r_water 0/1`, `r_weather 0/1`, `r_lights 0/1` | water, weather, lamps |
| `fx_decal showcase:scorch` | a scorch mark where you look |
| `warp x z` | go to a zone (absolute metres: add 512 to the coordinates above) |

## What it shows badly (engine gaps, 2026-10-30)

- Brush faces (`.map` levels, `sage:brush`) have no tangents: the gallery's brick wall and the maps' normal
  maps change nothing (`lit.fx`, `MapMesh`/`BlockoutMesh`).
- A data game has no message log on screen (`message`/`Say` are drawn only by a game's HUD) and the
  subtitle box does not wrap, so the signs are barks of short lines.
- No data action sets the weather, the sky or the hour; the signs run console commands (`command`), which
  need `sv_cheats`, so the start-up turns cheats on. A scene cannot name a calendar either (hence the
  two weeks passed at start for a full moon).
- At night the engine grade's night tint greys fire and emissive light (halved here, `NightStrength`), and
  `sage:post_depth_haze`'s colour is fixed, so distant ground glows at night.
- Terrain layers are projected from above (the ridge's rock stretches on steep slopes); puddles on terrain
  are large pale blotches in a 16 m tiling pattern, on slopes too, while the court's lightmapped paving
  darkens but shows none.
- Rain drops are round dots, not streaks; clouds stay dark blots in a fogged sky.

## Regenerating its content

| What | How |
|---|---|
| tiling textures | `python tools/texgen.py games/Showcase/tools/textures.json --out games/Showcase/content/textures` |
| models (`.glb`) | `blender --background --factory-startup --python games/Showcase/tools/make_models.py` |
| particle and decal sprites | `blender --background --factory-startup --python games/Showcase/tools/make_sprites.py -- <Kenney Particle Pack PNG folder>` |
| brush levels | `python games/Showcase/tools/make_maps.py` |
| the scene and the signs' lines | `python games/Showcase/tools/make_scene.py` (writes `scene.json` and `signs.json`) |

Where the pictures from OpenGameArt came from, and their licences (all CC0), is in
`THIRD_PARTY_NOTICES.md`. `tests/Sage.Tests/Games/ShowcaseTests.cs` boots the game headlessly and checks
every zone is there.
