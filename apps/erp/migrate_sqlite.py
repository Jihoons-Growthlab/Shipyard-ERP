"""Copy a SQLite database into an empty ShipYard PostgreSQL database."""

import argparse
import os
import re
import sqlite3
from collections import Counter
from contextlib import closing
from pathlib import Path

import app
import storage


def migrate(source, url):
    uri = source.resolve().as_uri() + '?mode=ro'
    tables = re.findall(r'CREATE TABLE IF NOT EXISTS (\w+)\(', app.SCHEMA)
    with closing(sqlite3.connect(uri, uri=True)) as old, closing(storage.Postgres(url)) as new, new:
        old.execute('BEGIN')
        if old.execute('PRAGMA foreign_key_check').fetchone():
            raise ValueError('원본 SQLite DB에 참조 무결성 오류가 있습니다.')
        storage.init_schema(new, app.SCHEMA)
        if any(new.execute(f'SELECT 1 FROM {table} LIMIT 1').fetchone() for table in tables):
            raise ValueError('PostgreSQL 대상이 비어 있어야 합니다.')
        source_tables = {row[0] for row in old.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        counts = {}
        for table in tables:
            if table not in source_tables:
                continue
            cursor = old.execute(f'SELECT * FROM {table} ORDER BY rowid')
            columns = ','.join(col[0] for col in cursor.description)
            placeholders = ','.join('?' for _ in cursor.description)
            expected = list(cursor)
            for row in expected:
                new.execute(f'INSERT INTO {table} ({columns}) VALUES ({placeholders})', row)
            actual = [tuple(row.values()) for row in new.execute(f'SELECT {columns} FROM {table}')]
            if Counter(expected) != Counter(actual):
                raise ValueError(f'{table} 데이터 대조 실패')
            counts[table] = len(actual)
        for table in ('plans', 'telemetry'):
            new.execute(f"""SELECT setval(pg_get_serial_sequence('{table}', 'id'),
                         COALESCE(MAX(id), 1), MAX(id) IS NOT NULL) FROM {table}""")
    return counts


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('source', type=Path)
    args = parser.parse_args()
    url = os.environ.get('SHIPYARD_DATABASE_URL')
    if not url:
        parser.error('SHIPYARD_DATABASE_URL이 필요합니다.')
    counts = migrate(args.source, url)
    import app
    app.bump_external_cache()
    print(f'{len(counts)}개 테이블, {sum(counts.values())}개 행 이전 및 대조 완료')
    for table, count in counts.items():
        if count:
            print(f'{table} {count}')
