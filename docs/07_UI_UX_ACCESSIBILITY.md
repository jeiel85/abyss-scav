# 07. UI / UX / Accessibility

## 1. HUD
중앙 시야는 최대한 비운다.
- left: hull / pressure
- right: power / noise
- bottom: tool/action
- sonar contact는 world-space + compact compass에 동시 표시

## 2. 조타석
중요 계기 5개만 항상 노출:
1. depth
2. hull
3. pressure margin
4. noise
5. power

## 3. 소나 표현
색만으로 분류하지 않는다.
- terrain: line pattern
- structure: square glyph
- biological: pulse glyph
- salvage: diamond
- unknown: question-like abstract marker

## 4. 메뉴
- Continue
- Solo Dive
- Online Co-op
- Loadout
- Research
- Codex
- Settings
- Quit

## 5. Lobby UX
- invite friends
- privacy: friends/invite-only
- region/relay status
- each player loadout
- ready state
- host badge
- contract preview

## 6. 접근성
필수:
- full key rebinding
- controller remapping where platform allows
- hold/toggle 선택
- subtitle size/background
- colorblind-safe sonar palettes
- flash reduction
- camera shake 0..100
- head bob 0..100
- FOV 70..110
- motion blur off default
- tinnitus/high-frequency reduction option
- text-to-speech는 v1.1 검토

## 7. 공포 접근성
- spider-like imagery 없음 기준이 아니라 사용자에게 특정 생물 숨김은 범위 밖
- sudden scare intensity slider: audiovisual spike만 완화, gameplay timing은 유지

## 8. Onboarding
첫 5분:
- 단일 목표
- 하나의 계기씩 활성화
- 실패 가능한 튜토리얼이지만 즉사 금지

## 9. 오류 메시지
기술 오류 코드 + 사람 친화 설명.
예: `NET-LOBBY-004: 호스트와 연결이 끊어졌습니다. 확보된 화물 상태를 복구했습니다.`
