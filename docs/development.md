# 로컬 개발

## 포트

| 서비스 | 주소 | 쓰임 |
| --- | --- | --- |
| Portal | http://127.0.0.1:8080 | 로컬 진입 화면 |
| ERP | http://127.0.0.1:8001 | 웹과 API |
| PostgreSQL | 127.0.0.1:55432 | Docker 내부 5432, VS Code DB 접속 |
| Redis | 127.0.0.1:56379 | Docker 내부 6379, ERP 세션과 캐시 |
| Unity Twin | macOS 앱 또는 Unity Play Mode | ERP 8001에 접속 |
| 실험용 ERP | 8031 | 이전 벤치마크용 포트, 일반 실행은 8001 |

## 최초 한 번

저장소 루트에서 실행한다.

```sh
python3 -m venv apps/erp/.venv
apps/erp/.venv/bin/pip install -r apps/erp/requirements.txt
cp apps/erp/.env.example apps/erp/.env
```

`apps/erp/.env`에 로컬 비밀번호를 입력한다. 기존 Docker DB를 계속 쓸 경우 기존 ERP의 로컬 비밀번호를 동일하게 설정한다. Compose 프로젝트 이름을 `shipyard`로 유지하므로 기존 PostgreSQL·Redis 볼륨을 사용한다. 기존 ERP와 통합 ERP를 같은 포트에서 함께 실행할 수 없다.

```sh
tools/dev db-up
```

새 DB에만 첫 관리자를 생성한다.

```sh
apps/erp/shipyard-postgres create_admin.py --id admin --name 관리자
```

## 실행·종료

각 터미널에서 `tools/dev erp`, `tools/dev portal`을 실행한다. 각 서버는 해당 터미널에서 Ctrl+C로 종료한다. DB 상태는 `tools/dev db-status`, DB 컨테이너 중지는 `tools/dev db-stop`이다. 같은 Compose 프로젝트를 사용하는 기존 ERP도 DB 중지의 영향을 받는다.

Unity Hub에서 `apps/twin/unity`를 연다. 터미널에서는 `tools/dev twin-test`, `tools/dev twin-build`, `tools/dev twin-run`을 사용한다. Unity 앱에 실시간 ERP 연결이 필요하면 실행 터미널에서 `SHIPYARD_API_URL=http://127.0.0.1:8001`과 `SHIPYARD_API_TOKEN`을 환경변수로 제공한다. Unity Editor는 같은 환경변수를 가진 터미널에서 열어야 한다. 위성 사진이 필요하면 배포 조건을 확인한 뒤 `apps/twin/pipeline/fetch_satellite.py`로 로컬에서 생성한다.

## Claude Code와 Git

저장소 루트에서 `claude`를 실행한다. 대시보드 전용 `/shipyard-dashboard` 스킬을 사용할 때는 `apps/twin`에서 Claude Code를 시작한다.

- ERP 작업: “`apps/erp`에서 블록 적치 조회를 수정하고 ERP 테스트를 실행해줘.”
- Twin 작업: “`apps/twin/CLAUDE.md`의 경로 안내에서 야드 배치 관련 파일만 읽고 수정해줘.”
- 연동 작업: “`shared/contracts/README.md`를 참고해서 ERP 응답과 Unity 파싱을 함께 수정하고 앱별로 검증해줘.”

원격 `origin`은 기존 ERP 저장소다. 통합 브랜치에서 검토한 뒤 올린다. 빌드 결과, Unity Library, 로컬 비밀번호와 DB는 Git에서 제외한다. 위성 사진은 배포 권한을 확인할 때까지 로컬 파일로 관리한다.

현재 통합 변경은 로컬 `integration/monorepo` 브랜치의 작업 트리에 있다. 변경을 검토한 뒤 아래 순서로 올린다.

```sh
git status --short
git add -A
git diff --cached --stat
git commit -m "Organize Shipyard apps in one repository"
git push -u origin integration/monorepo
```
