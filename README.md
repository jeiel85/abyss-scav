# AbyssScav GitHub Production Design Package v2.1

이 패키지는 기존 v1.0 GDD와 v2.0 Production 설계를 바탕으로, **Steam/상점 출시를 전제로 하지 않고 GitHub 저장소 + GitHub Releases에서 공유하여 실제 플레이 가능한 Windows 빌드를 제공하는 수준**으로 재정리한 설계 문서입니다.

## 기준
- Target: Windows 10/11 PC
- Engine: Godot 4.7.2 stable, .NET build
- Language: C# 중심
- Players: Solo + 2~4 player co-op
- Network: Godot ENet 기반 host-authoritative listen server
- Discovery: LAN discovery + Direct IP join
- Internet co-op: UPnP 자동 포트 개방 시도, 실패 시 수동 포트 포워딩 또는 사용자 선택형 VPN(Tailscale 등) 안내
- Dedicated backend: 없음
- Account/login: 없음
- Analytics/ads: 없음
- Distribution: GitHub repository + GitHub Releases ZIP
- Save: 로컬 저장 + 3세대 백업. 외부 cloud 필수 아님

## 문서 구조
1. `docs/00_MASTER_GDD.md` — 전체 게임 설계
2. `docs/01_TECHNICAL_ARCHITECTURE.md` — 엔진/코드/런타임 구조
3. `docs/02_MULTIPLAYER_NETWORKING.md` — LAN/Direct-IP 협동, 권한, RPC, 재접속
4. `docs/03_GAMEPLAY_SYSTEMS.md` — 잠수정/소나/압력/소음/AI/인양
5. `docs/04_PROCEDURAL_GENERATION.md` — 해구/시설/POI 절차 생성
6. `docs/05_PROGRESSION_ECONOMY.md` — 계약/보상/메타 성장/경제
7. `docs/06_CONTENT_BIBLE.md` — GitHub 공개 빌드 콘텐츠 목표
8. `docs/07_UI_UX_ACCESSIBILITY.md` — HUD/메뉴/입력/접근성
9. `docs/08_ART_AUDIO_PIPELINE.md` — 그래픽·사운드·에셋 파이프라인
10. `docs/09_SAVE_CONFIG_TELEMETRY.md` — 로컬 저장/설정/로그/충돌 복구
11. `docs/10_GITHUB_DISTRIBUTION.md` — GitHub 저장소/Actions/Releases 운영
12. `docs/11_QA_PERFORMANCE_SECURITY.md` — QA, 성능 예산, 네트워크/보안
13. `docs/12_PRODUCTION_PLAN.md` — 마일스톤/작업분해/Definition of Done
14. `docs/13_RISK_REGISTER.md` — 제품/기술/콘텐츠 리스크
15. `docs/14_VALIDATION_REPORT.md` — v2.1 재검증 결과
16. `docs/15_IMPLEMENTATION_BACKLOG.md` — 구현 Epic/Acceptance Criteria
17. `docs/16_CODE_CONTRACTS.md` — 핵심 인터페이스/오류 규약
18. `schemas/*.json` — 데이터 스키마 예시
19. `checklists/*.md` — 개발/공개빌드 체크리스트
20. `templates/github/` — GitHub Actions/Issue/Release 템플릿

## 중요한 범위 결정
### 포함
- 완전한 솔로 플레이
- LAN 1~4인
- Direct IP 1~4인
- 인터넷 직접 접속을 위한 UPnP 시도
- 호스트 권한형 동기화/재접속/host-loss 안전 정산
- Windows portable ZIP build
- GitHub Actions 자동 export/검증
- GitHub Releases 수동 또는 tag 기반 공개

### 제외
- Steamworks
- Steam Lobby/Relay/Achievement/Cloud
- 전용 매치메이킹 서버
- 전용 게임 서버
- 필수 로그인/계정
- 인게임 자동 업데이트 프로그램

## 설계 원칙
- gameplay state는 host가 결정한다.
- 절차 생성은 seed와 manifest로 재현할 수 있어야 한다.
- 네트워크 실패가 save/progression 손상으로 이어지지 않아야 한다.
- GitHub 배포판은 설치 프로그램 대신 portable ZIP을 기본으로 한다.
- 사용자 PC에서 별도 런타임 설치 없이 실행 가능한 export를 목표로 한다.
- 외부 분석 SDK를 넣지 않는다. 진단 자료는 사용자가 직접 export할 때만 생성한다.

## Public Build Gate 요약
- P0/P1 blocker 0건
- Windows clean PC에서 압축 해제 후 실행 성공
- solo 45분 run 완주
- LAN 4-player 90분 soak 성공
- Direct-IP 2-player WAN test 성공
- save corruption recovery 성공
- 10,000 procedural seed reachability test 성공
- GitHub Actions build + SHA-256 manifest 생성 성공
- README에 네트워크/포트/세이브/라이선스가 명확히 기술됨
