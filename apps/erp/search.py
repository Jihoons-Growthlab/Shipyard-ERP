"""Server-side search, hull scoping, sorting and paging over a route's rows.

GET /api/<route>?q=&hull=&material=&location=&date_key=&from=&until=&sort=&dir=&page=&size=&f.<column>=
returns {"rows", "total", "page", "size", "pages"}. Without query parameters a route returns its full array.
`f.<column>` filters one column: partial text match, a numeric comparison such as `>=10`, `<5`, `=3`,
or a date range `2026-11-01~2026-11-30` (either end may be omitted).
"""
import re
PARAMS = {'q', 'hull', 'material', 'location', 'date_key', 'from', 'until', 'sort', 'dir', 'page', 'size'}
MAX_SIZE = 200
MAX_COLUMN_FILTERS = 20
COLUMN_FILTER = re.compile(r'f\.[a-z][a-z0-9_]{0,39}')
COMPARISON = re.compile(r'(>=|<=|>|<|=)\s*(-?\d+(?:\.\d+)?)')
DATE_RANGE = re.compile(r'(\d{4}-\d{2}-\d{2})?~(\d{4}-\d{2}-\d{2})?')
# Routes the hull resolver needs, loaded only when a hull filter is requested.
HULL_SOURCES = ['blocks', 'activities', 'moves', 'orders', 'requirements', 'block_stages']


def parse(raw):
    """Validate a parse_qs dict into flat parameters."""
    filters = {key for key in raw if COLUMN_FILTER.fullmatch(key)}
    if len(filters) > MAX_COLUMN_FILTERS:
        raise ValueError(f'열 필터는 {MAX_COLUMN_FILTERS}개까지 지정할 수 있습니다.')
    unknown = set(raw) - PARAMS - filters
    if unknown:
        raise ValueError('지원하지 않는 조회 조건입니다: ' + ', '.join(sorted(unknown)))
    params = {key: values[-1].strip()[:200] for key, values in raw.items() if values and values[-1].strip()}
    for key, default, upper in (('page', 0, 100000), ('size', 20, MAX_SIZE)):
        value = params.get(key, str(default))
        if not value.isdigit() or int(value) > upper or (key == 'size' and int(value) < 1):
            raise ValueError(f'{key} 값을 확인하세요.')
        params[key] = int(value)
    if params.get('dir', 'asc') not in {'asc', 'desc'}:
        raise ValueError('dir은 asc 또는 desc입니다.')
    return params


def hull_maps(sources):
    blocks = {row['id']: row['hull'] for row in sources['blocks']}
    activities = {row['id']: row['hull'] for row in sources['activities']}
    return {
        'block': blocks,
        'activity': activities,
        'move': {row['id']: blocks.get(row['block']) for row in sources['moves']},
        'work_order': {row['id']: activities.get(row['activity']) for row in sources['orders']},
        'requirement': {row['id']: activities.get(row['activity']) for row in sources['requirements']},
        'block_stage': {row['id']: blocks.get(row['block']) for row in sources['block_stages']},
    }


def hull_of(row, maps):
    if row.get('hull'):
        return row['hull']
    for key, mapping in maps.items():
        if row.get(key) is not None:
            return mapping.get(row[key])
    return None


def sort_key(value):
    if isinstance(value, (int, float)) and not isinstance(value, bool):
        return (0, value, '')
    return (1, 0, '' if value is None else str(value))


def column_match(value, term):
    """Partial text match, a numeric comparison (>=10, <5, =3) or a date range (from~until, inclusive)."""
    span = DATE_RANGE.fullmatch(term)
    if span:
        day = ('' if value is None else str(value))[:10]
        return bool(day) and (not span.group(1) or day >= span.group(1)) and (not span.group(2) or day <= span.group(2))
    compare = COMPARISON.fullmatch(term)
    if compare and isinstance(value, (int, float)) and not isinstance(value, bool):
        op, number = compare.group(1), float(compare.group(2))
        return {'>=': value >= number, '<=': value <= number, '>': value > number,
                '<': value < number, '=': value == number}[op]
    return term.lower() in ('' if value is None else str(value)).lower()


def query(rows, params, maps=None, material_names=None):
    hits = rows
    if params.get('hull') and maps is not None:
        scoped = [row for row in hits if hull_of(row, maps) == params['hull']]
        if any(hull_of(row, maps) is not None for row in hits):
            hits = scoped
    if params.get('material'):
        hits = [row for row in hits if row.get('material') == params['material']]
    if params.get('location'):
        hits = [row for row in hits if params['location'] in (row.get('location'), row.get('source'))]
    date_key = params.get('date_key')
    if date_key and (params.get('from') or params.get('until')):
        if rows and date_key not in rows[0]:
            raise ValueError('date_key 열을 확인하세요.')
        start, end = params.get('from', ''), params.get('until', '')
        hits = [row for row in hits if (lambda day: day and (not start or day >= start) and (not end or day <= end))(
            str(row.get(date_key) or '')[:10])]
    term = params.get('q', '').lower()
    if term:
        names = material_names or {}

        def text(row):
            values = [*row.values(), names.get(row.get('material'))]
            return ' '.join('' if value is None else str(value) for value in values).lower()
        hits = [row for row in hits if term in text(row)]
    for key in sorted(key for key in params if key.startswith('f.')):
        column = key[2:]
        if rows and column not in rows[0]:
            raise ValueError('필터 열을 확인하세요.')
        hits = [row for row in hits if column_match(row.get(column), params[key])]
    if params.get('sort'):
        if rows and params['sort'] not in rows[0]:
            raise ValueError('sort 열을 확인하세요.')
        hits = sorted(hits, key=lambda row: sort_key(row.get(params['sort'])), reverse=params.get('dir') == 'desc')
    size, page = params['size'], params['page']
    pages = max(1, -(-len(hits) // size))
    page = min(page, pages - 1)
    return {'rows': hits[page * size:(page + 1) * size], 'total': len(hits), 'page': page, 'size': size, 'pages': pages}
