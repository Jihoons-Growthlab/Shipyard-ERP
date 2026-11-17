# GrowthLab Unity 모듈 사용 매뉴얼

여러 Unity 게임·앱에서 재사용하는 공통 부품 모음이다. 필요한 모듈만 골라 설치하고, 나머지는 프로젝트에 들어가지 않는다.

- 기준 Unity: **6000.3 이상** (검증: 6000.3.23f1)
- 마지막 검증: 2026-11-16. EditMode 테스트 111개, PlayMode 테스트 9개 통과. 쇼케이스 앱 자동 시연 32개 시나리오 통과. 빈 프로젝트 3곳에 설치해 컴파일 확인.

---

## 0. 5분 체험: 쇼케이스 앱

모든 모듈을 버튼으로 직접 실행해 보는 앱이다. 앱 안의 가짜 서버로 동작하므로 인터넷이나 실제 계정이 필요 없다.

```
cd /Users/hoon/Desktop/personal_project/UnityModule
playground/build.sh                               # 약 20초. 앱 3개를 playground/Builds/에 만든다
open playground/Builds/GrowthLabShowcase.app
```

화면 구성:
- **위쪽 탭 4개**: Core, Data, Network, Auth
- **왼쪽**: 버튼, 그리고 결과 목록. 결과마다 **기대**와 **실제**가 표시되고, 둘이 같으면 초록 `[OK]`, 다르면 빨강 `[다름]`으로 나온다.
- **오른쪽**: 가짜 서버가 받은 요청 기록과 앱 로그

각 탭의 **▶ 이 탭 모두 실행**을 누르면 그 탭의 시나리오를 차례로 전부 실행한다. 탭별 버튼 설명은 [5장](#5-쇼케이스-화면-안내)에 있다.

---

## 1. 모듈 한눈에 보기

| 모듈 | 이럴 때 쓴다 | 같이 설치되는 모듈 |
|---|---|---|
| **core** | 앱 시작 순서 관리, 서비스 연결, 이벤트, 설정, 씬 이동, 로그. 거의 모든 프로젝트의 기반 | 없음 |
| **data** | 세이브 파일, 캐시 등 로컬 저장 | 없음 |
| **network** | REST API 호출. 타임아웃, 자동 재시도, 오류 분류 포함 | 없음 |
| **auth** | 로그인 토큰 보관, 자동 갱신, 401 처리 | network |
| **ui** | UI Toolkit 대시보드 부품(게이지, 상태 배지, 카드, 메시지 목록, 3D 라벨)과 테마 | 없음 |
| **authoring** (에디터 전용) | 명세 JSON, CSV, UXML로 프리팹·씬·UI 자동 생성 | 없음 |

샘플(선택 설치):

| 샘플 | 내용 |
|---|---|
| showcase | 모든 모듈을 버튼으로 시험하는 화면 (0장의 앱) |
| basicgame | 메뉴 → 게임 → 최고 점수 저장까지 되는 최소 게임 |
| digitaltwin | 모의 설비 데이터 폴링, UI Toolkit 대시보드, 오프라인 복원. 씬 전체가 명세 파일로 생성된다 |

> 프리팹·씬·UI를 만드는 방법(디지털 트윈용)은 별도 매뉴얼 **[AUTHORING.md](AUTHORING.md)**에 있다.

**모듈은 서로 독립이다.** data만 설치해도 되고, core 없이 network만 써도 된다. 의존 관계는 auth → network 하나뿐이다.

---

## 2. 내 프로젝트에 설치하기

### 2.1 설치 스크립트 (권장)

의존 모듈까지 알아서 함께 넣어 준다.

```
# 사용할 수 있는 모듈과 현재 설치 상태 보기
python3 tools/install-modules.py ~/Projects/MyGame --list

# core와 data 설치
python3 tools/install-modules.py ~/Projects/MyGame core data

# auth 설치 (network가 자동으로 같이 들어감)
python3 tools/install-modules.py ~/Projects/MyGame auth

# 쇼케이스 샘플까지 함께 설치
python3 tools/install-modules.py ~/Projects/MyGame core data network auth --samples showcase

# 미리보기만 (파일 변경 없음)
python3 tools/install-modules.py ~/Projects/MyGame auth --dry-run

# 제거 (다른 모듈이 아직 쓰고 있으면 거부한다)
python3 tools/install-modules.py ~/Projects/MyGame --remove auth
```

스크립트는 대상 프로젝트의 `Packages/manifest.json`만 수정하고, 수정 전 내용은 `manifest.json.bak`으로 남긴다. 다음에 Unity 창으로 돌아가면 패키지가 로드된다.

**설치 방식 3가지:** 상황에 맞게 고른다.

| 방식 | 명령 | 이럴 때 | 특징 |
|---|---|---|---|
| 링크 (기본) | `... core` | 모듈을 계속 개발하면서 여러 프로젝트에 쓸 때 | 이 저장소 폴더를 직접 가리킨다. 모듈을 고치면 모든 프로젝트에 바로 반영된다. 이 폴더를 옮기면 경로가 끊긴다 |
| Git | `... core --git <저장소URL> [--tag v0.1.0]` | 팀원과 공유할 때, 버전을 고정할 때 | 저장소를 원격에 올려야 한다(아래 주의). 프로젝트가 이 폴더 없이도 동작한다 |
| 복사 | `... core --copy` | 이 프로젝트에서만 모듈을 고쳐 쓰고 싶을 때 | 프로젝트의 `Packages/` 안으로 복사한다. 이후 원본과 연결이 끊긴다. 모듈 자체 테스트는 복사하지 않는다 |

> **Git 방식 주의:** `.meta` 파일을 함께 커밋해야 한다. 이번 실행으로 `packages/`와 `samples/` 아래에 이미 생성되어 있다. 저장소는 아직 원격에 올리지 않았으므로, Git 방식은 올린 다음에만 쓸 수 있다.

### 2.2 직접 설치 (스크립트 없이)

대상 프로젝트의 `Packages/manifest.json`에 줄을 추가한다. **GrowthLab 패키지는 레지스트리에 없으므로, 의존 모듈도 직접 적어야 한다.** 예를 들어 auth를 쓰려면 network도 적는다.

```json
{
  "dependencies": {
    "com.growthlab.core": "file:/Users/hoon/Desktop/personal_project/UnityModule/packages/com.growthlab.core",
    "com.growthlab.network": "file:/Users/hoon/Desktop/personal_project/UnityModule/packages/com.growthlab.network",
    "com.growthlab.auth": "file:/Users/hoon/Desktop/personal_project/UnityModule/packages/com.growthlab.auth"
  }
}
```

Unity 메뉴의 **Window > Package Manager > + > Install package from disk**에서 각 패키지의 `package.json`을 골라도 결과는 같다.

### 2.3 설치 확인

- **Package Manager:** "GrowthLab Core" 같은 항목이 보이면 설치된 것이다.
- **코드:** `using GrowthLab.Core;`가 컴파일되면 된다. 모든 모듈 어셈블리는 자동 참조로 설정되어 있어서, asmdef가 없는 일반 `Assets/` 스크립트에서도 바로 쓸 수 있다.
- **asmdef를 쓰는 경우:** 내 asmdef의 references에 `GrowthLab.Core`, `GrowthLab.Data`, `GrowthLab.Network`, `GrowthLab.Auth` 중 필요한 것을 추가한다.

---

## 3. 첫 코드: 앱 시작점 만들기 (core)

**1단계.** 아래 스크립트를 만든다.

```csharp
using System.Threading;
using System.Threading.Tasks;
using GrowthLab.Core;
using UnityEngine;

[System.Serializable]
public sealed class GameSettings          // 설정 묶음: public 필드만, JSON으로 덮어쓰기 가능
{
    public int StartGold = 100;
    public string FirstScene = "Menu";
}

public sealed class GameBootstrap : CoreBootstrap
{
    [SerializeField] private TextAsset _settingsJson;   // 선택: {"StartGold":500} 같은 JSON

    protected override void Configure(CoreContext c)
    {
        var settings = c.Settings.SetDefaults(new GameSettings(), _settingsJson ? _settingsJson.text : null);
        var wallet = new Wallet(c.Events, settings.StartGold);
        c.Services.Register(wallet);                    // 다른 코드에서 c.Services.Get<Wallet>()

        c.Pipeline                                      // 등록한 순서대로 시작, 종료는 역순
            .Add("wallet", wallet)
            .Add("first-scene", ct => c.Scenes.LoadAsync(settings.FirstScene, cancellationToken: ct));
    }
}

public readonly struct GoldChanged { public GoldChanged(int gold) { Gold = gold; } public int Gold { get; } }

public sealed class Wallet : IInitializable, IShutdownable
{
    private readonly IEventBus _events;
    public Wallet(IEventBus events, int gold) { _events = events; Gold = gold; }
    public int Gold { get; private set; }
    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;   // 저장 불러오기 등
    public void Shutdown() { }                                                // 저장 등
    public void Add(int amount) { Gold += amount; _events.Publish(new GoldChanged(Gold)); }
}
```

**2단계.** 첫 씬(Boot)에 빈 GameObject를 만들고 `GameBootstrap`을 붙인다. Build Settings에 Boot 씬을 첫 번째로, Menu 씬을 그다음에 넣는다.

**3단계.** 씬의 UI 스크립트는 `ICoreConsumer`를 구현하면 Core를 자동으로 받는다.

```csharp
public sealed class GoldLabel : MonoBehaviour, ICoreConsumer
{
    private System.IDisposable _subscription;

    public void Construct(CoreContext c)               // 자동 호출 (나중에 열린 씬은 Start보다 먼저)
    {
        var wallet = c.Services.Get<Wallet>();
        _subscription = c.Events.Subscribe<GoldChanged>(e => Debug.Log("Gold " + e.Gold));
    }

    private void OnDestroy() => _subscription?.Dispose();   // 구독 해제는 반드시
}
```

**4단계.** Play한다. 콘솔에 `[Init] Initialized 2 step(s).`가 찍히고 Menu 씬으로 넘어가면 성공이다.

---

## 4. 모듈별 사용법

### 4.1 Core

| 기능 | 사용법 | 기억할 점 |
|---|---|---|
| 시작 순서 | `c.Pipeline.Add("이름", 객체 또는 람다)` | 앞 단계가 실패하면 이미 끝난 단계만 역순으로 종료되고, `OnInitializationFailed`가 호출된다 |
| 서비스 | `c.Services.Register(x)`, `Get<T>()`, `TryGet<T>()` | 같은 타입을 두 번 등록하면 예외. 인터페이스로 등록하려면 `Register<IFoo>(x)` |
| 이벤트 | `Subscribe<T>(handler)`는 해제 핸들을 돌려준다. 발행은 `Publish(msg)` | 타입이 정확히 같아야 받는다. 핸들러 하나가 예외를 던져도 나머지는 계속 받는다 |
| 설정 | `SetDefaults(기본값, json)`, `Get<T>()`, `ApplyJson<T>(json)` | `Get`은 복사본을 준다. 바꾸려면 `Set`이나 `ApplyJson`을 쓰고, 그러면 `SettingsChanged<T>` 이벤트가 나간다 |
| 씬 | `c.Scenes.LoadAsync(name, mode)`, `UnloadAsync(name)` | 진행률, 취소, 로드 이벤트가 필요하면 `c.Scenes`를 쓴다. 로드는 한 번에 하나씩만 된다 |
| 자동 주입 | `ICoreConsumer` 구현 | Core가 준비된 뒤에 열린 씬은 **어떤 방법으로 열든**(`c.Scenes`든 `SceneManager.LoadScene`이든) 자동 주입되고, `Construct`가 그 씬의 `Start`보다 먼저 호출된다. 첫 씬처럼 초기화 중에 이미 열려 있던 씬은 초기화가 끝난 직후에 주입된다 |
| 런타임 생성 오브젝트 | `c.Instantiate(prefab, parent)`, 이미 만든 오브젝트는 `c.Inject(go)` | `Object.Instantiate`로 만든 오브젝트는 자동 주입되지 않는다 |
| 컴포넌트가 아닌 코드 | `CoreBootstrap.WhenReady(ctx => ...)` | 준비됐으면 바로, 아니면 준비 직후 실행된다. 초기화가 실패하면 실행되지 않는다 |
| 오류 격리 | 자동 | 한 `Construct`가 예외를 던져도 로그만 남고 나머지 주입과 `OnReady`는 계속된다 |
| 로그 | `c.Log.Info("태그", "메시지")` | 인스펙터의 Log Level로 출력 수준을 조절한다. 로그를 바꾸려면 `CreateLog()`를 재정의한다(쇼케이스의 `ShowcaseLog` 참고) |

`CoreBootstrap` 인스펙터 옵션: **Persist Across Scenes**(기본 켜짐)를 켜 두면 씬이 바뀌어도 Core가 유지된다.

**Bootstrap은 하나만** 둘 수 있다. 다른 씬에 실수로 하나 더 들어 있으면 경고를 남기고 그 컴포넌트는 스스로 제거된다(Core가 두 벌 생기지 않는다). 현재 Bootstrap은 `CoreBootstrap.Active`로 확인한다. `Awake`를 재정의할 때는 `base.Awake()`를 호출해야 한다.

### 4.2 Data

```csharp
using GrowthLab.Data;

[System.Serializable] public sealed class SaveData { public int Level; public string Name; }

var store = new DataStore(FileStorage.InPersistentData("mygame"));   // persistentDataPath/mygame/*.json
store.Save("slot1", new SaveData { Level = 3, Name = "Hero" });

var result = store.Load<SaveData>("slot1");
switch (result.Status)
{
    case LoadStatus.Loaded:    Use(result.Value); break;
    case LoadStatus.Missing:   NewGame(); break;               // 처음 실행
    case LoadStatus.Corrupted: Backup(); NewGame(); break;     // 파일 손상. 예외로 앱이 죽지 않는다
}

var simple = store.LoadOrDefault("slot1", new SaveData());     // 없거나 손상이면 기본값
```

- **안전한 저장:** 임시 파일에 먼저 쓰고 교체하므로, 저장 도중 앱이 꺼져도 이전 파일이 남는다.
- **키 규칙:** 영문, 숫자, `_`, `-`, `.`만 쓸 수 있고 1~64자이며 `.`으로 시작할 수 없다. `../`처럼 폴더 밖으로 나가는 키는 거부된다.
- **저장 형식:** Unity `JsonUtility` 규칙을 따른다. `[Serializable]` 클래스의 public 필드만 저장되며, Dictionary나 최상위 배열은 안 된다. 다른 형식이 필요하면 `ISerializer`를 구현해 넘긴다.
- **테스트:** `new DataStore(new MemoryStorage())`를 쓰면 파일 없이 메모리에서 동작한다.

### 4.3 Network

```csharp
using GrowthLab.Network;

var rest = new RestClient(new UnityWebRequestTransport(), new RestClientOptions
{
    BaseUrl = "https://api.example.com",       // 실제 주소·토큰은 코드에 커밋하지 말고 설정으로 넣는다
    Timeout = TimeSpan.FromSeconds(10),
    MaxRetries = 2,
});

try
{
    var item = await rest.GetJsonAsync<Item>("/items/5", destroyCancellationToken);
    var created = await rest.SendJsonAsync<NewItem, Item>(HttpMethod.Post, "/items", new NewItem { Name = "a" });
}
catch (NetworkException e) when (e.Kind == NetworkErrorKind.ConnectionFailed || e.Kind == NetworkErrorKind.Timeout)
{
    ShowOffline();                    // e.Attempts = 실제 보낸 횟수 (재시도 포함)
}
catch (NetworkException e) when (e.Kind == NetworkErrorKind.Unauthorized) { GoToLogin(); }
catch (OperationCanceledException) { /* 화면을 닫아 취소함 */ }
```

자동 재시도 규칙. 쇼케이스 Network 탭에서 같은 결과를 직접 볼 수 있다.

| 상황 | 결과 | 재시도 |
|---|---|---|
| 연결 실패, 타임아웃, 429, 502/503/504 | `ConnectionFailed`, `Timeout`, `RateLimited`, `ServerError` | **GET/HEAD만** 재시도한다. 기다리는 간격은 0.5초, 1초, 2초…로 늘어나고, 서버가 `Retry-After`를 보내면 그 값을 따른다 |
| POST/PUT/PATCH/DELETE | 위와 같은 종류의 실패 | **재시도하지 않는다** (주문이 두 번 들어가는 것을 막기 위해) |
| 400, 401, 403, 404, 409, 500 | `BadRequest`, `Unauthorized`, `Forbidden`, `NotFound`, `Conflict`, `ServerError` | 재시도하지 않는다 |
| 호출자가 취소 | `OperationCanceledException` | 재시도하지 않는다 |

**테스트와 오프라인 개발:** `IHttpTransport`를 직접 구현하면 서버 없이 개발할 수 있다. 예시는 `samples/Showcase/Runtime/ShowcaseServer.cs`.

### 4.4 Auth

앱이 할 일은 두 가지다. ① 로그인해서 받은 토큰을 `SignIn`에 넘긴다. ② 토큰 갱신 API를 `IAuthBackend`로 구현한다.

```csharp
using GrowthLab.Auth;
using GrowthLab.Network;

public sealed class MyAuthBackend : IAuthBackend
{
    private readonly IRestClient _rest;
    public MyAuthBackend(IRestClient rest) { _rest = rest; }

    public async Task<AuthTokens> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        try
        {
            var r = await _rest.SendJsonAsync<RefreshRequest, TokenResponse>(HttpMethod.Post, "/auth/refresh",
                new RefreshRequest { RefreshToken = refreshToken }, ct);
            return new AuthTokens(r.AccessToken, r.RefreshToken, DateTime.UtcNow.AddSeconds(r.ExpiresIn));
        }
        catch (NetworkException e) when (e.Kind == NetworkErrorKind.Unauthorized)
        {
            // 서버가 refresh 토큰을 거부함 → 세션이 자동으로 로그아웃(Expired)된다
            throw new AuthException(AuthErrorKind.RefreshRejected, "refresh rejected", e);
        }
    }
}

var session = new AuthSession(new MyAuthBackend(rest));              // 만료 30초 전부터 미리 갱신
var api = new AuthenticatedRestClient(rest, session);                // Bearer 헤더 자동, 401이면 1번 갱신 후 재전송
session.Changed += change => { if (change == AuthChange.Expired) GoToLogin(); };

session.SignIn(new AuthTokens(login.AccessToken, login.RefreshToken, DateTime.UtcNow.AddSeconds(login.ExpiresIn)));
var me = await api.GetJsonAsync<Profile>("/me");
```

보장되는 동작은 다음과 같다. 쇼케이스 Auth 탭에서 각각 확인할 수 있다.
- **동시 갱신:** 요청 여러 개가 동시에 만료를 만나도 갱신 요청은 1번만 보낸다.
- **만료 임박:** 만료가 가까운 토큰은 보내기 전에 미리 갱신하므로 401 왕복이 생기지 않는다.
- **갱신 중 로그아웃:** 기다리던 요청은 `SessionEnded`로 실패한다. 늦게 도착한 갱신 결과는 버려지므로, 로그아웃한 세션이 다시 살아나지 않는다.
- **`Changed` 이벤트 순서:** 이벤트는 실제 일어난 순서대로 전달된다. 이번 세션에서 순서가 뒤바뀌는 버그를 고쳤다.

---

## 5. 쇼케이스 화면 안내

| 탭 | 버튼 | 확인하는 것 |
|---|---|---|
| Core | 이벤트 발행 / 구독 해제 / 다시 구독 | 구독 중에만 이벤트를 받는다. 해제(Dispose)를 두 번 해도 오류가 없다 |
| | 설정 덮어쓰기 | JSON에 있는 항목만 바뀌고(제목) 나머지는 유지된다. 변경 이벤트가 1번 나간다 |
| | 추가 씬 로드 / 언로드 | 새 씬의 오브젝트가 Core를 자동으로 받는다(`ExtraSceneProbe`) |
| Data | 저장 / 불러오기 / 삭제 | Loaded, Missing 상태. 오른쪽에 실제 파일 내용이 보인다 |
| | 파일 망가뜨리기 → 불러오기 | 앱이 멈추지 않고 Corrupted를 돌려준다 |
| | 잘못된 키로 저장 | `../outside` 같은 키는 거부된다 |
| | 폴더 열기 | Finder에서 저장 폴더를 연다 |
| Network | 정상 / 일시 장애 / 계속 장애 / POST / 타임아웃 / 429 / 404 | 4.3의 규칙표대로 시도 횟수와 결과가 나온다. 오른쪽에 서버가 받은 요청이 모두 보인다 |
| | GET /slow 보내기 → 취소 | 사용자 취소는 재시도 없이 즉시 Canceled가 된다 |
| Auth | 로그인 → 내 정보 요청 | 토큰이 정상이면 갱신 없이 200 |
| | 서버에서 토큰 무효화 → 요청 | 401을 받은 뒤 자동으로 갱신하고 재전송한다(401 1번, 갱신 1번) |
| | 만료 임박 토큰으로 요청 | 보내기 전에 갱신한다(401 0번) |
| | 동시에 5개 요청 | 5개 모두 성공하고, 갱신은 1번만 한다 |
| | 갱신 도중 로그아웃 / 서버가 갱신 거부 | SessionEnded, 그리고 자동 로그아웃(Expired) |

설정 바꿔 보기: 에디터에서 `Bootstrap` 오브젝트의 Settings Json에 TextAsset을 넣는다. 예: `{"TimeoutSeconds":3,"MaxRetries":0}`. 넣고 나면 Network 탭 결과가 그에 맞게 달라진다.

---

## 6. 테스트, 빌드, 자동 시연

| 하는 일 | 명령 | 결과 위치 |
|---|---|---|
| 순수 로직 테스트 | `validation/run-tests.sh EditMode` | `validation/TestResults/EditMode.xml` |
| 씬, 수명주기 테스트 | `validation/run-tests.sh PlayMode` | `validation/TestResults/PlayMode.xml` |
| 샘플 앱 3개 빌드 | `playground/build.sh` | `playground/Builds/*.app` |
| 쇼케이스 자동 시연 | `playground/Builds/GrowthLabShowcase.app/Contents/MacOS/GrowthLabShowcase -showcase-autorun ./autorun` | `./autorun/results.txt`, 탭별 스크린샷 4장 |

- **테스트 종료 코드:** 0 = 통과, 2 = 실패한 테스트 있음, 그 외 = 컴파일·환경 오류. 컴파일 오류는 로그에서 `error CS`로 찾는다.
- **자동 시연:** 모든 시나리오가 기대와 같으면 종료 코드 0이다. 화면 창이 잠깐 뜬다.

---

## 7. 폴더 구조와 확장 규칙

```
packages/               설치 대상 모듈 (UPM 패키지). 각 폴더 = Runtime / Editor / Tests 분리
  com.growthlab.core       Core는 다른 GrowthLab 모듈을 절대 참조하지 않는다
  com.growthlab.data
  com.growthlab.network
  com.growthlab.auth       → network
  com.growthlab.ui, resources, diagnostics   로드맵 자리만 있음 (package.json 없음 = 설치 불가)
samples/                선택 설치 샘플 (각각 독립 패키지). 모듈은 샘플을 참조하지 않는다
  Showcase, BasicGame, DigitalTwin
tools/install-modules.py   다른 프로젝트에 설치
validation/             테스트 전용 Unity 프로젝트 + run-tests.sh
playground/             샘플 빌드용 Unity 프로젝트 + build.sh
docs/                   이 매뉴얼, 설치(INSTALLATION), 코드맵(CODEMAP), 로드맵
```

새 모듈을 추가할 때 지킬 규칙:
1. `packages/com.growthlab.<이름>/`에 `package.json`, `Runtime/<asmdef>`, `Tests/Editor/<asmdef>`를 만든다. 설치 스크립트는 `package.json`이 있는 폴더를 자동으로 인식한다.
2. 다른 GrowthLab 모듈을 쓰면 `package.json`의 `dependencies`와 asmdef의 `references` 양쪽에 적는다. Core가 선택 모듈을 참조하면 안 된다.
3. 게임 규칙이나 디지털 트윈 도메인 모델은 모듈에 넣지 않고 샘플이나 앱 쪽에 둔다.
4. `validation/UnityModuleValidation/Packages/manifest.json`의 dependencies와 testables에 추가한 뒤 테스트를 실행한다.
5. 공개 API를 바꾸면 `version`을 올리고, 그 모듈에 의존하는 package.json의 버전도 맞춘다.

---

## 8. 문제 해결

| 증상 | 원인과 해결 |
|---|---|
| `The type or namespace 'GrowthLab' could not be found` | 내 스크립트가 asmdef 안에 있다면 references에 `GrowthLab.Core` 등을 추가한다 |
| auth만 manifest에 적었더니 설치 실패 | network도 적어야 한다. 설치 스크립트를 쓰면 자동으로 함께 들어간다 |
| `ICoreConsumer.Construct`가 호출되지 않음 | `Object.Instantiate`로 만든 오브젝트다. `c.Instantiate`나 `c.Inject(go)`를 쓴다. 초기화가 실패하지 않았는지 로그(`[Init]`)도 확인한다 |
| 첫 씬의 `Start`에서 Core가 아직 null | 첫 씬은 초기화가 끝난 뒤 주입된다. 시작 코드는 `Start` 대신 `Construct`에 둔다 |
| `Another CoreBootstrap ... is already active` 경고 | Bootstrap이 두 개다. 첫 씬에만 남기고 나머지는 지운다 |
| `Another scene operation is in progress` | 씬 로드는 한 번에 하나만 된다. `c.Scenes.IsBusy`를 확인한다 |
| 빌드한 앱이 다른 창을 누르면 멈춤 | Player Settings > Resolution > **Run In Background**를 켠다(플레이그라운드 빌드에는 켜져 있음) |
| EditMode 테스트에서 `GetResult()`가 멈춤 | Unity 메인 스레드 동기화 때문이다. 테스트 SetUp에서 `SynchronizationContext.SetSynchronizationContext(null)`로 바꾼다(기존 테스트 참고) |
| `Retry-After`가 너무 길다 | `RestClientOptions.MaxRetryDelay`가 상한이다(기본 10초) |
| WebGL | `docs/WEBSOCKET_WEBGL.md` 참고. WebGL 빌드는 아직 검증하지 않았다(빌드 모듈 미설치) |
