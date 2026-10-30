# Handoff — 2026-10-29 (later), the first Polish pack

The session that started the Polish milestone (#596): the UI design pass, weapon switching, the kit's screens on
keys, and three of the four on-screen bugs from 5-5. Read this first. The earlier handoff of the same day,
[`handoff-2026-10-29.md`](handoff-2026-10-29.md), still holds for phase 5, the polish backlog as filed, and its
traps; [`handoff-2026-09-28.md`](handoff-2026-09-28.md) §3 for how to build and verify.

---

## 0. Start here

1. **Polish-1, -3, -4 are done, and -5 is all but the lake** (table in §1). Each was one branch and one PR,
   merged on green; this docs PR is the pack's docs.
2. **Next:** #592 (the lake, the rest of #601: a test game with a pool and a pixel check first, §2), then #602
   (saves at scale), then the P2s (#603–#612) and P3s (#613–#615). #617 (styled runs in a label) was filed by
   the UI pass. Still waiting for the owner: #296 (a public NuGet feed) and #582 (the scripting gate).
3. Build and verify as `CLAUDE.md` says. Now: **2689 headless tests**. <!-- counts -->
4. **The owner's rules this session:** game assets (textures, sounds, models) come from opengameart.org rather
   than being generated, each with its licence in `THIRD_PARTY_NOTICES.md` (prefer CC0); commits carry no
   `Co-Authored-By` trailer, a plain "Written with Claude Code" line instead.

## 1. What landed

| Issue | PR | What | Left out |
|---|---|---|---|
| #597 (Polish-1) | #618 | **The kit's look**, to rules read from Impeccable, Hallmark and UI/UX Pro Max (design 13 "As built (the kit's look)"): IBM Plex Sans (OFL, `engine_content/fonts`), a role palette at 4.5:1, a 4-unit spacing step, menus as lists, hints with `{action:}`; `rpg:high_contrast` (7:1) and a text size slider. Toolkit: styled dialogs and tooltips (`DialogTitleStyle`, `DialogTextStyle`), a dialog's message wraps, tabs take their style's font, the contrast check judges `tabStyle`, a high-contrast set holds only its own namespace, the pixel font's dashes | Highlighted topic links, inline button pictures, subtitles in a style's font: #617. The styled confirm dialog was not seen on screen |
| #599 (Polish-3) | #619 | **Weapon switching from data**: `NextWeapon` / `PreviousWeapon` (wheel, D-pad) and `Weapon1`–`9` (number keys), `sage.combat.weapon_switch` over `Items.Equip`; a button can be bound to the wheel; two wheel buttons turning opposite ways are no conflict | The prompt glyph says "Wheel" either way |
| #600 (Polish-4) | #620 | **The kit's screens on keys**: P perks, G factions, N map, I inventory, Escape pause (the host leaves Menu to a screen bound to it rather than quitting); Controls in the pause menu; **`Sage.Kits.Rpg.Client` deleted** (the owner's choice), the SDK references a kit's client half only with `Client="true"` | — |
| #586, #590 (Polish-5) | #621 | The audio backend's dispose at quit no longer throws; a Development host compiles a folder mod's shaders on load with `asset_hotreload` off. The compile time is measured and written down: ~0.5 s to start, ~0.85 s a technique (lit.fx, 17 techniques, ~15 s) | — |
| #591 (Polish-5) | #623 | **An `onTerrain` level flattens the ground under its brushes** (`Terrain.Flatten`, written into the sculpt, loaded sectors refreshed; `flattenTerrain: false` opts out). The hut stands on a level pad | The pad is ~40 m across (8 m terrain cells); the crypt door near the hut is inside it and was not walked up to |
| #622 | #624 | **The Sandbox's generated art and sound replaced with OpenGameArt assets** (owner's request, a background agent on its own branch): the brick wall's albedo, normal and spec maps, the hut's four textures, terrain rock and detail, clouds, moon, tree, sword, fireball, the UI frame, all 20 sounds and the four music tracks. Every one CC0, same paths and formats, credited in `THIRD_PARTY_NOTICES.md` "Game content" | Kept: `creature.png` (no fitting 8-rotation sprite), `hands.png` and `sword_fp.png` (their 3-frame layout), `map_main.png` (it is this scene), every `.glb` (rigs, clips, sockets and hitboxes records depend on). Nobody listened to the sounds: chosen by name, length and onset; the wander drum layer is not hand-aligned with the wander track |

Issues filed: #617 (styled runs in a label), #622 (the asset replacement). #592 has the lake's findings as a comment.

## 2. Known limits and what is not verified

- **The lake (#592) is not drawn on a real GPU**, and nothing checks water on screen. What is known (the issue's
  comment): the data and the headless maths are right; the water step runs when the lake is in front of the camera
  and its parameters reach the shader; depth reads right. Not settled: whether `WaterExtract`'s frustum test or the
  shader's hit test drops it. A scripted camera is the obstacle: `warp` keeps the spawn's facing, a `warp` into the
  lake floats the player up (buoyancy), and `cam_free` + `cam_set` did not give a usable view. Make a test game with a
  small pool in front of a fixed camera and an `r_pixelcheck` spec, then debug against that.
- **The frame goldens are written on Windows** (a host built with `-p:SageSkipShaders=true`), and CI's Linux check
  passes against them. Write them with the cursor off the window (§5).
- **The hut's pad** may change what stands near it (the crypt door); nothing was checked but the hut.

## 3. For the owner

- A stray folder `C:\c\Users\...` holds 21 golden JSON files a run wrote to the wrong path; a safety check stopped
  the session deleting it. Delete `C:\c`.
- The previous handoff's items still stand: the local `games/Sandbox/content/data/daggerfall.json` (`AIState` → `sage:ai_state`),
  the old branch `fix/transform-lookat-billboard`, releases are your tag push.

## 4. How the UI pass was done (for the next one)

`tools/kit_screens_check.sh` needs `xvfb-run`; on Windows the session ran the host straight: every line of
`tests/games/kit-screens/screens.txt` as `+ui_open` / `+in_tap` commands, `+r_framecheck <golden>` after each
(a host with shaders skips the comparison but saves `user/kitscreens/screenshots/framecheck-*.png`), `update` to
write. The rules came from the skills' `SKILL.md` and reference files (Impeccable: craft-floor, typeset, colorize,
layout; Hallmark: craft, interaction-and-states, dark-mode; UI/UX Pro Max: its priority table), and the palette
was checked with a WCAG ratio script before any record was written.

## 5. Traps

- **Archived cvars persist in `user/<game>/config.cfg`.** A run with `+ui_style_set rpg:high_contrast` left the
  kit-screens game in high contrast for every run after, goldens included. Reset what a check sets.
- **On Windows the desktop cursor hovers the game window**, so a frame golden written with the mouse over it has a
  hovered row. Park the cursor first (`[System.Windows.Forms.Cursor]::Position = 0,0`) and re-check twice.
- **`screens.txt` and other repo text files are CRLF** on this checkout: a bash `read` keeps the `\r`; strip it.
- **`-mods <folder>`** takes the mod's own folder (with `mod.json`), not the folder of mods.
- **`dotnet mgfxc` is a repo-local tool**: run it from the repository, with absolute paths.
- **A host is a Windows program**: a `/c/Users/...` path in a command lands at `C:\c\Users\...`; pass `cygpath -m`.
- **A `&&` chain ending in `grep` succeeds when the test run fails**: read the result before committing.
- **`warp` keeps the player's facing; a warp into water floats them to the surface.**
- **The Sandbox's old art generators still write over the new assets**: `make_brick_wall.py`, `make_hut.py`,
  `make_placeholder_art.py`, `make_ui_art.py`, `make_sky_art.py`, `make_terrain_art.py` and
  `make_placeholder_audio.py` each still make something that was kept, or are named in the docs. Do not run them
  over `games/Sandbox/content`; trimming them to what was kept is a small job for a later pack.
