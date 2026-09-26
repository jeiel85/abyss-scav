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
- reconnect_token(optional, ≤64 B — v2, §11)

Host validates:
- protocol exact match (현재 `NetLimits.ProtocolVersion = 2`; v1과는 접속 불가)
- content catalog hash exact match
- player count < 4 (재접속 대기 좌석 포함)
- run join policy (§10 JIP, §11 재접속)

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

wire 상한: id 필드는 64 B, layout/catalog hash 필드는 128 B(`sha256:` + 64 hex = 71 B). v1 코덱은 해시에도 64 B 상한을 적용해 실제 해시를 인코딩하지 못했고 런 시작이 "Run manifest is too large" 로 실패했다 — v2에서 수정.

## 9. Snapshot/interpolation
**구현 상태: 공유 월드 동기화 + 함선 transform 복제·보간 + 플레이어별 추출 — 구현됨 (protocol v2).**

### 9.1 World snapshot (host → client, unreliable, 15 Hz)
호스트의 `RunSimulation`이 월드 권위를 가진다. 클라이언트는 **같은 매니페스트(동일 LayoutHash)로 생성한 결정적 월드**를 로컬에 두고, 호스트 스냅샷을 **인덱스 정렬**로 적용한다(월드가 결정적이므로 ID 대신 인덱스/비트마스크로 충분하다).

`WorldSnapshot` wire 필드:
- `SessionId`, `Phase`, `FailureReason`, `MajorEventId`, `MajorEventRemaining`
- `SecuredSalvageValue`, `SurveysDone`, `PulsesUsed`, `ObserveSeconds`
- `SalvagedLootMask` / `ServicedNodesMask` — 로트·노드 상태 비트마스크
- `DrillLootIndex`(-1 = 없음), `DrillElapsedSeconds`
- `Creatures[]` — `CreatureWire(X, Y, Z, State, StateTime, StunnedSeconds, SonarExposedSeconds)` 25 B/개

- `Ships[]` (v2) — 모든 플레이어의 최신 검증 포즈 `ShipPoseWire` 46 B/개(§9.5)

선박 로컬 시뮬레이션 상태(선체/압력/전력/소나/도크/elapsed)는 스냅샷에서 제외 — 각 플레이어의 함선은 자기 로컬 sim이 소유한다. 포즈(위치/방향/속도/선체 %/상태 플래그)는 **렌더링용**으로만 복제된다.

### 9.2 Player intent (client → host, reliable)
상호작용은 클라이언트 로컬 sim에 **예측 적용** 후 성공 시 인텐트를 전송하고, 호스트가 위치 파라미터 메서드(`TrySalvageFrom`/`TryStartDrillFrom`/`ApplyRemoteService`)로 재검증·승인한다. 다음 스냅샷이 확정/롤백을 전달한다.

`IntentType`: `Salvage=1`, `DrillStart=2`, `DrillCancel=3`, `Survey=4`, `Service=5`, `Pulse=6`, `Extract=7`(v2, §9.6)

- **Salvage/DrillStart/Service**: 호스트가 요청자 위치로 범위 검증. 드릴은 호스트 sim에서 원격 모드로 진행(도크 불요, 전력/소음/위협 미부과, 8초 절단 후 화물 입금).
- **DrillCancel**: 클라이언트 로컬 드릴이 취소된 경우(언도크/범위 이탈/타깃 소실) watcher가 전송.
- **Survey**: 클라이언트가 소나 기반으로 검증(호스트는 요청자 소나를 볼 수 없음) → 호스트는 병합만 수행. 문서화된 한계.
- **Pulse**: 호스트가 범위 내 크리처에 `SonarExposedSeconds=15`만 적용(세계 효과). 요청자의 접촉/위협은 로컬.

### 9.3 Phase 전이
스냅샷의 `Phase`는 클라이언트 로컬 sim이 Active일 때만, **공유 상태를 모두 적용한 뒤 마지막에** 적용된다. 와이어 phase는 `Active/Extracted/Failed`만 합법(그 외 값의 스냅샷은 거부).
- `Failed` → 클라이언트 `OnRunFailed`(자기 sim의 `BuildFailureSettlement`로 정산). 호스트 잠수정 파괴 = 팀 실패(기존 정책 유지).
- `Extracted` → **팀 탈출**(§9.6): 클라이언트 sim이 `TryExtractByHostAuthority()`로 자기 성공 정산 초안을 만들고 원장으로 1회 적용한다.
- 호스트는 런 종료 시 최종 스냅샷을 unreliable로 1회 + **reliable `GameEvent(FinalSnapshot)`** 로 한 번 더 보낸다(데이터그램 유실로 종료를 모르는 클라이언트가 없도록). 이어서 `EndRunAdmission()` — 재접속 토큰 폐기(§11), JIP 차단.

### 9.4 Host loss
§12 참조(8초 재연결 창 → host-loss settlement). 호스트 마이그레이션은 v2.1 범위 밖.

### 9.5 Ship transform replication (v2)
- **Client → host**: `MessageType.ShipPose = 21`(unreliable, ≤64 B) — `session_id u64 + ShipPoseWire`. 15 Hz(`NetLimits.PoseSendHz`, 물리 4프레임마다), unreliable 수신 한도 20/s 이하.
- **`ShipPoseWire`** 46 B: `peer u32, pos f32×3(도메인 좌표), rot quat f32×4(Godot basis), vel f32×3, hull % u8(0–100), flags u8`(`Quiet/Docked/Drilling/Extracted/Failed/Teleported`). 디코드 시 비유한값·미정의 플래그 비트·hull>100 거부.
- **Host 검증** `ShipPoseGate`(Domain): 유한값, 쿼터니언 길이 0.9–1.1, 속도 ≤ 60 m/s(부스트 종단속도 ≈33 m/s의 여유치), 월드 AABB(노드 + 최대 clearance + 80 m) 안, 도착 간격 ≥ 1/30 s, 마지막 **승인** 포즈 대비 이동거리 ≤ 60 m/s × dt + 12 m. 1회용 비상 윈치 순간이동은 `Teleported` 플래그로 **플레이어당 런 1회**만 허용(재접속 rekey 후에도 예산 유지). 거부된 포즈는 기준을 오염시키지 않는다. 포즈의 소유 peer는 항상 transport 발신자로 덮어쓴다.
- **Host → all**: 호스트 자신의 포즈 + 3초 이내 수신된 각 클라이언트의 최신 승인 포즈를 `WorldSnapshot.Ships`에 실어 15 Hz로 재방송(별도 메시지 없음 → 클라이언트 unreliable 수신량 불변).
- **보간** `PoseInterpolationBuffer`(Domain): 수신자 로컬 도착 시각 기준 타임라인, **렌더 지연 120 ms**(≈15 Hz 2샘플), 버퍼 고갈 시 마지막 속도로 최대 250 ms 외삽 후 정지, 연속 샘플 간 40 m 초과 점프는 버퍼 리셋 + **텔레포트 스냅**. `PoseCorrection`: 표시 위치와 보간 목표 차이 ≤1.5 m는 그대로 추종, 1.5–40 m는 τ=150 ms 지수 블렌드(**large-correction blend**), 40 m 초과는 스냅.
- **렌더링**: `RemoteSubmarineProxy` — 이름표(Label3D) + 호박색 조명. 3초 무수신 시 숨김, 30초 무수신 시 해제, `Extracted/Failed` 플래그면 숨김. 호스트 쪽은 끊긴 peer에 `(link lost)` 표시 후 재접속 시 같은 엔티티를 새 peer id로 rekey. 클라이언트는 매 스냅샷의 함선 목록(호스트가 보낸 전체 목록)에 없는 원격 프록시를 즉시 해제하므로, 재접속으로 peer id가 바뀐 팀원이 유령 프록시로 남지 않는다.
- **충돌 정책(결정)**: 프록시는 **물리 바디/콜라이더가 없다**. 지터·보간 지연·보정 스냅 때문에 원격 선체가 로컬 잠수정을 예기치 않게 밀어내는 일을 원천 차단하기 위함이며, 팀원 잠수정은 서로 통과한다.
- **소나**: `TeammateSonarOverlay`가 `SonarDisplay`의 전체 크기 자식으로 붙어 팀원을 "빈 원 + 이름 첫 글자"로 그린다(모양으로 구분, 색에만 의존하지 않음). 범위 밖 팀원은 테두리에 방위만 표시. 스코프 기하(중심 y=144, 반경 96 px)는 `SonarDisplay._Draw`와 동기화해야 한다.

### 9.6 Per-player extraction & settlement (v2)
docs/05는 협동 분배를 명시하지 않으므로 **가장 단순하고 공정한 규칙**을 택했다.
- **개인 탈출**: 공유 주 목표 완료 + **자기 잠수정**이 탈출 반경(30 m) 안이면 누구나 탈출 가능. 클라이언트는 같은 규칙을 로컬로 선검사(즉시 현지화된 거부 메시지) 후 `IntentType.Extract`를 보내고, 호스트가 **인텐트의 보고 위치가 아니라 `ShipPoseGate`가 마지막으로 승인한 요청자 포즈**(1.5초 이내, 없거나 오래되면 거부)로 `CanAuthorizeRemoteExtraction(pos)` 재검증해 reliable `GameEvent`로 `ExtractApproved`(현재 월드 스냅샷 포함) 또는 `ExtractRefused`(사유)를 회신한다. 6초 무응답이면 요청을 해제하고 재시도를 안내한다. 승인 시 클라이언트는 동봉 스냅샷(호스트 확정 공유 상태)을 적용한 뒤 `TryExtractByHostAuthority()`.
- **팀 탈출**: 호스트가 탈출하면 아직 Active인 모든 클라이언트가 각자 성공 정산을 받는다(늦게 도착한 팀원을 버리는 호스트 그리핑 방지). 이미 실패/탈출/host-loss 정산한 클라이언트는 영향 없음.
- **정산 금액**: 플레이어마다 **자기 sim**의 `BuildSuccessSettlement` — 계약 기본급·확보 화물(공유 마스크로 재구성)·목표/리스크 보너스는 팀 공유 값, 수리비는 **자기 잠수정** 기준. 즉 각 플레이어가 전체 계약 보상을 받는다(분할 없음).
- **멱등성**: 정산 id는 `settle.{seed}.{runInstanceId}.{contract}`로 각 클라이언트 sim 인스턴스마다 고유하고 초안은 캐시된다 — 같은 로컬 세이브에는 원장(`src/Persistence/Settlement.cs`)으로 정확히 1회 적용된다. 반복 스냅샷/이벤트 수신도 두 번째 초안을 만들지 않는다.
- **JIP 악용 방지**: 주 목표가 완료되면(탈출 최종 단계) 신규 JIP를 받지 않으므로, 탈출 직전 합류로 전체 보상을 가져가는 경로가 없다(§10).

### 9.7 남은 한계
- 크리처 AI는 호스트 sim에서 호스트 잠수정 위치 기준으로 동작한다. 클라이언트 로컬 sim은 복제된 크리처 상태로 FSM을 계속 진행하지만, 타격은 **자기 잠수정이 실제 공격 사거리(`AttackRangeMeters` x1.5) 안에 있을 때만** 선체 피해로 처리된다. 멀리서 팀원을 공격 중인 크리처의 `Attack` 상태는 클라이언트 선체에 피해를 주지 않는다(디코이/플레어로 유인된 크리처도 동일).
- 서베이는 클라이언트 소나 기준 검증(호스트는 요청자 소나를 볼 수 없음).
- 로트/크리처 마커는 스냅샷 보정 전 예측 표시.

## 10. Join-in-progress
**구현 상태: 구현됨 (v2).** 허용 조건:
- host lobby setting이 허용 — 로비의 **"진행 중인 다이브 참가 허용"** (`LobbySettings.JoinInProgress`, 기본 꺼짐, `LobbyUpdate` 입장 비트 1). `JoinAllowed`(비트 0)가 꺼져 있으면 로비 자체가 닫힌다.
- extraction final sequence 이전 — **호스트의 공유 주 목표가 완료되는 순간**부터 최종 단계로 보고 `SetRunFinalSequence()`로 JIP를 닫는다(거부 사유 `RunFinalSequence`). 런 종료 시 `EndRunAdmission()`.
- protocol/catalog 일치 — 핸드셰이크의 exact match(§6)와 동일. 좌석 수에는 재접속 대기 좌석(§11)도 포함된다.

실제 순서: 핸드셰이크 승인 → 좌석/토큰 발급 → **manifest 즉시 전송** → 클라이언트는 로비 화면에서(이미 manifest가 와 있으면 곧바로) 월드를 재생성·LayoutHash 검증 후 런 씬으로 이동 → 다음 15 Hz 월드 스냅샷이 **전체 상태**(델타 아님)를 전달하므로 2~5단계가 한 번에 충족된다. 잠수정은 다른 플레이어처럼 탈출 지점 근처에서 스폰된다.

동기화 순서:
1. static RunManifest
2. world persistent delta
3. submarine state
4. inventory/contract state
5. creature compact state
6. player spawn

## 11. Reconnect
**구현 상태: 구현됨 (v2).**
- peer마다 session-scoped reconnect token 생성(16 B 난수, base64, ≤64 B). IP address를 identity로 사용하지 않는다.
- 기본 grace: 120초 — **마지막으로 해당 peer의 프레임을 받은 시점**(그리고 끊긴 시점)부터 다시 잰다(`ReconnectTokenStore.Refresh`). 발급 시점 기준이 아니므로 120초보다 긴 런에서도 재접속 가능.
- 런 중 끊긴 peer의 좌석은 `ReservedSeats`로 토큰 만료까지 보관(용량에 포함, `Players`에는 미표시). 로비 단계에서 끊기면 좌석은 해제된다.
- 토큰은 **핸드셰이크 요청에 실린다**(v2) — 로비가 꽉 찼거나 신규 참가가 닫혀 있어도 보관 좌석을 되찾을 수 있다. 좌석이 이미 해제됐으면 일반 용량 규칙으로 재입장. v1의 사후 `ReconnectClaim`도 같은 takeover 경로로 처리.
- reconnect 성공 시 기존 player entity takeover: 좌석(준비 상태는 리셋)이 새 peer id로 이동, 토큰 회전, `PeerReconnected(old,new)` → 호스트의 함선 프록시·포즈 게이트가 같은 엔티티를 rekey, 런 중이면 manifest 재전송 → 다음 전체 월드 스냅샷으로 재동기화. 클라이언트는 재전송된 manifest가 현재 런(시드/LayoutHash)과 다르면 호스트 상실로 처리한다.
- 클라이언트 쪽 재접속은 §12의 8초 창 안에서 자동 수행된다(`CoopRunLink` → `GodotNetworkSession.ReconnectAsync`). 토큰은 메모리에만 있으므로 프로세스 재시작 후 재접속은 지원하지 않는다(이미 host-loss 정산을 한 클라이언트가 같은 런에 다시 들어와 이중 정산하는 경로도 없다).
- token은 run 종료 시 폐기(`EndRunAdmission`).
- 채널별 재생 방지: reliable/unreliable 프레임은 ENet 채널이 달라 서로 추월할 수 있으므로 수신 시퀀스 창을 **채널별로 분리**한다(늦게 온 reliable 인텐트가 새 포즈/스냅샷 뒤에서 버려지지 않음). 클라이언트는 호스트 전용 메시지(스냅샷/이벤트/manifest/로비/토큰)를 peer 1 이외 발신자로부터 받으면 버린다(서버 relay 스푸핑 차단).

## 12. Host loss
**구현 상태: 구현됨 (v2).** v2.1은 full host migration을 제공하지 않는다.
1. host disconnect 감지 — transport 끊김(ENet이 `peer_disconnected(1)`을 주지 않아도 클라이언트 연결 상태 전이를 transport가 1회 보고) 또는 런 중 호스트를 한 번이라도 들은 뒤 **10초 무수신**(`HostSilenceSeconds`; 호스트는 일시정지 중에도 스냅샷을 계속 보낸다). `HostLinkMonitor`(Protocol)가 판정.
2. 8초 reconnect window — HUD 경고 "HOST LINK LOST — reconnecting (Ns left)". 창 안에서 토큰 재접속(§11)을 반복 시도하고, 성공하면 같은 잠수정으로 계속. 창 동안 포즈 전송·탈출 요청은 중단.
3. 실패하면 클라이언트는 host-loss settlement 진입(`RunPhase.HostLost`, `SettlementOutcome.HostLost`) 후 세션을 종료한다. 늦게 성공한 재접속은 재개하지 않는다.
4. host가 마지막으로 확정한 cargo만 부분 보상 — 마지막으로 적용된 호스트 스냅샷의 확보 화물 가치 × 보험 보존율(실패 규칙과 동일: 무보험 20%/기본 50%/프리미엄 70%, 부이 발사 시 +0.3·최대 0.9; 협동은 무보험). 호스트 확정 서베이·퀘스트 연구 데이터는 100% 유지(docs/05 §3), 계약 기본급·샤드 없음. 호스트가 확인하지 않은 로컬 예측 인양은 0. 스냅샷을 한 번도 못 받았으면 0.
5. 각 클라이언트 local save에는 동일 settlement id를 1회만 적용 — 초안 캐시 + 원장 멱등. 통계상 완료/실패 런 어느 쪽에도 집계하지 않는다.

목표는 세션 복원보다 **진행 손상 방지**다.

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
자동화된 검증:
- `tests/Net` `CoopSession` 스위트 — 포즈/이벤트/스냅샷 코덱, 핸드셰이크 토큰, JIP·최종 단계·재접속 입장 규칙, 좌석 보관/만료, 토큰 갱신, 채널별 재생 창, relay 스푸핑 차단, host-link 창.
- `tests/Domain` `CoopShips` 스위트 — 보간/외삽/텔레포트, 보정 블렌드, 포즈 게이트, 개인/팀 탈출 초안, host-loss 정산.
- 실제 ENet 2-프로세스 스모크(수동 실행, 각각 `--mode=host` / `--mode=client`, 같은 `--workdir`):
  - `res://tests/Net/network_smoke.tscn` — 세션 계층(핸드셰이크·로비·매니페스트·런 중 토큰 재접속).
  - `res://tests/Net/coop_run_smoke.tscn` — 실제 런 씬: 양쪽 함선 프록시 표시·추종, 소나 오버레이, 콜라이더 없음, 강제 링크 끊김 후 8초 창 내 토큰 재접속과 엔티티 rekey, 호스트 종료 후 host-loss 정산 1회 적용.
  - 예: `Godot_v4.7.2-stable_mono_win64_console.exe --headless --path . res://tests/Net/coop_run_smoke.tscn -- --mode=host --workdir=<dir>` (클라이언트도 동일하게 `--mode=client`).

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
