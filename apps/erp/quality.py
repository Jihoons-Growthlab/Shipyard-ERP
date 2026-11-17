"""Inspection & test plan (owner/class witness), non-conformance reports and punch lists."""
from datetime import date

CHOICES = {
    'witness': ['사내', '선주', '선급', '선주·선급'],
    'disposition': ['재작업', '수리', '현상 사용', '폐기'],
    'category': ['A', 'B', 'C'],
    'raised_by': ['사내', '선주', '선급'],
}
SCHEMA = '''
CREATE TABLE IF NOT EXISTS inspection_points(id TEXT PRIMARY KEY, hull TEXT NOT NULL REFERENCES hulls(id),
 block TEXT REFERENCES blocks(id), activity_ref TEXT REFERENCES activities(id), item TEXT NOT NULL,
 witness TEXT NOT NULL, planned_day TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS inspection_records(id TEXT PRIMARY KEY, point TEXT NOT NULL REFERENCES inspection_points(id),
 inspect_day TEXT NOT NULL, inspection TEXT NOT NULL, inspector TEXT NOT NULL, remarks TEXT, recorded_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS ncrs(id TEXT PRIMARY KEY, hull TEXT NOT NULL REFERENCES hulls(id),
 block TEXT REFERENCES blocks(id), point TEXT REFERENCES inspection_points(id), description TEXT NOT NULL,
 cause TEXT, disposition TEXT NOT NULL, raised_day TEXT NOT NULL, closed_day TEXT, close_evidence TEXT);
CREATE TABLE IF NOT EXISTS punch_items(id TEXT PRIMARY KEY, hull TEXT NOT NULL REFERENCES hulls(id),
 block TEXT REFERENCES blocks(id), category TEXT NOT NULL, description TEXT NOT NULL, raised_by TEXT NOT NULL,
 raised_day TEXT NOT NULL, closed_day TEXT, close_evidence TEXT);
'''
FIELDS = {
    'inspection_points': ('id', 'hull', 'block', 'activity_ref', 'item', 'witness', 'planned_day'),
    'inspection_records': ('id', 'point', 'inspect_day', 'inspection', 'inspector', 'remarks'),
    'ncrs': ('id', 'hull', 'block', 'point', 'description', 'cause', 'disposition', 'raised_day'),
    'ncr_close': ('id', 'closed_day', 'evidence'),
    'punch_items': ('id', 'hull', 'block', 'category', 'description', 'raised_by', 'raised_day'),
    'punch_close': ('id', 'closed_day', 'evidence'),
}
PREFIXES = {'inspection_points': 'ITP', 'inspection_records': 'IR', 'ncrs': 'NCR', 'punch_items': 'PCH'}
OPTIONAL = {'inspection_points': {'activity_ref'}, 'inspection_records': {'remarks'}, 'ncrs': {'point', 'cause'}}
DATES = {'planned_day', 'inspect_day', 'raised_day', 'closed_day'}
NUMBERS = set()
READS = (set(FIELDS) - {'ncr_close', 'punch_close'})


def latest(db, point):
    return db.execute('SELECT * FROM inspection_records WHERE point=? ORDER BY recorded_at DESC, id DESC LIMIT 1',
                      (point,)).fetchone()


def check_hull(db, lookup, require, v):
    if v.get('block'):
        require(lookup(db, 'blocks', v['block'])['hull'] == v['hull'], '호선과 블록이 일치해야 합니다.')
    if v.get('activity_ref'):
        require(lookup(db, 'activities', v['activity_ref'])['hull'] == v['hull'], '호선과 공정이 일치해야 합니다.')
    if v.get('point'):
        require(lookup(db, 'inspection_points', v['point'])['hull'] == v['hull'], '호선과 검사 항목이 일치해야 합니다.')


def close(db, table, v, lookup, require):
    row = lookup(db, table, v['id'])
    require(not row['closed_day'], '이미 종결된 항목입니다.')
    require(row['raised_day'] <= v['closed_day'] <= date.today().isoformat(), '발행일 이후, 오늘 이전 날짜로 종결하세요.')
    return row


def write(db, action, v, api):
    require, lookup, insert = api.require, api.lookup, api.insert
    today = date.today().isoformat()
    for key, values in CHOICES.items():
        if key in v:
            require(v[key] in values, f'{key} 값을 목록에서 선택하세요.')
    if action in {'inspection_points', 'ncrs', 'punch_items'}:
        check_hull(db, lookup, require, v)
    if action in {'ncrs', 'punch_items'}:
        require(v['raised_day'] <= today, '발행일은 오늘 이후일 수 없습니다.')
    if action == 'inspection_records':
        point = lookup(db, 'inspection_points', v['point'])
        last = latest(db, v['point'])
        require(not last or last['inspection'] != '합격', '이미 합격한 검사 항목입니다.')
        require(v['inspection'] in {'합격', '불합격'}, '검사 결과를 선택하세요.')
        require(v['inspect_day'] <= today, '검사일은 오늘 이후일 수 없습니다.')
        require(not last or v['inspect_day'] >= last['inspect_day'], '이전 검사일 이후 날짜를 입력하세요.')
        v['recorded_at'] = api.now()
        key = insert(db, action, **v)
        if v['inspection'] == '불합격':
            # A failed witness inspection always opens a non-conformance report for rework.
            insert(db, 'ncrs', id=api.traceability.document_number(db, 'ncrs'), hull=point['hull'], block=point['block'],
                   point=point['id'], description=v['remarks'] or f'{point["item"]} 검사 불합격 ({key})', cause=None,
                   disposition='재작업', raised_day=v['inspect_day'], closed_day=None, close_evidence=None)
        return key
    if action == 'ncr_close':
        ncr = close(db, 'ncrs', v, lookup, require)
        if ncr['point']:
            last = latest(db, ncr['point'])
            require(last and last['inspection'] == '합격' and last['inspect_day'] >= ncr['raised_day'],
                    '연결된 검사 항목의 재검사 합격 후 종결하세요.')
        db.execute('UPDATE ncrs SET closed_day=?, close_evidence=? WHERE id=?', (v['closed_day'], v['evidence'], v['id']))
        return v['id']
    if action == 'punch_close':
        close(db, 'punch_items', v, lookup, require)
        db.execute('UPDATE punch_items SET closed_day=?, close_evidence=? WHERE id=?',
                   (v['closed_day'], v['evidence'], v['id']))
        return v['id']
    return insert(db, action, **v)


def open_points(db, column, key):
    count = 0
    for point in db.execute(f'SELECT id FROM inspection_points WHERE {column}=?', (key,)):
        last = latest(db, point['id'])
        count += not last or last['inspection'] != '합격'
    return count


def blockers(db, block, plan):
    out = []
    ncr = db.execute('SELECT COUNT(*) FROM ncrs WHERE block=? AND closed_day IS NULL', (block['id'],)).fetchone()[0]
    if ncr:
        out.append(f'미결 NCR {ncr}건')
    points = open_points(db, 'block', block['id'])
    if points:
        out.append(f'ITP 검사 미합격 {points}건')
    return out


def milestone_gate(db, hull, milestone, require):
    if milestone == '인도':
        ncr = db.execute('SELECT COUNT(*) FROM ncrs WHERE hull=? AND closed_day IS NULL', (hull,)).fetchone()[0]
        require(not ncr, f'인도 전 미결 NCR {ncr}건을 종결하세요.')
        punch = db.execute("""SELECT COUNT(*) FROM punch_items WHERE hull=? AND closed_day IS NULL
            AND category IN ('A','B')""", (hull,)).fetchone()[0]
        require(not punch, f'인도 전 A·B 등급 미결 펀치 {punch}건을 종결하세요.')
        points = open_points(db, 'hull', hull)
        require(not points, f'인도 전 미합격 ITP 검사 {points}건이 있습니다.')
    if milestone == '시운전':
        punch = db.execute("SELECT COUNT(*) FROM punch_items WHERE hull=? AND closed_day IS NULL AND category='A'",
                           (hull,)).fetchone()[0]
        require(not punch, f'시운전 전 A 등급 미결 펀치 {punch}건을 종결하세요.')


def read(db, table):
    if table == 'inspection_points':
        rows = []
        for row in db.execute('SELECT * FROM inspection_points ORDER BY planned_day, id'):
            last = latest(db, row['id'])
            rows.append({**dict(row), 'inspection': last['inspection'] if last else None,
                         'last_record': last['id'] if last else None})
        return rows
    return None
