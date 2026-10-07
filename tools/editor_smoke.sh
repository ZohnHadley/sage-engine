#!/usr/bin/env bash
# The editor in the real host, every panel drawn and an edit saved (issue #371).
#
#   tools/editor_smoke.sh <host-output-dir> [allowed-category ...]
#
# Runs `Sage.Host -edit level` (tools/smoke_run.sh, under Xvfb) on a copy of tests/games/editor, so the save
# never touches the repository, and drives it from the console the way a person drives the menus:
# place a plate, a door and a crate, undo the crate, override the door's speed, wire the plate to it, select
# it, then `ed_panel all`, which brings every editor panel to the front in turn for a few frames (each one's
# Draw runs with something to show), then play, stop and save. The run fails as any smoke run does (a panel
# that throws crashes the host; a WARN/ERROR outside the allowed categories), when the log lacks the steps
# in order, or when the saved file does not hold exactly the plate and the door, the door's override and the
# wire. The allowed categories default to the editor smoke's: Shaders Audio Records Input.
set -euo pipefail

host_dir=$1
shift
allowed=("$@")
[ ${#allowed[@]} -gt 0 ] || allowed=(Shaders Audio Records Input)

repo=$(cd "$(dirname "$0")/.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
cp -r "$repo/tests/games/editor" "$work/editor"
saved=$work/editor/content/data/level_placements.json

export SAGE_SMOKE_ARGS='-edit level'
export SAGE_SMOKE_COMMANDS=$'+wait 1
+doc_status
+ed_place plate 0 0.5 2 0 plate
+ed_place door 0 1.5 -4 180 door
+ed_place crate 3 0.5 0 0 crate
+ed_undo
+ed_set door mover.seconds 0.5
+ed_wire plate OnStartTouch door Open
+ed_select door
+ed_panel all
+wait 6
+ed_play
+wait 1
+ed_stop
+wait 1
+doc_save
+ed_history
+wait 1'
# In order: the tour started and finished (a panel listed after the first and the count, whatever it is), play
# went in and came out, and the save wrote the two placements left after the undo.
export SAGE_SMOKE_EXPECT=$'placed crate
ed_panel: Outliner \\(1/[0-9]+\\)
ed_panel: Inspector \\([0-9]+/[0-9]+\\)
ed_panel: showed [0-9]+ panel\\(s\\)
Playing \'
Stopped playing: back to the editor
Saved \'editor:level_placements\' \\(2 placement\\(s\\)\\)'

"$repo/tools/smoke_run.sh" "$host_dir" "$work/editor" 10 "${allowed[@]}"

# The saved file: the plate and the door (the crate was undone), the door's speed and the plate's wire,
# and the comment written by hand above the list still there.
python3 - "$saved" <<'EOF'
import json, re, sys
path = sys.argv[1]
text = open(path).read()
def fail(message):
    print(f"::error::editor: {path}: {message}")
    print(text)
    sys.exit(1)
if "// Written by hand, before the editor ever opened this file" not in text:
    fail("the hand-written comment was lost")
plain = re.sub(r"(?m)^\s*//[^\n]*$", "", text)
plain = re.sub(r",(\s*[}\]])", r"\1", plain)
record = json.loads(plain)
place = record.get("place", [])
names = sorted(p.get("name") for p in place)
if names != ["door", "plate"]:
    fail(f"expected the placements door and plate, found {names}")
door = next(p for p in place if p["name"] == "door")
plate = next(p for p in place if p["name"] == "plate")
seconds = door.get("overrides", {}).get("parts", {}).get("mover", {}).get("seconds")
if seconds != 0.5:
    fail(f"expected the door's mover.seconds 0.5, found {seconds!r} in {json.dumps(door)}")
wires = [(o.get("output"), o.get("target"), o.get("input")) for o in plate.get("outputs", [])]
if wires != [("OnStartTouch", "door", "Open")]:
    fail(f"expected the plate wired OnStartTouch -> door Open, found {json.dumps(plate)}")
print(f"editor: saved {len(place)} placement(s): door (mover.seconds 0.5) and plate (wired to it)")
EOF
