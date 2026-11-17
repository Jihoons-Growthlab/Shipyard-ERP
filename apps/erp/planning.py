from datetime import date, timedelta
from decimal import Decimal, InvalidOperation

import procurement

SCHEMA = '''
CREATE TABLE IF NOT EXISTS vessel_details(hull TEXT PRIMARY KEY REFERENCES hulls(id),
 vessel_type TEXT NOT NULL, vessel_name TEXT NOT NULL, customer TEXT NOT NULL,
 length_m REAL NOT NULL, beam_m REAL NOT NULL, delivery TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS block_details(block TEXT PRIMARY KEY REFERENCES blocks(id),
 zone TEXT NOT NULL, assembly TEXT NOT NULL, weight_t REAL NOT NULL, drawing TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS materials(id TEXT PRIMARY KEY, name TEXT NOT NULL, unit TEXT NOT NULL,
 stock INTEGER NOT NULL, lead_days INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS receipts(id TEXT PRIMARY KEY, material TEXT NOT NULL REFERENCES materials(id),
 qty INTEGER NOT NULL, receipt_day TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS requirements(id TEXT PRIMARY KEY, activity TEXT NOT NULL REFERENCES activities(id),
 material TEXT NOT NULL REFERENCES materials(id), qty INTEGER NOT NULL, need_day TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS centers(id TEXT PRIMARY KEY, name TEXT NOT NULL, hours INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS operations(activity TEXT PRIMARY KEY REFERENCES activities(id),
 center TEXT NOT NULL REFERENCES centers(id), duration INTEGER NOT NULL, predecessor TEXT REFERENCES operations(activity),
 earliest TEXT NOT NULL);
'''
FIELDS = {
    'vessel_details': ('hull', 'vessel_type', 'vessel_name', 'customer', 'length_m', 'beam_m', 'delivery'),
    'block_details': ('block', 'zone', 'assembly', 'weight_t', 'drawing'),
    'materials': ('id', 'name', 'unit', 'stock', 'lead_days'),
    'receipts': ('id', 'material', 'qty', 'receipt_day'),
    'requirements': ('id', 'activity', 'material', 'qty', 'need_day'),
    'centers': ('id', 'name', 'hours'),
    'operations': ('activity', 'center', 'duration', 'predecessor', 'earliest'),
}
NUMBERS = {'length_m', 'beam_m', 'weight_t', 'stock', 'lead_days', 'qty', 'hours', 'duration'}
MEASURES = {'length_m', 'beam_m', 'weight_t'}
NONNEGATIVE = {'stock', 'lead_days'}
# Measures that may be zero or negative, such as ship coordinates.
SIGNED = set()


def validate(db, action, values):
    for key in values.keys() & NUMBERS:
        if values[key] is None:  # optional number left empty
            continue
        minimum = (-1000000 if key in SIGNED else 0 if key in NONNEGATIVE else Decimal('0.001') if key in MEASURES
                   else 1)
        maximum = 24 if key == 'hours' else 365 if key == 'lead_days' else 1000000
        try:
            number = Decimal(str(values[key]))
            scale = 1000 if key in MEASURES else 1
            valid = number.is_finite() and minimum <= number <= maximum and number * scale == (number * scale).to_integral_value()
        except InvalidOperation:
            valid = False
        if not valid:
            raise ValueError(f'{key} 값은 {minimum}~{maximum} 사이 수치와 허용 소수 자릿수를 확인하세요.')
        values[key] = float(number) if key in MEASURES else int(number)
    if action == 'operations' and values['predecessor']:
        prior = db.execute('SELECT a.hull FROM operations o JOIN activities a ON a.id=o.activity WHERE o.activity=?',
                           (values['predecessor'],)).fetchone()
        current = db.execute('SELECT hull FROM activities WHERE id=?', (values['activity'],)).fetchone()
        if not prior or not current or prior['hull'] != current['hull']:
            raise ValueError('같은 호선에서 먼저 등록한 선행 작업을 선택하세요.')


def mrp(db):
    materials = {row['id']: dict(row) for row in db.execute('SELECT * FROM materials')}
    receipts = {}
    for row in db.execute('SELECT * FROM receipts ORDER BY receipt_day, id'):
        receipts.setdefault(row['material'], []).append(row)
    for order in db.execute(procurement.OPEN_ORDERS):
        if order['open_qty']:
            receipts.setdefault(order['material'], []).append({
                'receipt_day': order['promised_day'], 'qty': order['open_qty']
            })
    for pending in receipts.values():
        pending.sort(key=lambda row: row['receipt_day'])
    rows = []
    for req in db.execute('''SELECT r.* FROM requirements r JOIN activities a ON a.id=r.activity
                             WHERE a.actual IS NULL ORDER BY r.need_day, r.id'''):
        item = materials[req['material']]
        pending = receipts.get(req['material'], [])
        while pending and pending[0]['receipt_day'] <= req['need_day']:
            item['stock'] += pending.pop(0)['qty']
        issued = db.execute(procurement.ISSUED, (req['id'],)).fetchone()[0]
        remaining = max(0, req['qty'] - issued)
        available = item['stock']
        shortage = max(0, remaining - available)
        item['stock'] = max(0, available - remaining)
        need = date.fromisoformat(req['need_day'])
        release = need - timedelta(days=min(item['lead_days'], need.toordinal() - 1))
        rows.append({**dict(req), 'issued_qty': issued, 'remaining_qty': remaining, 'available': available, 'shortage': shortage,
                     'release_day': release.isoformat() if shortage else None})
    return rows


def workday(day, closed):
    while day.weekday() >= 5 or day.isoformat() in closed:
        day += timedelta(days=1)
    return day


def aps(db):
    shortages = {row['activity'] for row in mrp(db) if row['shortage']}
    closed = {row['holiday'] for row in db.execute('SELECT holiday FROM holidays')}
    finish = {}
    capacities = {}
    rows = []
    operations = list(db.execute('''SELECT o.*, c.hours, a.actual, a.forecast FROM operations o
        JOIN centers c ON c.id=o.center JOIN activities a ON a.id=o.activity
        ORDER BY a.forecast, o.activity'''))
    preds = {row['activity']: {row['predecessor']} - {None} for row in operations}
    for link in db.execute('SELECT activity, predecessor FROM operation_links'):
        preds[link['activity']].add(link['predecessor'])
    while operations:
        # Predecessor links are validated acyclic, so one operation always has finished predecessors.
        operation = next(row for row in operations if preds[row['activity']] <= finish.keys())
        operations.remove(operation)
        key = operation['activity']
        prior = sorted(preds[key])
        start = end = None
        if operation['actual']:
            end = date.fromisoformat(operation['actual'])
            status = '완료'
        elif key in shortages or any(finish[item] is None for item in prior):
            status = '자재 부족' if key in shortages else '선행 작업 보류'
        else:
            start = date.fromisoformat(operation['earliest'])
            for item in prior:
                start = max(start, finish[item] + timedelta(days=1))
            center = operation['center']
            start = workday(max(start, capacities.get(center, start)), closed)
            end = start
            for _ in range((operation['duration'] - 1) // operation['hours']):
                end = workday(end + timedelta(days=1), closed)
            capacities[center] = end + timedelta(days=1)
            status = '일정 제안'
        finish[key] = end
        rows.append({'activity': key, 'center': operation['center'], 'predecessor': ', '.join(prior) or None,
                     'forecast': operation['forecast'], 'scheduled_start': start.isoformat() if start else None,
                     'scheduled_end': end.isoformat() if end else None, 'planning_status': status})
    return rows
