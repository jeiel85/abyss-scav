# 09. Save, Config, Diagnostics — Local First

## 1. 저장 정책
- 기본: local-only
- `profile_vN.json` 또는 binary + JSON debug exporter
- atomic write: temp -> flush -> rename/replace
- latest + backup 3세대
- `schema_version` 필수
- GitHub 배포판은 cloud sync를 전제로 하지 않는다.

## 2. 저장 대상
- progression currencies
- blueprint/frame unlocks
- settings
- tutorial state
- codex discoveries
- statistics
- applied settlement ids

런 중 authoritative state는 host memory에 존재한다. 정산 시 각 플레이어에게 signed가 아닌 **host-confirmed settlement payload**를 전달하고 로컬 save에 idempotent하게 적용한다.

## 3. 디렉터리
Godot `user://`를 기준으로 한다.
```text
user://
  saves/
    profile_vN.dat
    backups/
  config/
    settings.cfg
  logs/
  diagnostics/
```

## 4. Migration
`v1 -> v2 -> v3` 순차 migration.
- intermediate migration 생략 금지
- 실패 시 원본 파일 보존
- backup restore 가능
- future schema는 write 금지, read-only 안내 또는 실행 중단

## 5. Safe mode
`--safe-mode`
- 1280x720 windowed
- low graphics
- custom post-processing 최소화
- last session network 자동 재접속 금지

## 6. Crash marker
- boot 시 `session.lock`
- 정상 종료 시 삭제
- 다음 시작에 lock이 남아 있으면 crash 가능성 감지
- safe mode 버튼 표시

## 7. Diagnostics
외부 analytics/telemetry endpoint를 두지 않는다.
로컬 수집 가능 항목:
- FPS/frame-time histogram
- memory/VRAM peak
- RTT/loss
- disconnect reason
- generation validation failure
- last 200 domain events summary

## 8. Support bundle
사용자가 메뉴에서 **직접 생성**할 때만 ZIP을 만든다.
포함:
- latest logs
- graphics/input config
- game/protocol version
- hardware summary
- last crash marker

제외:
- save 원본
- public/private IP 전체 값
- OS username
- arbitrary user files

사용자가 GitHub Issue에 직접 첨부하도록 안내하며 자동 업로드하지 않는다.
