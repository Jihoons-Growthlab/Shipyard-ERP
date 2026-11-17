#!/usr/bin/env bash
# Runs the Unity EditMode tests of the ShipyardTwin project. Usage: tools/test.sh [EditMode|PlayMode]
# Exit code: 0 = all passed, 2 = some failed, other = editor/compile error (see the log). Override UNITY=...
set -u
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/6000.3.23f1/Unity.app/Contents/MacOS/Unity}"
MODE="${1:-EditMode}"
mkdir -p "$ROOT/TestResults"
"$UNITY" -batchmode -nographics -projectPath "$ROOT/unity" -runTests -testPlatform "$MODE" \
  -testResults "$ROOT/TestResults/$MODE.xml" -logFile "$ROOT/TestResults/$MODE.log"
code=$?
python3 - "$ROOT/TestResults/$MODE.xml" <<'PY' 2>/dev/null || true
import re, sys
text = open(sys.argv[1], encoding="utf-8").read()
run = re.search(r"<test-run [^>]*", text).group(0)
print(" ".join(f"{k}={v}" for k, v in re.findall(r'(total|passed|failed)="(\d+)"', run)))
for case in re.findall(r'<test-case [^>]*fullname="([^"]+)"[^>]*result="Failed"', text):
    print("FAILED", case)
PY
echo "$MODE exit code: $code (log: $ROOT/TestResults/$MODE.log)"
exit $code
