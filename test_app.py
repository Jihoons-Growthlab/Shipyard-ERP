import io
import json
import os
import tempfile
import unittest
from concurrent.futures import ThreadPoolExecutor
from contextlib import closing
from datetime import date
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

    def request(self, route, data=None, expected=200):
        method = 'GET' if data is None else 'POST'
        body = b'' if data is None else json.dumps(data).encode()
        headers = f'Content-Type: application/json\r\nContent-Length: {len(body)}\r\n'
        wire = Request(f'{method} {route} HTTP/1.0\r\n{headers}\r\n'.encode() + body)
        QuietHandler(wire, ('127.0.0.1', 0), None)
        response, body = wire.output.getvalue().split(b'\r\n\r\n', 1)
        status = int(response.split()[1])
        if expected is None:
            return status
        self.assertEqual(status, expected, body)
        return json.loads(body) if b'application/json' in response else body

    def api(self, route, data=None, expected=None):
        return self.request('/api/' + route, data, expected or (201 if data is not None else 200))

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


if __name__ == '__main__':
    unittest.main()
