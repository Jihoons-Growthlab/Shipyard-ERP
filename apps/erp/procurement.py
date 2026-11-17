from uuid import uuid4

SCHEMA = '''
CREATE TABLE IF NOT EXISTS suppliers(id TEXT PRIMARY KEY, name TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS purchase_requests(id TEXT PRIMARY KEY,
 requirement TEXT NOT NULL REFERENCES requirements(id), qty INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS purchase_orders(id TEXT PRIMARY KEY,
 request TEXT NOT NULL UNIQUE REFERENCES purchase_requests(id),
 supplier TEXT NOT NULL REFERENCES suppliers(id), promised_day TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS goods_receipts(id TEXT PRIMARY KEY,
 purchase_order TEXT NOT NULL REFERENCES purchase_orders(id),
 day TEXT NOT NULL, qty INTEGER NOT NULL, inspection TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS issues(id TEXT PRIMARY KEY,
 requirement TEXT NOT NULL REFERENCES requirements(id), work_order TEXT NOT NULL REFERENCES orders(id),
 qty INTEGER NOT NULL, day TEXT NOT NULL);
'''
FIELDS = {
    'suppliers': ('id', 'name'),
    'purchase_requests': ('id', 'requirement', 'qty'),
    'purchase_orders': ('id', 'request', 'supplier', 'promised_day'),
    'goods_receipts': ('id', 'purchase_order', 'day', 'qty', 'inspection'),
    'issues': ('id', 'requirement', 'work_order', 'qty', 'day'),
}
OPEN_ORDERS = '''
SELECT p.id, p.promised_day, r.material, q.qty, q.requirement, p.request, p.supplier,
       q.qty - COALESCE(SUM(CASE WHEN g.inspection='합격' THEN g.qty ELSE 0 END), 0) AS open_qty
FROM purchase_orders p JOIN purchase_requests q ON p.request=q.id
JOIN requirements r ON q.requirement=r.id
LEFT JOIN goods_receipts g ON g.purchase_order=p.id GROUP BY p.id, p.promised_day, r.material, q.qty, q.requirement, p.request, p.supplier
'''
ISSUED = 'SELECT COALESCE(SUM(qty),0) FROM issues WHERE requirement=?'


def apply(db, action, v, lookup, require):
    if action == 'purchase_requests':
        req = lookup(db, 'requirements', v['requirement'])
        activity = lookup(db, 'activities', req['activity'])
        require(not activity['actual'], '완료한 공정에는 구매요청을 추가할 수 없습니다.')
    if action == 'purchase_orders':
        request = lookup(db, 'purchase_requests', v['request'])
        req = lookup(db, 'requirements', request['requirement'])
        require(not lookup(db, 'activities', req['activity'])['actual'], '완료한 공정에는 발주할 수 없습니다.')
    if action == 'goods_receipts':
        order = db.execute(f'SELECT * FROM ({OPEN_ORDERS}) WHERE id=?', (v['purchase_order'],)).fetchone()
        require(order, '구매 발주를 선택하세요.')
        require(v['inspection'] in {'합격', '불합격'}, '검사 결과를 선택하세요.')
        require(v['qty'] <= order['open_qty'], '미입고 발주량을 초과할 수 없습니다.')
        if v['inspection'] == '합격':
            db.execute('UPDATE materials SET stock=stock+? WHERE id=?', (v['qty'], order['material']))
    if action == 'issues':
        req = lookup(db, 'requirements', v['requirement'])
        order = lookup(db, 'orders', v['work_order'])
        require(req['activity'] == order['activity'] and not order['actual'] and order['cadence'] == '주간',
                '같은 공정의 미완료 주간 작업지시를 선택하세요.')
        issued = db.execute(ISSUED, (req['id'],)).fetchone()[0]
        require(issued + v['qty'] <= req['qty'], '미출고 소요량을 초과할 수 없습니다.')
        updated = db.execute('UPDATE materials SET stock=stock-? WHERE id=? AND stock>=?',
                             (v['qty'], req['material'], v['qty']))
        require(updated.rowcount, '실제 가용 재고가 부족합니다.')


def require_issued(db, order, actual, require):
    missing = db.execute('''SELECT r.id FROM requirements r
        WHERE r.activity=? AND r.qty > COALESCE((SELECT SUM(i.qty) FROM issues i
        WHERE i.requirement=r.id AND i.day<=?),0) LIMIT 1''',
        (order['activity'], actual)).fetchone()
    require(not missing, '해당 공정의 필수 자재 출고를 완료하세요.')

SCHEMA += '''
CREATE TABLE IF NOT EXISTS center_locations(center TEXT PRIMARY KEY REFERENCES centers(id),
 location TEXT NOT NULL REFERENCES locations(id));
CREATE TABLE IF NOT EXISTS warehouse_stock(id TEXT PRIMARY KEY,
 location TEXT NOT NULL REFERENCES locations(id), material TEXT NOT NULL REFERENCES materials(id),
 qty INTEGER NOT NULL, UNIQUE(location, material));
CREATE TABLE IF NOT EXISTS receipt_locations(receipt TEXT PRIMARY KEY REFERENCES goods_receipts(id),
 location TEXT NOT NULL REFERENCES locations(id));
CREATE TABLE IF NOT EXISTS issue_locations(issue TEXT PRIMARY KEY REFERENCES issues(id),
 source TEXT NOT NULL REFERENCES locations(id), destination TEXT NOT NULL REFERENCES locations(id));
'''
FIELDS.update({
    'center_locations': ('center', 'location'),
    'warehouse_stock': ('id', 'location', 'material', 'qty'),
})
FIELDS['goods_receipts'] += ('location',)
FIELDS['issues'] += ('source',)


def warehouse(db, lookup, require, location):
    require(lookup(db, 'locations', location)['kind'] == '창고', '창고 유형의 시설을 선택하세요.')


def transfer(db, action, v, lookup, require):
    if action == 'center_locations':
        facility = lookup(db, 'locations', v['location'])
        require(facility['kind'] in {'야드', '작업장', '도크', '안벽'}, '생산 시설을 선택하세요.')
    if action == 'warehouse_stock':
        warehouse(db, lookup, require, v['location'])
        stock = lookup(db, 'materials', v['material'])['stock']
        allocated = db.execute('SELECT COALESCE(SUM(qty),0) FROM warehouse_stock WHERE material=?',
                               (v['material'],)).fetchone()[0]
        require(v['qty'] <= stock - allocated, '창고 미배정 재고를 초과할 수 없습니다.')
    if action == 'goods_receipts':
        location = v.pop('location')
        warehouse(db, lookup, require, location)
        if v['inspection'] == '합격':
            material = db.execute(f'SELECT material FROM ({OPEN_ORDERS}) WHERE id=?',
                                  (v['purchase_order'],)).fetchone()['material']
            db.execute('''INSERT INTO warehouse_stock(id,location,material,qty)
                VALUES(?,?,?,?)
                ON CONFLICT(location,material) DO UPDATE SET qty=warehouse_stock.qty+excluded.qty''',
                       (uuid4().hex, location, material, v['qty']))
        return ('receipt_locations', {'receipt': v['id'], 'location': location})
    if action == 'issues':
        source = v.pop('source')
        warehouse(db, lookup, require, source)
        order = lookup(db, 'orders', v['work_order'])
        target = db.execute('''SELECT cl.location FROM operations o
            JOIN center_locations cl ON cl.center=o.center WHERE o.activity=?''',
                            (order['activity'],)).fetchone()
        require(target, '공정의 APS 작업장과 실제 생산 시설을 먼저 연결하세요.')
        material = lookup(db, 'requirements', v['requirement'])['material']
        updated = db.execute('UPDATE warehouse_stock SET qty=qty-? WHERE location=? AND material=? AND qty>=?',
                             (v['qty'], source, material, v['qty']))
        require(updated.rowcount, '선택한 창고의 가용 재고가 부족합니다.')
        return ('issue_locations', {'issue': v['id'], 'source': source, 'destination': target['location']})
    return None
