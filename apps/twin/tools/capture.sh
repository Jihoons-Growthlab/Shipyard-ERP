#!/usr/bin/env bash
# Screenshot of the built app at a playback day, paused, with the erection panel open.
# Usage: tools/capture.sh <day> <out.png> [overview|bow|stern|crane|top|yard|coast|pier|follow]
set -u
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
"$ROOT/Builds/ShipyardTwin.app/Contents/MacOS/ShipyardTwin" -screen-fullscreen 0 -screen-width 1600 -screen-height 900 \
  -shipyard-hull "${HULL:-SN2040}" -shipyard-seek "${1:?day}" -shipyard-panel -shipyard-view "${3:-overview}" \
  -growthlab-screenshot "${2:?out.png}" -growthlab-screenshot-delay 5 -logFile "$ROOT/Builds/capture.log" > /dev/null
