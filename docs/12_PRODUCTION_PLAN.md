# 12. Production Plan — GitHub Playable Build Track

원본 6주 계획은 기술 MVP에 가깝다. 여기서는 **사람이 GitHub에서 내려받아 반복 플레이할 수 있는 완성도**를 목표로 한다. 상점 심사/마케팅/achievement/cloud/store asset은 범위에서 제외한다.

소규모 인디 팀 기준 7~10개월, 1인 개발은 10~16개월 수준으로 재산정하는 것이 안전하다. 이는 일정 보장이 아니라 범위 추정치다.

## M0 — Preproduction / Risk Burn-down (4주)
- project skeleton
- coding/data conventions
- sonar readability prototype
- submarine controller
- ENet 2-player spike
- LAN discovery spike
- art/audio style test

Gate:
- sonar로 공간 판독 가능
- submarine stuck recovery 동작
- 2-player authoritative sync 30분 유지

## M1 — Vertical Slice (8주)
- biome 1
- contract 1
- creature 2
- room 6+
- module 6
- full extraction loop
- solo + LAN/direct-IP 2-player

Gate: placeholder/production mix로 30분 run 완주.

## M2 — Systems Complete (10주)
- 4-player
- pressure/flood/power
- save/progression
- join/reconnect
- host-loss settlement
- procedural validation/fallback
- UI/accessibility baseline
- UPnP helper

Gate: 모든 핵심 시스템 feature complete.

## M3 — Content Alpha (10주)
- biome 3
- creature 8 + apex framework
- contract archetype 8
- module 24
- relic 30+
- facility room 35+

Gate: feature lock. 이후 새 시스템 추가 금지.

## M4 — Public Beta Readiness (6~8주)
- onboarding
- balance
- performance
- save migration rehearsal
- LAN/WAN network matrix
- Windows clean-machine test
- GitHub Actions/release packaging

Gate: P0=0, known P1은 public stable 이전 전부 해결.

## M5 — GitHub Stable Candidate (4~6주)
- content lock
- 4-player soak
- seed corpus
- compatibility
- third-party licenses
- README/network troubleshooting
- stable save schema

Gate: `checklists/RELEASE_GATE.md` 전부 통과.

## 이후 — Maintenance
- issue triage
- hotfix
- compatibility regressions
- balance/content patch

## Feature Definition of Done
- spec/acceptance 연결
- implementation complete
- network authority 명시
- save impact 명시
- keyboard/controller flow
- accessibility 영향 확인
- automated test where practical
- profiler budget 확인
- P0/P1 없음
- docs/changelog 업데이트

## P0 범위
- core loop
- sonar
- submarine control/stuck safety
- generation validation
- local save safety
- solo
- 1~4 player LAN/direct IP
- extraction/settlement

## P1 범위
- progression
- full content target
- accessibility
- reconnect
- UPnP helper
- GitHub CI/release packaging

## P2 범위
- cosmetics polish
- advanced codex viewer
- replay/debug viewer
- optional extra apex variants

## 핵심 원칙
네트워크를 마지막에 붙이지 않는다. M0/M1부터 authority 구조로 개발한다.
