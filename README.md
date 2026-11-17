# SIWS · 조선소 생산관리 ERP와 디지털 트윈

호선별 생산계획, 설계·제조 BOM, 작업지시·실적, 블록 이동을 ERP에서 관리합니다. Unity에서는 야드 배치와 도크의 블록 탑재를 3D로 재생합니다. Portal에서 ERP와 Swagger에 접근할 수 있습니다.

## 화면별 시연

[화면별 GIF와 시연 순서](docs/demo.md) · [기능 명세](docs/spec.md)

### SIWS 포털

![SIWS 로그인, 운영 현황, 조선소 지도와 ERP 진입](docs/media/portal/portal-overview.gif)

통합 포털(8080)은 ERP 계정 로그인, 조선소 운영 지표와 블록 이동 현황, 지도, ERP·Swagger 진입과 Unity 앱 실행을 제공합니다.

### 생산관리 ERP

![통합 ERP의 업무 홈, 대일정, 제조 BOM과 MRP](docs/media/erp/planning.gif)

[작업지시·품질·블록 이동 시연](docs/media/erp/execution.gif)

### Unity 디지털 트윈

![Unity 기본 시점, 야드 전체 시점과 크레인 시점](docs/media/shipyard-twin.gif)

현재 Unity 앱에서 기본 시점 → 야드 전체 → 크레인 시점으로 전환한 화면입니다.

## 구현 기능

| 화면 | 주요 기능 |
| --- | --- |
| Portal | ERP와 Swagger 진입 |
| 업무 홈 | 자재 부족, 구매·작업·이동과 미결 품질 건 확인, 호선별 조회 |
| 대일정 | 호선 주요 일정, 도크 점유, 선석 배정, 일정 변경 이력 |
| 중일정 | 탑재 선후행, 블록 적치·준비, 크레인 부하, 의장 진척, 안벽 이동 |
| 소일정 | 12주 계획·주간 발행, APS 일정 제안, 크레인 작업 배정 |
| EBOM → MBOM → MRP | 도면·설계 BOM 리비전, 제조 공정·투입품, 발행 전 비교, 자재 소요와 부족 근거 |
| 실행 | 구매·입고·출고, 강재 로트, 작업지시·실적, 조립·공수, 의장품, 블록 이동 |
| 진척·품질 | 계획 대비 실적, 물량·공수, 검사 계획·입회 실적, NCR·펀치리스트 |
| 관리·지도 | 기준정보, 시설·도크 배치, 조선소 지도, 그룹별 권한·감사 이력 |
| Unity Twin | 3D 야드, 크레인 탑재 재생, 카메라 프리셋, 대상 툴팁, 수심도 |

## 시스템 구조

~~~mermaid
flowchart LR
    Portal["Portal"] --> ERP["ERP 웹 · API"]
    ERP --> DB["PostgreSQL"]
    ERP --> Cache["Redis"]
    ERP --> Twin["Unity Twin"]
    ERP --> Export["Python 스냅샷 내보내기"]
    Export --> Twin
~~~

| 경로 | 구성 |
| --- | --- |
| apps/portal | 브랜드 포털 HTML 및 Python ERP 프록시 |
| apps/erp | Python HTTP API, JavaScript 업무 화면, 생산·BOM·야드 업무 모듈 |
| apps/twin/unity | Unity 6 · C# · URP · UI Toolkit. 야드 렌더링, 탑재 재생과 패널 |
| apps/twin/pipeline | 예시 시설·블록·운영계획 생성, OBJ 모델 생성, 스냅샷 내보내기 |
| shared/contracts | ERP–Twin 데이터 연동 기준 |
| shared/unity-packages | 공용 Unity 패키지 |
| tools | 로컬 실행과 데이터 이관 |

## 실행

[로컬 개발 가이드](docs/development.md)에서 최초 설치와 환경변수 설정을 마친 뒤, 저장소 루트에서 실행합니다.

| 서비스 | 명령 | 접속 |
| --- | --- | --- |
| PostgreSQL 17 · Redis | `tools/dev db-up` | 55432 · 56379 |
| ERP | `tools/dev erp` | [ERP](http://127.0.0.1:8001/) · [Swagger](http://127.0.0.1:8001/docs) |
| Portal | `tools/dev portal` | [Portal](http://127.0.0.1:8080/) |
| Unity Twin | `tools/dev twin-build` 후 `tools/dev twin-run` | macOS 앱 · 에디터 6000.3.23f1 |

- [ERP 업무 흐름](apps/erp/docs/ERP_WORKFLOW.md) · [ERP 사용 가이드와 API](apps/erp/README.md)
- [Unity 실행과 데이터 갱신](apps/twin/README.md) · [Unity 코드 구조](apps/twin/docs/project-map.md)
- [ERP–Twin 연동 기준](shared/contracts/README.md) · [스냅샷 형식](apps/twin/docs/TWIN_FORMAT.md)
