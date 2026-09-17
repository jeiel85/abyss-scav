# 04. Procedural Generation

## 1. 목표
절차 생성은 “완전 랜덤”이 아니라 **검증 가능한 조각 조합**이다.

## 2. Seed
- run_seed: 64-bit
- host만 생성
- 모든 generation 단계는 deterministic PRNG stream 분리
  - terrain
  - POI
  - facility
  - loot
  - creature
  - event

동일 seed + content catalog version이면 동일 layout을 생성해야 한다.

## 3. 월드 생성 단계
1. biome config 로드
2. spline 기반 main trench route 생성
3. navigation clearance 검사
4. branch route 2~5개 배치
5. POI socket 배치
6. facility/shipwreck/ore prefab 배치
7. safe extraction corridor 확보
8. navmesh/AI navigation bake/runtime region 연결
9. loot/event spawn table 적용
10. validation pass

## 4. 안전 제약
- 잠수정 최소 통과 폭 = hull bounding diameter * 1.8
- dead-end branch 길이 제한
- critical POI는 extraction에서 graph distance 3 이상
- primary objective로 가는 경로 최소 1개 보장
- 강제 좁은 구간은 한 런 최대 2개

## 5. Facility generation
Room graph grammar:
- ENTRY
- HUB
- CORRIDOR
- UTILITY
- LAB
- STORAGE
- HAZARD
- OBJECTIVE
- EXIT/LOOP

규칙:
- objective까지 최소 4 rooms
- dead-end reward room 1~3
- objective room 앞 hazard 확률 60%
- critical route에 key-lock chain은 최대 1단

## 6. Validation pass
생성 후 자동 검증:
- objective reachable
- extraction reachable
- submarine route clearance
- facility nav connectivity
- required interactable count
- overlapping collider count == 0
- spawn inside geometry == 0

실패 시 seed를 버리지 않고 해당 단계의 sub-seed만 최대 3회 재생성. 3회 실패하면 safe fallback layout 사용.

## 7. 디버그 도구
- seed 입력/재생성
- POI graph overlay
- clearance volume 표시
- creature spawn heatmap
- loot heatmap
- unreachable node detector
- one-click validation report export

## 8. 콘텐츠 제작 규격
Room prefab은 다음 marker를 제공:
- DoorSocket[]
- LootSocket[]
- HazardSocket[]
- AIAnchor[]
- ObjectiveSocket[]
- AudioZone
- OcclusionProxy

## 9. 반복 피로 방지
한 런의 체감 다양성은 geometry보다 조합에서 확보:
- biome modifier
- contract objective
- threat profile
- weather/current
- relic trait
- apex presence
- facility condition

같은 facility prefab이 연속 2런에서 동일 위치/동일 objective로 등장하지 않도록 history bias 적용.
