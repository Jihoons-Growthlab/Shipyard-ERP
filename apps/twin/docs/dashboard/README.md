# 대시보드 기능 명세

Unity 앱은 시작 시 조선소 화면과 왼쪽 아이콘 바만 표시한다. 탑재 패널은 닫힌 상태이고, 패널의 재생 버튼을 눌러야 시뮬레이션이 시작된다(`ShipyardTwinSettings.AutoPlay` 기본값 false). 진입점은 `ShipyardTwinBootstrap`이고, 아이콘 바·패널 열고 닫기·종료·수심도 토글은 `Runtime/Dashboard/DashboardShellView.cs`가 맡는다. 스타일은 `Authoring/Dashboard.uss`, 아이콘은 `Runtime/Dashboard/DashboardIcon.cs`(벡터)다.

| 작업 | 명세 |
| --- | --- |
| 화면 오른쪽 위 종료 | [exit.md](exit.md) |
| 왼쪽 아이콘 바 맨 아래 챗봇 | [chat.md](chat.md) |
| 좌측 내비게이션의 오브젝트 검색 | [object-search.md](object-search.md) |
| 에디터 검증 및 MCP 연결 | [unity-tools.md](unity-tools.md) |

각 명세의 소스 경로는 `unity/Assets/ShipyardTwin/` 기준이다. 공통 레이아웃은 `Authoring/Dashboard.uxml`에서 수정한다. 기능별 로직은 필요한 경우 `Runtime/`에 별도 클래스로 두고 기존 대시보드 및 생성 흐름을 사용한다. 검색창과 챗봇에 입력하는 동안 카메라 단축키와 포인터 조작이 개입하지 않아야 한다. 화면 크기를 줄여도 각 컨트롤을 사용할 수 있어야 한다.
