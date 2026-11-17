"""Download one hull's digital twin data from the ShipYard-ERP API into a single JSON file.

The file holds the hull, its dock placement and yard coordinates, and one row per block with geometry,
erection step, plan/actual days, predecessors and model key: everything a twin needs to replay erection.
"""

import argparse
import json
import re
import shutil
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import urlencode

from erp_client import base_url, login, request
from paths import MODELS, TWIN, UNITY_RESOURCES

PAGE = 200


def rows(base, token, route, hull=None):
    if not hull:
        return request(base, route, token=token)
    out, page = [], 0
    while True:
        result = request(base, f"{route}?{urlencode({'hull': hull, 'page': page, 'size': PAGE})}", token=token)
        out += result["rows"]
        page += 1
        if page >= result["pages"]:
            return out


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("hull")
    parser.add_argument("-o", "--output", type=Path)
    parser.add_argument("--models", type=Path, default=MODELS, help="generate_models.py 출력 폴더")
    parser.add_argument("--unity-resources", type=Path, default=UNITY_RESOURCES,
                        help="<hull>.twin.json과 Models/<model_asset>.bytes를 복사할 Unity Resources 폴더")
    parser.add_argument("--no-unity", action="store_true", help="Unity 폴더로 복사하지 않음")
    args = parser.parse_args()
    if not re.fullmatch(r"[A-Za-z0-9_-]{1,40}", args.hull):
        parser.error("호선 ID 형식을 확인하세요.")
    base = base_url(parser)
    token = login(base, parser)

    pick = lambda items, key="hull": next((row for row in items if row[key] == args.hull), None)
    vessel = pick(rows(base, token, "vessel_details"))
    geometry = pick(rows(base, token, "hull_geometry"))
    placement = pick(rows(base, token, "hull_placements"))
    timeline = rows(base, token, "erection_timeline", args.hull)
    if not (vessel and geometry and placement and timeline):
        raise SystemExit("호선 상세, 호선 형상, 도크 배치, 블록 형상이 모두 등록된 호선이어야 합니다.")
    dock = next((row for row in rows(base, token, "location_geometry") if row["location"] == placement["dock"]), None)
    timeline.sort(key=lambda row: (row["step"] is None, row["step"] or 0, row["forecast"] or "9999", row["block"]))
    sites = rows(base, token, "yard_sites")
    facilities = rows(base, token, "location_geo")
    keep = ("location", "name", "kind", "x", "y", "z", "heading_deg", "size_x_m", "size_y_m", "size_z_m", "lat", "lon")
    twin = {
        "format": "shipyard-twin/1",
        "exported_at": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "coordinates": "ship: x fwd from aft end, y port, z up from baseline (m); yard: telemetry x/y, z up (m)",
        "hull": {**vessel, **geometry},
        "placement": {**placement, "dock_position": dock},
        "blocks": timeline,
        "yard": {"site": sites[0] if sites else None, "facilities": [{k: f[k] for k in keep} for f in facilities]},
    }
    output = args.output or TWIN / f"{args.hull}.twin.json"
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(twin, ensure_ascii=False, indent=1), encoding="utf-8")
    if not args.no_unity:
        target = args.unity_resources
        target.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(output, target / output.name)
        copied = missing = 0
        for row in timeline:
            asset = row["model_asset"]
            if not asset:
                continue
            source = args.models / asset
            if ".." in Path(asset).parts or not source.is_file():
                missing += 1
                continue
            destination = target / "Models" / (asset + ".bytes")  # TextAsset: Resources.Load("Models/<model_asset>")
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, destination)
            copied += 1
        print(f"{target}: 데이터 1개, 모델 {copied}개 복사" + (f", 모델 없음 {missing}개(상자로 표시)" if missing else ""))
    steps = [row["step"] for row in timeline if row["step"]]
    print(f"{output}: 블록 {len(timeline)}개, 탑재 단계 {max(steps) if steps else 0}, "
          f"모델 키 {sum(1 for row in timeline if row['model_asset'])}개")


if __name__ == "__main__":
    try:
        main()
    except (OSError, KeyError, ValueError, RuntimeError) as exc:
        raise SystemExit(str(exc))
