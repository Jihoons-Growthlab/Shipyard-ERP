SCHEMA = '''
CREATE TABLE IF NOT EXISTS document_counters(prefix TEXT PRIMARY KEY, value INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS order_plans(work_order TEXT PRIMARY KEY REFERENCES orders(id),
 plan_id BIGINT NOT NULL REFERENCES plans(id));
CREATE TABLE IF NOT EXISTS production_results(id TEXT PRIMARY KEY, work_order TEXT NOT NULL REFERENCES orders(id),
 actual TEXT NOT NULL, result TEXT NOT NULL, inspection TEXT NOT NULL, reason TEXT NOT NULL, recorded_at TEXT);
CREATE TABLE IF NOT EXISTS inventory_ledger(id TEXT PRIMARY KEY, material TEXT NOT NULL REFERENCES materials(id),
 location TEXT NOT NULL REFERENCES locations(id), day TEXT NOT NULL, recorded_at TEXT NOT NULL,
 movement TEXT NOT NULL, source_document TEXT NOT NULL, qty_delta INTEGER NOT NULL, balance INTEGER NOT NULL,
 UNIQUE(movement, source_document));
CREATE TABLE IF NOT EXISTS data_upgrades(id TEXT PRIMARY KEY);
'''
PREFIXES = {
    'activities': 'OP', 'orders': 'WO', 'requirements': 'REQ', 'purchase_requests': 'PR',
    'purchase_orders': 'PO', 'goods_receipts': 'GR', 'issues': 'GI', 'moves': 'MV',
    'production_results': 'RES', 'inventory_ledger': 'STK',
}


def document_number(db, table):
    prefix = PREFIXES[table]
    while True:
        counter = db.execute('''INSERT INTO document_counters(prefix,value) VALUES(?,1)
            ON CONFLICT(prefix) DO UPDATE SET value=document_counters.value+1 RETURNING value''',
                             (prefix,)).fetchone()['value']
        number = f'{prefix}-{counter:06d}'
        if not db.execute(f'SELECT 1 FROM {table} WHERE id=?', (number,)).fetchone():
            return number


def ledger(db, insert, timestamp, material, location, day, movement, source_document, qty_delta):
    balance = db.execute('SELECT qty FROM warehouse_stock WHERE location=? AND material=?',
                         (location, material)).fetchone()['qty']
    insert(db, 'inventory_ledger', id=document_number(db, 'inventory_ledger'), material=material,
           location=location, day=day, recorded_at=timestamp, movement=movement,
           source_document=source_document, qty_delta=qty_delta, balance=balance)


def upgrade(db, insert, timestamp):
    marker = 'traceability-v1'
    if db.execute('SELECT 1 FROM data_upgrades WHERE id=?', (marker,)).fetchone():
        return
    for row in db.execute('SELECT * FROM warehouse_stock ORDER BY id'):
        if row['qty']:
            ledger(db, insert, timestamp, row['material'], row['location'], timestamp[:10],
                   '전환 기초잔액', row['id'], row['qty'])
    for row in db.execute('SELECT * FROM orders WHERE actual IS NOT NULL ORDER BY id'):
        insert(db, 'production_results', id=document_number(db, 'production_results'),
               work_order=row['id'], actual=row['actual'], result=row['result'],
               inspection=row['inspection'], reason='', recorded_at=None)
    insert(db, 'data_upgrades', id=marker)
