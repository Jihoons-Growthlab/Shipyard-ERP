#!/usr/bin/env bash
# Benchmark one build at a fixed window and camera: startup, idle/playing frame times, memory, CPU%, RSS.
# Usage: tools/bench.sh <ShipyardTwin.app> <out.json> [extra app args...]
# The app writes its own numbers (PerfProbe, -shipyard-bench); this script adds process CPU% and RSS sampled by ps.
set -u
APP="${1:?app}"; OUT="${2:?out.json}"; shift 2
BIN="$APP/Contents/MacOS/ShipyardTwin"
CAMERA="${BENCH_CAMERA:-300,120,40,38,900}"
SAMPLES="${OUT%.json}.ps.txt"
"$BIN" -screen-fullscreen 0 -screen-width 1600 -screen-height 900 -shipyard-camera "$CAMERA" \
  -shipyard-bench "$OUT" -shipyard-bench-seconds "${BENCH_SECONDS:-8}" -logFile "${OUT%.json}.log" "$@" >/dev/null 2>&1 &
PID=$!
: > "$SAMPLES"
START=$(python3 -c 'import time; print(time.time())')
while kill -0 "$PID" 2>/dev/null; do
  NOW=$(python3 -c "import time; print(round(time.time()-$START,1))")
  ps -o %cpu=,rss= -p "$PID" 2>/dev/null | awk -v t="$NOW" '{print t, $1, $2}' >> "$SAMPLES"
  sleep 0.5
done
wait "$PID" 2>/dev/null
python3 - "$OUT" "$SAMPLES" <<'PY'
import json, sys
out, samples = sys.argv[1], sys.argv[2]
rows = [tuple(map(float, l.split())) for l in open(samples) if l.strip()]
data = json.load(open(out))
data["ps_samples"] = len(rows)
if rows:
    data["ps_rss_peak_mb"] = round(max(r[2] for r in rows) / 1024, 1)
    data["ps_rss_last_mb"] = round(rows[-1][2] / 1024, 1)
    # PerfProbe timeline from launch: ready, +2 s settle, idle phase, then playing phase (BENCH_SECONDS each).
    seconds = float(__import__("os").environ.get("BENCH_SECONDS", "8"))
    ready = data.get("ready_s", 0)
    play_start = ready + 2.5 + seconds + data.get("select_load_s", 0)
    for name, start in (("idle", ready + 2.5), ("playing", play_start)):
        window = [r for r in rows if start <= r[0] <= start + seconds - 1]
        if window:
            data[f"ps_cpu_{name}_pct"] = round(sum(r[1] for r in window) / len(window), 1)
json.dump(data, open(out, "w"), indent=1)
PY
echo "$OUT"
