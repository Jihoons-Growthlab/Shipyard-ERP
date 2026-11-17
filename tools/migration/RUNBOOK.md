# ShipYard-ERP 데이터 마이그레이션

현재 확인한 ERP 코드의 저장소는 PostgreSQL 17 또는 로컬 SQLite입니다. ERP의 `/docs`, `/api/openapi.json`, `/api/meta`에서 입력 경로와 필드를 확인할 수 있습니다. Oracle이 원본이면 조회 결과를 ERP API 입력 형식으로 변환합니다. Oracle이 대상이면 운영 ERP가 해당 Oracle에 실제로 저장하는지 운영 설정부터 확인해야 합니다.

## 원격 실행 설정

- 실행할 서버에 Python 3.9 이상이 필요합니다. 별도 패키지 설치는 필요 없습니다.
- 서버에서 ERP HTTPS 주소에 접속할 수 있어야 합니다. ERP가 내부망에 있으면 VPN 또는 승인된 SSH 터널을 사용합니다. 현재 ERP 앱은 기본적으로 루프백에 바인딩합니다.
- ERP 계정에는 대상 메뉴의 저장 권한이 필요합니다. `SHIPYARD_API_TOKEN`을 실행 환경의 비밀 변수로 주입하세요. 계정으로 로그인할 경우 `SHIPYARD_API_USER`와 `SHIPYARD_API_PASSWORD`를 사용합니다.
- Oracle에 직접 접속해야 한다면 실행 서버에서 Oracle 리스너까지 네트워크 경로, 호스트와 포트, 서비스명, 계정 및 대상 스키마 권한이 필요합니다. 신뢰 설정이나 Wallet이 필요한지는 운영 환경에 따라 달라집니다. 원본 추출에는 읽기 권한을 사용합니다.

## 입력 파일과 실행

`data/`에 UTF-8 JSONL 파일을 둡니다. 각 줄의 `action`은 POST 경로의 마지막 부분이며 `data`는 요청 본문입니다. 참조되는 레코드를 먼저 배치합니다. 다음은 호선과 블록의 예입니다.

```jsonl
{"action":"hulls","data":{"id":"MIG-001","name":"마이그레이션 예시 호선"}}
{"action":"blocks","data":{"id":"MIG-001-B01","hull":"MIG-001","location":"EXISTING-YARD"}}
```

두 번째 줄의 장소 ID는 대상 ERP에 존재하는 실제 값으로 바꿉니다.

```bash
export SHIPYARD_API_URL=https://erp.example.com
python3 migrate.py
python3 migrate.py data/input.jsonl
python3 migrate.py data/input.jsonl --apply
```

첫 명령은 입력 경로와 필드를 출력합니다. 두 번째 명령은 API 경로, 필드 이름과 요청 크기를 검사합니다. 필수값, 데이터 타입, 참조 무결성 및 업무 규칙은 실제 저장 시 ERP가 검사합니다. 세 번째 명령은 계정의 저장 권한을 확인한 뒤 파일 순서대로 실제 저장합니다.

ERP는 건별로 커밋합니다. 오류가 나면 스크립트는 첫 실패에서 멈추며, 성공 행과 반환 ID를 출력합니다. 응답이 끊긴 건은 이미 저장됐을 수 있으므로 ERP에서 확인한 다음, 미저장 건으로 새 입력 파일을 만들어 재개합니다.

## 디지털 트윈 데이터

블록 분할 생성, 블록 모델, ERP 트윈 데이터 내보내기, Unity 탑재 재생은 `../../apps/twin` 프로젝트에서 한다. 생성한 JSONL을 ERP에 넣을 때는 이 폴더의 `migrate.py`를 사용한다(`python3 migrate.py ../../apps/twin/data/SN2040-blocks.jsonl --apply`).
