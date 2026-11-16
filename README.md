# ShipYard-ERP

**디지털 트윈 연동을 위한 조선소 생산관리 ERP (MVP)**

호선·블록 계획부터 자재 구매, 작업 실적, 블록 이동까지 조선 생산 데이터를 한곳에서 관리하고, 그 결과를 Unity 등 디지털 트윈이 바로 읽을 수 있는 JSON API로 제공합니다. (Python · PostgreSQL 17)
트윈은 ERP가 확정한 **블록 위치·이동 상태**를 기준으로, 크레인 **텔레메트리(X/Y·하중)**를 시간순으로 재생합니다.

![ShipYard-ERP 데모](docs/demo.gif)

> 데모: 업무 홈 → 호선 선택 → 블록 탑재 준비 현황 → 블록 이동 → ERP API 기반 크레인 이동 재생
> 모든 호선·블록·좌표 데이터는 가상(모의) 데이터입니다.

## 주요 기능

| 영역 | 내용 |
|---|---|
| 계획 | 호선 주요 일정(착공~인도), 블록 구성·탑재 선후행, 12주 계획 발행본, MRP(자재 부족), APS(휴일·다중 선행 반영) |
| 도크·크레인 | 장비 정격하중·배치, 도크 점유 예약(중복 금지), 휴일 달력, 크레인 용량·선행 블록·도크 예약을 반영한 탑재 일정 제안 |
| 실행 | 구매요청 → 발주 → 입고·검수 → 자재 출고(수불부), 주간/일일 작업지시와 실적 |
| 블록 조립·공수 | 절단·가공 → 소조립 → 중조립 → 대조립 → PE → 탑재 단계, 단계별 계획/투입 공수(M/H)와 누적 진척률, 사내협력사 |
| 강재 추적 | 입고 로트(히트번호·밀시트), 출고별 로트 배정, 반납·잔재·폐기, 블록별 강재 추적, 블록 트리 자재 소요 |
| 의장품 | 선행/후행 의장품의 입고 → 설치 → 검사 완료 진행 |
| 진척·품질 | 공정별 계획 대비 실적, 중량·공수 기반 물량 진척, 작업 검사, ITP(선주·선급 입회), NCR, 펀치리스트, 탑재 준비 판정 |
| 블록 이동·탑재 | 이동 시작·확정 트랜잭션, 크레인 정격하중 검사, 탑재 검사 실적(선행 블록 탑재를 증명) |
| 관리 | 도면 리비전·설계 변경, 역할별 저장 권한, 감사 이력 |

## 디지털 트윈 연동

```
GET /api/blocks     → 블록 id, 확인 위치(location), position_status(confirmed | in_transit | unknown)
GET /api/moves      → block, equipment, origin → destination, started_at / confirmed_at
GET /api/telemetry  → move, recorded_at, x, y (m), load (t)
```

- 트윈은 `blocks.id → moves.block → telemetry.move` 순으로 연결하고 `recorded_at` 기준으로 재생합니다.
- 블록의 위치는 **ERP에서 이동을 확정해야** 바뀝니다. 텔레메트리만으로는 위치가 바뀌지 않습니다.
- 가상 시설 기준점: `DT-FAB(20,30)`, `DT-OUT(160,50)`, `DT-YARD(290,115)`, `DT-DOCK(430,170)`
- 가상 데이터: SN2037~SN2039 호선, 블록 72개, 이동 39건, 관측값 약 2,200건
- 그 밖의 조회 항목(`hulls`, `locations`, `activities`, `plans`, `orders`, `mrp`, `aps`, `meta` 등)도 `GET /api/<항목>`으로 조회합니다.

## 실행 방법

Python 3.9 이상과 Docker가 필요합니다. 데이터베이스는 **PostgreSQL 17**(Docker Compose, `127.0.0.1:55432`)을 사용합니다.

```sh
cp .env.example .env                      # SHIPYARD_DB_PASSWORD 값 변경
docker compose up -d                      # PostgreSQL 17 실행
python3 -m venv .venv && .venv/bin/pip install -r requirements.txt
./shipyard-postgres app.py                # 서버 실행 → http://127.0.0.1:8001
./shipyard-postgres seed_sn2036.py        # SN2036 실습 데이터
./shipyard-postgres seed_twin.py          # 디지털 트윈용 SN2037~2039 데이터
./shipyard-postgres seed_production.py    # SN2036에 조립 단계·공수·로트·ITP/NCR 예시 추가
```

새 버전으로 서버를 다시 시작하면 추가 테이블이 자동으로 생성됩니다(기존 데이터는 그대로 유지).

`shipyard-postgres`는 `.env`의 비밀번호로 `SHIPYARD_DATABASE_URL`을 구성한 뒤 Python을 실행하는 스크립트입니다. 기존 SQLite 예시 DB를 옮길 때는 `migrate_sqlite.py`를 사용합니다.

> Docker 없이 화면만 확인하려면 SQLite 파일로도 실행할 수 있습니다: `python3 -B app.py --db local.sqlite3`

**테스트**

```sh
python3 -B -m unittest -v
```

## 구성

```
app.py            HTTP 서버, 스키마, API
index.html        업무 화면 (단일 페이지)
shipbuilding.py   호선 일정, 블록 구성·선후행, 탑재 준비 판정
production.py     블록 조립 단계, 공수·진척, 의장품, 도면 리비전, 탑재 검사 실적
resources.py      장비 능력, 도크 예약, 휴일 달력, 탑재 일정 제안
lots.py           강재 로트·히트번호, 로트 배정, 반납·잔재, 블록 트리 소요
quality.py        ITP, 입회 검사, NCR, 펀치리스트
governance.py     역할별 권한, 감사 이력
planning.py       MRP · APS
procurement.py    구매·입고·출고
traceability.py   문서 번호와 원천 문서 추적
storage.py        PostgreSQL 어댑터 (로컬 SQLite 겸용)
compose.yaml      PostgreSQL 17 컨테이너
seed_*.py         가상 데이터
```

자세한 사용법은 [USER_GUIDE.md](USER_GUIDE.md), 작업 내역은 [REPORT.md](REPORT.md)를 참고하세요.

## 범위와 한계

로컬 환경에서만 실행하는 MVP입니다. 권한은 화면에서 선택한 작업자(`X-ShipYard-User` 헤더)를 기준으로 하며 비밀번호 로그인은 없습니다. 블록의 세부 좌표·회전·3D 형상, 실제 센서 연결, 네스팅(절단 배치) 최적화, 대금 정산은 포함하지 않습니다.
