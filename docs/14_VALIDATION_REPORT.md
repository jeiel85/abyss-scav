# 14. Design Validation Report — v2.1 GitHub Track

## 1. 검증 범위
원본 v1.0과 v2.0 production 설계에서 **Steam 상점/Steamworks 종속성을 제거한 뒤에도 실제 실행 가능한 public build 설계가 유지되는지** 점검했다.

## 2. 유지한 핵심
- 1~4인 협동 심해 인양
- 소나 중심 탐색
- solo-friendly AI Sonar Buddy
- field module welding
- noise stealth
- procedural trench/facility
- pressure/creature tension
- host-authoritative multiplayer

## 3. 변경 사항
### A. 배포 채널
- 이전: Steam 배포 전제
- 현재: GitHub repository + GitHub Releases portable ZIP

### B. 멀티플레이 transport
- 이전: Steam Networking Sockets/Relay 중심
- 현재: Godot ENet

### C. 세션 발견
- 이전: Steam lobby
- 현재: LAN discovery + Direct IP

### D. 인터넷 NAT 처리
- 이전: platform relay
- 현재: UPnP 시도 + manual port forwarding + optional user-managed VPN

### E. Platform 기능
제거:
- achievement
- rich presence
- store page
- cloud save
- depot/branch
- overlay invite

대체:
- local save backup
- GitHub tag/release
- GitHub issue/support bundle

## 4. 내부 일관성 체크
- player count 1~4: PASS
- solo path: PASS
- authority host-centric: PASS
- transport ENet: PASS
- central backend 없음: PASS
- local save: PASS
- GitHub portable ZIP distribution: PASS
- store/platform SDK dependency 없음: PASS
- generation host seed + manifest: PASS
- release content target 일관성: PASS

## 5. 구현 가능성 검증
### 네트워크
LAN과 Direct-IP는 ENet으로 구현 가능하다. 다만 **CGNAT 환경까지 backendless direct connection을 보장할 수는 없다.** 이 제한을 문서와 UX에 명시했다.

### 세이브
Steam Cloud 제거로 conflict merge 복잡도가 줄고, local atomic save + 3 generation backup으로 단순화되었다.

### 배포
portable ZIP + SHA-256 + tag/version 일치를 gate로 정의했다. 설치 관리자나 auto-updater는 필수 범위가 아니다.

## 6. 높은 기술 위험
1. sonar readability
2. submarine collision/stuck recovery
3. procedural reachability
4. 4-player sync
5. WAN NAT support UX

이 항목은 M0~M2에 먼저 검증한다.

## 7. 의도적 비지원
- dedicated server
- full host migration
- public matchmaking directory
- built-in relay
- mandatory accounts
- auto updater
- store achievements/cloud
- native Linux/macOS guarantee

## 8. 최종 판정
**구현 착수 가능성: PASS**

**GitHub에서 portable Windows 빌드를 내려받아 solo/LAN/direct-IP로 실제 사용할 수 있는 범위 정의: PASS**

**모든 인터넷 환경에서 별도 설정 없는 친구 초대: NOT GUARANTEED** — backend/relay를 사용하지 않는 범위 결정 때문에 NAT/CGNAT 제약이 남는다.

**Save/QA/public build gate: PASS**

문서 완성이 게임 완성을 보장하지는 않는다. M0/M1의 실제 prototype, profiler, network soak 결과에 따라 수치 조정은 필요하다.
