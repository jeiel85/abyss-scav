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
- host snapshot: 15 Hz baseline
- transform buffer: 100~150 ms
- local interaction feedback는 즉시 표시 가능하지만 결과는 host ack 후 확정
- submarine은 host transform을 기준으로 보정
- large correction은 0.3~0.8초 blend, teleport threshold 초과 시 snap

## 10. Join-in-progress
허용 조건:
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
- peer마다 session-scoped reconnect token 생성
- 기본 grace: 120초
- reconnect 성공 시 기존 player entity takeover
- token은 run 종료 시 폐기
- IP address를 identity로 사용하지 않는다.

## 12. Host loss
v2.1은 full host migration을 제공하지 않는다.

정책:
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
