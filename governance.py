"""Role-based write permissions and an audit trail of every accepted write request.

The acting user is self-declared through the X-ShipYard-User header (local MVP, no password login).
Enforcement starts once the first user exists, and the first user must be an administrator.
"""
import json

ROLES = ['관리자', '계획', '생산', '구매', '품질', '조회']
CHOICES = {'role': ROLES}
SCHEMA = '''
CREATE TABLE IF NOT EXISTS users(id TEXT PRIMARY KEY, name TEXT NOT NULL, role TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS audit_log(id TEXT PRIMARY KEY, user_id TEXT NOT NULL, action TEXT NOT NULL,
 document TEXT NOT NULL, payload TEXT NOT NULL, recorded_at TEXT NOT NULL);
'''
FIELDS = {'users': ('id', 'name', 'role')}
PREFIXES = {'audit_log': 'AUD'}
OPTIONAL = {}
DATES = set()
NUMBERS = set()
READS = {'users', 'audit_log'}
PERMISSIONS = {
    '계획': {'activities', 'forecast', 'plans', 'hull_milestones', 'milestone_update', 'block_structure',
             'erection_links', 'erection_plans', 'requirements', 'receipts', 'operations', 'operation_links',
             'dock_bookings', 'holidays', 'block_stages', 'outfitting_items', 'drawing_revisions',
             'vessel_details', 'block_details'},
    '생산': {'orders', 'results', 'moves', 'confirm', 'telemetry', 'work_logs', 'erection_records',
             'outfitting_events', 'issues', 'issue_lots', 'material_returns', 'block_gate_checks'},
    '구매': {'suppliers', 'purchase_requests', 'purchase_orders', 'goods_receipts', 'receipt_lots', 'materials',
             'warehouse_stock', 'issues', 'issue_lots', 'material_returns'},
    '품질': {'inspection_points', 'inspection_records', 'ncrs', 'ncr_close', 'punch_items', 'punch_close',
             'block_gate_checks', 'results', 'erection_records'},
    '조회': set(),
}


def authorize(db, user_id, action):
    """Return the acting user id, or raise PermissionError."""
    if not db.execute('SELECT 1 FROM users LIMIT 1').fetchone():
        return user_id or ''
    if not user_id:
        raise PermissionError('작업자를 선택한 뒤 저장하세요.')
    user = db.execute('SELECT * FROM users WHERE id=?', (user_id,)).fetchone()
    if not user:
        raise PermissionError('등록되지 않은 작업자입니다.')
    if user['role'] != '관리자' and action not in PERMISSIONS.get(user['role'], set()):
        raise PermissionError(f'{user["role"]} 권한으로는 이 작업을 저장할 수 없습니다.')
    return user['id']


def write(db, action, v, api):
    api.require(v['role'] in ROLES, '권한을 선택하세요.')
    if not db.execute('SELECT 1 FROM users LIMIT 1').fetchone():
        api.require(v['role'] == '관리자', '첫 사용자는 관리자 권한으로 등록하세요.')
    return api.insert(db, action, **v)


def audit(db, api, user_id, action, document, payload):
    api.insert(db, 'audit_log', id=api.traceability.document_number(db, 'audit_log'), user_id=user_id or '',
               action=action, document='' if document is None else str(document),
               payload=json.dumps(payload, ensure_ascii=False)[:4000], recorded_at=api.now())


def read(db, table):
    if table == 'audit_log':
        return [dict(row) for row in db.execute('SELECT * FROM audit_log ORDER BY recorded_at DESC, id DESC LIMIT 500')]
    return None
