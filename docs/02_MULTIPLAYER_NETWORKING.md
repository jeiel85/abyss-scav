# 02. Multiplayer & Networking — Backendless GitHub Build

## 1. 목표
GitHub에서 내려받은 동일 빌드끼리 **별도 계정/중앙 서버 없이** 1~4인 협동이 가능해야 한다.

기본 모델은 **host-authoritative listen server**다.
- host가 잠수정, AI, RNG, inventory, contract state를 확정
- client는 input/interaction intent를 전송
- transport는 Godot `ENetMultiplayerPeer`

## 2. 접속 방식
### A. Solo
네트워크 transport를 열지 않는다.

### B. LAN
- host가 UDP discovery beacon을 주기적으로 broadcast
- client는 같은 subnet의 세션을 목록 표시
- discovery 정보: game version, protocol, lobby name, players/max, port
- gameplay 연결은 ENet UDP

### C. Direct IP
- 기본 game UDP port: `24857`
- client가 IPv4/hostname + port 입력
- 최근 접속 주소는 사용자 opt-in일 때만 로컬 config에 저장

### D. Internet direct connection
host에서 다음 순서로 시도한다.
1. UPnP gateway 발견
2. UDP `24857` mapping 요청
3. 성공 시 UI에 `Automatic port mapping: Ready`
4. 실패 시 수동 port-forward 안내
5. CGNAT 등으로 직접 연결 불가 시 사용자 선택형 VPN overlay 사용 안내

**v2.1은 자체 relay/matchmaking 서버를 제공하지 않는다.** 따라서 모든 인터넷 환경에서 direct connection을 보장하지 않는다.

## 3. Authority table
| State | Host | Client |
|---|---|---|
| Player input | validate/apply | send intent |
| Submarine physics | simulate | interpolate/predict visual |
| Creature AI | simulate | render |
| Inventory mutation | authorize | request |
| Relic ownership | authoritative | display |
| Contract progress | authoritative | display |
| RNG seed | generate | receive |
| Damage | calculate | feedback only |

## 4. Reliable vs Unreliable
### Reliable
- lobby settings
- ready/loadout
- inventory mutation
- door/valve
- loot pickup/drop
- contract progress
- incapacitation/revive
- scene transition
- settlement

### Unreliable / sequenced
- transform snapshots
- analog input
- creature pose/state visualization
- sonar visual pulse

## 5. Protocol envelope
```text
protocol_version : u16
message_type     : u16
sequence         : u32
session_id       : u64
sender_peer      : u64
payload_size     : u16
payload          : binary
```

- payload upper bound는 message type별 고정
- oversize packet 즉시 drop
- unknown message type drop + rate-limited warning
- gameplay packet은 JSON 사용 금지

## 6. Session handshake
Client -> Host:
- protocol_version
- game_version
- catalog_hash
- player_display_name(max 24 UTF-8 chars)

Host validates:
- protocol exact match
- content catalog hash exact match
- player count < 4
- run join policy

실패 시 사람이 이해할 수 있는 reason code를 반환한다.

## 7. Lobby
host가 authoritative.
- lobby name
- difficulty
- biome/contract
- privacy: LAN visible / hidden-direct
- max players
- ready state
- loadout

중앙 lobby directory는 없다.

## 8. Run start
Host가 `RunManifest`를 생성한다.
- seed
- biome
- contract
- difficulty
- layout hash
- catalog hash
- modifiers

client는 manifest를 받은 뒤 로컬 generation을 수행하되, host의 layout hash와 불일치하면 join을 실패시키고 diagnostic code를 표시한다.

## 9. Snapshot/interpolation
**구현 상태: 공유 월드 상태 동기화(호스트 권위) — 1단계 완료.** 함선 복제(transform 보간)는 다음 배치.

### 9.1 World snapshot (host → client, unreliable, 15 Hz)
호스트의 `RunSimulation`이 월드 권위를 가진다. 클라이언트는 **같은 매니페스트(동일 LayoutHash)로 생성한 결정적 월드**를 로컬에 두고, 호스트 스냅샷을 **인덱스 정렬**로 적용한다(월드가 결정적이므로 ID 대신 인덱스/비트마스크로 충분하다).

`WorldSnapshot` wire 필드:
- `SessionId`, `Phase`, `FailureReason`, `MajorEventId`, `MajorEventRemaining`
- `SecuredSalvageValue`, `SurveysDone`, `PulsesUsed`, `ObserveSeconds`
- `SalvagedLootMask` / `ServicedNodesMask` — 로트·노드 상태 비트마스크
- `DrillLootIndex`(-1 = 없음), `DrillElapsedSeconds`
- `Creatures[]` — `CreatureWire(X, Y, Z, State, StateTime, StunnedSeconds, SonarExposedSeconds)` 25 B/개

선박 로컬 상태(선체/압력/전력/소나/도크/elapsed)는 스냅샷에서 제외 — 각 플레이어의 함선은 자기 로컬 sim이 소유한다.

### 9.2 Player intent (client → host, reliable)
상호작용은 클라이언트 로컬 sim에 **예측 적용** 후 성공 시 인텐트를 전송하고, 호스트가 위치 파라미터 메서드(`TrySalvageFrom`/`TryStartDrillFrom`/`ApplyRemoteService`)로 재검증·승인한다. 다음 스냅샷이 확정/롤백을 전달한다.

`IntentType`: `Salvage=1`, `DrillStart=2`, `DrillCancel=3`, `Survey=4`, `Service=5`, `Pulse=6`

- **Salvage/DrillStart/Service**: 호스트가 요청자 위치로 범위 검증. 드릴은 호스트 sim에서 원격 모드로 진행(도크 불요, 전력/소음/위협 미부과, 8초 절단 후 화물 입금).
- **DrillCancel**: 클라이언트 로컬 드릴이 취소된 경우(언도크/범위 이탈/타깃 소실) watcher가 전송.
- **Survey**: 클라이언트가 소나 기반으로 검증(호스트는 요청자 소나를 볼 수 없음) → 호스트는 병합만 수행. 문서화된 한계.
- **Pulse**: 호스트가 범위 내 크리처에 `SonarExposedSeconds=15`만 적용(세계 효과). 요청자의 접촉/위협은 로컬.

### 9.3 Phase 전이
스냅샷의 `Phase`는 클라이언트 로컬 sim이 Active일 때만 적용된다.
- `Failed` → 기존 `OnRunFailed` 경로(클라이언트 sim이 `BuildFailureSettlement`로 정산)
- `Extracted` → `OnHostExtracted`(메시지 표시, 정산 없음 — 호스트만 크레딧)
- 호스트는 런 종료 시 최종 스냅샷을 1회 브로드캐스트해 클라이언트가 종료를 인지한다.

### 9.4 Host loss
호스트 세션이 비활성화되면 클라이언트는 `OnHostDisconnected`로 런을 종료한다(정산 없음). 호스트 마이그레이션은 v2.1 범위 밖.

### 9.5 다음 배치(미구현)
- 함선 transform 복제·보간(100~150 ms buffer, large correction blend)
- 크리처 마커/로트 마커의 스냅샷 보정 렌더링(현재 클라이언트는 예측 크리처 표시)
- join-in-progress, reconnect, host-loss settlement(§10-12 참조)

## 10. Join-in-progress
**미구현(다음 배치).** 허용 조건과 동기화 순서는 아래 설계를 따른다.
- host lobby setting이 허용
- extraction final sequence 이전
- protocol/catalog 일치

동기화 순서:
1. static RunManifest
2. world persistent delta
3. submarine state
4. inventory/contract state
5. creature compact state
6. player spawn

## 11. Reconnect
**미구현(다음 배치).** 설계:
- peer마다 session-scoped reconnect token 생성
- 기본 grace: 120초
- reconnect 성공 시 기존 player entity takeover
- token은 run 종료 시 폐기
- IP address를 identity로 사용하지 않는다.

## 12. Host loss
**구현 상태: 즉시 종료 정책만 구현.** 호스트 세션 비활성화 시 클라이언트는 `OnHostDisconnected`로 런을 종료한다(정산 없음). v2.1은 full host migration을 제공하지 않는다.

설계(다음 배치):
1. host disconnect 감지
2. 8초 reconnect window
3. 실패하면 클라이언트는 host-loss settlement 진입
4. host가 마지막으로 확정한 `SecuredCargoLedger`만 부분 보상
5. 각 클라이언트 local save에는 동일 settlement id를 1회만 적용

목표는 세션 복원보다 **진행 손상 방지**다.

## 13. Abuse/security limits
- interaction distance host 검증
- inventory amount host-only
- enum/id whitelist
- string/array length cap
- per-peer RPC rate limiter
- generation/contract ID는 catalog 존재 여부 확인
- remote file path/path traversal 입력 금지

## 14. Network test matrix
필수:
- LAN 1/2/4 player
- localhost debug 2 instance
- WAN direct IP
- 50/100/180 ms artificial latency
- 1/3/5% packet loss
- host quit mid-run
- client reconnect
- version mismatch
- catalog mismatch
- UPnP unavailable

## 15. UX copy 요구
Host 화면에는 다음 상태를 분명히 표시한다.
- LAN only
- Internet ready (UPnP mapped)
- Internet requires router configuration
- Direct connection may not work behind CGNAT

네트워크 제약을 오류처럼 숨기지 않는다.
