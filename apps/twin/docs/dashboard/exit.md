# 종료 버튼

- 위치는 화면 오른쪽 위다(전원 아이콘 + ‘종료’). 누르면 확인 창이 뜨고, 확인 창의 ‘종료’를 눌러야 종료된다. Esc나 ‘취소’로 닫는다.
- 현재 별도 시작 메뉴가 확인되지 않아 종료는 macOS 앱 닫기로 정의한다. Unity 에디터에서는 Play Mode만 종료한다. 에디터 전용 API는 `UNITY_EDITOR`로 분리한다.
- 먼저 `Authoring/Dashboard.uxml`, `Runtime/Dashboard/ErectionPanelView.cs`를 확인한다. 종료 동작이 길어지면 `Runtime/Dashboard/DashboardExit.cs`로 분리한다.
- 확인 기준은 시작 화면에서 버튼이 보이고 클릭 시 실행 환경에 맞게 종료되는 것이다. 종료를 누르기 전에는 탑재 패널의 재생 버튼이 동작해야 한다.
