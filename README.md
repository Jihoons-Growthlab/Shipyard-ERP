# ShipYard-ERP

**디지털 트윈 연동을 위한 조선소 생산관리 ERP (MVP)**

호선·블록 계획부터 자재 구매, 작업 실적, 블록 이동까지 조선 생산 데이터를 한곳에서 관리하고, 그 결과를 Unity 등 디지털 트윈이 바로 읽을 수 있는 JSON API로 제공합니다.
트윈은 ERP가 확정한 **블록 위치·이동 상태**를 기준으로, 크레인 **텔레메트리(X/Y·하중)**를 시간순으로 재생합니다.

![ShipYard-ERP 데모](docs/demo.gif)

> 데모: 업무 홈 → 호선 선택 → 블록 탑재 준비 현황 → 블록 이동 → ERP API 기반 크레인 이동 재생
> 모든 호선·블록·좌표 데이터는 가상(모의) 데이터입니다.

## 주요 기능

| 영역 | 내용 |
|---|---|
| 계획 | 호선 주요 일정(착공~인도), 블록 구성·탑재 선후행, 12주 계획 발행본, MRP(자재 부족), APS(제약 일정) |
| 실행 | 구매요청 → 발주 → 입고·검수 → 자재 출고(수불부), 주간/일일 작업지시와 실적 |
| 진척·품질 | 공정별 계획 대비 실적, 작업 검사와 최초 불합격률, 블록 탑재 준비(근거 기반) 판정 |
| 블록 이동 | 이동 시작·확정 트랜잭션, 확인 위치 / 이동 중 / 미확인 상태 관리, 장비 관측값 기록 |

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

Python 3.9 이상이 필요합니다.

**SQLite로 빠르게 실행**

```sh
python3 -B app.py --db local.sqlite3     # http://127.0.0.1:8001
```

**PostgreSQL(Docker)로 실행**

```sh
cp .env.example .env                      # SHIPYARD_DB_PASSWORD 값 변경
docker compose up -d
python3 -m venv .venv && .venv/bin/pip install -r requirements.txt
./shipyard-postgres app.py                # 기본 데이터로 서버 실행
./shipyard-postgres seed_sn2036.py        # SN2036 실습 데이터
./shipyard-postgres seed_twin.py          # 디지털 트윈용 SN2037~2039 데이터
```

**테스트**

```sh
python3 -B -m unittest -v
```

## 구성

```
app.py            HTTP 서버, 스키마, API
index.html        업무 화면 (단일 페이지)
shipbuilding.py   호선 일정, 블록 구성·선후행, 탑재 준비 판정
planning.py       MRP · APS
procurement.py    구매·입고·출고
traceability.py   문서 번호와 원천 문서 추적
storage.py        PostgreSQL / SQLite 어댑터
seed_*.py         가상 데이터
```

자세한 사용법은 [USER_GUIDE.md](USER_GUIDE.md), 작업 내역은 [REPORT.md](REPORT.md)를 참고하세요.

## 범위와 한계

로컬 환경에서만 실행하는 MVP입니다. 사용자 권한과 감사 이력, 블록의 세부 좌표·회전·3D 형상, 실제 센서 연결은 포함하지 않습니다.
