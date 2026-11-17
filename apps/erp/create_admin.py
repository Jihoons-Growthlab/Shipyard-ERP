"""Create (or reset) an administrator account.

PostgreSQL:  ./shipyard-postgres create_admin.py --id admin --name 관리자
SQLite:      python3 create_admin.py --db local.sqlite3 --id admin --name 관리자
The password is read interactively (or from SHIPYARD_ADMIN_PASSWORD for automation).
"""
import argparse
import getpass
import os
from contextlib import closing
from pathlib import Path

import app
import auth
import storage


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--id', required=True)
    parser.add_argument('--name', required=True)
    parser.add_argument('--db', type=Path, help='SQLite 파일 (생략하면 SHIPYARD_DATABASE_URL)')
    args = parser.parse_args()
    if args.db:
        app.DATABASE = args.db
    else:
        app.DATABASE_URL = os.environ['SHIPYARD_DATABASE_URL']
    password = os.environ.get('SHIPYARD_ADMIN_PASSWORD') or getpass.getpass('관리자 비밀번호: ')
    if not os.environ.get('SHIPYARD_ADMIN_PASSWORD') and password != getpass.getpass('비밀번호 확인: '):
        raise SystemExit('비밀번호가 일치하지 않습니다.')
    auth.check_password(app.require, args.id, password)
    with closing(app.connect()) as db, db:
        storage.init_schema(db, app.SCHEMA)
    with closing(app.connect()) as db, db:
        storage.begin_write(db)
        auth.ensure_groups(db, app.insert)
        if db.execute('SELECT 1 FROM accounts WHERE id=?', (args.id,)).fetchone():
            db.execute('UPDATE accounts SET name=?, group_id=?, active=1, password_hash=?, password_changed_at=? WHERE id=?',
                       (args.name, auth.ADMIN_GROUP, auth.hash_password(password), app.now(), args.id))
            message = f'{args.id} 계정을 관리자로 재설정했습니다.'
        else:
            auth.create_account(db, app.insert, app.now(), args.id, args.name, auth.ADMIN_GROUP, password)
            message = f'관리자 {args.id} 계정을 만들었습니다.'
    url = os.environ.get('SHIPYARD_REDIS_URL')
    if url:
        store = app.cache.connect(url)
        store.delete(f'account:{args.id}')
        auth.revoke(store, args.id)
        app.cache.bump(store)
    print(message)


if __name__ == '__main__':
    main()
