# Unity 검증 도구

- 기본 검증은 `tools/test.sh`(EditMode)다. macOS 종료는 `tools/build.sh` 결과물에서 확인하고 UI 배치는 Play Mode에서 본다.
- MCP가 필요할 때는 한 가지를 선택한다. [Unity 공식 MCP](https://unity.com/blog/unity-ai-mcp-how-to-get-started)는 Unity AI Assistant 패키지의 연결 안내를 따른다. [Coplay MCP for Unity](https://github.com/CoplayDev/unity-mcp)는 커뮤니티 대안이며 콘솔과 테스트를 Claude Code에서 확인할 수 있다.
- 설치 전에 `unity/ProjectSettings/ProjectVersion.txt`와 선택한 MCP의 지원 버전을 확인한다. 연결 후 ShipyardTwin 인스턴스를 선택하고 해당 기능의 콘솔 오류, 테스트 결과, UI 항목만 요청한다. 씬이나 로그 전체를 가져오면 토큰을 더 사용한다.
- MCP로 상태를 확인하고, 수정은 `CLAUDE.md`에 적힌 Authoring 및 런타임 소스 경로에서 진행한다.
