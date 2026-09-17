# 11. QA, Performance, Security — GitHub Public Build

## 1. 버그 등급
- P0: crash loop, save loss, progression corruption, remote code/file exploit
- P1: run blocker, major desync, objective impossible, connection flow broken
- P2: 기능 저하/명백한 UX 문제
- P3: cosmetic/minor

Public stable build gate: **P0=0, P1=0**.

## 2. 테스트 층
- unit: formula/data/authority validator
- integration: save/network/scene flow
- simulation: seeded generation corpus
- soak: 90~180 min multiplayer
- manual exploratory
- clean-machine compatibility

## 3. 자동 테스트 필수
- definition ID unique
- unknown ID rejection
- objective resolvable
- generation reachability 10,000 seeds
- save migration roundtrip
- inventory non-negative invariant
- duplicate settlement id idempotency
- invalid-distance RPC rejection
- protocol/catalog mismatch rejection

## 4. 성능 목표
1080p 기준 목표:
- 60 FPS target
- 99% frame <= 25 ms
- physics <= 4 ms typical
- AI <= 3 ms typical
- sonar post <= 2.5 ms medium/high
- RAM working set <= 8 GB target
- VRAM <= 6 GB target

이 값은 profiler 측정으로 확정하며 문서상의 수치는 acceptance budget이다.

## 5. Network QA
4인 90분 soak 30회 목표:
- hard desync 0
- objective divergence 0
- save/progression corruption 0

추가:
- LAN
- WAN direct-IP
- UPnP success/failure
- host crash/quit
- reconnect
- 50~180 ms latency
- 1~5% loss

## 6. Security
중앙 계정 서버는 없지만 remote peer input은 불신한다.
- packet size bounds
- RPC whitelist
- enum/id catalog validation
- interaction distance validation
- string length limits
- rate limits
- path traversal 금지
- network input으로 file path 생성 금지
- support bundle이 save/private path를 포함하지 않는지 테스트

## 7. Save fuzz
- truncated file
- invalid JSON/binary
- missing field
- future schema
- duplicate settlement
- interrupted write
- permission denied

## 8. Accessibility QA
- keyboard-only
- controller-only
- 200% UI scale
- colorblind palette/glyph
- subtitles large
- camera shake 0
- flashing reduction

## 9. Compatibility
필수:
- Windows 10/11 x64
- NVIDIA/AMD/Intel GPU smoke coverage
- 16:9 / 16:10 / 21:9
- clean machine portable ZIP execution

Linux/Steam Deck/native macOS는 v2.1 보장 범위 밖이다.

## 10. GitHub artifact QA
Public asset은:
- ZIP integrity test
- SHA-256 생성
- executable boot smoke
- version string = tag
- `README_FIRST.txt` 존재
- third-party license bundle 존재
