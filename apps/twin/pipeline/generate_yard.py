"""Generate a virtual shipyard site plan as ShipYard-ERP JSONL: the geo-referenced yard site and its facilities.

Yard coordinates (m): +x points seaward (docks open to the sea at +x), +y runs along the shore; z up. The site
ties the yard origin to latitude/longitude and gives the compass bearing of the yard +x axis (clockwise from
true north). Default site: 34.833114N 128.597490E, bearing 235 (south-west), about 450 m inland of
Geoje-si Dongbu-myeon Sanchon-ri 761; adjust on the ERP map (/map) or pass --lat/--lon/--bearing. Footprints are rectangles centred on x/y, aligned with the yard
axes (heading 0); docks span from their head to the shoreline gate.
"""

import argparse
import json
from pathlib import Path

from paths import DATA

# (id, kind, name, x, y, size_x, size_y, height). DT-DOCK is the dock generate_blocks.py places hulls in.
# Fitted to the default site (Geoje Dongbu-myeon Sanchon-ri reclaimed flat, docks opening south-west): both docks
# inside the sea wall, shops on the flat valley floor, the block stockyard inland next to the warehouse.
# Layout revision 2026-11-17: goliath per dock with PE yards at the dock heads, one long pier instead of the two
# jetties beside the dock gates (they blocked the gate approach and were shorter than the ship).
FACILITIES = [
    ('DT-DOCK', '도크', '1도크', 430, 170, 370, 100, 0),
    ('GY-DOCK2', '도크', '2도크', 430, 10, 370, 100, 0),
    # One 640 m fitting-out pier into the open bay (north-west of the docks), berths on both sides with two ships
    # end to end: four berths, about 60 m clear of the 2도크 gate approach.
    ('GY-PIER', '안벽', '의장 피어', 935, -150, 640, 40, 0),
    ('GY-FAB', '작업장', '강재 가공공장', 40, 400, 170, 70, 22),
    ('GY-ASSY1', '작업장', '블록 조립 1공장', 40, 300, 170, 80, 30),
    ('GY-ASSY2', '작업장', '블록 조립 2공장', 40, 190, 170, 80, 30),
    ('GY-OUTFIT', '작업장', '선행의장 공장', 40, 80, 170, 70, 24),
    ('GY-PAINT', '작업장', '도장 공장', 40, -30, 150, 70, 26),
    # PE yards at the dock heads, under each dock's goliath rails, so the goliath lifts PE'd blocks straight in.
    ('GY-PE1', '야드', '1 PE장', 197.5, 170, 95, 100, 0),
    ('GY-PE2', '야드', '2 PE장', 197.5, 10, 95, 100, 0),
    # Quay-side outfitting support behind the pier root (former PE yard).
    ('GY-QOUTFIT', '작업장', '안벽 의장공장', 470, -150, 120, 60, 18),
    ('GY-QSTOCK', '야드', '의장 자재 적치장', 300, -150, 160, 60, 0),
    ('GY-STOCK', '야드', '블록 적치장', -115, -60, 70, 160, 0),
    ('GY-STORE', '창고', '자재 창고', -110, 190, 60, 200, 14),
]


def overlaps(a, b):
    (_, _, _, ax, ay, asx, asy, _), (_, _, _, bx, by, bsx, bsy, _) = a, b
    return abs(ax - bx) * 2 < asx + bsx and abs(ay - by) * 2 < asy + bsy


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--site-id', default='GY-SITE')
    parser.add_argument('--name', default='가상 거제 조선소')
    parser.add_argument('--lat', type=float, default=34.833114, help='야드 원점 위도')
    parser.add_argument('--lon', type=float, default=128.597490, help='야드 원점 경도')
    parser.add_argument('--bearing', type=float, default=235.0, help='야드 +x축(바다 방향) 방위각, 진북 기준 시계 방향')
    parser.add_argument('--existing', default='', help='ERP에 이미 있는 시설 ID (쉼표 구분): 시설 등록은 건너뛰고 좌표만 갱신')
    parser.add_argument('-o', '--output', type=Path, default=DATA / 'yard.jsonl')
    args = parser.parse_args()
    if not (-90 <= args.lat <= 90 and -180 <= args.lon <= 180 and 0 <= args.bearing < 360):
        parser.error('위도 -90~90, 경도 -180~180, 방위각 0~360 미만이어야 합니다.')
    for i, a in enumerate(FACILITIES):
        for b in FACILITIES[i + 1:]:
            if overlaps(a, b):
                raise SystemExit(f'배치 겹침: {a[0]} / {b[0]}')

    existing = {item.strip() for item in args.existing.split(',') if item.strip()}
    rows = [{'action': 'yard_sites', 'data': {'id': args.site_id, 'name': args.name, 'lat': args.lat, 'lon': args.lon,
                                              'x_axis_bearing_deg': args.bearing}}]
    for location, kind, name, *_ in FACILITIES:
        if location not in existing:
            rows.append({'action': 'locations', 'data': {'id': location, 'name': name, 'kind': kind}})
    for location, _, _, x, y, sx, sy, sz in FACILITIES:
        rows.append({'action': 'location_geometry', 'data': {'location': location, 'x': x, 'y': y, 'z': 0, 'heading_deg': 0,
                                                             'size_x_m': sx, 'size_y_m': sy, 'size_z_m': sz}})
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(''.join(json.dumps(row, ensure_ascii=False) + '\n' for row in rows), encoding='utf-8')
    kinds = {}
    for _, kind, *_ in FACILITIES:
        kinds[kind] = kinds.get(kind, 0) + 1
    print(f'{args.output}: {len(rows)}행 · 부지 {args.site_id} ({args.lat}, {args.lon}, 방위 {args.bearing}°) · '
          + ', '.join(f'{k} {n}' for k, n in kinds.items()))


if __name__ == '__main__':
    main()
