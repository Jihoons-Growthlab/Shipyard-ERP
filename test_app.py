import io
import json
import os
import tempfile
import unittest
from concurrent.futures import ThreadPoolExecutor
from contextlib import closing
from datetime import date, timedelta
from pathlib import Path
from uuid import uuid4

import app
import storage


class Request:
    def __init__(self, request):
        self.input = io.BytesIO(request)
        self.output = io.BytesIO()

    def sendall(self, data):
        self.output.write(data)


    def makefile(self, mode, *args):
        if mode == 'rb':
            return self.input
        return self.output


class QuietHandler(app.Handler):
    def log_message(self, *args):
        pass


class ShipYardTest(unittest.TestCase):
    def setUp(self):
        url = os.environ.get('SHIPYARD_TEST_POSTGRES_URL')
        self.addCleanup(setattr, app, 'DATABASE_URL', app.DATABASE_URL)
        if url:
            schema = 'test_' + uuid4().hex
            with closing(storage.Postgres(url)) as db, db:
                db.execute(f'CREATE SCHEMA {schema}')
            self.addCleanup(self.drop_schema, url, schema)
            app.DATABASE_URL = storage.psycopg.conninfo.make_conninfo(url, options=f'-c search_path={schema}')
            with closing(app.connect()) as db, db:
                storage.init_schema(db, app.SCHEMA)
                app.seed(db)
            return
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.addCleanup(setattr, app, 'DATABASE', app.DATABASE)
        app.DATABASE = Path(directory.name) / 'test.sqlite3'
        with closing(app.connect()) as db, db:
            db.executescript(app.SCHEMA)
            app.seed(db)

    @staticmethod
    def drop_schema(url, schema):
        with closing(storage.Postgres(url)) as db, db:
            db.execute(f'DROP SCHEMA {schema} CASCADE')

    def request(self, route, data=None, expected=200, user=None):
        method = 'GET' if data is None else 'POST'
        body = b'' if data is None else json.dumps(data).encode()
        headers = f'Content-Type: application/json\r\nContent-Length: {len(body)}\r\n'
        if user:
            headers += f'X-ShipYard-User: {user}\r\n'
        wire = Request(f'{method} {route} HTTP/1.0\r\n{headers}\r\n'.encode() + body)
        QuietHandler(wire, ('127.0.0.1', 0), None)
        response, body = wire.output.getvalue().split(b'\r\n\r\n', 1)
        status = int(response.split()[1])
        if expected is None:
            return status
        self.assertEqual(status, expected, body)
        return json.loads(body) if b'application/json' in response else body

    def api(self, route, data=None, expected=None, user=None):
        return self.request('/api/' + route, data, expected or (201 if data is not None else 200), user)

    def test_plan_snapshot_and_validation(self):
        self.assertIn('ShipYard'.encode(), self.request('/'))
        first = self.api('plans')[0]
        self.assertEqual(len(first['snapshot']), 6)
        self.api('plans', {'start': '2026-11-17'}, 400)
        activity = first['snapshot'][2]
        self.api('forecast', {'id': 'A3', 'forecast': '2099-01-01'}, 400)
        self.api('forecast', {'id': 'A3', 'forecast': '2099-01-01', 'reason': '재발주'})
        self.api('plans', {'start': first['start']})
        plans = self.api('plans')
        self.assertEqual(plans[0], first)
        latest = next(row for row in plans[-1]['snapshot'] if row['id'] == 'A3')
        self.assertNotEqual(latest['forecast'], activity['forecast'])
        self.assertEqual(latest['baseline'], activity['baseline'])

    def test_work_order_reinspection_and_rollback(self):
        start = self.api('plans')[0]['start']
        self.api('orders', {'id': 'D2', 'activity': 'A1', 'cadence': '일일',
                            'day': start, 'parent': 'W1', 'instruction': '검사'})
        weekly = {'id': 'W1', 'actual': start, 'result': '완료', 'inspection': '합격'}
        self.api('results', weekly, 400)
        daily = {**weekly, 'id': 'D2'}
        self.api('results', {**daily, 'inspection': '불합격'})
        self.api('results', weekly, 400)
        self.api('results', daily)
        self.api('results', {**weekly, 'actual': '2099-01-01'}, 400)
        self.assertIsNone(next(row for row in self.api('orders') if row['id'] == 'W1')['actual'])
        self.api('results', weekly)
        self.assertEqual(self.api('activities')[0]['actual_variance_days'], 0)

    def test_vessel_detail_update_and_planning_constraints(self):
        original = self.api('vessel_details')[0]
        self.api('vessel_details', {**original, 'length_m': 299.125})
        self.assertEqual(self.api('vessel_details')[0]['length_m'], 299.125)
        self.api('vessel_details', {**original, 'length_m': -1}, 400)
        need = self.api('mrp')[0]
        self.assertEqual(need['shortage'], 5)
        self.assertEqual([row['planning_status'] for row in self.api('aps')],
                         ['자재 부족', '선행 작업 보류'])
        self.api('receipts', {'id': 'R1', 'material': 'PLATE', 'qty': 5, 'receipt_day': need['need_day']})
        self.assertEqual(self.api('mrp')[0]['shortage'], 0)
        self.api('operations', {'activity': 'A4', 'center': 'C1', 'duration': 8,
                                'earliest': need['need_day']})
        scheduled = {row['activity']: row for row in self.api('aps')}
        self.assertLess(scheduled['A2']['scheduled_end'], scheduled['A3']['scheduled_start'])
        self.assertLess(scheduled['A3']['scheduled_end'], scheduled['A4']['scheduled_start'])
        self.assertEqual(scheduled['A2']['scheduled_start'], need['need_day'])
        self.assertEqual(self.api('activities')[1]['forecast'], scheduled['A2']['forecast'])

    def test_procure_receive_issue_and_post_actual(self):
        today = date.today().isoformat()
        week = self.api('plans')[0]['start']
        self.api('suppliers', {'id': 'S1', 'name': '강판 공급사'})
        self.api('purchase_requests', {'id': 'PR1', 'requirement': 'REQ1', 'qty': 5})
        self.api('purchase_orders', {'id': 'PO1', 'request': 'PR1', 'supplier': 'S1', 'promised_day': today})
        self.assertEqual(self.api('mrp')[0]['shortage'], 0)
        self.api('orders', {'id': 'W2', 'activity': 'A2', 'cadence': '주간',
                            'day': week, 'instruction': '조립'})
        actual = {'id': 'W2', 'actual': today, 'result': '조립 완료', 'inspection': '합격'}
        self.api('results', actual, 400)
        issue = {'id': 'I1', 'requirement': 'REQ1', 'work_order': 'W2', 'qty': 25, 'day': today, 'source': 'STORE'}
        self.api('issues', issue, 400)
        receipt = {'id': 'G1', 'purchase_order': 'PO1', 'day': today, 'qty': 2, 'inspection': '불합격', 'location': 'STORE'}
        self.api('goods_receipts', receipt)
        self.assertEqual(self.api('materials')[0]['stock'], 20)
        self.assertEqual(self.api('purchase_orders')[0]['open_qty'], 5)
        self.api('goods_receipts', {**receipt, 'id': 'G2', 'inspection': '합격'})
        self.assertEqual(self.api('purchase_orders')[0]['open_qty'], 3)
        self.api('goods_receipts', {**receipt, 'id': 'G3', 'qty': 4, 'inspection': '합격'}, 400)
        self.api('issues', issue, 400)
        self.api('goods_receipts', {**receipt, 'id': 'G3', 'qty': 3, 'inspection': '합격'})
        self.assertEqual(self.api('purchase_orders')[0]['open_qty'], 0)
        self.api('issues', issue)
        self.assertEqual(self.api('materials')[0]['stock'], 0)
        self.assertEqual(self.api('warehouse_stock')[0]['qty'], 0)
        self.assertEqual(self.api('issues')[0]['destination'], 'SHOP')
        ledger = self.api('inventory_ledger')
        self.assertEqual([(row['source_document'], row['qty_delta'], row['balance']) for row in ledger],
                         [('OPEN-PLATE', 20, 20), ('G2', 2, 22), ('G3', 3, 25), ('I1', -25, 0)])
        self.assertEqual(self.api('mrp')[0]['remaining_qty'], 0)
        self.api('results', actual)
        self.assertEqual(self.api('activities')[1]['actual'], today)

    def test_concurrent_issues_do_not_overdraw_stock(self):
        week = self.api('plans')[0]['start']
        self.api('orders', {'id': 'W2', 'activity': 'A2', 'cadence': '주간', 'day': week, 'instruction': '조립'})
        self.api('requirements', {'id': 'REQ2', 'activity': 'A2', 'material': 'PLATE',
                                  'qty': 25, 'need_day': week})

        def issue(index):
            return self.request('/api/issues', {'id': f'I{index}', 'requirement': f'REQ{index}',
                'work_order': 'W2', 'qty': 15, 'day': date.today().isoformat(), 'source': 'STORE'}, None)

        with ThreadPoolExecutor(max_workers=2) as executor:
            self.assertEqual(sorted(executor.map(issue, (1, 2))), [201, 400])
        self.assertEqual(self.api('materials')[0]['stock'], 5)
        self.assertEqual(self.api('warehouse_stock')[0]['qty'], 5)
        self.assertEqual(len(self.api('issues')), 1)

    def test_document_numbering_and_plan_lineage(self):
        plan = self.api('plans')[0]
        self.assertEqual(plan['document_no'], 'PLAN-000001')
        work = {'activity': 'A1', 'cadence': '주간', 'day': plan['start'],
                'instruction': '도면 확인', 'plan_id': str(plan['id'])}
        self.api('orders', {**work, 'day': '2099-01-05'}, 400)
        order = self.api('orders', work)['id']
        self.assertEqual(order, 'WO-000001')
        child = self.api('orders', {**work, 'cadence': '일일', 'plan_id': '', 'parent': order})['id']
        by_id = {row['id']: row for row in self.api('orders')}
        self.assertEqual(by_id[child]['plan_id'], plan['id'])
        actual = {'id': child, 'actual': plan['start'], 'result': '도면 확인'}
        failed = self.api('results', {**actual, 'inspection': '불합격'})['id']
        passed = self.api('results', {**actual, 'inspection': '합격'})['id']
        results = {row['id']: row for row in self.api('production_results')}
        self.assertNotEqual(failed, passed)
        self.assertEqual(results[failed]['inspection'], '불합격')
        self.assertEqual(results[passed]['work_order'], child)

    def test_shipbuilding_dates_relationships_and_readiness(self):
        self.api('hull_milestones', {'hull':'H101','milestone':'진수','baseline':'2027-01-04',
                  'forecast':'2027-01-04','reason':''})
        milestone = self.api('hull_milestones')[0]
        self.api('milestone_update', {'id':milestone['id'],'forecast':'2027-01-05',
                  'actual':'','reason':''}, 400)
        self.api('milestone_update', {'id':milestone['id'],'forecast':'2027-01-05',
                  'actual':'','reason':'도크 일정 조정'})
        self.assertEqual(self.api('hull_milestones')[0]['baseline'], '2027-01-04')
        self.assertEqual(len(self.api('milestone_history')), 1)
        self.api('block_structure', {'parent_block':'B101','child_block':'B102'})
        self.api('block_structure', {'parent_block':'B102','child_block':'B101'}, 400)
        self.api('erection_links', {'predecessor_block':'B101','successor_block':'B102'})
        self.api('erection_links', {'predecessor_block':'B102','successor_block':'B101'}, 400)
        self.api('hulls', {'id':'OTHER','name':'다른 호선'})
        self.api('blocks', {'id':'OTHER-B','hull':'OTHER','location':''})
        self.api('erection_links', {'predecessor_block':'B101','successor_block':'OTHER-B'}, 400)
        readiness = {row['id']:row for row in self.api('block_readiness')}
        self.assertIn('도면 출도 미확인', readiness['B101']['blockers'])
        self.assertIn('구성 블록 조립 미확인 B102', readiness['B101']['blockers'])
        self.assertIn('선행 블록 탑재 미확인 B101', readiness['B102']['blockers'])
        for gate in app.shipbuilding.GATES:
            self.api('block_gate_checks', {'block':'B102','gate':gate,'verdict':'확인','evidence':'검증용 근거'})
        self.api('erection_plans', {'block':'B102','destination':'DOCK','baseline':'2027-01-04',
                                    'forecast':'2027-01-04','reason':''})
        readiness = {row['id']:row for row in self.api('block_readiness')}
        self.assertEqual(readiness['B102']['readiness'], '준비 필요')
        self.assertIn('선행 블록', readiness['B102']['blockers'])

    def test_movement_and_telemetry(self):
        before = self.api('blocks')
        self.assertEqual([(row['position_status'], row['location']) for row in before],
                         [('in_transit', 'SHOP'), ('unknown', None)])
        self.api('telemetry', {'move': 'M1', 'x': 100, 'y': 100, 'load': 0, 'status': '정지'})
        self.assertEqual(self.api('blocks'), before)
        move = {'id': 'M2', 'block': 'B102', 'equipment': 'CR1', 'destination': 'QUAY'}
        self.api('moves', move, 409)
        self.api('confirm', {'id': 'M1'})
        self.api('confirm', {'id': 'M1'}, 400)
        self.assertEqual(self.api('blocks')[0]['position_status'], 'confirmed')
        self.assertEqual(self.api('blocks')[0]['location'], 'DOCK')
        self.api('moves', move)
        self.assertEqual(self.api('blocks')[1]['position_status'], 'in_transit')
        self.assertIsNone(self.api('moves')[1]['origin'])

    def ready(self, block):
        return {row['id']: row for row in self.api('block_readiness')}[block]

    def test_assembly_stages_manhours_and_progress(self):
        today = date.today().isoformat()
        self.api('contractors', {'id': 'SUB1', 'name': '모의 용접 협력사', 'trade': '용접'})
        small = self.api('block_stages', {'block': 'B102', 'assembly_stage': '소조립', 'center': 'C1',
                                          'planned_mh': 100, 'baseline': today, 'forecast': today})['id']
        grand = self.api('block_stages', {'block': 'B102', 'assembly_stage': '대조립', 'center': 'C1',
                                          'planned_mh': 300, 'baseline': today, 'forecast': today})['id']
        parent = self.api('block_stages', {'block': 'B101', 'assembly_stage': '대조립', 'center': 'C1',
                                           'planned_mh': 200, 'baseline': today, 'forecast': today})['id']
        self.api('block_structure', {'parent_block': 'B101', 'child_block': 'B102'})
        log = {'work_day': today, 'contractor': '', 'note': ''}
        self.api('work_logs', {**log, 'block_stage': grand, 'manhours': 10, 'progress_pct': 100}, 400)
        self.api('work_logs', {**log, 'block_stage': small, 'manhours': 40, 'progress_pct': 50, 'contractor': 'SUB1'})
        self.api('work_logs', {**log, 'block_stage': small, 'manhours': 5, 'progress_pct': 40}, 400)
        self.api('work_logs', {**log, 'block_stage': small, 'manhours': 60, 'progress_pct': 101}, 400)
        self.api('work_logs', {**log, 'block_stage': small, 'manhours': 60, 'progress_pct': 100, 'contractor': 'SUB1'})
        self.api('work_logs', {**log, 'block_stage': small, 'manhours': 1, 'progress_pct': 100}, 400)
        self.api('work_logs', {**log, 'block_stage': parent, 'manhours': 10, 'progress_pct': 100}, 400)
        self.api('work_logs', {**log, 'block_stage': grand, 'manhours': 100, 'progress_pct': 50})
        progress = {row['block']: row for row in self.api('block_progress')}
        self.assertEqual((progress['B102']['planned_mh'], progress['B102']['actual_mh']), (400, 200))
        self.assertEqual(progress['B102']['progress_pct'], 62.5)
        self.assertEqual(progress['B102']['productivity'], 1.25)
        self.assertEqual(progress['B102']['current_stage'], '대조립')
        hull = self.api('hull_progress')[0]
        self.assertEqual((hull['weight_t'], hull['weight_progress_pct'], hull['mh_progress_pct']), (120, 0.0, 41.7))
        summary = {row['contractor']: row['actual_mh'] for row in self.api('contractor_summary')}
        self.assertEqual(summary, {'SUB1': 100, '직영': 100})
        self.assertIn('조립 단계 미완료 대조립', self.ready('B102')['blockers'])

    def test_crane_capacity_dock_booking_schedule_and_erection(self):
        today = date.today()
        forecast = today + timedelta(days=14 - today.weekday())
        plan = {'destination': 'DOCK', 'baseline': forecast.isoformat(), 'forecast': forecast.isoformat(), 'reason': ''}
        self.api('equipment', {'id': 'TR1', 'name': '블록 트랜스포터'})
        self.api('equipment_specs', {'equipment': 'TR1', 'equipment_type': '트랜스포터', 'capacity_t': 100, 'station': ''})
        self.api('block_details', {'block': 'B102', 'zone': '선수', 'assembly': '대조립', 'weight_t': 150, 'drawing': 'D'})
        self.api('moves', {'block': 'B102', 'equipment': 'TR1', 'destination': 'YARD'}, 400)
        self.api('equipment_specs', {'equipment': 'TR1', 'equipment_type': '트랜스포터', 'capacity_t': 200, 'station': ''})
        self.api('moves', {'block': 'B102', 'equipment': 'TR1', 'destination': 'YARD'})
        self.api('equipment_specs', {'equipment': 'CR1', 'equipment_type': '갠트리 크레인', 'capacity_t': 100, 'station': 'DOCK'})
        self.api('erection_plans', {**plan, 'block': 'B101'})
        self.api('erection_plans', {**plan, 'block': 'B102'})
        self.api('erection_links', {'predecessor_block': 'B101', 'successor_block': 'B102'})
        blockers = self.ready('B101')['blockers']
        self.assertIn('도크 점유 예약 없음', blockers)
        self.assertIn('크레인 용량 부족 (120.0t > 100.0t)', blockers)
        schedule = {row['block']: row for row in self.api('erection_schedule')}
        self.assertEqual(schedule['B101']['planning_status'], '크레인 용량 부족')
        self.assertEqual(schedule['B102']['planning_status'], '선행 블록 보류')
        self.api('equipment_specs', {'equipment': 'CR1', 'equipment_type': '갠트리 크레인', 'capacity_t': 400, 'station': 'DOCK'})
        self.api('holidays', {'holiday': forecast.isoformat(), 'name': '모의 휴무'})
        booking = {'dock': 'DOCK', 'hull': 'H101', 'start_day': today.isoformat(),
                   'end_day': (forecast + timedelta(days=30)).isoformat(), 'reason': ''}
        self.api('dock_bookings', booking)
        self.api('hulls', {'id': 'H102', 'name': '다른 호선'})
        self.api('dock_bookings', {**booking, 'hull': 'H102', 'start_day': forecast.isoformat()}, 400)
        schedule = {row['block']: row for row in self.api('erection_schedule')}
        self.assertEqual(schedule['B101']['scheduled_day'], (forecast + timedelta(days=1)).isoformat())
        self.assertEqual(schedule['B102']['scheduled_day'], (forecast + timedelta(days=2)).isoformat())
        self.assertEqual(schedule['B101']['crane'], 'CR1')
        self.assertNotIn('도크', self.ready('B101')['blockers'])
        # A clean block goes through the full erection proof: move confirmed, all gates, inspection record.
        self.api('blocks', {'id': 'B201', 'hull': 'H101', 'location': 'SHOP'})
        self.api('block_details', {'block': 'B201', 'zone': '선미', 'assembly': '대조립', 'weight_t': 80, 'drawing': 'D'})
        self.api('erection_plans', {**plan, 'block': 'B201'})
        record = {'block': 'B201', 'dock': 'DOCK', 'erect_day': today.isoformat(), 'inspection': '합격', 'evidence': '탑재 검사'}
        self.api('erection_records', record, 400)
        milestone = self.api('hull_milestones', {'hull': 'H101', 'milestone': '기공', 'baseline': today.isoformat(),
                                                 'forecast': today.isoformat(), 'reason': ''})['id']
        keel = {'id': milestone, 'forecast': today.isoformat(), 'actual': today.isoformat(), 'reason': '실적 등록'}
        self.api('milestone_update', keel, 400)
        self.api('equipment', {'id': 'CR2', 'name': '골리앗 크레인'})
        move = self.api('moves', {'block': 'B201', 'equipment': 'CR2', 'destination': 'DOCK'})['id']
        self.api('confirm', {'id': move})
        self.api('erection_records', record, 400)
        for gate in app.shipbuilding.GATES:
            self.api('block_gate_checks', {'block': 'B201', 'gate': gate, 'verdict': '확인', 'evidence': '근거'})
        self.assertEqual(self.ready('B201')['readiness'], '준비 확인')
        self.api('erection_records', record)
        self.api('erection_records', record, 400)
        self.assertEqual(self.ready('B201')['readiness'], '탑재 완료')
        self.api('milestone_update', keel)
        launch = self.api('hull_milestones', {'hull': 'H101', 'milestone': '진수', 'baseline': today.isoformat(),
                                              'forecast': today.isoformat(), 'reason': ''})['id']
        self.api('milestone_update', {**keel, 'id': launch}, 400)

    def test_steel_heat_lots_returns_and_rollup(self):
        today = date.today().isoformat()
        week = self.api('plans')[0]['start']
        self.api('suppliers', {'id': 'S1', 'name': '강판 공급사'})
        self.api('purchase_requests', {'id': 'PR1', 'requirement': 'REQ1', 'qty': 5})
        self.api('purchase_orders', {'id': 'PO1', 'request': 'PR1', 'supplier': 'S1', 'promised_day': today})
        self.api('goods_receipts', {'id': 'G0', 'purchase_order': 'PO1', 'day': today, 'qty': 1,
                                    'inspection': '불합격', 'location': 'STORE'})
        self.api('goods_receipts', {'id': 'G1', 'purchase_order': 'PO1', 'day': today, 'qty': 5,
                                    'inspection': '합격', 'location': 'STORE'})
        self.api('receipt_lots', {'receipt': 'G0', 'heat_no': 'H-0', 'cert_no': 'C-0', 'qty': 1}, 400)
        lot1 = self.api('receipt_lots', {'receipt': 'G1', 'heat_no': 'H-1', 'cert_no': 'MC-1', 'qty': 3})['id']
        self.api('receipt_lots', {'receipt': 'G1', 'heat_no': 'H-2', 'cert_no': 'MC-2', 'qty': 3}, 400)
        lot2 = self.api('receipt_lots', {'receipt': 'G1', 'heat_no': 'H-2', 'cert_no': 'MC-2', 'qty': 2})['id']
        self.api('orders', {'id': 'W2', 'activity': 'A2', 'cadence': '주간', 'day': week, 'instruction': '조립'})
        self.api('issues', {'id': 'I1', 'requirement': 'REQ1', 'work_order': 'W2', 'qty': 25, 'day': today, 'source': 'STORE'})
        self.api('issue_lots', {'issue': 'I1', 'lot': lot1, 'qty': 4}, 400)
        self.api('issue_lots', {'issue': 'I1', 'lot': lot1, 'qty': 3})
        self.api('issue_lots', {'issue': 'I1', 'lot': lot2, 'qty': 2})
        trace = self.api('block_traceability')
        self.assertEqual(sorted((row['heat_no'], row['qty']) for row in trace if row['block'] == 'B101'),
                         [('H-1', 3), ('H-2', 2), ('로트 미배정', 20)])
        self.assertEqual({row['supplier'] for row in trace if row['lot']}, {'S1'})
        remnant = {'issue': 'I1', 'lot': lot1, 'return_type': '잔재', 'qty': 1, 'return_day': today, 'warehouse': 'STORE'}
        self.api('material_returns', {**remnant, 'warehouse': ''}, 400)
        self.api('material_returns', remnant)
        self.api('material_returns', {**remnant, 'lot': '', 'return_type': '폐기', 'qty': 30, 'warehouse': ''}, 400)
        self.assertEqual(self.api('materials')[0]['stock'], 1)
        self.assertEqual(self.api('warehouse_stock')[0]['qty'], 1)
        self.assertEqual(self.api('inventory_ledger')[-1]['movement'], '잔재 입고')
        self.assertEqual({row['id']: row['balance'] for row in self.api('receipt_lots')}[lot1], 1)
        self.api('block_structure', {'parent_block': 'B101', 'child_block': 'B102'})
        self.api('activities', {'id': 'A9', 'hull': 'H101', 'block': 'B102', 'stage': '가공·조립',
                                'baseline': week, 'forecast': week, 'reason': ''})
        self.api('requirements', {'id': 'REQ9', 'activity': 'A9', 'material': 'PLATE', 'qty': 4, 'need_day': week})
        rollup = {row['block']: row for row in self.api('material_rollup')}
        self.assertEqual((rollup['B101']['own_qty'], rollup['B101']['tree_qty'], rollup['B101']['levels']), (25, 29, 2))
        self.assertEqual(rollup['B101']['remaining_qty'], 4)

    def test_itp_ncr_punch_outfitting_and_delivery_gate(self):
        today = date.today().isoformat()
        point = self.api('inspection_points', {'hull': 'H101', 'block': 'B101', 'activity_ref': '',
                                               'item': '용접 비파괴검사', 'witness': '선급', 'planned_day': today})['id']
        record = {'point': point, 'inspect_day': today, 'inspector': '모의 선급 검사관', 'remarks': '균열 지시'}
        self.api('inspection_records', {**record, 'inspection': '불합격'})
        ncr = self.api('ncrs')[0]
        self.assertEqual((ncr['point'], ncr['disposition'], ncr['description']), (point, '재작업', '균열 지시'))
        blockers = self.ready('B101')['blockers']
        self.assertIn('미결 NCR 1건', blockers)
        self.assertIn('ITP 검사 미합격 1건', blockers)
        close = {'id': ncr['id'], 'closed_day': today, 'evidence': '재용접 후 UT 합격'}
        self.api('ncr_close', close, 400)
        self.api('inspection_records', {**record, 'inspection': '합격', 'remarks': ''})
        self.api('inspection_records', {**record, 'inspection': '합격'}, 400)
        self.api('ncr_close', close)
        self.api('ncr_close', close, 400)
        self.assertNotIn('NCR', self.ready('B101')['blockers'])
        self.api('outfitting_items', {'hull': 'H101', 'block': '', 'item_type': '배관 스풀', 'name': 'BW 스풀',
                                      'outfit_stage': '선행의장', 'required_day': today}, 400)
        item = self.api('outfitting_items', {'hull': 'H101', 'block': 'B101', 'item_type': '배관 스풀',
                                             'name': 'BW 스풀', 'outfit_stage': '선행의장', 'required_day': today})['id']
        self.assertIn('선행의장품 미설치 1건', self.ready('B101')['blockers'])
        event = {'item': item, 'event_day': today, 'evidence': '현장 확인'}
        self.api('outfitting_events', {**event, 'event': '설치'}, 400)
        self.api('outfitting_events', {**event, 'event': '입고'})
        self.api('outfitting_events', {**event, 'event': '설치'})
        self.assertNotIn('선행의장품', self.ready('B101')['blockers'])
        self.assertEqual(self.api('outfitting_items')[0]['status'], '설치')
        punch = self.api('punch_items', {'hull': 'H101', 'block': '', 'category': 'A', 'description': '비상 발전기 기동 불량',
                                         'raised_by': '선주', 'raised_day': today})['id']
        delivery = self.api('hull_milestones', {'hull': 'H101', 'milestone': '인도', 'baseline': today,
                                                'forecast': today, 'reason': ''})['id']
        deliver = {'id': delivery, 'forecast': today, 'actual': today, 'reason': '인도 실적'}
        self.api('milestone_update', deliver, 400)
        self.api('punch_close', {'id': punch, 'closed_day': today, 'evidence': '재시험 합격'})
        self.api('milestone_update', deliver, 400)
        self.api('outfitting_events', {**event, 'event': '검사 완료'})
        self.api('milestone_update', deliver)

    def test_drawing_revision_requires_reconfirmed_release(self):
        today = date.today().isoformat()
        drawing = {'drawing_no': 'D-B101', 'block': 'B101', 'issued_day': today, 'description': '구조 도면'}
        self.api('drawing_revisions', {**drawing, 'revision': 'R01', 'change_type': '설계 변경'}, 400)
        self.api('drawing_revisions', {**drawing, 'revision': 'R01', 'change_type': '최초 출도'})
        self.assertIn('최신 도면 D-B101 R01 출도 확인 필요', self.ready('B101')['blockers'])
        self.api('block_gate_checks', {'block': 'B101', 'gate': '도면 출도', 'verdict': '확인', 'evidence': 'R01'})
        self.assertNotIn('최신 도면', self.ready('B101')['blockers'])
        self.api('drawing_revisions', {**drawing, 'revision': 'R02', 'change_type': '설계 변경', 'description': '보강재 추가'})
        self.assertIn('최신 도면 D-B101 R02 출도 확인 필요', self.ready('B101')['blockers'])

    def test_aps_holidays_and_multiple_predecessors(self):
        need = self.api('mrp')[0]
        self.api('receipts', {'id': 'R1', 'material': 'PLATE', 'qty': 5, 'receipt_day': need['need_day']})
        self.api('operations', {'activity': 'A4', 'center': 'C1', 'duration': 8, 'earliest': need['need_day']})
        self.api('operation_links', {'activity': 'A4', 'predecessor': 'A3'})
        self.api('operation_links', {'activity': 'A2', 'predecessor': 'A4'}, 400)
        self.api('operation_links', {'activity': 'A4', 'predecessor': ''}, 400)
        before = {row['activity']: row for row in self.api('aps')}
        self.api('holidays', {'holiday': need['need_day'], 'name': '모의 휴무'})
        after = {row['activity']: row for row in self.api('aps')}
        self.assertGreater(after['A2']['scheduled_start'], before['A2']['scheduled_start'])
        self.assertNotEqual(after['A2']['scheduled_start'], need['need_day'])
        self.assertLess(after['A3']['scheduled_end'], after['A4']['scheduled_start'])
        self.assertEqual(after['A4']['predecessor'], 'A3')

    def test_roles_and_audit_log(self):
        self.api('users', {'id': 'kim', 'name': '김생산', 'role': '생산'}, 400)
        self.api('users', {'id': 'admin', 'name': '관리자', 'role': '관리자'})
        self.api('suppliers', {'id': 'S9', 'name': '공급사'}, 403)
        self.api('users', {'id': 'qc', 'name': '품질 담당', 'role': '품질'}, user='admin')
        self.api('suppliers', {'id': 'S9', 'name': '공급사'}, 403, user='qc')
        self.api('suppliers', {'id': 'S9', 'name': '공급사'}, 403, user='ghost')
        self.api('punch_items', {'hull': 'H101', 'block': '', 'category': 'C', 'description': '도장 손상',
                                 'raised_by': '사내', 'raised_day': date.today().isoformat()}, user='qc')
        audit = self.api('audit_log')
        self.assertEqual([(row['user_id'], row['action']) for row in audit][:2],
                         [('qc', 'punch_items'), ('admin', 'users')])
        self.assertEqual(len(audit), 3)


if __name__ == '__main__':
    unittest.main()
