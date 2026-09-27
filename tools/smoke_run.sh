#!/usr/bin/env bash
# Runs the real game executable for a few seconds and fails if anything went wrong (issue #6).
#
#   tools/smoke_run.sh <host-output-dir> <game-dir> [seconds] [allowed-category ...]
#
# The host is started under a virtual display (xvfb-run) with `+quit <seconds>`, so it boots the game,
# runs the loop and shuts down on its own. The run fails if the host exits non-zero, writes a crash
# report, never logs its shutdown, or logs a WARN/ERROR/FATAL in any category not named as allowed —
# CI allows `Shaders` because it builds the host without them on Linux (SageSkipShaders), and `Audio`
# because a runner has no sound device.
set -euo pipefail

host_dir=$1
game_dir=$(cd "$2" && pwd)
seconds=${3:-3}
shift $(( $# < 3 ? $# : 3 ))
allowed=("$@")

game_id=$(python3 -c 'import json,re,sys; t=open(sys.argv[1]).read(); t=re.sub(r"//[^\n]*","",t); t=re.sub(r",(\s*[}\]])",r"\1",t); print(json.loads(t)["id"])' "$game_dir/game.json")

# The host writes its log under <repo>/user/<game id>/logs in a dev build.
repo=$(cd "$(dirname "$0")/.." && pwd)
log_dir="$repo/user/$game_id/logs"
rm -rf "$log_dir"

status=0
( cd "$host_dir" && timeout $((seconds + 60)) xvfb-run -a -s "-screen 0 1024x768x24" \
    ./Sage.Host -game "$game_dir" "+quit $seconds" > /dev/null 2>&1 ) || status=$?

log=$(ls -t "$log_dir"/sage-*.log 2>/dev/null | head -1 || true)
fail() { echo "::error::$game_id: $1"; [ -n "$log" ] && tail -n 40 "$log"; exit 1; }

[ -n "$log" ] || fail "no log was written (exit $status)"
[ "$status" -eq 0 ] || fail "the host exited with $status"
if ls "$log_dir"/crash-*.txt > /dev/null 2>&1; then
    cat "$log_dir"/crash-*.txt
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

lines=$(wc -l < "$log")
echo "$game_id: ran ${seconds}s, exited cleanly, $lines log lines, nothing outside (${allowed[*]:-none})"
