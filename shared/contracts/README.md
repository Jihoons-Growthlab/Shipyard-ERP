# ERP–Twin 연동 기준

- ERP PostgreSQL과 ERP API를 업무 계획의 원본으로 사용한다. Portal과 Twin은 각자 DB를 연결하지 않는다.
- 기존 ERP OpenAPI 문서는 ERP의 `/docs`와 `/api/openapi.json`에 있다.
- 호선·블록·시설 ID와 좌표계는 ERP 조회와 Unity 파싱에서 함께 검토한다. [Twin 데이터 형식](../../apps/twin/docs/TWIN_FORMAT.md)을 참고한다.
- 응답을 변경할 때 ERP 조회와 Twin 소비 코드를 같은 PR에서 검증한다.
