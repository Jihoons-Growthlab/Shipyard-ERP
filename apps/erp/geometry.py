"""Hull reference dimensions, block bounding boxes and yard placement for the digital twin.

Ship coordinates (m): x from the aft end forward (0..LOA), y from the centre line (+ port, - starboard),
z from the baseline up. A block is an axis-aligned box with a centre of gravity inside it.
Yard coordinates (m): the same plane as crane telemetry x/y, z up; heading_deg is measured from the yard
x axis toward the yard y axis. A hull placement puts the ship origin (aft end, centre line, baseline) on a dock.
Geo reference: one yard site ties the yard origin to WGS84 latitude/longitude, with the compass bearing of the yard
+x axis (clockwise from true north). Facility footprints (size_x/y/z_m) are rectangles centred on x/y.
"""

import math
import re

EARTH_RADIUS_M = 6378137.0

SOURCES = ['추정', 'CAD']
CHOICES = {'geometry_source': SOURCES}
SCHEMA = '''
CREATE TABLE IF NOT EXISTS hull_geometry(hull TEXT PRIMARY KEY REFERENCES hulls(id),
 depth_m REAL NOT NULL, draft_m REAL NOT NULL, frame_spacing_m REAL NOT NULL);
CREATE TABLE IF NOT EXISTS block_geometry(block TEXT PRIMARY KEY REFERENCES blocks(id),
 x_min REAL NOT NULL, x_max REAL NOT NULL, y_min REAL NOT NULL, y_max REAL NOT NULL,
 z_min REAL NOT NULL, z_max REAL NOT NULL, cog_x REAL NOT NULL, cog_y REAL NOT NULL, cog_z REAL NOT NULL,
 geometry_source TEXT NOT NULL, model_asset TEXT);
CREATE TABLE IF NOT EXISTS location_geometry(location TEXT PRIMARY KEY REFERENCES locations(id),
 x REAL NOT NULL, y REAL NOT NULL, z REAL NOT NULL, heading_deg REAL NOT NULL,
 size_x_m REAL NOT NULL DEFAULT 0, size_y_m REAL NOT NULL DEFAULT 0, size_z_m REAL NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS yard_sites(id TEXT PRIMARY KEY, name TEXT NOT NULL, lat REAL NOT NULL, lon REAL NOT NULL,
 x_axis_bearing_deg REAL NOT NULL);
CREATE TABLE IF NOT EXISTS hull_placements(hull TEXT PRIMARY KEY REFERENCES hulls(id),
 dock TEXT NOT NULL REFERENCES locations(id), origin_x REAL NOT NULL, origin_y REAL NOT NULL,
 origin_z REAL NOT NULL, heading_deg REAL NOT NULL);
'''
FOOTPRINT_KEYS = ('size_x_m', 'size_y_m', 'size_z_m')
COORDINATES = ('x_min', 'x_max', 'y_min', 'y_max', 'z_min', 'z_max', 'cog_x', 'cog_y', 'cog_z')
FIELDS = {
    'hull_geometry': ('hull', 'depth_m', 'draft_m', 'frame_spacing_m'),
    'block_geometry': ('block',) + COORDINATES + ('geometry_source', 'model_asset'),
    'location_geometry': ('location', 'x', 'y', 'z', 'heading_deg', 'size_x_m', 'size_y_m', 'size_z_m'),
    'yard_sites': ('id', 'name', 'lat', 'lon', 'x_axis_bearing_deg'),
    'hull_placements': ('hull', 'dock', 'origin_x', 'origin_y', 'origin_z', 'heading_deg'),
}
PREFIXES = {}
OPTIONAL = {'block_geometry': {'model_asset'}, 'location_geometry': set(FOOTPRINT_KEYS)}
DATES = set()
PLACEMENT = {'origin_x', 'origin_y', 'origin_z', 'heading_deg'}
FOOTPRINT = set(FOOTPRINT_KEYS)
# Latitude/longitude need more than 3 decimals: free-precision finite numbers (app.FLOAT_KEYS).
FLOATS = {'lat', 'lon'}
# x, y are already crane telemetry measures in the core app; z joins them for yard positions.
MEASURES = {'depth_m', 'draft_m', 'frame_spacing_m', *COORDINATES, *PLACEMENT, *FOOTPRINT, 'z', 'x_axis_bearing_deg'}
NUMBERS = set(MEASURES)
SIGNED = set(COORDINATES) | PLACEMENT | FOOTPRINT | {'z', 'x_axis_bearing_deg'}
READS = set(FIELDS) | {'erection_timeline', 'location_geo'}
KEYS = {'hull_geometry': 'hull', 'block_geometry': 'block', 'location_geometry': 'location', 'hull_placements': 'hull',
        'yard_sites': 'id'}
MODEL_ASSET = re.compile(r'[A-Za-z0-9][A-Za-z0-9._/-]{0,199}')


def upgrade_location_footprints(db):
    import storage

    if isinstance(db, storage.Postgres):
        for key in FOOTPRINT_KEYS:
            db.execute(f'ALTER TABLE location_geometry ADD COLUMN IF NOT EXISTS {key} DOUBLE PRECISION NOT NULL DEFAULT 0')
    else:
        columns = {row['name'] for row in db.execute('PRAGMA table_info(location_geometry)')}
        for key in FOOTPRINT_KEYS:
            if key not in columns:
                db.execute(f'ALTER TABLE location_geometry ADD COLUMN {key} REAL NOT NULL DEFAULT 0')


def upsert(db, table, v):
    key = KEYS[table]
    updates = ','.join(f'{column}=excluded.{column}' for column in v if column != key)
    db.execute(f'INSERT INTO {table} ({",".join(v)}) VALUES ({",".join("?" for _ in v)}) '
               f'ON CONFLICT({key}) DO UPDATE SET {updates}', tuple(v.values()))
    return v[key]


def write(db, action, v, api):
    require, lookup = api.require, api.lookup
    if 'heading_deg' in v:
        require(0 <= v['heading_deg'] < 360, '방향은 0 이상 360 미만의 각도(도)로 입력하세요.')
    if action == 'location_geometry':
        lookup(db, 'locations', v['location'])
        for key in FOOTPRINT:
            v[key] = 0.0 if v[key] is None else v[key]
        require(all(v[key] >= 0 for key in FOOTPRINT), '시설 크기는 0 이상으로 입력하세요(0이면 점 위치).')
        return upsert(db, action, v)
    if action == 'yard_sites':
        require(-90 <= v['lat'] <= 90 and -180 <= v['lon'] <= 180, '위도는 -90~90, 경도는 -180~180 사이로 입력하세요.')
        require(0 <= v['x_axis_bearing_deg'] < 360, '야드 x축 방위각은 0 이상 360 미만(진북 기준 시계 방향)으로 입력하세요.')
        other = db.execute('SELECT id FROM yard_sites WHERE id<>?', (v['id'],)).fetchone()
        require(not other, '조선소 부지는 1곳만 등록합니다. 기존 부지를 수정하세요.')
        return upsert(db, action, v)
    if action == 'hull_placements':
        lookup(db, 'hulls', v['hull'])
        require(lookup(db, 'locations', v['dock'])['kind'] in {'도크', '야드'}, '배치 시설은 도크 또는 야드로 지정하세요.')
        return upsert(db, action, v)
    if action == 'hull_geometry':
        lookup(db, 'hulls', v['hull'])
        require(v['draft_m'] < v['depth_m'], '흘수는 형심보다 작아야 합니다.')
        return upsert(db, action, v)
    require(v['geometry_source'] in SOURCES, '형상 출처를 목록에서 선택하세요.')
    require(not v['model_asset'] or (MODEL_ASSET.fullmatch(v['model_asset']) and '..' not in v['model_asset']),
            '모델 키는 영문·숫자·._/- 조합의 상대 경로로 입력하세요.')
    block = lookup(db, 'blocks', v['block'])
    for axis in 'xyz':
        low, high, cog = v[f'{axis}_min'], v[f'{axis}_max'], v[f'cog_{axis}']
        require(low < high, f'{axis}_min은 {axis}_max보다 작아야 합니다.')
        require(low <= cog <= high, f'무게중심 cog_{axis}는 블록 범위 안에 있어야 합니다.')
    require(v['z_min'] >= 0, '블록은 기선(z=0) 위에 있어야 합니다.')
    vessel = db.execute('SELECT length_m, beam_m FROM vessel_details WHERE hull=?', (block['hull'],)).fetchone()
    if vessel:
        require(v['x_min'] >= 0 and v['x_max'] <= vessel['length_m'], f'x 범위는 0~전장 {vessel["length_m"]}m 안이어야 합니다.')
        half = vessel['beam_m'] / 2
        require(-half <= v['y_min'] and v['y_max'] <= half, f'y 범위는 선폭의 절반 ±{half}m 안이어야 합니다.')
    return upsert(db, action, v)


def block_rows(db):
    rows = []
    for row in db.execute('''SELECT g.*, b.hull, d.zone, d.weight_t, h.frame_spacing_m FROM block_geometry g
            JOIN blocks b ON b.id=g.block LEFT JOIN block_details d ON d.block=g.block
            LEFT JOIN hull_geometry h ON h.hull=b.hull ORDER BY b.hull, g.x_min, g.z_min, g.y_min, g.block'''):
        item = dict(row)
        spacing = item.pop('frame_spacing_m')
        for axis in 'xyz':
            item[f'size_{axis}_m'] = round(item[f'{axis}_max'] - item[f'{axis}_min'], 3)
        item['frame_from'] = round(item['x_min'] / spacing) if spacing else None
        item['frame_to'] = round(item['x_max'] / spacing) if spacing else None
        rows.append(item)
    return rows


def erection_steps(db):
    """Erection step per block: 1 + the longest chain of erection predecessors (None inside a cycle)."""
    predecessors, successors = {}, {}
    for row in db.execute('SELECT predecessor_block, successor_block FROM erection_links'):
        predecessors.setdefault(row['successor_block'], []).append(row['predecessor_block'])
        successors.setdefault(row['predecessor_block'], []).append(row['successor_block'])
    nodes = set(predecessors) | set(successors)
    waiting = {node: len(predecessors.get(node, ())) for node in nodes}
    ready = [node for node, count in waiting.items() if count == 0]
    steps = {node: 1 for node in ready}
    while ready:
        node = ready.pop()
        for after in successors.get(node, ()):
            steps[after] = max(steps.get(after, 1), steps[node] + 1)
            waiting[after] -= 1
            if waiting[after] == 0:
                ready.append(after)
    return {node: steps[node] if waiting[node] == 0 else None for node in nodes}, predecessors


def erection_timeline(db):
    """One row per block with geometry: erection step, plan/actual days and predecessors, for twin playback."""
    steps, predecessors = erection_steps(db)
    plans = {row['block']: dict(row) for row in db.execute('SELECT * FROM erection_plans')}
    erected = {}
    for row in db.execute("SELECT block, erect_day, inspection FROM erection_records ORDER BY recorded_at, id"):
        erected[row['block']] = row['erect_day'] if row['inspection'] == '합격' else None
    rows = []
    for item in block_rows(db):
        plan = plans.get(item['block'], {})
        rows.append({
            'block': item['block'], 'hull': item['hull'], 'zone': item['zone'], 'weight_t': item['weight_t'],
            'step': steps.get(item['block'], 1), 'predecessors': sorted(predecessors.get(item['block'], ())),
            'destination': plan.get('destination'), 'baseline': plan.get('baseline'), 'forecast': plan.get('forecast'),
            'erected_day': erected.get(item['block']), 'model_asset': item['model_asset'],
            **{key: item[key] for key in COORDINATES}})
    rows.sort(key=lambda row: (row['hull'], row['step'] is None, row['step'] or 0, row['forecast'] or '9999', row['block']))
    return rows


def yard_to_geo(site, x, y):
    """Yard metres -> (lat, lon). Local tangent plane at the site origin: accurate to millimetres over a few km."""
    bearing = math.radians(site['x_axis_bearing_deg'])
    # Yard +x points along the bearing; yard +y is 90 degrees counter-clockwise from it (right-handed, from above).
    east = x * math.sin(bearing) - y * math.cos(bearing)
    north = x * math.cos(bearing) + y * math.sin(bearing)
    lat = site['lat'] + math.degrees(north / EARTH_RADIUS_M)
    lon = site['lon'] + math.degrees(east / (EARTH_RADIUS_M * math.cos(math.radians(site['lat']))))
    return round(lat, 8), round(lon, 8)


def location_geo(db):
    """Facilities with WGS84 centre and footprint corners (counter-clockwise) once a yard site is registered."""
    site = db.execute('SELECT * FROM yard_sites ORDER BY id LIMIT 1').fetchone()
    rows = []
    query = ('SELECT g.*, l.name, l.kind FROM location_geometry g JOIN locations l ON l.id=g.location '
             'ORDER BY g.location')
    for row in db.execute(query):
        item = dict(row)
        item['site'] = site['id'] if site else None
        item['lat'], item['lon'] = yard_to_geo(site, item['x'], item['y']) if site else (None, None)
        corners = []
        if site and item['size_x_m'] > 0 and item['size_y_m'] > 0:
            a = math.radians(item['heading_deg'])
            hx, hy = item['size_x_m'] / 2, item['size_y_m'] / 2
            for sx, sy in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
                dx, dy = sx * hx, sy * hy
                corners.append(list(yard_to_geo(site, item['x'] + dx * math.cos(a) - dy * math.sin(a),
                                                item['y'] + dx * math.sin(a) + dy * math.cos(a))))
        item['corners'] = corners
        rows.append(item)
    return rows


def read(db, table):
    if table == 'location_geo':
        return location_geo(db)
    if table == 'yard_sites':
        return [dict(row) for row in db.execute('SELECT * FROM yard_sites ORDER BY id')]
    if table == 'block_geometry':
        return block_rows(db)
    if table == 'erection_timeline':
        return erection_timeline(db)
    if table in ('hull_geometry', 'hull_placements'):
        return [dict(row) for row in db.execute(f'SELECT * FROM {table} ORDER BY hull')]
    if table == 'location_geometry':
        return [dict(row) for row in db.execute('SELECT * FROM location_geometry ORDER BY location')]
    return None
