"""Upload explicitly ordered, mapped records through the ShipYard-ERP API."""

import argparse
import json
import os
import re
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.parse import urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def request(base, route, payload=None, token=None):
    headers = {"Accept": "application/json"}
    body = None
    if payload is not None:
        headers["Content-Type"] = "application/json"
        body = json.dumps(payload, ensure_ascii=False, allow_nan=False).encode()
    if token:
        headers["Authorization"] = f"Bearer {token}"
    try:
        with build_opener(NoRedirect()).open(
            Request(f"{base}/api/{route}", data=body, headers=headers), timeout=30
        ) as response:
            return json.load(response)
    except HTTPError as exc:
        raise RuntimeError(f"{route}: HTTP {exc.code}") from exc


def load_records(path, fields):
    records = []
    for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        if not line.strip():
            continue
        try:
            record = json.loads(line)
            action, payload = record["action"], record["data"]
            if (
                not isinstance(action, str)
                or not re.fullmatch(r"[a-z][a-z0-9_]*", action)
                or action not in fields
                or not isinstance(payload, dict)
            ):
                raise ValueError("지원되는 action과 data 객체가 필요합니다.")
            unknown = payload.keys() - set(fields[action])
            if unknown:
                raise ValueError(f"알 수 없는 필드: {', '.join(sorted(unknown))}")
            if len(json.dumps(payload, ensure_ascii=False, allow_nan=False).encode()) > 65536:
                raise ValueError("요청 크기가 64 KiB를 초과합니다.")
        except (KeyError, TypeError, ValueError) as exc:
            raise ValueError(f"{number}행 입력 오류: {exc}") from exc
        records.append((number, action, payload))
    if not records:
        raise ValueError("입력 파일에 레코드가 없습니다.")
    return records


def base_url(parser):
    base = os.environ.get("SHIPYARD_API_URL", "").rstrip("/")
    url = urlsplit(base)
    if (
        not url.hostname or url.username or url.password or url.query or url.fragment
        or (url.scheme != "https" and not (
            url.scheme == "http" and url.hostname in {"localhost", "127.0.0.1", "::1"}
        ))
    ):
        parser.error("SHIPYARD_API_URL에 HTTPS 또는 로컬 HTTP 주소를 설정하세요.")
    return base


def login(base, parser):
    token = os.environ.get("SHIPYARD_API_TOKEN")
    if not token:
        user = os.environ.get("SHIPYARD_API_USER")
        password = os.environ.get("SHIPYARD_API_PASSWORD")
        if not user or not password:
            parser.error("API 토큰 또는 사용자 ID와 비밀번호를 설정하세요.")
        token = request(base, "login", {"id": user, "password": password})["token"]
    return token


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("file", type=Path, nargs="?")
    parser.add_argument("--apply", action="store_true")
    args = parser.parse_args()
    if args.apply and not args.file:
        parser.error("--apply에는 입력 파일이 필요합니다.")

    base = base_url(parser)
    fields = request(base, "meta")["fields"]
    if not args.file:
        print(json.dumps(fields, ensure_ascii=False, indent=2))
        return

    records = load_records(args.file, fields)
    print(f"{len(records)}건의 API 경로, 필드 이름, 요청 크기를 확인했습니다.", flush=True)
    if not args.apply:
        return

    token = login(base, parser)
    actions = set(request(base, "me", token=token)["actions"])
    missing = {action for _, action, _ in records} - actions
    if missing:
        raise ValueError(f"입력 권한이 없는 작업: {', '.join(sorted(missing))}")

    for number, action, payload in records:
        try:
            result = request(base, action, payload, token)
        except (RuntimeError, OSError, ValueError) as exc:
            raise RuntimeError(
                f"{number}행 {action}에서 중단했습니다. 서버 저장 여부를 확인한 뒤 재개하세요. ({exc})"
            ) from exc
        print(f"{number}행 {action} 저장 완료 (ID {result['id']})", flush=True)


if __name__ == "__main__":
    try:
        main()
    except (OSError, KeyError, ValueError, RuntimeError, URLError) as exc:
        raise SystemExit(str(exc))
