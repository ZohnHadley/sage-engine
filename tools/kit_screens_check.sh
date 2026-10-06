#!/usr/bin/env bash
# Opens every RPG kit screen in the real client and checks its drawing against a golden (issue #355).
#
#   tools/kit_screens_check.sh <host-output-dir> [--update]
#
# The screens are tests/games/kit-screens/screens.txt, one line each. For each, the host (run by
# tools/smoke_run.sh under Xvfb, so everything that fails a smoke run fails this) opens it over the kit-screens
# test game at 960x540, waits for its fade and focus to settle, and `r_framecheck` reads the frame back and
# compares its grid of cell colours with goldens/frame/<screen>.json (FrameGrid, Sage.Simulation): a screen
# that stops drawing its panel, its text or its highlight is an ERROR, and the run fails. Each screen must also
# log its check, in order (SAGE_SMOKE_EXPECT), so a screen that never opened cannot pass by checking nothing.
# The frames are saved as user/kitscreens/screenshots/framecheck-<screen>.png.
#
# --update writes the goldens from this run's frames instead: after a deliberate change to a kit screen, or
# for a new line in screens.txt. Look at the frames before committing the goldens.
#
# The goldens are taken from a host built without the engine's shaders (SageSkipShaders, CI's Linux job), where
# the world behind a screen is its clear colour; a host with shaders skips them (the world is another picture).
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
host_dir=$1
update=false
[ "${2:-}" = "--update" ] && update=true
game=$repo/tests/games/kit-screens

# The frame the goldens are for; cheats for quest_start (the journal has a quest in it).
commands=$'+vid_width 960\n+vid_height 540\n+sv_cheats 1\n+quest_start crossing\n+wait 1'
expect=
count=0
while read -r screen how argument _; do
    [ -z "$screen" ] || [ "${screen:0:1}" = "#" ] && continue
    name=${screen//:/_}
    golden=$game/goldens/frame/$name.json
    case $how in
        ui)  open="+ui_open $screen${argument:+ $argument}"; close='+ui_close all' ;;
        tap) open="+in_tap $argument"; close='+in_tap MenuBack' ;;
        use) open='+in_tap Use'; close='+in_tap MenuBack' ;;
        *)   echo "::error::screens.txt: '$screen' opens by '$how' (ui, tap or use)"; exit 1 ;;
    esac
    if $update; then check="+r_framecheck $golden update"; logged="Frame check $name: golden written"
    else check="+r_framecheck $golden"; logged="Frame check $name: passed"; fi
    # Open it, let its fade (0.18 s) and focus highlight (0.12 s) finish, check the next frame, close it and
    # let it fade out before the next one opens.
    commands+=$'\n'"$open"$'\n+wait 0.8\n'"$check"$'\n+wait\n'"$close"$'\n+wait 0.5'
    expect+="$logged"$'\n'
    count=$((count + 1))
done < "$game/screens.txt"

echo "kit screens: $count, $($update && echo "writing their goldens" || echo "checked against their goldens")"
SAGE_SMOKE_COMMANDS=$commands SAGE_SMOKE_EXPECT=$expect \
    "$repo/tools/smoke_run.sh" "$host_dir" "$game" 1 Shaders Audio Records Input
