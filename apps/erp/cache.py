"""Key/value store for sessions and read caches: Redis in production, an in-process stand-in for local runs.

Read results are cached under the current data version. Every committed write increments the version, so
older entries are never served again and simply expire.
"""
import hashlib
import json
import threading
import time

try:
    import redis
except ImportError:
    redis = None

VERSION_KEY = 'data_version'
CACHE_TTL = 600
STORE_ERRORS = (ConnectionError, TimeoutError, OSError)
if redis:
    STORE_ERRORS += (redis.RedisError,)


class MemoryStore:
    """Thread-safe subset of Redis used when no Redis URL is configured (SQLite demo runs and tests)."""

    def __init__(self):
        self.data, self.expiry, self.lock = {}, {}, threading.Lock()

    def _alive(self, key):
        deadline = self.expiry.get(key)
        if deadline is not None and deadline <= time.monotonic():
            self.data.pop(key, None)
            self.expiry.pop(key, None)
        return key in self.data

    def get(self, key):
        with self.lock:
            return self.data[key] if self._alive(key) else None

    def set(self, key, value, ttl=None):
        with self.lock:
            self.data[key] = value
            if ttl:
                self.expiry[key] = time.monotonic() + ttl
            else:
                self.expiry.pop(key, None)

    def delete(self, *keys):
        with self.lock:
            for key in keys:
                self.data.pop(key, None)
                self.expiry.pop(key, None)

    def incr(self, key):
        with self.lock:
            value = int(self.data[key] if self._alive(key) else 0) + 1
            self.data[key] = str(value)
            return value

    def expire(self, key, ttl):
        with self.lock:
            if self._alive(key):
                self.expiry[key] = time.monotonic() + ttl

    def sadd(self, key, member):
        with self.lock:
            if not self._alive(key):
                self.data[key] = set()
            self.data[key].add(member)

    def srem(self, key, member):
        with self.lock:
            if self._alive(key):
                self.data[key].discard(member)

    def smembers(self, key):
        with self.lock:
            return set(self.data[key]) if self._alive(key) else set()

    def ping(self):
        return True


class RedisStore:
    def __init__(self, url):
        if redis is None:
            raise RuntimeError('Redis 연결에는 redis 패키지가 필요합니다. requirements.txt를 설치하세요.')
        self.client = redis.Redis.from_url(url, decode_responses=True, socket_timeout=2, socket_connect_timeout=2)

    def get(self, key):
        return self.client.get(key)

    def set(self, key, value, ttl=None):
        self.client.set(key, value, ex=ttl)

    def delete(self, *keys):
        if keys:
            self.client.delete(*keys)

    def incr(self, key):
        return self.client.incr(key)

    def expire(self, key, ttl):
        self.client.expire(key, ttl)

    def sadd(self, key, member):
        self.client.sadd(key, member)

    def srem(self, key, member):
        self.client.srem(key, member)

    def smembers(self, key):
        return self.client.smembers(key)

    def ping(self):
        return self.client.ping()


def connect(url):
    return RedisStore(url) if url else MemoryStore()


def bump(store):
    store.incr(VERSION_KEY)


def cached(store, name, compute, ttl=CACHE_TTL):
    """Return (json_text, hit). The version is read before computing, so a racing write never gets masked."""
    try:
        key = f'cache:{store.get(VERSION_KEY) or 0}:{name}'
        body = store.get(key)
        if body is not None:
            return body, True
    except STORE_ERRORS:
        return json.dumps(compute(), ensure_ascii=False), False
    body = json.dumps(compute(), ensure_ascii=False)
    try:
        store.set(key, body, ttl)
    except STORE_ERRORS:
        pass
    return body, False


def digest(params):
    return hashlib.sha256(json.dumps(sorted(params.items()), ensure_ascii=False).encode()).hexdigest()[:32]
