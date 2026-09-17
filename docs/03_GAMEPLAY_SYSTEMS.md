# 03. Gameplay Systems Specification

## 1. Submarine
### 상태
- Hull Integrity: 0..1000
- Pressure Margin: 0..100
- Power: 0..100
- Noise: 0..100 normalized
- Flooding Zones: 4 compartments
- Heat: engine/sonar/drill subsystem별

### 입력
- throttle -1..1
- yaw -1..1
- vertical -1..1
- boost
- silent running
- active sonar

### 전력 예산
기본 발전량 100 PU.
- engines 10~45
- active sonar burst 20 for 2s
- life support 15
- lights 5
- pumps 5~30
- drill 35
- utility 5~25

발전량 초과 시 자동 차단 순서: cosmetic lights -> utility -> drill -> sonar. life support와 pumps는 우선 보호.

## 2. Pressure
`EffectivePressure = DepthPressure * BiomeModifier * EventModifier`
`PressureMargin = HullRating - EffectivePressure`

Margin이 0 아래이면:
- creak event 증가
- breach probability 증가
- 지속 damage가 아니라 “stress check” 이벤트로 처리하여 예측 가능성과 긴장 유지

## 3. Noise
Noise source:
- engine RPM
- cavitation
- active sonar
- collision
- drill
- repair hammer/welding
- creature lure

크리처는 `hearing_radius = base * noise_curve(noise)`로 탐지.

Silent running:
- engine max 25%
- active sonar disabled
- interior lights dim
- passive sonar sensitivity +20%

## 4. Sonar
### Passive
- 위험 없음
- 거리/방향 정확도 낮음
- 큰 소음원만 탐지

### Active
- 360° pulse
- 장애물/POI/생명체 반사
- signal class: terrain / structure / salvage / biological / unknown
- 사용 시 threat 증가

### Contact confidence
0~1.
- 첫 ping 0.3
- 연속 반사 +0.2
- 같은 contact 두 방향 triangulation +0.25
- 가까운 거리 +0.15

## 5. Creature AI
FSM:
`Dormant -> Investigate -> Stalk -> Hunt -> Attack -> Disengage`

Perception:
- sound dominant
- sonar exposure
- line-of-sight는 근거리 보조

Apex는 즉시 사살 목적보다 “경로 차단/추격/공포 연출” 비중을 높인다.

## 6. Hull breach
- impact 또는 pressure stress로 생성
- breach slot은 미리 정의된 위치 pool에서 선택
- severity 1..3
- severity에 따라 flooding rate

Repair loop:
1. 해당 compartment 이동
2. valve close 또는 pump route
3. weld interaction
4. consumable sealant 사용 가능

QTE 실패 즉사 금지. 실패는 시간/자원 손실로 귀결.

## 7. Salvage
유형:
- Loose Scrap
- Heavy Crate
- Fragile Relic
- Bio Sample
- Cursed Artifact

인양 방식:
- manipulator arm
- docking airlock
- drill extraction
- EVA tether(선택 콘텐츠)

## 8. Inventory
- 개인: 4 small slots
- sub cargo: weight + volume 기반 12~20 slots
- quest cargo: 별도 protected slot
- relic에는 `value`, `risk`, `mass`, `trait`가 있다.

## 9. Field welding modules
런 중 임시 부착 가능.
- installation time 8~20s
- noise spike
- 일부는 hull hardpoint 필요
- 제거 시 50% 자원 회수

## 10. Contract system
Archetype:
1. Salvage quota
2. Black box recovery
3. Facility core extraction
4. Bio sample hunt
5. Beacon repair
6. Survey scan
7. Rescue pod recovery
8. Apex observation/escape

계약에는 primary + optional secondary + hidden complication을 둘 수 있다.

## 11. Threat events
- sonar ghost contacts
- current surge
- pressure spike
- power brownout
- hostile migration
- facility alarm
- collapsing trench
- false distress beacon
- relic resonance
- extraction ambush

## 12. Death / revive
- HP 0 -> incapacitated 60s
- teammate stabilize 5s
- medkit revive 12s
- solo: 1회 emergency auto-injector 기본 지급(난이도별 조정)
- 전원 incapacitated이면 run failure
