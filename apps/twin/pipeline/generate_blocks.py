"""Generate a rule-based (estimated) block division for one hull as ShipYard-ERP JSONL.

The blocks are bounding boxes in ship coordinates (m): x from the aft end forward, y from the centre
line (+ port), z from the baseline. Weights are rough box-volume estimates, marked geometry_source=추정.
Replace them with CAD values (geometry_source=CAD) when real design data is available.
"""

import argparse
import json
import math
from datetime import date, timedelta
from pathlib import Path

from hullform import ENGINE_END, HOLDS_END, STERN_END, HullForm
from paths import DATA

# Estimated steel weight per m3 of block bounding box (t/m3).
# Calibrated so a 174K LNG carrier totals roughly 27,000-28,000t of hull steel.
DENSITY = {'db': 0.30, 'er_db': 0.38, 'side': 0.27, 'deck': 0.14, 'end': 0.09, 'house': 0.06}
SIDES = {'P': '좌현', 'S': '우현', 'C': '중앙'}


def r(value):
    return round(value, 3)


def section_params(beam):
    """Double bottom height, double side width, centre double-bottom block width, deck block band (m)."""
    return r(max(2.0, beam * 0.065)), r(beam * 0.055), r(beam * 0.30), 2.5


def frame_segments(x0, x1, spacing, segment_frames, length):
    """Split [x0, x1] into frame-aligned segments of about segment_frames frames."""
    f0, f1 = round(x0 / spacing), round(x1 / spacing)
    count = max(1, round((f1 - f0) / segment_frames))
    frames = [f0 + round((f1 - f0) * i / count) for i in range(count + 1)]
    return [(r(a * spacing), r(min(b * spacing, length))) for a, b in zip(frames, frames[1:])]


def build(args):
    L, B, D, s = args.length, args.beam, args.depth, args.frame_spacing
    half = B / 2
    h_db, w_side, c_db, deck_band = section_params(B)
    hull = HullForm(L, B, D, args.draft)
    blocks, links = [], []

    def add(code, zone, density, x, y, z, group):
        box = {'x_min': x[0], 'x_max': x[1], 'y_min': r(y[0]), 'y_max': r(y[1]), 'z_min': r(z[0]), 'z_max': r(z[1])}
        volume = (box['x_max'] - box['x_min']) * (box['y_max'] - box['y_min']) * (box['z_max'] - box['z_min'])
        block_id = f'{args.hull}-{code}'
        blocks.append({'id': block_id, 'zone': zone, 'group': group, 'weight_t': r(max(1.0, volume * density)), **box,
                       'cog_x': r((box['x_min'] + box['x_max']) / 2), 'cog_y': r((box['y_min'] + box['y_max']) / 2),
                       'cog_z': r((box['z_min'] + box['z_max']) / 2)})
        return block_id

    def link(before, after):
        links.append((before, after))

    def midship(zone_digit, group, x, label, db_density, seg):
        """Double bottom P/C/S, side shell P/S and upper deck for one parallel-body segment."""
        db = {side: add(f'{zone_digit}{group}1{side}{seg}', f'{label} 이중저 {SIDES[side]}', db_density, x, y, (0, h_db), label)
              for side, y in (('P', (c_db / 2, half)), ('C', (-c_db / 2, c_db / 2)), ('S', (-half, -c_db / 2)))}
        side = {key: add(f'{zone_digit}{group}2{key}{seg}', f'{label} 측외판 {SIDES[key]}', DENSITY['side'], x, y, (h_db, D), label)
                for key, y in (('P', (half - w_side, half)), ('S', (-half, -half + w_side)))}
        deck = add(f'{zone_digit}{group}3C{seg}', f'{label} 상갑판', DENSITY['deck'], x,
                   (-half + w_side, half - w_side), (D - deck_band, D), label)
        link(db['C'], db['P'])
        link(db['C'], db['S'])
        link(db['P'], side['P'])
        link(db['S'], side['S'])
        link(side['P'], deck)
        link(side['S'], deck)
        return db['C'], deck

    def end_body(zone_digit, x, label, previous):
        """Lower/upper, port/starboard boxes for a curved end segment, sized to enclose the hull form."""
        out = {}
        for part, z, tier in (('4', (0, D / 2), '하부'), ('5', (D / 2, D), '상부')):
            half_breadth = math.ceil(hull.max_half_breadth(x[0], x[1], z[0], z[1]) * 1000) / 1000
            for key, y in (('P', (0, half_breadth)), ('S', (-half_breadth, 0))):
                out[part + key] = add(f'{zone_digit}1{part}{key}{x[2]}', f'{label} {tier} {SIDES[key]}',
                                      DENSITY['end'], x[:2], y, z, label)
        for key in 'PS':
            link(previous, out['4' + key])
            link(out['4' + key], out['5' + key])
        return out['4P']

    holds = frame_segments(L * ENGINE_END, L * HOLDS_END, s, args.segment_frames, L)
    per_hold = [holds[i * len(holds) // args.holds:(i + 1) * len(holds) // args.holds] for i in range(args.holds)]
    engine = frame_segments(L * STERN_END, L * ENGINE_END, s, args.segment_frames, L)
    stern = frame_segments(0, L * STERN_END, s, args.segment_frames, L)
    bow = frame_segments(L * HOLDS_END, L, s, args.segment_frames, L)
    if max(map(len, per_hold + [engine, stern, bow])) > 9 or min(map(len, per_hold)) == 0:
        raise SystemExit('구역당 구간 수가 1~9개가 되도록 --segment-frames 또는 --holds를 조정하세요.')

    # Erection starts from the aft-most hold segment (key block) and proceeds fore and aft.
    hold_keys, hold_decks = [], []
    for index, segments in enumerate(per_hold):  # aft to fore; hold numbers count from the bow
        number = args.holds - index
        for seg, x in enumerate(segments, 1):
            key, deck = midship(2, number, x, f'{number}번 화물창', DENSITY['db'], seg)
            hold_keys.append(key)
            hold_decks.append(deck)
    for before, after in zip(hold_keys, hold_keys[1:]):
        link(before, after)

    engine_keys, engine_decks = [], []
    for seg, x in reversed(list(enumerate(engine, 1))):
        key, deck = midship(3, 1, x, '기관실', DENSITY['er_db'], seg)
        link(engine_keys[-1] if engine_keys else hold_keys[0], key)
        engine_keys.append(key)
        engine_decks.append(deck)

    previous = engine_keys[-1]
    for seg, (x0, x1) in reversed(list(enumerate(stern, 1))):
        previous = end_body(4, (x0, x1, seg), '선미부', previous)

    previous = hold_keys[-1]
    for seg, (x0, x1) in enumerate(bow, 1):
        previous = end_body(1, (x0, x1, seg), '선수부', previous)

    # Deckhouse tiers above the aft part of the engine room.
    house_x = (engine[0][0], engine[0][1])
    previous = engine_decks[-1]
    tier_height = 4.5
    for tier in range(1, args.deckhouse_tiers + 1):
        z = (D + (tier - 1) * tier_height, D + tier * tier_height)
        house = add(f'51{tier}C1', f'거주구 {tier}층', DENSITY['house'], house_x, (-B * 0.36, B * 0.36), z, '거주구')
        link(previous, house)
        previous = house
    return blocks, links


def erection_steps(blocks, links):
    """1 + longest chain of predecessors, the same rule as ERP /api/erection_timeline."""
    predecessors = {}
    for before, after in links:
        predecessors.setdefault(after, []).append(before)
    steps = {}
    for block in blocks:  # blocks are emitted after their predecessors' segments, but resolve iteratively anyway
        pending = [block['id']]
        while pending:
            node = pending[-1]
            missing = [p for p in predecessors.get(node, ()) if p not in steps]
            if missing:
                pending.extend(missing)
                continue
            steps[node] = 1 + max((steps[p] for p in predecessors.get(node, ())), default=0)
            pending.pop()
    return steps


def records(args, blocks, links):
    out = []
    # The dock facility and its coordinates come from generate_yard.py; only the hull placement is written here.
    dock_x, dock_y = args.dock_position
    if not args.existing_hull:
        out.append({'action': 'hulls', 'data': {'id': args.hull, 'name': args.name}})
        out.append({'action': 'vessel_details', 'data': {
            'hull': args.hull, 'vessel_type': args.vessel_type, 'vessel_name': args.vessel_name,
            'customer': args.customer, 'length_m': args.length, 'beam_m': args.beam, 'delivery': args.delivery}})
    out.append({'action': 'hull_geometry', 'data': {
        'hull': args.hull, 'depth_m': args.depth, 'draft_m': args.draft, 'frame_spacing_m': args.frame_spacing}})
    # Ship origin (aft end, centre line, baseline) placed so that midship sits on the dock reference point.
    out.append({'action': 'hull_placements', 'data': {
        'hull': args.hull, 'dock': args.dock, 'origin_x': round(dock_x - args.length / 2, 3), 'origin_y': dock_y,
        'origin_z': 0, 'heading_deg': 0}})
    for block in blocks:
        out.append({'action': 'blocks', 'data': {'id': block['id'], 'hull': args.hull}})
    for block in blocks:
        code = block['id'][len(args.hull) + 1:]
        out.append({'action': 'block_details', 'data': {
            'block': block['id'], 'zone': block['zone'], 'assembly': '대조립', 'weight_t': block['weight_t'],
            'drawing': f'{args.hull}-STR-{code}-R01'}})
    keys = ('x_min', 'x_max', 'y_min', 'y_max', 'z_min', 'z_max', 'cog_x', 'cog_y', 'cog_z')
    for block in blocks:
        out.append({'action': 'block_geometry', 'data': {
            'block': block['id'], **{key: block[key] for key in keys}, 'geometry_source': '추정',
            'model_asset': f'{args.hull}/{block["id"]}.obj'}})
    for before, after in links:
        out.append({'action': 'erection_links', 'data': {'predecessor_block': before, 'successor_block': after}})
    steps = erection_steps(blocks, links)
    for block in sorted(blocks, key=lambda b: (steps[b['id']], b['id'])):
        day = (args.erection_start + timedelta(days=(steps[block['id']] - 1) * args.days_per_step)).isoformat()
        out.append({'action': 'erection_plans', 'data': {
            'block': block['id'], 'destination': args.dock, 'baseline': day, 'forecast': day, 'reason': ''}})
    return out


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--hull', default='SN2040')
    parser.add_argument('--name', default='SN2040 · 174K LNG 운반선 (가상 블록 분할)')
    parser.add_argument('--existing-hull', action='store_true', help='호선과 호선 상세가 이미 ERP에 있으면 지정')
    parser.add_argument('--vessel-type', default='174,000㎥ LNG 운반선')
    parser.add_argument('--vessel-name', default='Generated Meridian (가상)')
    parser.add_argument('--customer', default='모의 선주 (가상)')
    parser.add_argument('--delivery', default='2028-06-30', type=lambda v: date.fromisoformat(v).isoformat())
    parser.add_argument('--length', type=float, default=299.0, help='전장 LOA (m)')
    parser.add_argument('--beam', type=float, default=46.4, help='선폭 (m)')
    parser.add_argument('--depth', type=float, default=26.5, help='형심 (m)')
    parser.add_argument('--draft', type=float, default=11.5, help='설계 흘수 (m)')
    parser.add_argument('--frame-spacing', type=float, default=0.8, help='프레임 간격 (m)')
    parser.add_argument('--holds', type=int, default=4, help='화물창 수')
    parser.add_argument('--segment-frames', type=int, default=16, help='블록 1개의 길이 방향 프레임 수')
    parser.add_argument('--deckhouse-tiers', type=int, default=4)
    parser.add_argument('--dock', default='DT-DOCK', help='탑재 도크 시설 ID (generate_yard.py로 ERP에 등록)')
    parser.add_argument('--dock-position', type=float, nargs=2, default=(430.0, 170.0), metavar=('X', 'Y'),
                        help='도크 중심 야드 좌표 (m). generate_yard.py의 DT-DOCK과 같게')
    parser.add_argument('--erection-start', default=date(2027, 3, 1), type=date.fromisoformat, help='첫 탑재일')
    parser.add_argument('--days-per-step', type=int, default=3, help='탑재 단계 사이 일수')
    parser.add_argument('-o', '--output', type=Path)
    args = parser.parse_args()
    if not (args.draft < args.depth and args.holds >= 1 and args.segment_frames >= 4 and 1 <= args.deckhouse_tiers <= 9
            and args.days_per_step >= 1):
        parser.error('흘수 < 형심, 화물창 1개 이상, 구간 4프레임 이상, 거주구 1~9층, 단계 간격 1일 이상이어야 합니다.')

    blocks, links = build(args)
    rows = records(args, blocks, links)
    output = args.output or DATA / f'{args.hull}-blocks.jsonl'
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(''.join(json.dumps(row, ensure_ascii=False) + '\n' for row in rows), encoding='utf-8')
    zones = {}
    for block in blocks:
        zone = block['group']
        zones.setdefault(zone, [0, 0.0])
        zones[zone][0] += 1
        zones[zone][1] += block['weight_t']
    print(f'{output}: {len(rows)}행 (블록 {len(blocks)}개, 탑재 선후행 {len(links)}건)')
    for zone, (count, weight) in zones.items():
        print(f'  {zone}: {count}개, {weight:,.0f}t')
    print(f'  추정 강재 중량 합계 {sum(b["weight_t"] for b in blocks):,.0f}t, 최대 블록 '
          f'{max(b["weight_t"] for b in blocks):,.1f}t')


if __name__ == '__main__':
    main()
