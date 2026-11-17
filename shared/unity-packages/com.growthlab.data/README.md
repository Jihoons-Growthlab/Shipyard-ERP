# GrowthLab Data

JSON 직렬화(`JsonUtilitySerializer`)와 로컬 키-값 저장(`FileStorage`, `MemoryStorage`), 타입 저장소(`DataStore`). Core에 의존하지 않는다.

```csharp
var store = new DataStore(FileStorage.InPersistentData("saves"));
store.Save("slot1", new SaveGame { Level = 3 });
var result = store.Load<SaveGame>("slot1");   // Loaded / Missing / Corrupted, 예외 없음
```

- 쓰기는 임시 파일 후 교체(`File.Replace`)라 중간 실패 시 이전 값 유지.
- 키는 영문·숫자·`_-.` 1~64자, 경로 탈출 불가.
- JsonUtility 제약: 최상위 배열·딕셔너리·원시값 불가. 필요해지면 `ISerializer`를 교체한다.
- WebGL: `persistentDataPath`는 IndexedDB 기반이라 쓰기 후 브라우저 동기화 시점이 다르다 — 미검증.
