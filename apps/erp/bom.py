"""Engineering BOM (EBOM) → manufacturing BOM (MBOM) → process material requirements.

EBOM: the design structure of a block (assemblies and parts) issued against a drawing revision. Issued revisions
are frozen; a design change is a new drawing revision and a new EBOM revision.

MBOM: one revision per block that names the EBOM revision it applies, sequences the block's own processes
(any order: outfitting and painting timing differ per block), assigns every design item to a process and lists the
items that are actually consumed in production: raw plates nested into fabricated parts, purchased outfitting
items, welding and other consumables. Only these input lines create requirements, so a fabricated part is never
counted as a purchase and a plate is never counted twice.

Publishing an MBOM reconciles the existing process requirements (the table MRP already reads) per
(activity, material): same revision again → no change; open requirements without purchase or issue documents are
adjusted in place; requirements already referenced by purchase requests or issues are never rewritten — an
increase becomes an extra requirement and anything else becomes a bom_adjustments row for a planner to settle.
"""
from datetime import date

import procurement

EBOM_STATUS = ('작성 중', '발행')
ITEM_LEVELS = ['조립품', '부품']
SUPPLY = ['제작', '구매']
# Processes are listed, not ordered: each block's MBOM sets its own sequence (seq).
PROCESSES = ['절단·가공', '소조립', '중조립', '대조립', '선행의장', '도장', '선행탑재(PE)', '탑재', '탑재 후 의장']
INPUT_TYPES = ['원판', '구매품', '용접재', '소모품']
LINKED_TYPES = {'원판': '제작', '구매품': '구매'}  # input type → the design supply type it may carry
CHOICES = {'item_level': ITEM_LEVELS, 'supply': SUPPLY, 'process': PROCESSES, 'input_type': INPUT_TYPES}
SCHEMA = '''
CREATE TABLE IF NOT EXISTS ebom_revisions(id TEXT PRIMARY KEY, hull TEXT NOT NULL REFERENCES hulls(id),
 block TEXT NOT NULL REFERENCES blocks(id), drawing_revision TEXT NOT NULL UNIQUE REFERENCES drawing_revisions(id),
 drawing_no TEXT NOT NULL, revision TEXT NOT NULL, base_ebom TEXT REFERENCES ebom_revisions(id),
 status TEXT NOT NULL, description TEXT, created_at TEXT NOT NULL, issued_at TEXT);
CREATE TABLE IF NOT EXISTS ebom_items(id TEXT PRIMARY KEY, ebom TEXT NOT NULL REFERENCES ebom_revisions(id),
 item_no TEXT NOT NULL, parent_no TEXT, item_level TEXT NOT NULL, name TEXT NOT NULL, spec TEXT, unit TEXT NOT NULL,
 design_qty REAL NOT NULL, supply TEXT NOT NULL, material TEXT REFERENCES materials(id), net_weight_kg REAL,
 UNIQUE(ebom, item_no));
CREATE TABLE IF NOT EXISTS mbom_revisions(id TEXT PRIMARY KEY, hull TEXT NOT NULL REFERENCES hulls(id),
 block TEXT NOT NULL REFERENCES blocks(id), revision TEXT NOT NULL, ebom TEXT NOT NULL REFERENCES ebom_revisions(id),
 base_mbom TEXT REFERENCES mbom_revisions(id), status TEXT NOT NULL, description TEXT, created_at TEXT NOT NULL,
 issued_at TEXT, UNIQUE(block, revision));
CREATE TABLE IF NOT EXISTS mbom_ops(id TEXT PRIMARY KEY, mbom TEXT NOT NULL REFERENCES mbom_revisions(id),
 seq INTEGER NOT NULL, process TEXT NOT NULL, activity TEXT NOT NULL REFERENCES activities(id), UNIQUE(mbom, seq));
CREATE TABLE IF NOT EXISTS mbom_items(id TEXT PRIMARY KEY, mbom TEXT NOT NULL REFERENCES mbom_revisions(id),
 op TEXT NOT NULL REFERENCES mbom_ops(id), ebom_item TEXT NOT NULL REFERENCES ebom_items(id), UNIQUE(mbom, ebom_item));
CREATE TABLE IF NOT EXISTS mbom_inputs(id TEXT PRIMARY KEY, mbom TEXT NOT NULL REFERENCES mbom_revisions(id),
 op TEXT NOT NULL REFERENCES mbom_ops(id), input_type TEXT NOT NULL, material TEXT NOT NULL REFERENCES materials(id),
 qty INTEGER NOT NULL, basis TEXT, UNIQUE(mbom, op, material));
CREATE TABLE IF NOT EXISTS mbom_links(id TEXT PRIMARY KEY, mbom TEXT NOT NULL REFERENCES mbom_revisions(id),
 input TEXT NOT NULL REFERENCES mbom_inputs(id), ebom_item TEXT NOT NULL REFERENCES ebom_items(id),
 UNIQUE(mbom, ebom_item));
CREATE TABLE IF NOT EXISTS bom_requirements(requirement TEXT PRIMARY KEY REFERENCES requirements(id),
 block TEXT NOT NULL REFERENCES blocks(id), activity TEXT NOT NULL REFERENCES activities(id),
 material TEXT NOT NULL REFERENCES materials(id), mbom TEXT NOT NULL REFERENCES mbom_revisions(id),
 origin TEXT NOT NULL, created_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS bom_adjustments(id TEXT PRIMARY KEY, mbom TEXT NOT NULL REFERENCES mbom_revisions(id),
 block TEXT NOT NULL REFERENCES blocks(id), activity TEXT NOT NULL REFERENCES activities(id),
 material TEXT NOT NULL REFERENCES materials(id), adjustment TEXT NOT NULL, qty_delta INTEGER NOT NULL,
 note TEXT NOT NULL, recorded_at TEXT NOT NULL, closed_day TEXT, close_evidence TEXT, UNIQUE(mbom, activity, material));
CREATE TABLE IF NOT EXISTS order_boms(id TEXT PRIMARY KEY, work_order TEXT NOT NULL REFERENCES orders(id),
 mbom TEXT NOT NULL REFERENCES mbom_revisions(id), link_type TEXT NOT NULL, linked_at TEXT NOT NULL,
 UNIQUE(work_order, mbom));
'''
FIELDS = {
    'ebom_revisions': ('id', 'drawing_revision', 'base_ebom', 'description'),
    'ebom_items': ('id', 'ebom', 'item_no', 'parent_no', 'item_level', 'name', 'spec', 'unit', 'design_qty', 'supply',
                   'material', 'net_weight_kg'),
    'ebom_publish': ('id',),
    'mbom_revisions': ('id', 'ebom', 'revision', 'base_mbom', 'description'),
    'mbom_ops': ('id', 'mbom', 'seq', 'process', 'activity'),
    'mbom_items': ('id', 'op', 'ebom_item'),
    'mbom_inputs': ('id', 'op', 'input_type', 'material', 'qty', 'basis'),
    'mbom_links': ('id', 'input', 'ebom_item'),
    'mbom_publish': ('id',),
    'bom_adjustment_close': ('id', 'closed_day', 'evidence'),
}
PREFIXES = {'ebom_revisions': 'EBOM', 'ebom_items': 'EBI', 'mbom_revisions': 'MBOM', 'mbom_ops': 'MOP',
            'mbom_items': 'MBI', 'mbom_inputs': 'MIN', 'mbom_links': 'MBL', 'bom_adjustments': 'BADJ',
            'order_boms': 'OBM'}
OPTIONAL = {'ebom_revisions': {'base_ebom', 'description'},
            'ebom_items': {'parent_no', 'spec', 'material', 'net_weight_kg'},
            'mbom_revisions': {'base_mbom', 'description'},
            'mbom_inputs': {'basis'}}
DATES = set()
NUMBERS = {'design_qty', 'net_weight_kg', 'seq'}
MEASURES = {'design_qty', 'net_weight_kg'}
READS = (set(FIELDS) - {'ebom_publish', 'mbom_publish', 'bom_adjustment_close'}) | {
    'mbom_changes', 'bom_trace', 'bom_adjustments', 'order_boms', 'requirements', 'mrp'}


def rows(db, sql, *params):
    return [dict(row) for row in db.execute(sql, params)]


def one(db, sql, *params):
    row = db.execute(sql, params).fetchone()
    return dict(row) if row else None


def draft(db, api, table, key):
    row = api.lookup(db, table, key)
    api.require(row['status'] == '작성 중', f'{key}은(는) 이미 발행된 리비전입니다. 발행본은 고칠 수 없으니 새 리비전을 만드세요.')
    return row


def mbom_of_op(db, api, op):
    row = api.lookup(db, 'mbom_ops', op)
    return row, draft(db, api, 'mbom_revisions', row['mbom'])


def ebom_children(db, ebom):
    children = {}
    for item in db.execute('SELECT item_no, parent_no FROM ebom_items WHERE ebom=?', (ebom,)):
        if item['parent_no']:
            children.setdefault(item['parent_no'], []).append(item['item_no'])
    return children


def latest_issued(db, block, before=None):
    """Latest published MBOM of a block (optionally the one published before `before`)."""
    sql = "SELECT * FROM mbom_revisions WHERE block=? AND status='발행'"
    params = [block]
    if before:
        sql += ' AND issued_at < (SELECT issued_at FROM mbom_revisions WHERE id=?) AND id<>?'
        params += [before, before]
    return one(db, sql + ' ORDER BY issued_at DESC, id DESC LIMIT 1', *params)


def upsert(db, api, table, unique, v):
    where = ' AND '.join(f'{key}=?' for key in unique)
    found = one(db, f'SELECT id FROM {table} WHERE {where}', *(v[key] for key in unique))
    if found:
        columns = [key for key in v if key != 'id' and key not in unique]
        if columns:
            db.execute(f'UPDATE {table} SET {",".join(c + "=?" for c in columns)} WHERE id=?',
                       tuple(v[c] for c in columns) + (found['id'],))
        return found['id']
    return api.insert(db, table, **v)


# ---------------------------------------------------------------- validation of a whole MBOM before publishing

def publish_problems(db, mbom):
    """Every reason the MBOM may not be published (empty list = ready)."""
    out = []
    ebom = one(db, 'SELECT * FROM ebom_revisions WHERE id=?', mbom['ebom'])
    if ebom['status'] != '발행':
        out.append(f'적용 EBOM {ebom["id"]}이(가) 발행되지 않았습니다')
    newer = one(db, '''SELECT e.id, e.revision FROM ebom_revisions e WHERE e.drawing_no=? AND e.status='발행'
        AND e.issued_at > ? ORDER BY e.issued_at DESC LIMIT 1''', ebom['drawing_no'], ebom['issued_at'] or '')
    if newer:
        out.append(f'더 새 EBOM {newer["id"]}({newer["revision"]})이 발행되었습니다')
    if mbom['status'] == '발행':
        latest = latest_issued(db, mbom['block'])
        if latest and latest['id'] != mbom['id']:
            out.append(f'최신 발행 MBOM은 {latest["id"]}입니다 (이전 리비전은 재처리 불가)')
    ops = rows(db, 'SELECT * FROM mbom_ops WHERE mbom=?', mbom['id'])
    if not ops:
        out.append('공정 편성이 없습니다')
    items = {row['id']: row for row in rows(db, 'SELECT * FROM ebom_items WHERE ebom=?', mbom['ebom'])}
    children = ebom_children(db, mbom['ebom'])
    assigned = {row['ebom_item'] for row in rows(db, 'SELECT ebom_item FROM mbom_items WHERE mbom=?', mbom['id'])}
    links = {row['ebom_item']: row for row in rows(db, '''SELECT l.ebom_item, i.input_type, i.id AS input
        FROM mbom_links l JOIN mbom_inputs i ON i.id=l.input WHERE l.mbom=?''', mbom['id'])}
    missing = sorted(item['item_no'] for key, item in items.items() if key not in assigned)
    if missing:
        out.append('공정 미배정 설계품목 ' + ', '.join(missing))
    for key, item in sorted(items.items(), key=lambda pair: pair[1]['item_no']):
        link = links.get(key)
        leaf = item['item_no'] not in children
        if item['supply'] == '구매' and (not link or link['input_type'] != '구매품'):
            out.append(f'구매 설계품목 {item["item_no"]}이(가) 구매품 투입과 연결되지 않았습니다')
        if item['supply'] == '제작' and leaf and (not link or link['input_type'] != '원판'):
            out.append(f'제작 부품 {item["item_no"]}의 원판(원자재) 투입이 연결되지 않았습니다')
    for line in rows(db, '''SELECT i.*, m.unit FROM mbom_inputs i JOIN materials m ON m.id=i.material
            WHERE i.mbom=? ORDER BY i.id''', mbom['id']):
        linked = rows(db, '''SELECT e.* FROM mbom_links l JOIN ebom_items e ON e.id=l.ebom_item
            WHERE l.input=?''', line['id'])
        label = f'투입품 {line["material"]}'
        if line['input_type'] in LINKED_TYPES and not linked:
            out.append(f'{label}({line["input_type"]})에 연결된 설계품목이 없습니다')
        if line['input_type'] in ('원판', '용접재') and not (line['basis'] or '').strip():
            out.append(f'{label}의 소요량 산정 근거가 없습니다')
        if line['input_type'] == '구매품' and linked:
            units = {item['unit'] for item in linked}
            if units != {line['unit']}:
                out.append(f'{label} 단위 {line["unit"]}와 설계 단위 {", ".join(sorted(units))}가 다릅니다')
            else:
                design = sum(item['design_qty'] for item in linked)
                if line['qty'] < design:
                    out.append(f'{label} 수량 {line["qty"]}{line["unit"]}이 설계 수량 {design:g}보다 적습니다')
                elif line['qty'] > design and not (line['basis'] or '').strip():
                    out.append(f'{label}의 설계 수량 초과분(예비품) 근거가 없습니다')
    return out


# ---------------------------------------------------------------- reconciliation with process requirements

def targets(db, mbom_id):
    return {(row['activity'], row['material']): row['qty'] for row in db.execute('''
        SELECT o.activity, i.material, SUM(i.qty) AS qty FROM mbom_inputs i JOIN mbom_ops o ON o.id=i.op
        WHERE i.mbom=? GROUP BY o.activity, i.material''', (mbom_id,))}


def current(db, block, activity, material):
    out = []
    for row in db.execute('''SELECT r.id, r.qty, b.origin, b.mbom FROM bom_requirements b JOIN requirements r ON r.id=b.requirement
            WHERE b.block=? AND b.activity=? AND b.material=? ORDER BY r.id''', (block, activity, material)):
        requested = db.execute('SELECT COALESCE(SUM(qty),0) FROM purchase_requests WHERE requirement=?',
                               (row['id'],)).fetchone()[0]
        issued = db.execute(procurement.ISSUED, (row['id'],)).fetchone()[0]
        out.append({**dict(row), 'requested': requested, 'issued': issued, 'locked': bool(requested or issued)})
    return out


def plan(db, mbom):
    """Per (activity, material): what publishing `mbom` does to the process requirements."""
    block = mbom['block']
    wanted = targets(db, mbom['id'])
    prior = latest_issued(db, block, mbom['id'] if mbom['status'] == '발행' else None)
    if prior and prior['id'] == mbom['id']:
        prior = None
    before = targets(db, prior['id']) if prior else {}
    keys = set(wanted) | set(before)
    for row in db.execute('SELECT DISTINCT activity, material FROM bom_requirements WHERE block=?', (block,)):
        keys.add((row['activity'], row['material']))
    units = {row['id']: row['unit'] for row in db.execute('SELECT id, unit FROM materials')}
    out = []
    for activity_id, material in sorted(keys):
        activity = one(db, 'SELECT * FROM activities WHERE id=?', activity_id)
        target = wanted.get((activity_id, material), 0)
        reqs = current(db, block, activity_id, material)
        have = sum(row['qty'] for row in reqs)
        row = {'mbom': mbom['id'], 'hull': mbom['hull'], 'block': block, 'activity': activity_id,
               'stage': activity['stage'], 'material': material, 'unit': units.get(material),
               'previous_qty': before.get((activity_id, material)) if prior else None,
               'current_qty': have, 'target_qty': target, 'delta': 0,
               'requested_qty': sum(r['requested'] for r in reqs), 'issued_qty': sum(r['issued'] for r in reqs),
               'requirements': ', '.join(r['id'] for r in reqs), 'change': '변경 없음', 'note': ''}
        if activity['actual']:
            old = before.get((activity_id, material), 0)
            if not prior and not reqs:
                row.update(change='이력 연결 (완료 공정)', note=f'{activity["actual"]} 완료된 공정이라 새 자재 소요를 만들지 않습니다')
            elif target != (old if prior else have):
                row.update(change='조정 필요 (완료 공정)', delta=target - (old if prior else have),
                           note='완료된 공정은 소요를 바꾸지 않습니다. 반납·잔재 처리나 추가 작업지시로 정리하세요')
        else:
            delta = target - have
            free = [r for r in reqs if not r['locked']]
            row['delta'] = delta
            if delta > 0:
                row['change'] = '수량 변경' if free else '신규 소요' if not reqs else '추가 소요'
                if row['change'] == '추가 소요':
                    row['note'] = '기존 구매요청·출고 문서는 보존하고 차이만 별도 소요로 만듭니다'
            elif delta < 0:
                releasable = sum(r['qty'] for r in free)
                if releasable >= -delta:
                    row['change'] = '수량 감소' if releasable > -delta or len(free) < len(reqs) or target else '소요 삭제'
                else:
                    row['change'] = '조정 필요 (발주·출고 후 감소)'
                    row['note'] = (f'구매요청·출고가 연결된 {-delta - releasable}{units.get(material) or ""}은 문서를 고치지 않습니다. '
                                   '발주 취소·반납 여부를 결정하세요')
        out.append(row)
    return out


def need_day(db, activity):
    row = one(db, 'SELECT earliest FROM operations WHERE activity=?', activity['id'])
    return min(activity['forecast'], row['earliest']) if row else activity['forecast']


def apply(db, api, mbom):
    stamp = api.now()
    for row in plan(db, mbom):
        key = (row['activity'], row['material'])
        if row['change'].startswith('조정 필요'):
            qty = row['delta'] if row['change'] == '조정 필요 (완료 공정)' else row['delta'] + sum(
                r['qty'] for r in current(db, mbom['block'], *key) if not r['locked'])
            existing = one(db, 'SELECT * FROM bom_adjustments WHERE mbom=? AND activity=? AND material=?',
                           mbom['id'], *key)
            if existing:
                if existing['qty_delta'] != qty:
                    db.execute('''UPDATE bom_adjustments SET qty_delta=?, note=?, recorded_at=?, closed_day=NULL,
                        close_evidence=NULL WHERE id=?''', (qty, row['note'], stamp, existing['id']))
            else:
                api.insert(db, 'bom_adjustments', id=api.traceability.document_number(db, 'bom_adjustments'),
                           mbom=mbom['id'], block=mbom['block'], activity=row['activity'], material=row['material'],
                           adjustment=row['change'], qty_delta=qty, note=row['note'], recorded_at=stamp,
                           closed_day=None, close_evidence=None)
        if row['change'] == '조정 필요 (완료 공정)' or not row['delta']:
            continue
        reqs = current(db, mbom['block'], *key)
        free = [r for r in reqs if not r['locked']]
        delta = row['delta']
        if delta > 0:
            if free:
                db.execute('UPDATE requirements SET qty=qty+? WHERE id=?', (delta, free[0]['id']))
                continue
            activity = api.lookup(db, 'activities', row['activity'])
            number = api.traceability.document_number(db, 'requirements')
            api.insert(db, 'requirements', id=number, activity=row['activity'], material=row['material'], qty=delta,
                       need_day=need_day(db, activity))
            api.insert(db, 'bom_requirements', requirement=number, block=mbom['block'], activity=row['activity'],
                       material=row['material'], mbom=mbom['id'], origin='추가 소요' if reqs else '최초 소요',
                       created_at=stamp)
            continue
        cut = -delta
        for req in reversed(free):
            if not cut:
                break
            take = min(cut, req['qty'])
            cut -= take
            if take == req['qty']:
                db.execute('DELETE FROM bom_requirements WHERE requirement=?', (req['id'],))
                db.execute('DELETE FROM requirements WHERE id=?', (req['id'],))
            else:
                db.execute('UPDATE requirements SET qty=qty-? WHERE id=?', (take, req['id']))
    link_orders(db, api, mbom)


def link_orders(db, api, mbom, only=None):
    """Record the applied MBOM revision on the weekly work orders of its processes."""
    first = not latest_issued(db, mbom['block'], mbom['id'])
    for order in db.execute('''SELECT DISTINCT o.id, o.actual FROM orders o JOIN mbom_ops p ON p.activity=o.activity
            WHERE p.mbom=? AND o.cadence='주간' ORDER BY o.id''', (mbom['id'],)):
        if only and order['id'] != only:
            continue
        if order['actual'] and not first:
            continue  # executed under an earlier revision; keep that record
        if db.execute('SELECT 1 FROM order_boms WHERE work_order=? AND mbom=?', (order['id'], mbom['id'])).fetchone():
            continue
        api.insert(db, 'order_boms', id=api.traceability.document_number(db, 'order_boms'), work_order=order['id'],
                   mbom=mbom['id'], link_type='완료 작업 이력 연결' if order['actual'] else '적용', linked_at=api.now())


def link_new_order(db, api, order_id):
    """A weekly order created after publishing gets the block's current MBOM revision."""
    order = api.lookup(db, 'orders', order_id)
    if order['cadence'] != '주간':
        return
    for mbom in rows(db, '''SELECT DISTINCT m.* FROM mbom_revisions m JOIN mbom_ops p ON p.mbom=m.id
            WHERE p.activity=? AND m.status='발행' ''', order['activity']):
        latest = latest_issued(db, mbom['block'])
        if latest and latest['id'] == mbom['id']:
            link_orders(db, api, mbom, only=order_id)


def stage_gate(db, stage, day, require):
    """A block assembly stage cannot be completed before the MBOM inputs of that process are issued."""
    mbom = latest_issued(db, stage['block'])
    if not mbom:
        return
    for line in db.execute('''SELECT o.activity, i.material, SUM(i.qty) AS qty FROM mbom_inputs i
            JOIN mbom_ops o ON o.id=i.op WHERE i.mbom=? AND o.process=? GROUP BY o.activity, i.material''',
                           (mbom['id'], stage['assembly_stage'])):
        if one(db, 'SELECT actual FROM activities WHERE id=?', line['activity'])['actual']:
            continue
        issued = db.execute('''SELECT COALESCE(SUM(i.qty),0) FROM issues i JOIN bom_requirements b ON b.requirement=i.requirement
            WHERE b.block=? AND b.activity=? AND b.material=? AND i.day<=?''',
                            (stage['block'], line['activity'], line['material'], day)).fetchone()[0]
        require(issued >= line['qty'], f'{stage["assembly_stage"]} 완료 전에 MBOM 투입품 {line["material"]} '
                                       f'{line["qty"]} 중 {issued}만 출고되었습니다. 자재 출고를 먼저 등록하세요.')


# ---------------------------------------------------------------- writes

def write(db, action, v, api):
    require, lookup, insert = api.require, api.lookup, api.insert
    for key, values in CHOICES.items():
        if key in v and v[key] is not None:
            require(v[key] in values, f'{key} 값을 목록에서 선택하세요.')
    if action == 'ebom_revisions':
        drawing = lookup(db, 'drawing_revisions', v['drawing_revision'])
        block = lookup(db, 'blocks', drawing['block'])
        newer = one(db, 'SELECT revision FROM drawing_revisions WHERE drawing_no=? AND recorded_at>? ORDER BY recorded_at DESC LIMIT 1',
                    drawing['drawing_no'], drawing['recorded_at'])
        require(not newer, f'{drawing["drawing_no"]}은(는) {newer["revision"] if newer else ""}가 최신 리비전입니다. 최신 도면으로 EBOM을 만드세요.')
        base = None
        if v['base_ebom']:
            base = lookup(db, 'ebom_revisions', v['base_ebom'])
            require(base['drawing_no'] == drawing['drawing_no'], '같은 도면 번호의 이전 EBOM만 복사할 수 있습니다.')
        key = insert(db, 'ebom_revisions', id=v['id'], hull=block['hull'], block=block['id'],
                     drawing_revision=drawing['id'], drawing_no=drawing['drawing_no'], revision=drawing['revision'],
                     base_ebom=v['base_ebom'], status='작성 중', description=v['description'], created_at=api.now(), issued_at=None)
        if base:
            for item in rows(db, 'SELECT * FROM ebom_items WHERE ebom=? ORDER BY id', base['id']):
                item.update(id=api.traceability.document_number(db, 'ebom_items'), ebom=key)
                insert(db, 'ebom_items', **item)
        return key
    if action == 'ebom_items':
        draft(db, api, 'ebom_revisions', v['ebom'])
        require(v['item_no'].replace('-', '').replace('_', '').isalnum(), '품목 번호는 영문·숫자·-·_ 로 입력하세요.')
        if v['parent_no']:
            require(v['parent_no'] != v['item_no'], '자기 자신을 상위 품목으로 지정할 수 없습니다.')
            parents = {row['item_no']: row for row in rows(db, 'SELECT * FROM ebom_items WHERE ebom=?', v['ebom'])}
            parent = parents.get(v['parent_no'])
            require(parent and parent['item_level'] == '조립품', '같은 EBOM의 조립품을 상위 품목으로 지정하세요.')
            node = parent
            while node and node['parent_no']:
                require(node['parent_no'] != v['item_no'], '품목 구조가 순환합니다.')
                node = parents.get(node['parent_no'])
        if v['item_level'] == '부품':
            require(not db.execute('SELECT 1 FROM ebom_items WHERE ebom=? AND parent_no=?', (v['ebom'], v['item_no'])).fetchone(),
                    '하위 품목이 있는 품목은 조립품이어야 합니다.')
        if v['supply'] == '구매':
            require(v['material'], '구매 설계품목은 구매할 자재 코드를 지정하세요.')
            unit = lookup(db, 'materials', v['material'])['unit']
            require(unit == v['unit'], f'구매 자재 단위({unit})와 설계 단위({v["unit"]})가 같아야 합니다. 단위가 다르면 별도 자재 코드를 쓰세요.')
        elif v['material']:
            lookup(db, 'materials', v['material'])
        return upsert(db, api, 'ebom_items', ('ebom', 'item_no'), dict(v))
    if action == 'ebom_publish':
        ebom = draft(db, api, 'ebom_revisions', v['id'])
        require(db.execute('SELECT 1 FROM ebom_items WHERE ebom=?', (ebom['id'],)).fetchone(), '설계품목을 먼저 등록하세요.')
        db.execute("UPDATE ebom_revisions SET status='발행', issued_at=? WHERE id=?", (api.now(), ebom['id']))
        return ebom['id']
    if action == 'mbom_revisions':
        ebom = lookup(db, 'ebom_revisions', v['ebom'])
        require(ebom['status'] == '발행', '발행된 EBOM 리비전만 MBOM에 적용할 수 있습니다.')
        base = lookup(db, 'mbom_revisions', v['base_mbom']) if v['base_mbom'] else None
        require(not base or base['block'] == ebom['block'], '같은 블록의 이전 MBOM만 복사할 수 있습니다.')
        key = insert(db, 'mbom_revisions', id=v['id'], hull=ebom['hull'], block=ebom['block'], revision=v['revision'],
                     ebom=ebom['id'], base_mbom=v['base_mbom'], status='작성 중', description=v['description'],
                     created_at=api.now(), issued_at=None)
        if base:
            items = {row['item_no']: row['id'] for row in rows(db, 'SELECT id, item_no FROM ebom_items WHERE ebom=?', ebom['id'])}
            old_items = {row['id']: row['item_no'] for row in rows(db, 'SELECT id, item_no FROM ebom_items WHERE ebom=?', base['ebom'])}
            ops, inputs = {}, {}
            for row in rows(db, 'SELECT * FROM mbom_ops WHERE mbom=? ORDER BY seq', base['id']):
                ops[row['id']] = insert(db, 'mbom_ops', id=api.traceability.document_number(db, 'mbom_ops'), mbom=key,
                                        seq=row['seq'], process=row['process'], activity=row['activity'])
            for row in rows(db, 'SELECT * FROM mbom_items WHERE mbom=? ORDER BY id', base['id']):
                if old_items.get(row['ebom_item']) in items:
                    insert(db, 'mbom_items', id=api.traceability.document_number(db, 'mbom_items'), mbom=key,
                           op=ops[row['op']], ebom_item=items[old_items[row['ebom_item']]])
            for row in rows(db, 'SELECT * FROM mbom_inputs WHERE mbom=? ORDER BY id', base['id']):
                inputs[row['id']] = insert(db, 'mbom_inputs', id=api.traceability.document_number(db, 'mbom_inputs'),
                                           mbom=key, op=ops[row['op']], input_type=row['input_type'],
                                           material=row['material'], qty=row['qty'], basis=row['basis'])
            for row in rows(db, 'SELECT * FROM mbom_links WHERE mbom=? ORDER BY id', base['id']):
                if old_items.get(row['ebom_item']) in items:
                    insert(db, 'mbom_links', id=api.traceability.document_number(db, 'mbom_links'), mbom=key,
                           input=inputs[row['input']], ebom_item=items[old_items[row['ebom_item']]])
        return key
    if action == 'mbom_ops':
        mbom = draft(db, api, 'mbom_revisions', v['mbom'])
        activity = lookup(db, 'activities', v['activity'])
        require(activity['block'] == mbom['block'], f'{mbom["block"]} 블록의 공정을 선택하세요.')
        return upsert(db, api, 'mbom_ops', ('mbom', 'seq'), dict(v))
    if action == 'mbom_items':
        op, mbom = mbom_of_op(db, api, v['op'])
        require(lookup(db, 'ebom_items', v['ebom_item'])['ebom'] == mbom['ebom'], '적용 EBOM 리비전의 설계품목을 선택하세요.')
        return upsert(db, api, 'mbom_items', ('mbom', 'ebom_item'), dict(v, mbom=mbom['id']))
    if action == 'mbom_inputs':
        op, mbom = mbom_of_op(db, api, v['op'])
        lookup(db, 'materials', v['material'])
        if v['input_type'] in ('원판', '용접재'):
            require(v['basis'], '원판·용접재는 소요량 산정 근거(네스팅·용접장 등)를 입력하세요.')
        return upsert(db, api, 'mbom_inputs', ('mbom', 'op', 'material'), dict(v, mbom=mbom['id']))
    if action == 'mbom_links':
        line = lookup(db, 'mbom_inputs', v['input'])
        mbom = draft(db, api, 'mbom_revisions', line['mbom'])
        item = lookup(db, 'ebom_items', v['ebom_item'])
        require(item['ebom'] == mbom['ebom'], '적용 EBOM 리비전의 설계품목을 선택하세요.')
        expected = LINKED_TYPES.get(line['input_type'])
        require(expected, f'{line["input_type"]}은(는) 설계 대응 없이 관리하는 투입품입니다. 설계품목을 연결하지 마세요.')
        require(item['supply'] == expected, f'{item["item_no"]}은(는) {item["supply"]} 품목이라 {line["input_type"]} 투입과 연결할 수 없습니다.'
                + (' 제작 부품은 원판에, 구매 품목은 구매품에 연결합니다.' if expected else ''))
        if expected == '제작':
            require(not db.execute('SELECT 1 FROM ebom_items WHERE ebom=? AND parent_no=?', (item['ebom'], item['item_no'])).fetchone(),
                    '조립품은 하위 부품을 통해 원판과 연결됩니다. 부품을 연결하세요.')
        else:
            require(item['material'] == line['material'], f'설계품목 자재 {item["material"]}와 투입품 자재 {line["material"]}가 다릅니다.')
        return upsert(db, api, 'mbom_links', ('mbom', 'ebom_item'), dict(v, mbom=mbom['id']))
    if action == 'mbom_publish':
        mbom = lookup(db, 'mbom_revisions', v['id'])
        problems = publish_problems(db, mbom)
        require(not problems, '발행할 수 없습니다: ' + ' / '.join(problems))
        if mbom['status'] != '발행':
            db.execute("UPDATE mbom_revisions SET status='발행', issued_at=? WHERE id=?", (api.now(), mbom['id']))
            mbom = lookup(db, 'mbom_revisions', v['id'])
        apply(db, api, mbom)
        return mbom['id']
    if action == 'bom_adjustment_close':
        row = lookup(db, 'bom_adjustments', v['id'])
        require(not row['closed_day'], '이미 정리한 조정 항목입니다.')
        require(row['recorded_at'][:10] <= v['closed_day'] <= date.today().isoformat(), '등록일 이후, 오늘 이전 날짜를 입력하세요.')
        db.execute('UPDATE bom_adjustments SET closed_day=?, close_evidence=? WHERE id=?', (v['closed_day'], v['evidence'], v['id']))
        return v['id']
    return insert(db, action, **v)


# ---------------------------------------------------------------- reads

def ebom_views(db):
    latest = {}
    for row in db.execute('SELECT drawing_no, revision, recorded_at FROM drawing_revisions ORDER BY recorded_at, id'):
        latest[row['drawing_no']] = row['revision']
    out = []
    for row in rows(db, '''SELECT e.*, (SELECT COUNT(*) FROM ebom_items i WHERE i.ebom=e.id) AS items,
            (SELECT COUNT(*) FROM mbom_revisions m WHERE m.ebom=e.id) AS mboms FROM ebom_revisions e ORDER BY e.block, e.created_at, e.id'''):
        newest = latest.get(row['drawing_no'])
        row['drawing_check'] = '최신 도면' if newest == row['revision'] else f'도면 {newest} 출도 — EBOM 개정 필요'
        row['label'] = f'{row["drawing_no"]} {row["revision"]} · {row["status"]}'
        out.append(row)
    return out


def item_views(db):
    out = []
    items = rows(db, '''SELECT i.*, e.hull, e.block, e.drawing_no, e.revision, e.status, m.unit AS material_unit
        FROM ebom_items i JOIN ebom_revisions e ON e.id=i.ebom LEFT JOIN materials m ON m.id=i.material
        ORDER BY e.block, e.created_at, i.ebom, i.item_no''')
    by_no = {(row['ebom'], row['item_no']): row for row in items}
    for row in items:
        path, node = [row['item_no']], row
        while node and node['parent_no']:
            path.insert(0, node['parent_no'])
            node = by_no.get((row['ebom'], node['parent_no']))
        row['structure'] = f'{row["block"]} › ' + ' › '.join(path)
        row['label'] = f'{row["item_no"]} {row["name"]} ({row["drawing_no"]} {row["revision"]})'
        out.append(row)
    return out


def mbom_views(db):
    out = []
    for row in rows(db, '''SELECT m.*, e.drawing_no, e.revision AS ebom_revision,
            (SELECT COUNT(*) FROM mbom_ops o WHERE o.mbom=m.id) AS ops,
            (SELECT COUNT(*) FROM mbom_inputs i WHERE i.mbom=m.id) AS inputs
            FROM mbom_revisions m JOIN ebom_revisions e ON e.id=m.ebom ORDER BY m.block, m.created_at, m.id'''):
        problems = publish_problems(db, row)
        latest = latest_issued(db, row['block'])
        row['publish_check'] = ' / '.join(problems) if problems else (
            '발행 가능 — [발행 전 소요 비교] 확인' if row['status'] == '작성 중' else '최신 발행본')
        if row['status'] == '발행' and latest and latest['id'] != row['id']:
            row['publish_check'] = f'{latest["revision"]}로 대체됨 (이력 보존)'
        row['label'] = f'{row["block"]} MBOM {row["revision"]} · {row["status"]}'
        out.append(row)
    return out


def read(db, table):
    if table == 'ebom_revisions':
        return ebom_views(db)
    if table == 'ebom_items':
        return item_views(db)
    if table == 'mbom_revisions':
        return mbom_views(db)
    if table == 'mbom_ops':
        return [{**row, 'label': f'{row["block"]} {row["revision"]} · {row["seq"]} {row["process"]} ({row["activity"]})',
                 'activity_status': f'완료 {row["actual"]}' if row['actual'] else '진행 전·중'}
                for row in rows(db, '''SELECT o.*, m.hull, m.block, m.revision, m.status, a.stage, a.actual
                    FROM mbom_ops o JOIN mbom_revisions m ON m.id=o.mbom JOIN activities a ON a.id=o.activity
                    ORDER BY m.block, m.created_at, o.mbom, o.seq''')]
    if table == 'mbom_items':
        return rows(db, '''SELECT x.id, x.mbom, m.hull, m.block, m.revision, m.status, o.seq, o.process, o.activity,
            x.ebom_item, i.item_no, i.name AS item_name, i.item_level, i.supply, i.design_qty, i.unit
            FROM mbom_items x JOIN mbom_revisions m ON m.id=x.mbom JOIN mbom_ops o ON o.id=x.op
            JOIN ebom_items i ON i.id=x.ebom_item ORDER BY m.block, m.created_at, x.mbom, o.seq, i.item_no''')
    if table == 'mbom_inputs':
        out = []
        for row in rows(db, '''SELECT n.id, n.mbom, m.hull, m.block, m.revision, m.status, o.seq, o.process, o.activity,
                n.input_type, n.material, t.name AS material_name, n.qty, t.unit, n.basis
                FROM mbom_inputs n JOIN mbom_revisions m ON m.id=n.mbom JOIN mbom_ops o ON o.id=n.op
                JOIN materials t ON t.id=n.material ORDER BY m.block, m.created_at, n.mbom, o.seq, n.id'''):
            linked = rows(db, '''SELECT i.item_no, i.design_qty, i.unit, i.net_weight_kg FROM mbom_links l
                JOIN ebom_items i ON i.id=l.ebom_item WHERE l.input=? ORDER BY i.item_no''', row['id'])
            row['design_items'] = ', '.join(f'{i["item_no"]} {i["design_qty"]:g}{i["unit"]}' for i in linked) or None
            weight = sum((i['net_weight_kg'] or 0) * i['design_qty'] for i in linked)
            row['design_net_kg'] = round(weight, 1) if weight else None
            row['design_link'] = '설계 대응 없음 (소모품)' if not linked and row['input_type'] not in LINKED_TYPES else \
                '연결 필요' if not linked else f'설계품목 {len(linked)}건'
            row['label'] = f'{row["block"]} {row["revision"]} · {row["input_type"]} {row["material"]} {row["qty"]}{row["unit"]}'
            out.append(row)
        return out
    if table == 'mbom_links':
        return rows(db, '''SELECT l.id, l.mbom, m.hull, m.block, m.revision, l.input, n.input_type, n.material, n.qty,
            t.unit, l.ebom_item, i.item_no, i.name AS item_name, i.supply, i.design_qty, i.unit AS design_unit
            FROM mbom_links l JOIN mbom_revisions m ON m.id=l.mbom JOIN mbom_inputs n ON n.id=l.input
            JOIN materials t ON t.id=n.material JOIN ebom_items i ON i.id=l.ebom_item ORDER BY m.block, l.mbom, i.item_no''')
    if table == 'mbom_changes':
        out = []
        for mbom in rows(db, 'SELECT * FROM mbom_revisions ORDER BY block, created_at, id'):
            latest = latest_issued(db, mbom['block'])
            if mbom['status'] == '작성 중' or (latest and latest['id'] == mbom['id']):
                for row in plan(db, mbom):
                    out.append({'revision': mbom['revision'], 'mbom_status': mbom['status'], **row})
        return out
    if table == 'bom_adjustments':
        return rows(db, '''SELECT a.*, m.hull, m.revision, t.unit FROM bom_adjustments a JOIN mbom_revisions m ON m.id=a.mbom
            JOIN materials t ON t.id=a.material ORDER BY a.recorded_at DESC, a.id''')
    if table == 'order_boms':
        return rows(db, '''SELECT b.id, b.work_order, o.activity, o.actual, m.hull, m.block, b.mbom, m.revision,
            e.drawing_no, e.revision AS ebom_revision, b.link_type, b.linked_at FROM order_boms b
            JOIN orders o ON o.id=b.work_order JOIN mbom_revisions m ON m.id=b.mbom JOIN ebom_revisions e ON e.id=m.ebom
            ORDER BY b.work_order, b.linked_at''')
    if table == 'bom_trace':
        return trace(db)
    if table == 'requirements':
        sources = {row['requirement']: row for row in rows(db, '''SELECT b.*, m.revision FROM bom_requirements b
            JOIN mbom_revisions m ON m.id=b.mbom''')}
        units = {row['id']: row['unit'] for row in db.execute('SELECT id, unit FROM materials')}
        out = []
        for row in rows(db, 'SELECT * FROM requirements ORDER BY id'):
            source = sources.get(row['id'])
            row['unit'] = units.get(row['material'])
            row['source'] = f'MBOM {source["revision"]} · {source["origin"]}' if source else '수기 등록'
            row['mbom'] = source['mbom'] if source else None
            out.append(row)
        return out
    if table == 'mrp':
        import planning
        sources = {row['requirement']: row for row in rows(db, '''SELECT b.requirement, b.mbom, b.origin, m.revision
            FROM bom_requirements b JOIN mbom_revisions m ON m.id=b.mbom''')}
        units = {row['id']: row['unit'] for row in db.execute('SELECT id, unit FROM materials')}
        out = []
        for row in planning.mrp(db):
            source = sources.get(row['id'])
            out.append({**row, 'unit': units.get(row['material']),
                        'source': f'MBOM {source["revision"]} · {source["origin"]}' if source else '수기 등록',
                        'mbom': source['mbom'] if source else None})
        return out
    return None


def trace(db):
    """EBOM item → MBOM process/input → requirement → purchase/issue, for the latest published MBOM of each block."""
    shortages = None
    out = []
    for mbom in rows(db, "SELECT * FROM mbom_revisions WHERE status='발행' ORDER BY block, issued_at, id"):
        latest = latest_issued(db, mbom['block'])
        if latest['id'] != mbom['id']:
            continue
        if shortages is None:
            import planning
            shortages = {row['id']: row['shortage'] for row in planning.mrp(db)}
        ebom = one(db, 'SELECT * FROM ebom_revisions WHERE id=?', mbom['ebom'])
        for line in rows(db, '''SELECT n.*, o.seq, o.process, o.activity, t.unit, t.name AS material_name
                FROM mbom_inputs n JOIN mbom_ops o ON o.id=n.op JOIN materials t ON t.id=n.material
                WHERE n.mbom=? ORDER BY o.seq, n.id''', mbom['id']):
            items = rows(db, '''SELECT i.* FROM mbom_links l JOIN ebom_items i ON i.id=l.ebom_item
                WHERE l.input=? ORDER BY i.item_no''', line['id']) or [None]
            reqs = rows(db, '''SELECT r.*, b.origin FROM bom_requirements b JOIN requirements r ON r.id=b.requirement
                WHERE b.block=? AND b.activity=? AND b.material=? ORDER BY r.id''', mbom['block'], line['activity'], line['material'])
            activity = one(db, 'SELECT * FROM activities WHERE id=?', line['activity'])
            requirement_ids = ', '.join(r['id'] for r in reqs)
            requested = sum(db.execute('SELECT COALESCE(SUM(qty),0) FROM purchase_requests WHERE requirement=?',
                                       (r['id'],)).fetchone()[0] for r in reqs)
            issued = sum(db.execute(procurement.ISSUED, (r['id'],)).fetchone()[0] for r in reqs)
            shortage = sum(shortages.get(r['id'], 0) for r in reqs)
            status = ('완료 공정 이력' if activity['actual'] and not reqs else '출고 완료' if reqs and issued >= sum(r['qty'] for r in reqs)
                      else '자재 부족' if shortage else '구매요청 진행' if requested else '소요 반영' if reqs else '소요 없음')
            for item in items:
                out.append({'hull': mbom['hull'], 'block': mbom['block'], 'drawing_no': ebom['drawing_no'],
                            'ebom_revision': ebom['revision'], 'item_no': item['item_no'] if item else None,
                            'item_name': item['name'] if item else '(설계 대응 없음)',
                            'design_qty': item['design_qty'] if item else None, 'design_unit': item['unit'] if item else None,
                            'supply': item['supply'] if item else None, 'mbom': mbom['id'], 'mbom_revision': mbom['revision'],
                            'seq': line['seq'], 'process': line['process'], 'activity': line['activity'],
                            'input_type': line['input_type'], 'material': line['material'], 'input_qty': line['qty'],
                            'unit': line['unit'], 'basis': line['basis'], 'requirement': requirement_ids or None,
                            'requested_qty': requested, 'issued_qty': issued, 'shortage': shortage, 'trace_status': status})
    return out
