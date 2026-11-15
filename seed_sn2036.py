"""Load a fictional SN2036 shipbuilding scenario into the configured database."""
import os
from contextlib import closing
from datetime import date, timedelta

import app
import storage


def seed(db):
    hull = 'SN2036'
    if db.execute('SELECT 1 FROM hulls WHERE id=?', (hull,)).fetchone():
        return False

    def add(action, **values):
        return app.write(db, action, values)

    today = date.today()
    monday = today - timedelta(days=today.weekday())

    def schedule(weeks):
        return (monday + timedelta(weeks=weeks)).isoformat()

    add('hulls', id=hull, name='SN2036 · 174K LNG 운반선 (가상)')
    add('vessel_details', hull=hull, vessel_type='174,000㎥ LNG 운반선', vessel_name='Blue Meridian (가상)',
        customer='Meridian LNG Shipping (가상)', length_m=299, beam_m=46.4, delivery=schedule(24))
    for code, name, kind in [
        ('SN-ST01','후판 1창고','창고'), ('SN-FAB','선체 가공·조립장','작업장'),
        ('SN-OUT','선행의장장','작업장'), ('SN-Y02','2야드 PE 정반','야드'),
        ('SN-D02','2도크','도크'), ('SN-Q03','3안벽','안벽'),
    ]:
        add('locations', id=code, name=name, kind=kind)
    for code, zone, assembly, weight, location in [
        ('211A','2번 화물창 이중저','중조립',68.4,'SN-FAB'),
        ('211B','2번 화물창 이중저','중조립',74.8,'SN-FAB'),
        ('211P','2번 화물창 이중저 좌현','대조립',286.5,'SN-Y02'),
        ('212P','2번 화물창 측외판 좌현','대조립',318.2,'SN-OUT'),
        ('310C','기관실 이중저 중앙','대조립',402.6,'SN-Y02'),
    ]:
        block = f'{hull}-{code}'
        add('blocks',id=block,hull=hull,location=location)
        add('block_details',block=block,zone=zone,assembly=assembly,weight_t=weight,drawing=f'{hull}-STR-{code}-R02')
    for name, week in [('착공',-4),('기공',6),('진수',16),('시운전',21),('인도',24)]:
        milestone = add('hull_milestones',hull=hull,milestone=name,baseline=schedule(week),forecast=schedule(week),reason='')
        if week < 0:
            add('milestone_update',id=milestone,forecast=schedule(week),actual=schedule(week),reason='모의 강재 절단 실적 반영')
    for child in ('211A','211B'):
        add('block_structure',parent_block=f'{hull}-211P',child_block=f'{hull}-{child}')
    for prior, successor in [('310C','211P'),('211P','212P')]:
        add('erection_links',predecessor_block=f'{hull}-{prior}',successor_block=f'{hull}-{successor}')
    for code, week in [('310C',6),('211P',7),('212P',8)]:
        add('erection_plans',block=f'{hull}-{code}',destination='SN-D02',baseline=schedule(week),forecast=schedule(week),reason='')
    for center, location, hours in [('SN-WC-FAB','SN-FAB',16),('SN-WC-OUT','SN-OUT',12),('SN-WC-DOCK','SN-D02',12)]:
        add('centers', id=center,name=location+' 워크센터',hours=hours)
        add('center_locations',center=center,location=location)
    for key, block, stage, week, center, hours, prior in [
        ('FAB-211A','211A','가공·조립',1,'SN-WC-FAB',80,''),
        ('FAB-211B','211B','가공·조립',2,'SN-WC-FAB',96,''),
        ('OUT-211P','211P','선행의장·도장',4,'SN-WC-OUT',72,'FAB-211A'),
        ('ERE-310C','310C','선탑재·탑재',6,'SN-WC-DOCK',36,''),
        ('ERE-211P','211P','선탑재·탑재',7,'SN-WC-DOCK',36,'ERE-310C'),
        ('ERE-212P','212P','선탑재·탑재',8,'SN-WC-DOCK',48,'ERE-211P'),
    ]:
        op = f'{hull}-{key}'
        add('activities',id=op,hull=hull,block=f'{hull}-{block}',stage=stage,baseline=schedule(week),forecast=schedule(week),reason='')
        add('operations',activity=op,center=center,duration=hours,predecessor=f'{hull}-{prior}' if prior else '',earliest=schedule(week))
    add('materials', id='SN-AH36-20',name='AH36 선체 구조용 후판 20 mm',unit='장',stock=36,lead_days=14)
    add('warehouse_stock',id='SN2036-OPEN-AH36',location='SN-ST01',material='SN-AH36-20',qty=36)
    req = add('requirements',activity=f'{hull}-FAB-211A',material='SN-AH36-20',qty=52,need_day=schedule(1))
    add('requirements',activity=f'{hull}-FAB-211B',material='SN-AH36-20',qty=30,need_day=schedule(2))
    add('suppliers',id='SN-STEEL',name='동해 후판 공급사 (가상)')
    pr = add('purchase_requests',requirement=req,qty=16)
    po = add('purchase_orders',request=pr,supplier='SN-STEEL',promised_day=schedule(1))
    add('goods_receipts',purchase_order=po,day=today.isoformat(),qty=8,inspection='합격',location='SN-ST01')
    plan = add('plans',start=schedule(0))
    add('orders',activity=f'{hull}-FAB-211A',cadence='주간',day=schedule(1),
        instruction='AH36 후판 절단·마킹, 이중저 부재 조립 및 치수 확인',plan_id=str(int(plan.split('-')[-1])))
    add('block_gate_checks',block=f'{hull}-211P',gate='도면 출도',verdict='확인',evidence='가상 출도 통보 SN2036-STR-211P-R02')
    add('block_gate_checks',block=f'{hull}-211P',gate='검사 승인',verdict='보류',evidence='구성 블록 조립 후 치수 검사 예정')
    return True


if __name__ == '__main__':
    app.DATABASE_URL = os.environ['SHIPYARD_DATABASE_URL']
    with closing(app.connect()) as db, db:
        storage.begin_write(db)
        created = seed(db)
    print('SN2036 가상 데이터 등록 완료' if created else 'SN2036이 이미 등록되어 있습니다.')
