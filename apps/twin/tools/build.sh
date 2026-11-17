#!/usr/bin/env bash
# Regenerates the scene from Authoring/ and builds Builds/ShipyardTwin.app (macOS). Override UNITY=...
set -u
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/6000.3.23f1/Unity.app/Contents/MacOS/Unity}"
mkdir -p "$ROOT/Builds"
"$UNITY" -batchmode -projectPath "$ROOT/unity" -buildTarget StandaloneOSX \
  -executeMethod ShipyardTwin.Editor.ShipyardTwinBuild.BuildMacBatch -logFile "$ROOT/Builds/build.log"
code=$?
grep -E "\[ShipyardTwin\] build|error CS" "$ROOT/Builds/build.log" | head -5
echo "build exit code: $code (log: $ROOT/Builds/build.log)"
exit $code
