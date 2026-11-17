"""Block stocking plans: where a finished block waits in the yard before erection.

One row is one stay of one block at one yard facility: the slot centre (x, y) in yard metres (the crane telemetry
plane, see geometry.py) and the block's heading there (heading_deg from the yard +x axis toward +y), from
stock_from until stock_until (open-ended until the erection lift when empty). A block may move between yards, so
it can have several rows, but their periods must not overlap. The slot centre must lie inside the facility
footprint when the facility has one (location_geometry size_x/y_m).

The digital twin reads GET /api/block_stock_plans (all hulls, or ?hull= for one) next to erection_timeline: a
block is in the yard on day d when a row covers d, and its erection lift starts from that slot.
"""

import math

SCHEMA = '''
CREATE TABLE IF NOT EXISTS block_stock_plans(id TEXT PRIMARY KEY, block TEXT NOT NULL REFERENCES blocks(id),
 location TEXT NOT NULL REFERENCES locations(id), x REAL NOT NULL, y REAL NOT NULL, heading_deg REAL NOT NULL,
 stock_from TEXT NOT NULL, stock_until TEXT, note TEXT);
'''
FIELDS = {
    'block_stock_plans': ('id', 'block', 'location', 'x', 'y', 'heading_deg', 'stock_from', 'stock_until', 'note'),
}
PREFIXES = {'block_stock_plans': 'STK'}
OPTIONAL = {'block_stock_plans': {'stock_until', 'note'}}
DATES = {'stock_from', 'stock_until'}
NUMBERS = {'heading_deg'}
MEASURES = {'heading_deg'}
SIGNED = set()
CHOICES = {}
READS = set(FIELDS)
STOCK_KINDS = {'야드'}


def inside_footprint(geometry, x, y):
    """True when (x, y) lies in the facility rectangle (centre x/y, size_x/y_m along its heading)."""
    if not geometry or not geometry['size_x_m'] or not geometry['size_y_m']:
        return True
    angle = math.radians(geometry['heading_deg'])
    dx, dy = x - geometry['x'], y - geometry['y']
    along = dx * math.cos(angle) + dy * math.sin(angle)
    across = -dx * math.sin(angle) + dy * math.cos(angle)
    return abs(along) <= geometry['size_x_m'] / 2 + 1e-6 and abs(across) <= geometry['size_y_m'] / 2 + 1e-6


def write(db, action, v, api):
    require, lookup = api.require, api.lookup
    lookup(db, 'blocks', v['block'])
    location = lookup(db, 'locations', v['location'])
    require(location['kind'] in STOCK_KINDS, '적치 시설은 야드 유형으로 지정하세요.')
    require(0 <= v['heading_deg'] < 360, '방향은 0 이상 360 미만의 각도(도)로 입력하세요.')
    require(not v['stock_until'] or v['stock_from'] <= v['stock_until'], '적치 종료일은 시작일 이후여야 합니다.')
    geometry = db.execute('SELECT * FROM location_geometry WHERE location=?', (v['location'],)).fetchone()
    require(inside_footprint(geometry and dict(geometry), v['x'], v['y']),
            f'적치 좌표 ({v["x"]}, {v["y"]})가 {v["location"]} 시설 범위 밖입니다.')
    end = v['stock_until'] or '9999-12-31'
    for row in db.execute('SELECT id, stock_from, stock_until FROM block_stock_plans WHERE block=? AND id<>?',
                          (v['block'], v['id'])):
        other_end = row['stock_until'] or '9999-12-31'
        require(not (v['stock_from'] <= other_end and row['stock_from'] <= end),
                f'{row["id"]} 적치 계획과 기간이 겹칩니다. 한 블록은 한 번에 한 곳에만 적치합니다.')
    keys = [key for key in FIELDS[action]]
    updates = ','.join(f'{key}=excluded.{key}' for key in keys if key != 'id')
    db.execute(f'INSERT INTO {action} ({",".join(keys)}) VALUES ({",".join("?" for _ in keys)}) '
               f'ON CONFLICT(id) DO UPDATE SET {updates}', tuple(v[key] for key in keys))
    return v['id']


def read(db, table):
    if table == 'block_stock_plans':
        return [dict(row) for row in db.execute(
            'SELECT s.*, b.hull FROM block_stock_plans s JOIN blocks b ON b.id=s.block ORDER BY b.hull, s.stock_from, s.block')]
    return None
