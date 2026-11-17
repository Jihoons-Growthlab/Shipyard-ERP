"""Steel heat-number lots, lot consumption per issue, returns/remnants and multi-level block material roll-up."""
from datetime import date

import procurement

RETURN_TYPES = ['반납', '잔재', '폐기']
RESTOCK = {'반납': '반납 입고', '잔재': '잔재 입고'}
CHOICES = {'return_type': RETURN_TYPES}
SCHEMA = '''
CREATE TABLE IF NOT EXISTS receipt_lots(id TEXT PRIMARY KEY, receipt TEXT NOT NULL REFERENCES goods_receipts(id),
 material TEXT NOT NULL REFERENCES materials(id), warehouse TEXT NOT NULL REFERENCES locations(id),
 heat_no TEXT NOT NULL, cert_no TEXT NOT NULL, qty INTEGER NOT NULL, UNIQUE(receipt, heat_no));
CREATE TABLE IF NOT EXISTS issue_lots(id TEXT PRIMARY KEY, issue TEXT NOT NULL REFERENCES issues(id),
 lot TEXT NOT NULL REFERENCES receipt_lots(id), qty INTEGER NOT NULL, UNIQUE(issue, lot));
CREATE TABLE IF NOT EXISTS material_returns(id TEXT PRIMARY KEY, issue TEXT NOT NULL REFERENCES issues(id),
 lot TEXT REFERENCES receipt_lots(id), return_type TEXT NOT NULL, qty INTEGER NOT NULL, return_day TEXT NOT NULL,
 warehouse TEXT REFERENCES locations(id), recorded_at TEXT NOT NULL);
'''
FIELDS = {
    'receipt_lots': ('id', 'receipt', 'heat_no', 'cert_no', 'qty'),
    'issue_lots': ('id', 'issue', 'lot', 'qty'),
    'material_returns': ('id', 'issue', 'lot', 'return_type', 'qty', 'return_day', 'warehouse'),
}
PREFIXES = {'receipt_lots': 'LOT', 'issue_lots': 'IL', 'material_returns': 'RT'}
OPTIONAL = {'material_returns': {'lot', 'warehouse'}}
DATES = {'return_day'}
NUMBERS = set()
READS = set(FIELDS) | {'block_traceability', 'material_rollup'}


def total(db, sql, *params):
    return db.execute(sql, params).fetchone()[0] or 0


def lot_balance(db, lot):
    restocked = "('" + "','".join(RESTOCK) + "')"
    return (lot['qty'] - total(db, 'SELECT SUM(qty) FROM issue_lots WHERE lot=?', lot['id'])
            + total(db, f'SELECT SUM(qty) FROM material_returns WHERE lot=? AND return_type IN {restocked}', lot['id']))


def write(db, action, v, api):
    require, lookup, insert = api.require, api.lookup, api.insert
    if action == 'receipt_lots':
        receipt = lookup(db, 'goods_receipts', v['receipt'])
        require(receipt['inspection'] == '합격', '검사에 합격한 입고에만 로트를 등록할 수 있습니다.')
        order = db.execute(f'SELECT material FROM ({procurement.OPEN_ORDERS}) o WHERE id=?',
                           (receipt['purchase_order'],)).fetchone()
        place = db.execute('SELECT location FROM receipt_locations WHERE receipt=?', (receipt['id'],)).fetchone()
        require(place, '입고 창고가 기록된 입고만 로트를 등록할 수 있습니다.')
        used = total(db, 'SELECT SUM(qty) FROM receipt_lots WHERE receipt=?', receipt['id'])
        require(used + v['qty'] <= receipt['qty'], f'입고 수량 {receipt["qty"]}을(를) 넘는 로트 수량입니다.')
        v.update(material=order['material'], warehouse=place['location'])
    if action == 'issue_lots':
        issue = lookup(db, 'issues', v['issue'])
        lot = lookup(db, 'receipt_lots', v['lot'])
        source = db.execute('SELECT source FROM issue_locations WHERE issue=?', (issue['id'],)).fetchone()
        require(lot['material'] == lookup(db, 'requirements', issue['requirement'])['material'], '출고 자재와 로트 자재가 다릅니다.')
        require(source and source['source'] == lot['warehouse'], '출고 창고에 있는 로트를 선택하세요.')
        assigned = total(db, 'SELECT SUM(qty) FROM issue_lots WHERE issue=?', issue['id'])
        require(assigned + v['qty'] <= issue['qty'], f'출고 수량 {issue["qty"]}을(를) 넘게 배정할 수 없습니다.')
        require(v['qty'] <= lot_balance(db, lot), '로트 잔량이 부족합니다.')
    if action == 'material_returns':
        issue = lookup(db, 'issues', v['issue'])
        require(v['return_type'] in RETURN_TYPES, '반납 유형을 선택하세요.')
        require(issue['day'] <= v['return_day'] <= date.today().isoformat(), '출고일 이후, 오늘 이전 날짜를 입력하세요.')
        returned = total(db, 'SELECT SUM(qty) FROM material_returns WHERE issue=?', issue['id'])
        require(returned + v['qty'] <= issue['qty'], '출고 수량을 넘게 반납할 수 없습니다.')
        material = lookup(db, 'requirements', issue['requirement'])['material']
        if v['lot']:
            link = db.execute('SELECT qty FROM issue_lots WHERE issue=? AND lot=?', (issue['id'], v['lot'])).fetchone()
            require(link, '해당 출고에 배정된 로트를 선택하세요.')
            used = total(db, 'SELECT SUM(qty) FROM material_returns WHERE issue=? AND lot=?', issue['id'], v['lot'])
            require(used + v['qty'] <= link['qty'], '로트 배정 수량을 넘게 반납할 수 없습니다.')
        if v['return_type'] in RESTOCK:
            require(v['warehouse'], '반납할 창고를 선택하세요.')
            require(lookup(db, 'locations', v['warehouse'])['kind'] == '창고', '창고 유형의 시설을 선택하세요.')
            if v['lot']:
                require(lookup(db, 'receipt_lots', v['lot'])['warehouse'] == v['warehouse'], '로트의 원래 창고로 반납하세요.')
            db.execute('UPDATE materials SET stock=stock+? WHERE id=?', (v['qty'], material))
            db.execute('''INSERT INTO warehouse_stock(id,location,material,qty) VALUES(?,?,?,?)
                ON CONFLICT(location,material) DO UPDATE SET qty=warehouse_stock.qty+excluded.qty''',
                       ('WS-' + v['id'], v['warehouse'], material, v['qty']))
        else:
            v['warehouse'] = None
        v['recorded_at'] = api.now()
        key = insert(db, action, **v)
        if v['return_type'] in RESTOCK:
            api.traceability.ledger(db, insert, api.now(), material, v['warehouse'], v['return_day'],
                                    RESTOCK[v['return_type']], key, v['qty'])
        return key
    return insert(db, action, **v)


def descendants(db):
    children = {}
    for row in db.execute('SELECT * FROM block_structure'):
        children.setdefault(row['parent_block'], []).append(row['child_block'])

    def walk(block, depth):
        yield block, depth
        for child in children.get(block, []):
            yield from walk(child, depth + 1)
    return walk


def read(db, table):
    if table == 'receipt_lots':
        return [{**dict(row), 'balance': lot_balance(db, row)} for row in db.execute('SELECT * FROM receipt_lots ORDER BY id')]
    if table == 'block_traceability':
        rows = [dict(row) for row in db.execute('''SELECT a.block, a.id AS activity, i.requirement, i.id AS issue,
            l.id AS lot, l.material, l.heat_no, l.cert_no, il.qty, l.receipt, po.supplier
            FROM issue_lots il JOIN receipt_lots l ON l.id=il.lot JOIN issues i ON i.id=il.issue
            JOIN requirements r ON r.id=i.requirement JOIN activities a ON a.id=r.activity
            JOIN goods_receipts g ON g.id=l.receipt JOIN purchase_orders po ON po.id=g.purchase_order
            ORDER BY a.block, il.id''')]
        for issue in db.execute('''SELECT i.*, a.block, r.material FROM issues i JOIN requirements r ON r.id=i.requirement
                JOIN activities a ON a.id=r.activity ORDER BY i.id'''):
            missing = issue['qty'] - total(db, 'SELECT SUM(qty) FROM issue_lots WHERE issue=?', issue['id'])
            if missing > 0:
                rows.append({'block': issue['block'], 'activity': None, 'requirement': issue['requirement'],
                             'issue': issue['id'], 'lot': None, 'material': issue['material'],
                             'heat_no': '로트 미배정', 'cert_no': None, 'qty': missing, 'receipt': None, 'supplier': None})
        return rows
    if table == 'material_rollup':
        walk = descendants(db)
        own = {}
        for row in db.execute('''SELECT a.block, r.id, r.material, r.qty FROM requirements r
                JOIN activities a ON a.id=r.activity WHERE a.block IS NOT NULL'''):
            issued = total(db, procurement.ISSUED, row['id'])
            item = own.setdefault((row['block'], row['material']), [0, 0])
            item[0] += row['qty']
            item[1] += issued
        output = []
        for block in db.execute('SELECT id, hull FROM blocks ORDER BY id'):
            totals = {}
            for node, depth in walk(block['id'], 0):
                for (owner, material), (qty, issued) in own.items():
                    if owner == node:
                        item = totals.setdefault(material, {'own_qty': 0, 'tree_qty': 0, 'issued_qty': 0, 'levels': 0})
                        item['tree_qty'] += qty
                        item['issued_qty'] += issued
                        item['levels'] = max(item['levels'], depth + 1)
                        if depth == 0:
                            item['own_qty'] += qty
            for material, item in sorted(totals.items()):
                output.append({'block': block['id'], 'hull': block['hull'], 'material': material, **item,
                               'remaining_qty': item['tree_qty'] - item['issued_qty']})
        return output
    return None
