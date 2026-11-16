"""Add fictional assembly-stage, crane/dock, steel-lot, outfitting and quality data to the SN2036 scenario.

Run after seed_sn2036.py. The script does nothing if SN2036 is missing or already has assembly stages.
It does not create users, so write permissions stay disabled until an administrator is registered.
"""
import os
from contextlib import closing
from datetime import date, timedelta

import app
import storage


def seed(db):
    hull = 'SN2036'
    if not db.execute('SELECT 1 FROM hulls WHERE id=?', (hull,)).fetchone():
        return '먼저 seed_sn2036.py로 SN2036을 등록하세요.'
    if db.execute("SELECT 1 FROM block_stages WHERE block LIKE 'SN2036-%'").fetchone():
        return 'SN2036 생산 확장 데이터가 이미 등록되어 있습니다.'

    def add(action, **values):
        return app.write(db, action, values)

    today = date.today()
    monday = today - timedelta(days=today.weekday())

    def week(offset, weekday=0):
        return (monday + timedelta(weeks=offset, days=weekday)).isoformat()

    def past(days):
        return (today - timedelta(days=days)).isoformat()

    block = {code: f'{hull}-{code}' for code in ('211A', '211B', '211P', '212P', '310C')}
    add('contractors', id='SN-SUB-W', name='한바다 용접 (가상)', trade='용접')
    add('contractors', id='SN-SUB-P', name='청솔 도장 (가상)', trade='도장')
    add('equipment', id='SN-GC1', name='2도크 골리앗 크레인 (가상)')
    add('equipment_specs', equipment='SN-GC1', equipment_type='골리앗 크레인', capacity_t=900, station='SN-D02')
    add('equipment', id='SN-TP1', name='블록 트랜스포터 (가상)')
    add('equipment_specs', equipment='SN-TP1', equipment_type='트랜스포터', capacity_t=500, station='')
    add('dock_bookings', dock='SN-D02', hull=hull, start_day=week(5), end_day=week(16, 4), reason='')
    add('holidays', holiday=week(7, 2), name='모의 정기 휴무')

    stages = [
        ('211A', '절단·가공', 'SN-WC-FAB', 120, -3, [(past(20), 70, 60, ''), (past(18), 55, 100, '')]),
        ('211A', '소조립', 'SN-WC-FAB', 260, -2, [(past(14), 140, 55, 'SN-SUB-W'), (past(10), 130, 100, 'SN-SUB-W')]),
        ('211A', '중조립', 'SN-WC-FAB', 420, 0, [(past(5), 180, 45, 'SN-SUB-W')]),
        ('211B', '절단·가공', 'SN-WC-FAB', 130, -2, [(past(12), 140, 100, '')]),
        ('211B', '소조립', 'SN-WC-FAB', 280, 0, [(past(4), 90, 30, 'SN-SUB-W')]),
        ('211B', '중조립', 'SN-WC-FAB', 450, 1, []),
        ('211P', '대조립', 'SN-WC-FAB', 900, 3, []),
        ('211P', '선행탑재(PE)', 'SN-WC-OUT', 600, 5, []),
        ('212P', '대조립', 'SN-WC-FAB', 980, 4, []),
        ('310C', '대조립', 'SN-WC-FAB', 1200, 3, []),
        ('310C', '선행탑재(PE)', 'SN-WC-OUT', 700, 5, []),
    ]
    for code, stage, center, planned, offset, logs in stages:
        key = add('block_stages', block=block[code], assembly_stage=stage, center=center, planned_mh=planned,
                  baseline=week(offset), forecast=week(offset))
        for work_day, hours, progress, contractor in logs:
            add('work_logs', block_stage=key, work_day=work_day, manhours=hours, progress_pct=progress,
                contractor=contractor, note='')

    spool = add('outfitting_items', hull=hull, block=block['211P'], item_type='배관 스풀', name='화물창 밸러스트 배관 스풀 12EA',
                outfit_stage='선행의장', required_day=week(4))
    add('outfitting_events', item=spool, event='입고', event_day=past(2), evidence='가상 납품서 SP-2036-011')
    add('outfitting_items', hull=hull, block=block['310C'], item_type='기자재', name='주기관 대판 지지 철의장',
        outfit_stage='선행의장', required_day=week(4))
    add('outfitting_items', hull=hull, block='', item_type='기자재', name='화물 압축기 (가상)',
        outfit_stage='후행의장', required_day=week(18))

    drawing = f'{hull}-STR-211P'
    add('drawing_revisions', drawing_no=drawing, block=block['211P'], revision='R02', issued_day=past(30),
        change_type='최초 출도', description='이중저 좌현 구조도 출도')
    add('drawing_revisions', drawing_no=drawing, block=block['211P'], revision='R03', issued_day=past(3),
        change_type='설계 변경', description='선주 코멘트 반영: 거더 보강재 2EA 추가')

    point = add('inspection_points', hull=hull, block=block['211A'], activity_ref=f'{hull}-FAB-211A',
                item='이중저 맞대기 용접 UT', witness='선급', planned_day=past(1))
    add('inspection_records', point=point, inspect_day=past(1), inspection='불합격', inspector='모의 선급 검사관',
        remarks='거더 맞대기 용접부 UT 결함 지시 2개소')
    add('inspection_points', hull=hull, block=block['310C'], activity_ref='', item='기관실 이중저 치수 검사',
        witness='선주', planned_day=week(4))
    add('punch_items', hull=hull, block=block['212P'], category='C', description='측외판 임시 러그 제거 흔적 그라인딩',
        raised_by='선주', raised_day=past(1))

    receipt = db.execute('''SELECT g.id FROM goods_receipts g JOIN purchase_orders p ON p.id=g.purchase_order
        JOIN purchase_requests q ON q.id=p.request JOIN requirements r ON r.id=q.requirement
        JOIN activities a ON a.id=r.activity WHERE a.hull=? AND g.inspection='합격' ORDER BY g.id''', (hull,)).fetchone()
    if receipt:
        add('receipt_lots', receipt=receipt['id'], heat_no='HT-24A1187', cert_no='MC-DH-2036-01', qty=5)
        add('receipt_lots', receipt=receipt['id'], heat_no='HT-24A1192', cert_no='MC-DH-2036-02', qty=3)
    return 'SN2036 생산 확장 데이터 등록 완료'


if __name__ == '__main__':
    app.DATABASE_URL = os.environ['SHIPYARD_DATABASE_URL']
    with closing(app.connect()) as db, db:
        storage.init_schema(db, app.SCHEMA)
    with closing(app.connect()) as db, db:
        storage.begin_write(db)
        print(seed(db))
