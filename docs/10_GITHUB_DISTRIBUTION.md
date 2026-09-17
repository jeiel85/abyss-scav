# 10. GitHub Repository & Distribution Operations

## 1. 목표
상점 등록 없이 GitHub를 **소스 호스팅 + 빌드 공유 + 이슈 추적**에 사용한다.

공개 사용자는 다음 흐름으로 게임을 받는다.
`GitHub Releases -> Windows ZIP download -> extract -> AbyssScav.exe`

## 2. Repository 권장 구조
```text
/
  project.godot
  AbyssScav.sln
  src/ or game source
  scenes/
  art/
  audio/
  tests/
  docs/
  tools/
  .github/
    workflows/
    ISSUE_TEMPLATE/
  README.md
  LICENSE
  THIRD_PARTY_NOTICES.md
  CHANGELOG.md
```

큰 바이너리 원본 에셋은 Git LFS 정책을 별도로 정의한다. 생성 가능한 export 결과물은 git에 commit하지 않고 Release asset으로 제공한다.

## 3. Branch 정책
소규모/1인 개발 기준:
- `main`: 항상 빌드 가능한 기준
- feature branch: `feat/...`, `fix/...`
- tag: `v0.1.0`, `v0.2.0`, `v1.0.0`

별도 release branch는 필수가 아니다.

## 4. CI 기본 단계
Pull Request / main push:
1. source checkout
2. .NET restore/build
3. data/schema validation
4. unit/integration tests
5. deterministic seed smoke corpus
6. headless boot smoke test

Tag build:
1. 위 검증 전체
2. Windows release export
3. portable ZIP 생성
4. `SHA256SUMS.txt` 생성
5. artifact 보관
6. Release draft 생성 또는 수동 업로드

## 5. GitHub Actions 비밀값
v2.1 기본 파이프라인은 store SDK/signing을 사용하지 않으므로 secret이 없어도 동작하도록 설계한다.

코드 서명 인증서를 향후 사용한다면 repository secret에 인증서 자체를 장기간 두는 방식보다 별도 signing workflow를 설계한다.

## 6. Release asset 형식
권장:
```text
AbyssScav-v0.3.0-win-x64.zip
SHA256SUMS.txt
CHANGELOG-v0.3.0.md
```

ZIP root:
```text
AbyssScav.exe
AbyssScav.pck
README_FIRST.txt
LICENSES/
```

설치 프로그램은 초기 범위에서 제외한다.

## 7. README 필수 내용
- 프로젝트 상태(alpha/beta 등)
- Windows 지원 범위
- 실행 방법
- controls
- host/join 방법
- default UDP port `24857`
- UPnP/port-forward/CGNAT 제한
- save 위치 안내
- 버그 리포트 방법
- 개인정보/telemetry 없음 명시
- 라이선스/asset attribution

## 8. Versioning
Semantic Versioning 형태를 사용한다.
- `0.x`: 공개 개발 빌드
- protocol-breaking update가 있으면 `protocol_version`도 증가
- save schema는 game version과 독립적으로 증가 가능

Git tag와 게임 내 표시 버전은 일치해야 한다.

## 9. Public build 단계
### Internal
개발자/지인 검증. Release 공개 불필요.

### GitHub Pre-release
- 기능은 플레이 가능
- known issues 허용
- release page에서 Pre-release 표시 권장

### Stable GitHub Build
- `checklists/RELEASE_GATE.md` 전체 통과
- P0/P1 0
- save migration 보장
- 기존 stable 사용자 데이터 손실 없는 업데이트

## 10. Rollback
GitHub에서는 이전 Release asset을 유지한다.
문제가 있는 tag는 바이너리를 조용히 덮어쓰지 않는다.
- affected release에 warning 추가
- hotfix version 새 tag
- 필요 시 이전 stable 권장

같은 버전 ZIP을 다른 바이너리로 재업로드하지 않는 것을 원칙으로 한다.

## 11. Issue workflow
Bug issue에서 요구:
- game version
- OS
- solo/LAN/direct IP
- reproduction steps
- expected/actual
- support bundle(optional)

Security-sensitive exploit은 public issue 대신 README에 지정한 비공개 연락 수단이 있는 경우 그 경로를 안내한다.

## 12. 라이선스
코드 라이선스와 asset 라이선스를 분리해서 관리한다.
- repository `LICENSE`: 소스 코드
- `THIRD_PARTY_NOTICES.md`: 외부 라이브러리
- `assets/LICENSES/`: 에셋 출처/라이선스

라이선스를 정하기 전까지 임의의 오픈소스 라이선스를 붙이지 않는다.
