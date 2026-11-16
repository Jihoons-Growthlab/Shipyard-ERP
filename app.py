import argparse
import json
import math
import os
import sqlite3
from contextlib import closing
from datetime import date, datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit

import governance
import lots
import planning
import procurement
import production
import quality
import resources
import storage
import traceability
import shipbuilding
import sys
from urllib.parse import unquote

ROOT = Path(__file__).resolve().parent
DATABASE = ROOT / 'shipyard.sqlite3'
DATABASE_URL = None
STAGES = ['설계·구매', '가공·조립', '선행의장·도장', '선탑재·탑재', '진수·안벽의장', '시운전·인도']
SCHEMA = '''
CREATE TABLE IF NOT EXISTS hulls(id TEXT PRIMARY KEY, name TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS locations(id TEXT PRIMARY KEY, name TEXT NOT NULL, kind TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS blocks(id TEXT PRIMARY KEY, hull TEXT NOT NULL REFERENCES hulls(id),
 location TEXT REFERENCES locations(id));
CREATE TABLE IF NOT EXISTS equipment(id TEXT PRIMARY KEY, name TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS activities(id TEXT PRIMARY KEY, hull TEXT NOT NULL REFERENCES hulls(id),
 block TEXT REFERENCES blocks(id), stage TEXT NOT NULL, baseline TEXT NOT NULL,
 forecast TEXT NOT NULL, actual TEXT, reason TEXT NOT NULL, actual_reason TEXT, inspection TEXT);
CREATE TABLE IF NOT EXISTS plans(id INTEGER PRIMARY KEY, start TEXT NOT NULL,
 end_exclusive TEXT NOT NULL, issued_at TEXT NOT NULL, snapshot TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS orders(id TEXT PRIMARY KEY, activity TEXT NOT NULL REFERENCES activities(id),
 cadence TEXT NOT NULL, day TEXT NOT NULL, instruction TEXT NOT NULL,
 parent TEXT REFERENCES orders(id), actual TEXT, result TEXT, inspection TEXT);
CREATE TABLE IF NOT EXISTS moves(id TEXT PRIMARY KEY, block TEXT NOT NULL REFERENCES blocks(id),
 equipment TEXT NOT NULL REFERENCES equipment(id), origin TEXT REFERENCES locations(id),
 destination TEXT NOT NULL REFERENCES locations(id), started_at TEXT NOT NULL, confirmed_at TEXT);
CREATE UNIQUE INDEX IF NOT EXISTS one_active_block ON moves(block) WHERE confirmed_at IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS one_active_equipment ON moves(equipment) WHERE confirmed_at IS NULL;
CREATE TABLE IF NOT EXISTS telemetry(id INTEGER PRIMARY KEY, move TEXT NOT NULL REFERENCES moves(id),
 recorded_at TEXT NOT NULL, x REAL NOT NULL, y REAL NOT NULL, load REAL NOT NULL, status TEXT NOT NULL);
'''
FIELDS = {
    'hulls': ('id', 'name'),
    'locations': ('id', 'name', 'kind'),
    'blocks': ('id', 'hull', 'location'),
    'equipment': ('id', 'name'),
    'activities': ('id', 'hull', 'block', 'stage', 'baseline', 'forecast', 'reason'),
    'forecast': ('id', 'forecast', 'reason'),
    'plans': ('start',),
    'orders': ('id', 'activity', 'cadence', 'day', 'instruction', 'parent', 'plan_id'),
    'results': ('id', 'actual', 'result', 'inspection', 'reason'),
    'moves': ('id', 'block', 'equipment', 'destination'),
    'confirm': ('id',),
    'telemetry': ('move', 'x', 'y', 'load', 'status'),
}
FIELDS.update(shipbuilding.FIELDS)
traceability.PREFIXES.update(shipbuilding.PREFIXES)
FIELDS.update(planning.FIELDS)
FIELDS.update(procurement.FIELDS)
SCHEMA += shipbuilding.SCHEMA + planning.SCHEMA + procurement.SCHEMA + traceability.SCHEMA
READS = shipbuilding.READS | {'inventory_ledger', 'production_results'} | set(procurement.FIELDS) | set(planning.FIELDS) | {'mrp', 'aps'} | {'hulls', 'locations', 'blocks', 'equipment', 'activities', 'plans', 'orders', 'moves', 'telemetry'}
# Shipyard extensions: assembly stages/man-hours, crane & dock constraints, steel lots, ITP/NCR/punch, roles & audit.
EXTENSIONS = (production, resources, lots, quality, governance)
OPTIONAL = {'block', 'location', 'parent', 'reason', 'predecessor', 'plan_id'}
ACTION_OPTIONAL = {}
DATE_KEYS = {'baseline', 'forecast', 'start', 'day', 'actual', 'delivery', 'receipt_day', 'need_day', 'earliest',
             'promised_day'}
CHOICES = {}
for module in EXTENSIONS:
    SCHEMA += module.SCHEMA
    FIELDS.update(module.FIELDS)
    traceability.PREFIXES.update(module.PREFIXES)
    READS |= module.READS
    ACTION_OPTIONAL.update(module.OPTIONAL)
    DATE_KEYS |= module.DATES
    planning.NUMBERS.update(module.NUMBERS)
    planning.MEASURES.update(getattr(module, 'MEASURES', set()))
    CHOICES.update(module.CHOICES)
shipbuilding.EXTRA_BLOCKERS[:] = [production.blockers, resources.blockers, quality.blockers]


def connect():
    if DATABASE_URL:
        return storage.Postgres(DATABASE_URL)
    db = sqlite3.connect(DATABASE)
    db.row_factory = sqlite3.Row
    db.execute('PRAGMA foreign_keys = ON')
    return db


def now():
    return datetime.now(timezone.utc).isoformat()


def day(value):
    parsed = date.fromisoformat(value)
    if parsed.isoformat() != value:
        raise ValueError('날짜는 YYYY-MM-DD 형식이어야 합니다.')
    return parsed


def require(value, message):
    if not value:
        raise ValueError(message)


def lookup(db, table, key):
    row = db.execute(f'SELECT * FROM {table} WHERE id = ?', (key,)).fetchone()
    require(row, f'{table}에서 ID {key}을(를) 찾을 수 없습니다.')
    return row


def insert(db, table, **values):
    params = ','.join('?' for _ in values)
    query = f'INSERT INTO {table} ({",".join(values)}) VALUES ({params})'
    if table in {'plans', 'telemetry'} and 'id' not in values:
        return db.execute(query + ' RETURNING id', tuple(values.values())).fetchone()['id']
    db.execute(query, tuple(values.values()))
    key = {'operations': 'activity', 'center_locations': 'center', 'holidays': 'holiday'}.get(table, 'id')
    return values.get(key)


def activity_view(row):
    item = dict(row)
    baseline = day(item['baseline'])
    item['forecast_variance_days'] = (day(item['forecast']) - baseline).days
    item['actual_variance_days'] = (day(item['actual']) - baseline).days if item['actual'] else None
    return item


def read(db, table):
    if table == "block_readiness":
        return shipbuilding.readiness(db)
    for module in EXTENSIONS:
        if table in module.READS:
            rows = module.read(db, table)
            if rows is not None:
                return rows
    queries = {
        'orders': 'SELECT o.*, p.plan_id FROM orders o LEFT JOIN order_plans p ON p.work_order=o.id ORDER BY o.id',
        'goods_receipts': 'SELECT g.*, l.location FROM goods_receipts g LEFT JOIN receipt_locations l ON l.receipt=g.id ORDER BY g.id',
        'issues': 'SELECT i.*, l.source, l.destination FROM issues i LEFT JOIN issue_locations l ON l.issue=i.id ORDER BY i.id',
        'center_locations': 'SELECT * FROM center_locations ORDER BY center',
    }
    if table in queries:
        return [dict(row) for row in db.execute(queries[table])]
    if table == 'purchase_orders':
        return [dict(row) for row in db.execute(procurement.OPEN_ORDERS + ' ORDER BY p.id')]
    if table in ('mrp', 'aps'):
        return getattr(planning, table)(db)
    if table in ('vessel_details', 'block_details', 'operations'):
        key = {'vessel_details': 'hull', 'block_details': 'block', 'operations': 'activity'}[table]
        return [dict(row) for row in db.execute(f'SELECT * FROM {table} ORDER BY {key}')]
    if table == 'blocks':
        query = '''SELECT b.*, m.id AS active_move, CASE WHEN m.id IS NOT NULL THEN 'in_transit'
            WHEN b.location IS NOT NULL THEN 'confirmed' ELSE 'unknown' END AS position_status
            FROM blocks b LEFT JOIN moves m ON m.block = b.id AND m.confirmed_at IS NULL ORDER BY b.id'''
    else:
        query = f'SELECT * FROM {table} ORDER BY id'
    rows = [dict(row) for row in db.execute(query)]
    if table == 'activities':
        return [activity_view(row) for row in rows]
    if table == 'plans':
        for row in rows:
            row['snapshot'] = json.loads(row['snapshot'])
            row['document_no'] = f"PLAN-{row['id']:06d}" 
    return rows


def write(db, action, data):
    fields = FIELDS[action]
    require(isinstance(data, dict) and not (data.keys() - set(fields)), '지원하지 않는 입력 항목입니다.')
    optional = OPTIONAL | ACTION_OPTIONAL.get(action, set())
    values = {}
    for key in fields:
        value = data.get(key)
        if key == 'actual' and action == 'milestone_update' and not value:
            values[key] = None
            continue
        if key == 'id' and not value and action in traceability.PREFIXES:
            value = traceability.document_number(db, action)
        if key in planning.NUMBERS:
            require(type(value) in (str, int, float), f'{key} 수치가 필요합니다.')
        elif key in {'x', 'y', 'load'}:
            require(type(value) in (str, int, float), f'{key} 수치가 필요합니다.')
            value = float(value)
            require(math.isfinite(value), f'{key}에는 유한한 수치를 입력하세요.')
        else:
            require(value is None or isinstance(value, str), f'{key}에는 문자열을 입력하세요.')
            value = (value or '').strip()
            require(len(value) <= 1000 and '\x00' not in value, f'{key} 입력이 유효하지 않습니다.')
            require(value or key in optional, f'{key} 값을 입력하세요.')
            if key in DATE_KEYS and value:
                day(value)
            if not value and key != 'reason':
                value = None
        values[key] = value
    v = values
    if action == 'milestone_update' and v['actual']:
        milestone = lookup(db, 'hull_milestones', v['id'])
        if milestone['actual'] != v['actual']:
            production.milestone_gate(db, milestone['hull'], milestone['milestone'], require)
            quality.milestone_gate(db, milestone['hull'], milestone['milestone'], require)
    for module in EXTENSIONS:
        if action in module.FIELDS:
            planning.validate(db, action, v)
            return module.write(db, action, v, sys.modules[__name__])
    if action in shipbuilding.FIELDS:
        return shipbuilding.write(db, action, v, sys.modules[__name__])
    if action in procurement.FIELDS:
        planning.validate(db, action, v)
        if action in {'goods_receipts', 'issues'}:
            require(day(v['day']) <= date.today(), '실제 자재 거래일은 오늘 이후일 수 없습니다.')
        if action in {'goods_receipts', 'warehouse_stock', 'center_locations'}:
            require(v['location'], '시설을 선택하세요.')
        procurement.apply(db, action, v, lookup, require)
        link = procurement.transfer(db, action, v, lookup, require)
        key = insert(db, action, **v)
        if link:
            table, values = link
            insert(db, table, **values)
        if action == 'warehouse_stock':
            traceability.ledger(db, insert, now(), v['material'], v['location'], date.today().isoformat(),
                                '기초 배정', key, v['qty'])
        if action == 'goods_receipts' and v['inspection'] == '합격':
            order = db.execute(f'SELECT material FROM ({procurement.OPEN_ORDERS}) WHERE id=?',
                               (v['purchase_order'],)).fetchone()
            traceability.ledger(db, insert, now(), order['material'], link[1]['location'], v['day'],
                                '입고', key, v['qty'])
        if action == 'issues':
            material = lookup(db, 'requirements', v['requirement'])['material']
            traceability.ledger(db, insert, now(), material, link[1]['source'], v['day'], '출고', key, -v['qty'])
        return key
    if action in planning.FIELDS:
        planning.validate(db, action, v)
        key = {'vessel_details': 'hull', 'block_details': 'block'}.get(action)
        if key:
            columns = ','.join(v)
            placeholders = ','.join('?' for _ in v)
            updates = ','.join(f'{column}=excluded.{column}' for column in v if column != key)
            db.execute(f'INSERT INTO {action} ({columns}) VALUES ({placeholders}) '
                       f'ON CONFLICT({key}) DO UPDATE SET {updates}', tuple(v.values()))
            return v[key]
    if action == 'locations':
        require(v['kind'] in {'야드', '작업장', '도크', '안벽', '창고'}, '시설 유형을 선택하세요.')
    if action == 'activities':
        require(v['stage'] in STAGES, '공정을 선택하세요.')
        if v['block']:
            require(lookup(db, 'blocks', v['block'])['hull'] == v['hull'], '호선과 블록이 일치해야 합니다.')
        require(v['forecast'] <= v['baseline'] or v['reason'], '지연 원인을 입력하세요.')
    if action == 'forecast':
        activity = lookup(db, 'activities', v['id'])
        require(not activity['actual'], '완료한 공정의 예정일은 변경할 수 없습니다.')
        require(v['forecast'] <= activity['baseline'] or v['reason'], '지연 원인을 입력하세요.')
        db.execute('UPDATE activities SET forecast=?, reason=? WHERE id=?', (v['forecast'], v['reason'], v['id']))
        return v['id']
    if action == 'plans':
        start = day(v['start'])
        require(start.weekday() == 0, '계획 시작일은 월요일이어야 합니다.')
        end = (start + timedelta(weeks=12)).isoformat()
        rows = db.execute('''SELECT * FROM activities
            WHERE (baseline >= ? AND baseline < ?) OR (forecast >= ? AND forecast < ?)
            ORDER BY forecast, id''', (v['start'], end, v['start'], end))
        plan_id = insert(db, 'plans', start=v['start'], end_exclusive=end, issued_at=now(),
                      snapshot=json.dumps([activity_view(row) for row in rows], ensure_ascii=False))
        return f'PLAN-{plan_id:06d}'
    if action == 'orders':
        plan_value = v.pop('plan_id')
        plan_id = None
        if plan_value:
            require(plan_value.isascii() and plan_value.isdigit() and len(plan_value) <= 18, '발행 계획 번호를 선택하세요.')
            plan_id = int(plan_value)
            plan = lookup(db, 'plans', plan_id)
            require(plan['start'] <= v['day'] < plan['end_exclusive'] and
                    any(row['id'] == v['activity'] for row in json.loads(plan['snapshot'])),
                    '선택한 발행 계획에 작업일과 공정이 포함되어야 합니다.')
        activity = lookup(db, 'activities', v['activity'])
        require(not activity['actual'], '완료한 공정에 작업지시를 추가할 수 없습니다.')
        require(v['cadence'] in {'주간', '일일'}, '주간 또는 일일을 선택하세요.')
        start = day(v['day'])
        if v['cadence'] == '주간':
            require(start.weekday() == 0 and not v['parent'], '주간 지시는 월요일로 지정하고 상위 지시를 비워 주세요.')
        else:
            parent = lookup(db, 'orders', v['parent'])
            offset = (start - day(parent['day'])).days
            require(parent['cadence'] == '주간' and not parent['actual'] and
                    parent['activity'] == v['activity'] and 0 <= offset < 7,
                    '같은 공정의 미완료 주간 지시와 해당 주의 날짜가 필요합니다.')
            parent_plan = db.execute('SELECT plan_id FROM order_plans WHERE work_order=?',
                                     (parent['id'],)).fetchone()
            if parent_plan:
                require(plan_id is None or plan_id == parent_plan['plan_id'], '상위 작업지시의 발행 계획과 일치해야 합니다.')
                plan_id = parent_plan['plan_id']
        key = insert(db, 'orders', **v)
        if plan_id is not None:
            insert(db, 'order_plans', work_order=key, plan_id=plan_id)
        return key
    if action == 'results':
        order = lookup(db, 'orders', v['id'])
        require(order['inspection'] != '합격', '이미 합격한 작업지시입니다.')
        require(v['inspection'] in {'합격', '불합격'}, '검사 결과를 선택하세요.')
        children = list(db.execute('SELECT actual, inspection FROM orders WHERE parent=?', (v['id'],)))
        if v['inspection'] == '합격':
            procurement.require_issued(db, order, v['actual'], require)
            require(all(row['actual'] and row['inspection'] == '합격' and row['actual'] <= v['actual']
                        for row in children), '하위 작업의 합격 실적과 실제일을 확인하세요.')
        db.execute('UPDATE orders SET actual=?, result=?, inspection=? WHERE id=?',
                   (v['actual'], v['result'], v['inspection'], v['id']))
        if order['cadence'] == '주간':
            activity = lookup(db, 'activities', order['activity'])
            reason = v['reason'] or activity['actual_reason'] or activity['reason']
            require(v['actual'] <= activity['baseline'] or reason, '실제일 지연 원인을 입력하세요.')
            if reason:
                db.execute('UPDATE activities SET actual_reason=? WHERE id=?', (reason, order['activity']))
            remaining = db.execute('''SELECT COUNT(*) FROM orders WHERE activity=? AND cadence='주간'
                AND (inspection IS NULL OR inspection <> '합격')''', (order['activity'],)).fetchone()[0]
            db.execute("""UPDATE activities SET actual=(SELECT MAX(actual) FROM orders
                WHERE activity=? AND cadence='주간'), inspection='합격' WHERE id=? AND ?=0""",
                       (order['activity'], order['activity'], remaining))
            if v['inspection'] == '불합격':
                db.execute("UPDATE activities SET inspection='불합격' WHERE id=?", (order['activity'],))
        number = traceability.document_number(db, 'production_results')
        return insert(db, 'production_results', id=number, work_order=v['id'], actual=v['actual'],
                      result=v['result'], inspection=v['inspection'], reason=v['reason'], recorded_at=now())
    if action == 'moves':
        block = lookup(db, 'blocks', v['block'])
        require(block['location'] != v['destination'], '출발지와 목적지가 같을 수 없습니다.')
        resources.check_move(db, v['block'], v['equipment'], require)
        v.update(origin=block['location'], started_at=now())
    if action == 'confirm':
        move = lookup(db, 'moves', v['id'])
        require(not move['confirmed_at'], '이미 확정된 이동입니다.')
        db.execute('UPDATE moves SET confirmed_at=? WHERE id=?', (now(), v['id']))
        db.execute('UPDATE blocks SET location=? WHERE id=?', (move['destination'], move['block']))
        return v['id']
    if action == 'telemetry':
        lookup(db, 'moves', v['move'])
        require(v['load'] >= 0, '하중은 0 이상이어야 합니다.')
        v['recorded_at'] = now()
    return insert(db, action, **v)


def seed(db):
    if db.execute("SELECT 1 FROM hulls").fetchone():
        return
    def add(action, **values):
        return write(db, action, values)
    add('hulls', id='H101', name='상선 신조 101호선')
    add('locations', id='SHOP', name='블록 조립장', kind='작업장')
    add('locations', id='STORE', name='후판 자재창고', kind='창고')
    add('locations', id='YARD', name='블록 적치 야드', kind='야드')
    add('locations', id='DOCK', name='1도크', kind='도크')
    add('locations', id='QUAY', name='1안벽', kind='안벽')
    add('blocks', id='B101', hull='H101', location='SHOP')
    add('blocks', id='B102', hull='H101')
    add('equipment', id='CR1', name='갠트리 크레인 1호')
    start = date.today() - timedelta(days=date.today().weekday())
    for index, stage in enumerate(STAGES):
        baseline = start + timedelta(weeks=index * 2)
        delayed = index == 2
        add('activities', id=f'A{index + 1}', hull='H101', block='B101' if 0 < index < 4 else '',
            stage=stage, baseline=baseline.isoformat(), forecast=(baseline + timedelta(days=3 * delayed)).isoformat(),
            reason='도장 자재 입고 지연' if delayed else '')
    add('vessel_details', hull='H101', vessel_type='LNG 운반선', vessel_name='모의 101호',
        customer='모의 선주', length_m=299, beam_m=46, delivery=(start + timedelta(weeks=11)).isoformat())
    add('block_details', block='B101', zone='화물창 구역', assembly='대조립', weight_t=120, drawing='D-B101-R01')
    add('suppliers', id='SUP-STEEL', name='모의 후판 공급사')
    add('materials', id='PLATE', name='선체용 강판', unit='장', stock=20, lead_days=14)
    add('requirements', id='REQ1', activity='A2', material='PLATE', qty=25,
        need_day=(start + timedelta(weeks=2)).isoformat())
    add('warehouse_stock', id='OPEN-PLATE', location='STORE', material='PLATE', qty=20)
    add('centers', id='C1', name='블록 조립장', hours=8)
    add('center_locations', center='C1', location='SHOP')
    add('operations', activity='A2', center='C1', duration=16, earliest=(start + timedelta(weeks=2)).isoformat())
    add('operations', activity='A3', center='C1', duration=12, predecessor='A2',
        earliest=(start + timedelta(weeks=4)).isoformat())
    add('plans', start=start.isoformat())
    add('orders', id='W1', activity='A1', cadence='주간', day=start.isoformat(), instruction='설계 승인 및 자재 명세 확인')
    add('orders', id='D1', activity='A1', cadence='일일', day=start.isoformat(), instruction='자재 명세 확인', parent='W1')
    add('results', id='D1', actual=start.isoformat(), result='자재 명세 확인 완료', inspection='합격')
    add('moves', id='M1', block='B101', equipment='CR1', destination='DOCK')
    add('telemetry', move='M1', x=24, y=18, load=120, status='이송 중')


class Handler(BaseHTTPRequestHandler):
    def send(self, status, payload, content_type='application/json'):
        body = payload if isinstance(payload, bytes) else json.dumps(payload, ensure_ascii=False).encode()
        self.send_response(status)
        self.send_header('Content-Type', content_type + '; charset=utf-8')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        path = urlsplit(self.path).path
        if path == '/':
            self.send(200, (ROOT / 'index.html').read_bytes(), 'text/html')
            return
        if not path.startswith('/api/'):
            self.send(404, {'error': '주소를 찾을 수 없습니다.'})
            return
        table = path[5:]
        if table == 'meta':
            self.send(200, {'fields': FIELDS, 'stages': STAGES, 'numbered': list(traceability.PREFIXES),
                            'gates': shipbuilding.GATES, 'milestones': shipbuilding.MILESTONES,
                            'optional': {key: sorted(value) for key, value in ACTION_OPTIONAL.items()},
                            'dates': sorted(DATE_KEYS), 'measures': sorted(planning.MEASURES),
                            'integers': sorted(planning.NUMBERS - planning.MEASURES), 'choices': CHOICES})
        elif table in READS:
            with closing(connect()) as db, db:
                storage.begin_read(db)
                payload = read(db, table)
            self.send(200, payload)
        else:
            self.send(404, {'error': '조회 항목을 찾을 수 없습니다.'})

    def do_POST(self):
        action = urlsplit(self.path).path
        if not action.startswith('/api/') or action[5:] not in FIELDS:
            self.send(404, {'error': '입력 항목을 찾을 수 없습니다.'})
            return
        try:
            require(self.headers.get_content_type() == 'application/json', 'JSON 요청이 필요합니다.')
            length = int(self.headers.get('Content-Length', '0'))
            require(0 < length <= 16384, '요청 크기를 확인하세요.')
            data = json.loads(self.rfile.read(length))
            user = unquote(self.headers.get('X-ShipYard-User', '')).strip()
            with closing(connect()) as db, db:
                storage.begin_write(db)
                user = governance.authorize(db, user, action[5:])
                key = write(db, action[5:], data)
                governance.audit(db, sys.modules[__name__], user, action[5:], key, data)
        except PermissionError as exc:
            self.send(403, {'error': str(exc)})
        except (ValueError, OverflowError) as exc:
            self.send(400, {'error': str(exc)})
        except storage.INTEGRITY_ERRORS:
            self.send(409, {'error': 'ID 중복, 진행 중인 이동 또는 기준정보 참조를 확인하세요.'})
        except storage.DATABASE_ERRORS as exc:
            self.log_error('%s', exc)
            self.send(503, {'error': '저장소를 사용할 수 없습니다. 잠시 후 다시 시도하세요.'})
        else:
            self.send(201, {'id': key})


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--port', type=int, default=8001)
    parser.add_argument('--db', type=Path, help='명시하면 SQLite 파일로 실행')
    parser.add_argument('--empty', action='store_true')
    args = parser.parse_args()
    if args.db:
        DATABASE = args.db
    else:
        DATABASE_URL = os.environ.get('SHIPYARD_DATABASE_URL')
    with closing(connect()) as db, db:
        storage.init_schema(db, SCHEMA)
        if not DATABASE_URL:
            storage.begin_write(db)
        traceability.upgrade(db, insert, now())
        if not args.empty:
            seed(db)
    with ThreadingHTTPServer(('127.0.0.1', args.port), Handler) as server:
        print(f'http://127.0.0.1:{server.server_port}', flush=True)
        server.serve_forever()
