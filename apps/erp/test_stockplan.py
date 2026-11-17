"""Block stocking plans (stockplan.py): validation and the twin read. Runs on the same harness as test_app."""

import unittest

import test_app

_Base = test_app.ShipYardTest


class StockPlanTest(unittest.TestCase):
    # Borrow the harness (temp database, admin login, request helpers) without inheriting test_app's tests.
    setUp, bootstrap, login, request, api = _Base.setUp, _Base.bootstrap, _Base.login, _Base.request, _Base.api
    drop_schema = staticmethod(_Base.drop_schema)


    def test_block_stock_plans(self):
        self.api('location_geometry', {'location': 'YARD', 'x': 200, 'y': 0, 'z': 0, 'heading_deg': 0,
                                       'size_x_m': 100, 'size_y_m': 60, 'size_z_m': 0})
        plan = {'block': 'B101', 'location': 'YARD', 'x': 230, 'y': 10, 'heading_deg': 90,
                'stock_from': '2027-01-04', 'stock_until': '2027-01-20', 'note': ''}
        first = self.api('block_stock_plans', plan)['id']
        self.assertTrue(first.startswith('STK-'))
        self.api('block_stock_plans', {**plan, 'x': 260}, 400)                       # outside the 100 m footprint
        self.api('block_stock_plans', {**plan, 'location': 'SHOP'}, 400)             # not a yard
        self.api('block_stock_plans', {**plan, 'heading_deg': 360}, 400)
        self.api('block_stock_plans', {**plan, 'stock_until': '2027-01-01'}, 400)    # ends before it starts
        self.api('block_stock_plans', {**plan, 'stock_from': '2027-01-15'}, 400)     # overlaps the first stay
        second = self.api('block_stock_plans', {**plan, 'x': 180, 'stock_from': '2027-01-21', 'stock_until': None})['id']
        self.api('block_stock_plans', {**plan, 'block': 'B102', 'stock_until': None})  # other block, same days
        rows = self.api('block_stock_plans')
        self.assertEqual([r['stock_from'] for r in rows], sorted(r['stock_from'] for r in rows), 'By hull, then start day.')
        by_id = {r['id']: r for r in rows}
        self.assertEqual((by_id[first]['block'], by_id[first]['hull'], by_id[first]['heading_deg']), ('B101', 'H101', 90))
        self.assertIsNone(by_id[second]['stock_until'], 'Open-ended until the erection lift.')
        self.assertEqual(self.api('block_stock_plans?hull=H101')['total'], 3)
        self.api('block_stock_plans', {**plan, 'id': first, 'stock_until': '2027-01-10'})   # edit by id
        self.assertEqual({r['id']: r for r in self.api('block_stock_plans')}[first]['stock_until'], '2027-01-10')


del _Base   # keep test_app's TestCase out of this module, so its tests are not collected twice


if __name__ == '__main__':
    unittest.main()
