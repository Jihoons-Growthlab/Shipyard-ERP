# GrowthLab Network

UnityWebRequest 기반 REST. Core에 의존하지 않는다. 테스트는 `IHttpTransport` 가짜 구현으로 외부 서버 없이 실행한다.

```csharp
var client = new RestClient(new UnityWebRequestTransport(), new RestClientOptions
{
    BaseUrl = "https://example.invalid/api",   // 샘플 값. 실제 주소·토큰은 커밋하지 않는다.
    Timeout = TimeSpan.FromSeconds(5),
    MaxRetries = 2,
});
var item = await client.GetJsonAsync<Item>("items/5", token);
```

| 동작 | 규칙 |
|---|---|
| 타임아웃 | 요청별 `HttpRequest.Timeout` > 옵션 기본값. 시도마다 적용, `NetworkErrorKind.Timeout` |
| 취소 | 호출자 토큰 취소는 `OperationCanceledException`(재시도·Timeout 변환 없음), UnityWebRequest는 Abort |
| 오류 매핑 | 400/422 BadRequest, 401, 403, 404, 408 Timeout, 409, 429 RateLimited, 기타 4xx ClientError, 5xx ServerError |
| 재시도 | GET/HEAD만. 연결 실패·타임아웃·429·502·503·504. 지수 백오프, `Retry-After`(초) 우선, `MaxRetryDelay` 상한 |

WebSocket/WebGL: [docs/WEBSOCKET_WEBGL.md](../../docs/WEBSOCKET_WEBGL.md) 참고.
