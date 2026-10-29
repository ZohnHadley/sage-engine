# On-screen check — 2026-10-29 (issue #580, phase 5-5)

What the headless tests and the Linux CI's llvmpipe can't see, run once on Windows 10 with a real GPU. The
Development host with compiled shaders ran the Sandbox, driven from the command line
(`-notitle "+vid_width 1280" "+vid_height 720" … "+screenshot" "+quit"`). The screenshots aren't in the
repository.

| What | Result | Follow-up |
|---|---|---|
| The Sandbox at dusk: sky, clouds, terrain, the normal-mapped brick wall, sprites, the HUD | **Draws as it should**, no errors in the log | — |
| `r_instancing 1` against `r_instancing 0` | **Same frame.** 14 sprites drew instanced. No mesh was instanced, because the Sandbox repeats no mesh in view often enough, so instanced *meshes* are still not seen on a GPU | the polish pass: content that repeats a mesh |
| Lamp shadows at night (`time_set 22` in the hut) | **Work:** the lamp is lit and the mannequin's shadow falls on the wall | the hut's floor draws black and slanted: #591 |
| Water (the Sandbox's lake) | **Not confirmed:** no water surface seen from the shore, though `r_stats` lists the water step | #592 |
| A mod's `.fx` reloaded on screen (a copy of `lit.fx`, tinted, on the brick wall, edited while running) | **Works with `asset_hotreload 1`:** compiled (16.6 s), reloaded, and again after the edit | without it, a Development host announces the load-time compile and never runs it: #590 |
| Quitting | Every run logs `Disposing world resource MonoGameAudioBackend failed` | #586 |

Also seen: the Sandbox boots to its title screen (`rpg:title`). A scripted run needs `-notitle`, as
`tools/smoke_run.sh` passes. `time_set` and `warp` need `sv_cheats 1`.
