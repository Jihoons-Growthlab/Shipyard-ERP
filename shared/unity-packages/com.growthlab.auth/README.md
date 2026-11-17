# GrowthLab Auth (선택)

`com.growthlab.network`에만 의존한다. 로그인 화면·서버 프로토콜은 앱이 `IAuthBackend`로 구현한다.

```csharp
var session = new AuthSession(new MyAuthBackend(restClient));   // 갱신 호출만 구현
session.SignIn(new AuthTokens(access, refresh, expiresAtUtc)); // 로그인 결과를 넣는다
session.Changed += change => { if (change == AuthChange.Expired) ShowLogin(); };

IRestClient api = new AuthenticatedRestClient(restClient, session); // Bearer 헤더, 401 시 1회 갱신 후 재전송
```

| 상황 | 동작 |
|---|---|
| 만료 30초(기본) 이내 토큰 | 사용 전 갱신 |
| 동시 갱신 요청 | 백엔드 호출 1회 공유. 같은 오래된 토큰으로 본 401도 공유 |
| 갱신 중 로그아웃/재로그인 | 대기자는 즉시 `SessionEnded`, 백엔드 토큰 취소, 늦게 온 결과는 버림 |
| 갱신 거부(`RefreshRejected`) | 로그아웃 + `AuthChange.Expired` |
| 일시 오류 | 세션 유지, 다음 호출에서 다시 갱신 |
| 호출자 취소 | 그 호출자만 중단, 공유 갱신은 계속 |

실제 토큰은 샘플·로그·설정 파일에 넣지 않는다. 토큰 영구 저장은 이 패키지 범위 밖이다(플랫폼 보안 저장소 필요).
