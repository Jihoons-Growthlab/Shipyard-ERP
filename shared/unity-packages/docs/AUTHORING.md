# 프리팹·씬·UI 만들기 매뉴얼 (디지털 트윈용)

**한 줄 요약:** 짧은 텍스트 파일 3종(명세 JSON, 배치표 CSV, 화면 UXML)을 고치고 "빌드"를 누르면 프리팹, 씬, UI가 만들어진다. Unity 씬과 프리팹 파일은 결과물이라 손대지 않는다.

```
 고치는 파일 (짧은 텍스트)                         생성기                   결과물 (손대지 않음)
 ┌────────────────────────┐
 │ plant.build.json        │ 어떤 종류가 있나 ─┐
 │ equipment.csv           │ 어디에 몇 개 ────┼──▶ GrowthLab Authoring ──▶ Prefabs/*.prefab
 │ Dashboard.uxml / *.uxml │ 화면 구성 ───────┘    (Build From Spec)       Materials/*.mat
 └────────────────────────┘                                              Scene.unity, UI/PanelSettings
```

왜 이렇게 하나:
- **토큰과 실수가 줄어든다.** 큐브 3개짜리 씬 파일은 21.6KB인데, 같은 내용의 명세와 CSV는 1KB 안팎이다. 설비 하나를 추가하는 일은 CSV 한 줄이면 된다.
- **사람도 Claude도 같은 파일을 본다.** 무엇이 바뀌었는지 diff로 바로 보인다.

검증 (2026-11-16):
- 생성기 테스트 7개와 UI 부품 테스트 5개 통과 (전체 EditMode 123개 통과)
- DigitalTwin 샘플을 이 방식으로 만들어 빌드한 뒤 실행 화면을 캡처해 확인
- CSV에 한 줄을 추가해 설비 1대가 씬, 카드, 라벨, 데이터에 모두 반영되는 것 확인

---

## 1. 새 디지털 트윈 프로젝트 시작하기

1. **Unity 6000.3 이상으로 새 프로젝트를 만든다.** 3D 템플릿이면 된다.
2. **모듈을 설치한다.** 저장소 폴더에서 실행한다.
   ```
   python3 tools/install-modules.py ~/Projects/MyTwin core data network ui authoring
   ```
3. **시작 파일을 만든다.** Unity 메뉴 **GrowthLab > Authoring > New Spec**을 누르면 `Assets/NewTwin/`에 바로 빌드되는 예제 3개가 생긴다.
   - `NewTwin.build.json`
   - `placements.csv`
   - `Dashboard.uxml`
4. **빌드한다.** Project 창에서 `.build.json`을 우클릭하고 **GrowthLab > Build From Spec**을 누른다. 씬이 열리면 Play한다.
5. **이후 작업은 이 반복이다.** 3개 파일을 고치고, 다시 Build From Spec. 여러 명세를 한 번에 빌드하려면 **GrowthLab > Authoring > Build All Specs**를 쓴다.

> 동작하는 전체 예제는 `samples/DigitalTwin/Authoring/`에 있다(명세, CSV, 대시보드, 카드 템플릿). 새 프로젝트를 만들 때 이 폴더를 복사해서 시작하는 것도 좋다.

---

## 2. 명세 파일 (`*.build.json`)

```json
{
  "Scene": "Assets/Plant/Plant.unity",
  "Camera": { "Position": [0.5, 6, -8.5], "LookAt": [1.2, 0.6, 1], "Background": "#1E2230" },
  "Prefabs": [
    { "Name": "Pump",  "Shape": "Cylinder", "Scale": [1, 0.8, 1], "Color": "#64748B", "Components": ["EquipmentView"] },
    { "Name": "Floor", "Shape": "Plane",    "Scale": [2, 1, 1.2], "Color": "#161B26" }
  ],
  "Placements": "equipment.csv",
  "Objects": [ { "Name": "Bootstrap", "Components": ["DigitalTwinBootstrap"] } ],
  "Ui": [ { "Name": "Dashboard", "Uxml": "Dashboard.uxml", "Components": ["WorldLabels", "TwinDashboardView"],
            "Set": ["CardTemplate=EquipmentCard.uxml"] } ]
}
```

**키 이름은 대소문자를 구분한다** (`Scene`이 맞고 `scene`은 틀리다).

| 키 | 뜻 | 기본값 |
|---|---|---|
| `Scene` | 만들 씬 경로. `Assets/`로 시작하고 `.unity`로 끝나야 한다 | 필수 |
| `OutputFolder` | 프리팹, 머티리얼, UI 설정을 저장할 폴더 | 씬이 있는 폴더 |
| `AddToBuildSettings` | 씬을 Build Settings 맨 앞에 넣을지 | `true` |
| `Camera` | `Position`, `LookAt`(각각 [x,y,z]), `Background`(#RRGGBB) | 위에서 비스듬히 |
| `Light` | `Rotation`([x,y,z]), `Intensity` | 50, -30, 0 / 1 |
| `Prefabs[]` | 오브젝트 종류. 아래 표 참고 | — |
| `Placements` | 배치표 CSV 경로 | — |
| `Objects[]` | 매니저처럼 하나씩만 있는 오브젝트. `Name`, `Prefab`(선택), `Position`, `Rotation`, `Components`, `Set` | — |
| `Ui[]` | UI 문서. `Name`, `Uxml`, `SortingOrder`, `Components`, `Set` | — |

`Prefabs[]` 항목:

| 키 | 뜻 |
|---|---|
| `Name` | 프리팹 이름. CSV의 `prefab` 열에 이 이름을 쓴다 |
| `Shape` | `Cube`, `Sphere`, `Capsule`, `Cylinder`, `Plane`, `Quad`, `Empty` 중 하나 |
| `Model` | FBX 같은 모델 파일 경로. 지정하면 `Shape` 대신 쓰이고, 파일이 없으면 `Shape`로 대체하며 경고(WARN)를 낸다 |
| `Scale` | `[x,y,z]` 또는 `[n]`(모든 축에 같은 값) |
| `Color` | `#RRGGBB`. 머티리얼을 만들어 입힌다 |
| `Components` | 붙일 컴포넌트. 클래스 이름만 써도 되고, 이름이 겹치면 전체 이름을 쓴다 |
| `Set` | 필드 값. `"Field=value"` 또는 `"컴포넌트.Field=value"` |

**경로 규칙:** `Placements`, `Uxml`, `Model`, 그리고 에셋 값은 **명세 파일이 있는 폴더 기준**으로 찾는다. `Assets/...`나 `Packages/...`로 시작하면 그대로 쓴다.

---

## 3. 배치표 (CSV)

```csv
name,prefab,x,y,z,rotY,EquipmentId
# '#'으로 시작하는 줄은 주석이다
floor,Floor,2.5,0,1,0,
pump-01,Pump,0,0.8,0,0,pump-01
valve-02,Valve,2.5,0.4,0,45,valve-02
```
- **필수 열:** `name,prefab,x,y,z`. `rotY`(Y축 회전 각도)는 선택이다.
- **나머지 열:** 각 오브젝트의 필드 값이 된다(`Set`과 같은 규칙). 예: `EquipmentId`, `EquipmentView.EquipmentId`.
- **빈 칸:** 건너뛴다.
- **쉼표 금지:** 값 안에 쉼표를 쓸 수 없다.
- **오류가 있는 줄:** 그 줄만 빠지고, 오류에 `파일명:줄번호`가 표시된다.
- **DigitalTwin 샘플:** CSV에 배치된 설비가 곧 수신 대상 목록이다. 줄 하나를 추가하면 씬, 카드, 라벨, 데이터 수신이 모두 늘어난다.

### 필드 값 쓰는 법 (`Set`과 CSV 공통)

| 필드 종류 | 쓰는 법 |
|---|---|
| 문자열 | 그대로 쓴다: `pump-01` |
| 숫자 | `2.5`, `10` |
| bool | `true` 또는 `false` |
| enum | 이름으로 쓴다: `Warning` |
| 색 | `#FF0000` |
| Vector2/3 | `1;2;3` (쉼표가 아니라 세미콜론) |
| 에셋 참조 | 경로: `EquipmentCard.uxml`, `Models/Pump.fbx` |

**필드 이름 찾는 규칙:** `EquipmentId`라고 쓰면 `EquipmentId`, `equipmentId`, `_equipmentId`, `m_EquipmentId`를 차례로 찾는다. 그래서 private `[SerializeField]` 필드나 Unity 내장 컴포넌트(`Light.Intensity` 등)에도 쓸 수 있다. 어떤 컴포넌트에 어떤 필드가 있는지는 **카탈로그**(6장)에 나온다.

---

## 4. UI (UI Toolkit)

### 4.1 기본 구조
UXML 맨 위에 두 네임스페이스를 선언한다. 레이아웃은 `gl-` 클래스로 잡는다.
```xml
<ui:UXML xmlns:ui="UnityEngine.UIElements" xmlns:gl="GrowthLab.UI">
    <ui:VisualElement class="gl-screen" picking-mode="Ignore">
        <ui:VisualElement class="gl-panel gl-sidebar">
            <ui:Label text="설비 현황" class="gl-title" />
            <gl:StatCard title="pump-01" level="Normal">
                <gl:Gauge name="temp" label="온도" unit="°C" min="40" max="100" warning-at="75" alarm-at="82" />
            </gl:StatCard>
            <gl:MessageList name="events" capacity="30" empty-text="이벤트 없음" />
        </ui:VisualElement>
    </ui:VisualElement>
</ui:UXML>
```

### 4.2 공용 부품

| 요소 | 속성 | 코드에서 쓰는 법 |
|---|---|---|
| `gl:Gauge` | `label`, `unit`, `min`, `max`, `value`, `warning-at`, `alarm-at`, `decimals` | `.Value = 72.5f`. 경계값을 넘으면 노랑(주의)이나 빨강(경보)으로 바뀐다 |
| `gl:StatusBadge` | `level`(Normal/Info/Warning/Alarm/Offline), `text` | `.Level = StatusLevel.Alarm` |
| `gl:StatCard` | `title`, `level` | 안에 넣은 요소는 카드 본문으로 들어간다. 상태 배지는 `.Badge` |
| `gl:MessageList` | `capacity`, `empty-text` | `.Add(StatusLevel.Warning, "pump-01 주의")`. 최신 항목이 위에 온다 |
| `WorldLabels` (컴포넌트) | — | `.Track(transform, new StatusBadge(...), Vector3.up * 1.4f)`. 3D 물체 위에 따라다니는 라벨 |

### 4.3 레이아웃 클래스
- **화면 구성:** `gl-screen`(전체 화면), `gl-panel`(어두운 패널), `gl-sidebar`(폭 360px), `gl-bottombar`
- **배치:** `gl-row`, `gl-column`, `gl-grow`, `gl-spacer`, `gl-gap`
- **글자:** `gl-title`, `gl-subtitle`, `gl-section`

### 4.4 테마
`packages/com.growthlab.ui/Runtime/Resources/GrowthLabUI.uss` 맨 위의 `--gl-*` 변수(배경, 글자, 상태 색 등)를 바꾸면 모든 화면에 적용된다. 생성기가 만드는 UI 설정은 이 테마를 자동으로 쓴다.

### 4.5 디자인은 UXML, 데이터는 코드
반복되는 카드는 **템플릿 UXML**(예: `EquipmentCard.uxml`)로 만들고, 코드에서 복제해 값을 넣는다. `samples/DigitalTwin/Runtime/TwinDashboardView.cs`가 그 예다.
```csharp
var card = _cardTemplate.Instantiate();          // EquipmentCard.uxml
card.Q<StatCard>().Title = id;
card.Q<Gauge>("temperature").Value = t.Temperature;
list.Add(card);
```
디자인을 바꿀 때는 UXML만 고치면 되고, 코드는 요소의 `name`으로만 찾는다.

---

## 5. Claude에게 시키는 법

이 프로젝트에는 `unity-twin-authoring` 스킬이 있어서 프리팹, 씬, UI 요청이 오면 Claude가 위 규칙대로 명세 파일만 고치고 빌드로 확인한다. 또 `.claude/settings.json`이 `.unity`, `.prefab`, `.mat`, `Library/` 읽기를 막아서, 실수로 큰 파일을 읽어 토큰을 쓰는 일을 막는다.

### 그대로 따라 쓸 수 있는 명령 예

| 이렇게 말하면 | Claude가 고치는 것 |
|---|---|
| "pump-04를 x=7.5에 추가해줘" | CSV 한 줄 |
| "밸브는 빨간 큐브 말고 회색 실린더로 바꿔줘" | 명세 `Prefabs`의 Valve 항목 |
| "탱크 크기를 1.5배로" | `Prefabs[].Scale` |
| "카드에 유량 게이지(0~50 L/min, 40 이상 주의) 추가해줘" | `EquipmentCard.uxml`에 `gl:Gauge` 한 줄, View 코드에 값 연결 한 줄 |
| "오른쪽 아래에 알람 목록 패널 만들어줘" | `Dashboard.uxml` |
| "전체 색을 밝은 테마로" | 테마의 `--gl-*` 변수 |
| "설비 10대를 2열로 배치해줘 (간격 3m)" | CSV 10줄 |
| "Pump 모델을 Models/Pump.fbx로 바꿔줘" | `Prefabs[].Model` |

### 토큰을 더 아끼는 요령
1. **대상을 구체적으로 말한다.** 파일, ID, 숫자를 짚어 준다. "pump-01, pump-02 온도 경고 기준을 70으로"가 "경고 기준 좀 낮춰줘"보다 탐색이 적다.
2. **변경을 묶어서 한 번에 요청한다.** 빌드와 확인도 한 번이면 된다.
3. **확인 수준을 지정한다.**
   - "빌드까지만": 결과 한 줄로 확인한다. 가장 저렴하다.
   - "화면 캡처로 확인": 이미지 1장이 추가로 든다.
   - "파일만 고쳐줘": 빌드하지 않는다.
4. **Unity 에디터는 닫아 둔다.** 열려 있으면 Claude가 백그라운드로 빌드할 수 없어서, 사용자가 메뉴로 직접 빌드해야 한다.
5. **새 스크립트를 만들면 카탈로그를 갱신하게 한다.** 갱신해 두면 Claude가 소스 코드 대신 1~2KB짜리 목록만 읽는다.

---

## 6. 명령 모음 (터미널, 에디터가 닫혀 있을 때)

```
U=/Applications/Unity/Hub/Editor/6000.3.23f1/Unity.app/Contents/MacOS/Unity
P=<Unity 프로젝트 경로>

# 시작 파일 만들기 (메뉴 New Spec과 같음)
"$U" -batchmode -nographics -quit -projectPath "$P" -executeMethod GrowthLab.Authoring.Editor.SpecTemplates.CreateBatch -folder Assets/MyTwin

# 명세 빌드 (-spec 생략 시 프로젝트의 모든 *.build.json). 종료 코드 0 = 성공
"$U" -batchmode -nographics -quit -projectPath "$P" -executeMethod GrowthLab.Authoring.Editor.SpecBuilder.BuildBatch -spec Assets/Plant/plant.build.json -logFile build.log
grep -E "\[Authoring\]|  ERROR|  WARN" build.log

# 카탈로그 (쓸 수 있는 컴포넌트·필드·UI 요소 목록)
"$U" -batchmode -nographics -quit -projectPath "$P" -executeMethod GrowthLab.Authoring.Editor.AuthoringCatalog.WriteBatch -out GrowthLabCatalog.md

# 빌드한 앱 화면 캡처 (모든 GrowthLab UI 프로젝트에서 동작)
MyApp.app/Contents/MacOS/MyApp -screen-fullscreen 0 -growthlab-screenshot shot.png -growthlab-screenshot-delay 6
```

에디터 메뉴로도 같은 일을 할 수 있다.
- **GrowthLab > Authoring:** New Spec, Build All Specs, Write Catalog
- **Project 창에서 명세 우클릭:** GrowthLab > Build From Spec

---

## 7. 문제 해결

빌드 결과는 항상 `[Authoring] OK ...` 또는 `[Authoring] FAILED ...` 한 줄로 요약되고, 아래에 `ERROR`/`WARN` 줄이 붙는다.

| 메시지 | 원인과 해결 |
|---|---|
| `Spec is not valid JSON` | JSON 문법 오류이거나 키 대소문자가 틀렸다(`Scene`, `Prefabs`) |
| `unknown component 'X'. Similar: ...` | 클래스 이름 오타다. Similar에 나온 이름을 쓴다 |
| `component 'X' is ambiguous` | 같은 이름이 여러 개 있다. 네임스페이스까지 전체 이름을 쓴다 |
| `no serialized field 'X'` | 필드 이름이 틀렸거나 직렬화되지 않는 필드다. 카탈로그를 확인한다 |
| `unknown prefab 'X'. Defined: ...` | CSV `prefab` 열의 값이 명세 `Prefabs[].Name`에 없다 |
| `equipment.csv:5: column 'x' is not a number` | 그 줄의 숫자를 확인한다 |
| `cells but the header has` | 값에 쉼표가 들어갔다 |
| `no UXML at ...` | 경로가 틀렸거나 UXML 문법 오류다(Console에 원인이 나온다) |
| `model not found ... using shape` (WARN) | 모델 파일이 없어서 기본 도형으로 대신했다 |
| 빌드는 됐는데 UI가 안 보임 | `UIDocument`의 UXML이 비었거나 루트에 `gl-screen`이 없다 |
| batch 실행이 바로 실패함 | 같은 프로젝트가 에디터에 열려 있다. 에디터 메뉴로 빌드한다 |

---

## 8. 구조 (어디에 무엇이 있나)

| 위치 | 역할 |
|---|---|
| `packages/com.growthlab.ui` | 런타임 UI 부품(Gauge, StatusBadge, StatCard, MessageList), WorldLabels, 테마(USS/TSS), 화면 캡처 훅. 다른 GrowthLab 모듈에 의존하지 않는다 |
| `packages/com.growthlab.authoring` | 에디터 전용 생성기: `SpecBuilder`, `PlacementTable`, `FieldSetter`, `TypeLookup`, `AuthoringCatalog`, `SpecTemplates`. 빌드에는 포함되지 않는다 |
| `samples/DigitalTwin/Authoring` | 실제로 쓰이는 명세, CSV, UXML 예제 |
| `.claude/skills/unity-twin-authoring` | Claude 작업 레시피 |
| `.claude/settings.json` | 생성 파일 읽기·편집 금지 |

설계 규칙:
- **패키지에는 범용 기능만 둔다.** 생성기와 UI 부품은 "설비" 같은 도메인을 모른다. 설비 개념(`EquipmentView`, 경보 기준 등)은 프로젝트나 샘플 쪽에 둔다.
- **명세가 유일한 원본이다.** 생성된 씬과 프리팹은 다시 빌드하면 덮어쓰인다. 직접 수정하는 대신 명세에 반영한다.
