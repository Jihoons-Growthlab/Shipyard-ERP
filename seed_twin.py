"""Repeatable fictional fleet and crane tracks for digital twin integration."""
import os
from contextlib import closing
from datetime import date, datetime, timedelta, timezone

import app
import storage


def seed(db):
    def add(action, **values):
        return app.write(db, action, values)

    if db.execute("SELECT 1 FROM hulls WHERE id='SN2037'").fetchone():
        return
    facilities = [
        ('DT-FAB', '디지털 트윈 가공·조립장', '작업장', (20, 30)),
        ('DT-OUT', '디지털 트윈 선행의장장', '작업장', (160, 50)),
        ('DT-YARD', '디지털 트윈 적치장', '야드', (290, 115)),
        ('DT-DOCK', '디지털 트윈 건조 도크', '도크', (430, 170)),
    ]
    coordinates = {}
    for key, name, kind, position in facilities:
        add('locations', id=key, name=name, kind=kind)
        coordinates[key] = position
    now = datetime.now(timezone.utc)
    monday = date.today() - timedelta(days=date.today().weekday())
    for ship_index, (hull, vessel_type, length, beam) in enumerate([
        ('SN2037', '174K LNG 운반선', 299, 46.4),
        ('SN2038', '15,000 TEU 컨테이너선', 366, 51),
        ('SN2039', '아프라막스급 원유 운반선', 250, 44),
    ]):
        add('hulls', id=hull, name=f'{hull} · {vessel_type} (가상)')
        add('vessel_details', hull=hull, vessel_type=vessel_type, vessel_name=f'Twin Demo {hull}',
            customer='가상 선주', length_m=length, beam_m=beam,
            delivery=(monday + timedelta(weeks=28+ship_index*4)).isoformat())
        crane = f'DT-CR{ship_index+1}'
        add('equipment', id=crane, name=f'모의 갠트리 크레인 {ship_index+1}호')
        dock_blocks = []
        for index in range(24):
            block = f'{hull}-{201+index}P'
            weight = round(95 + index*9.3 + ship_index*22, 1)
            origin = facilities[index%3][0]
            add('blocks',id=block,hull=hull,location=origin if index < 19 else '')
            add('block_details',block=block,zone=['이중저','측외판','갑판','기관실'][index%4],
                assembly='대조립',weight_t=weight,drawing=f'{block}-STR-R02')
            week = (index+ship_index)%10
            baseline = (monday + timedelta(weeks=week)).isoformat()
            late = index%5 == 0
            forecast = (date.fromisoformat(baseline) + timedelta(days=3*late)).isoformat()
            op = f'{block}-OP'
            add('activities',id=op,hull=hull,block=block,stage=app.STAGES[1+index%3],
                baseline=baseline,forecast=forecast,reason='모의 의장품 납기 조정' if late else '')
            if index%3 == 0:
                add('orders',activity=op,cadence='주간',day=baseline,instruction=f'{block} 작업 및 치수 점검')
            if index%3 == 2:
                add('erection_plans',block=block,destination='DT-DOCK',baseline=baseline,forecast=forecast,
                    reason='모의 탑재 순서 조정' if late else '')
                if dock_blocks:
                    add('erection_links',predecessor_block=dock_blocks[-1],successor_block=block)
                dock_blocks.append(block)
            if index > 12:
                continue
            active = index == 12
            target = facilities[index%3+1][0]
            points = 25 if active else 60
            start = now - timedelta(seconds=(points-1)*5) if active else now - timedelta(hours=48-index*3)
            move = add('moves',block=block,equipment=crane,destination=target)
            db.execute('UPDATE moves SET started_at=? WHERE id=?',(start.isoformat(),move))
            x0,y0 = coordinates[origin]
            x1,y1 = coordinates[target]
            for point in range(points):
                fraction = point/59
                timestamp = start + timedelta(seconds=point*5)
                app.insert(db,'telemetry',move=move,recorded_at=timestamp.isoformat(),
                    x=round(x0+(x1-x0)*fraction,2),y=round(y0+(y1-y0)*fraction,2),
                    load=weight if 0 < point < 59 else 0,
                    status='인양 준비' if point == 0 else '하역 완료' if point == 59 else '이송 중')
            if not active:
                add('confirm',id=move)
                db.execute('UPDATE moves SET confirmed_at=? WHERE id=?',(timestamp.isoformat(),move))
    plan = add('plans',start=monday.isoformat())
    db.execute("""INSERT INTO order_plans(work_order,plan_id)
        SELECT o.id,? FROM orders o JOIN activities a ON a.id=o.activity
        WHERE a.hull IN ('SN2037','SN2038','SN2039')""",(int(plan.split('-')[-1]),))


if __name__ == '__main__':
    app.DATABASE_URL = os.environ['SHIPYARD_DATABASE_URL']
    with closing(app.connect()) as db, db:
        storage.begin_write(db)
        seed(db)
