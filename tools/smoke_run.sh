#!/usr/bin/env bash
# Runs the real game executable for a few seconds and fails if anything went wrong (issue #6).
#
#   tools/smoke_run.sh <host-output-dir> <game-dir> [seconds] [allowed-category ...]
#   tools/smoke_run.sh --dotnet-run <game-dir> [seconds] [allowed-category ...]
#   tools/smoke_run.sh --packaged <package-dir> [seconds] [allowed-category ...]
#   tools/smoke_run.sh --sage-run <game-dir> [seconds] [allowed-category ...]
#
# The host is started under a virtual display (xvfb-run) with `+quit <seconds>`, so it boots the game,
# runs the loop and shuts down on its own. The run fails if the host exits non-zero, writes a crash
# report, never logs its shutdown, or logs a WARN/ERROR/FATAL in any category not named as allowed —
# CI allows `Shaders` because it builds the host without them on Linux (SageSkipShaders), and `Audio`
# because a runner has no sound device.
#
# --dotnet-run runs a Sage.Sdk game project instead (issue #32): `dotnet run` in <game-dir>, which
# builds it and starts the Player host the SDK names with -game on that folder. SAGE_SMOKE_COMMANDS, one
# console command per line, runs before the quit (e.g. $'+wait 1\n+in_axis Move 0 1 2', a walk).
# SAGE_SMOKE_ARGS, launch options separated by spaces, go before them: `-edit yard` runs the editor (#219).
# With commands the run adds `-notitle`, so the world starts at once rather than at the game's title (#342).
#
# SAGE_SMOKE_EXPECT, one extended regular expression per line, must each match a log line, in that order: a
# check that something happened, not only that nothing went wrong (the mouse capture handoff, #334).
#
# --packaged runs a folder `sage package` wrote (issue #293): its ./Sage.Host with no -game, so the host finds
# the game in the `game` folder beside it, as a player's would.
#
# --sage-run runs `sage run <game-dir>` (issue #297): `dotnet run` on the game's Sage.Sdk project (its Client/
# one first), or the host on the folder. The `sage` is $SAGE_CLI, else this repository's Debug build.
#
# The log is found from what the host prints ("Log file ...", "user folder ..."), so it works wherever the
# user folder is: <repo>/user/<game id>/logs for a dev build in this repository, the platform's app-data
# folder otherwise.
set -euo pipefail

packaged=false
sage_run=false
if [ "$1" = "--dotnet-run" ]; then host_dir=; game_dir=$(cd "$2" && pwd)
elif [ "$1" = "--sage-run" ]; then sage_run=true; host_dir=; game_dir=$(cd "$2" && pwd)
elif [ "$1" = "--packaged" ]; then packaged=true; host_dir=$(cd "$2" && pwd); game_dir=$host_dir/game
else host_dir=$1; game_dir=$(cd "$2" && pwd); fi
seconds=${3:-3}
shift $(( $# < 3 ? $# : 3 ))
allowed=("$@")

commands=()
options=()
[ -n "${SAGE_SMOKE_ARGS:-}" ] && read -r -a options <<< "$SAGE_SMOKE_ARGS"
if [ -n "${SAGE_SMOKE_COMMANDS:-}" ]; then
    while IFS= read -r line; do [ -n "$line" ] && commands+=("$line"); done <<< "$SAGE_SMOKE_COMMANDS"
    # A scripted run plays the game, so it starts past a title screen (game.json's "title", issue #342);
    # a run with no commands boots to the title, as a player's would.
    [[ " ${options[*]} " == *" -notitle "* ]] || options+=(-notitle)
fi

game_id=$(python3 -c 'import json,re,sys; t=open(sys.argv[1]).read(); t=re.sub(r"//[^\n]*","",t); t=re.sub(r",(\s*[}\]])",r"\1",t); print(json.loads(t)["id"])' "$game_dir/game.json")

out=$(mktemp)
started=$(mktemp)   # crash reports newer than this are this run's
trap 'rm -f "$out" "$started"' EXIT

status=0
if $sage_run; then
    sage=${SAGE_CLI:-$(cd "$(dirname "$0")/.." && pwd)/src/Sage.Cli/bin/Debug/net8.0/sage}
    timeout $((seconds + 600)) xvfb-run -a -s "-screen 0 1024x768x24" \
        "$sage" run "$game_dir" -- "${options[@]}" "${commands[@]}" "+quit $seconds" > "$out" 2>&1 || status=$?
elif [ -z "$host_dir" ]; then
    ( cd "$game_dir" && timeout $((seconds + 600)) xvfb-run -a -s "-screen 0 1024x768x24" \
        dotnet run -- "${options[@]}" "${commands[@]}" "+quit $seconds" > "$out" 2>&1 ) || status=$?
else
    game_option=(-game "$game_dir")
    $packaged && game_option=()
    ( cd "$host_dir" && timeout $((seconds + 60)) xvfb-run -a -s "-screen 0 1024x768x24" \
        ./Sage.Host "${game_option[@]}" "${options[@]}" "${commands[@]}" "+quit $seconds" > "$out" 2>&1 ) || status=$?
fi

# The log this run wrote: the one the host printed, else the newest one written since it started in the
# user folders it can use (stdout goes quiet once `developer` is 0, which a Development build's is).
log=$(sed -n 's/.* INFO  Host      Log file \(.*\) (Program\.cs:[0-9]*)$/\1/p' "$out" | head -1)
if [ -z "$log" ]; then
    user=$(sed -n 's/.* user folder \(.*\) (Program\.cs:[0-9]*)$/\1/p' "$out" | head -1)
    repo=$(cd "$(dirname "$0")/.." && pwd)
    for dir in ${user:+"$user/logs"} "$repo/user/$game_id/logs" "${XDG_DATA_HOME:-$HOME/.local/share}/Sage/$game_id/logs"; do
        log=$(find "$dir" -maxdepth 1 -name 'sage-*.log' -newer "$started" 2>/dev/null | sort | tail -1)
        [ -n "$log" ] && break
    done
fi
fail() { echo "::error::$game_id: $1"; if [ -n "$log" ] && [ -f "$log" ]; then tail -n 40 "$log"; else tail -n 40 "$out"; fi; exit 1; }

[ -n "$log" ] && [ -f "$log" ] || fail "no log was written (exit $status)"
[ "$status" -eq 0 ] || fail "the host exited with $status"
crashes=$(find "$(dirname "$log")" -maxdepth 1 -name 'crash-*.txt' -newer "$started")
if [ -n "$crashes" ]; then
    cat $crashes
    fail "a crash report was written"
fi
grep -q "INFO  Host      Shutdown" "$log" || fail "the host never logged its shutdown"

pattern='\] (WARN|ERROR|FATAL) '
problems=$(grep -E "$pattern" "$log" || true)
for category in "${allowed[@]}"; do
    problems=$(printf '%s\n' "$problems" | grep -vE "\] (WARN|ERROR|FATAL) +$category " || true)
done
problems=$(printf '%s\n' "$problems" | sed '/^$/d')
if [ -n "$problems" ]; then
    printf '%s\n' "$problems"
    fail "logged problems outside the allowed categories (${allowed[*]:-none})"
fi

if [ -n "${SAGE_SMOKE_EXPECT:-}" ]; then
    from=1
    while IFS= read -r expected; do
        [ -n "$expected" ] || continue
        found=$(tail -n +"$from" "$log" | grep -nE -m1 "$expected" | cut -d: -f1 || true)
        [ -n "$found" ] || fail "expected a log line matching '$expected' (in order, after line $from)"
        from=$((from + found))
    done <<< "$SAGE_SMOKE_EXPECT"
fi

lines=$(wc -l < "$log")
echo "$game_id: ran ${seconds}s, exited cleanly, $lines log lines, nothing outside (${allowed[*]:-none})"
