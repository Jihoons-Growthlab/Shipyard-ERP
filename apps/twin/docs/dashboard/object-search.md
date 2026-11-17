# 오브젝트 검색

- 좌측 내비게이션 패널에 ‘오브젝트 검색’ 입력창과 결과 목록을 둔다. 초기 범위는 탑재 블록, 도크·시설, 크레인이다.
- 표시 이름과 ID를 검색하고, 입력 앞뒤 공백과 영문 대소문자 차이를 무시한다. 빈 검색어에는 결과를 숨기고 매칭 항목이 없으면 ‘검색 결과 없음’을 표시한다.
- 오브젝트 생성 시 기존 데이터 ID와 씬 대상을 연결한다. 입력마다 전체 씬을 순회하지 않는다. 표시 중인 결과를 선택하면 기존 카메라를 대상으로 이동한다. 탑재 전 블록 등 현재 볼 수 없는 대상은 목록에서 상태를 보여주고 재생 시점은 유지한다.
- 먼저 `Authoring/Dashboard.uxml`, `Runtime/Dashboard/ErectionPanelView.cs`, `Runtime/Erection/ShipView.cs`, `Runtime/Interaction/OrbitCameraRig.cs`를 확인한다. 시설·크레인 확장 시 `Runtime/Yard/YardEnvironment.cs`, `Runtime/Erection/GantryCraneView.cs`를 읽는다. 검색과 결과 UI는 `Runtime/Dashboard/DashboardObjectSearch.cs`를 분리 후보로 둔다.
- 확인 기준은 블록 ID와 시설명 부분 검색, 결과 없음 표시, 선택 시 카메라 이동, 입력 중 단축키 미작동, 탑재 재생 상태 유지다.
