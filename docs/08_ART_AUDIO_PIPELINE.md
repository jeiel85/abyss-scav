# 08. Art & Audio Pipeline

## 1. 비주얼 방향
Photorealism보다 **저조도 산업 잠수정 + 제한된 소나 발광 + 읽기 쉬운 실루엣**.

## 2. 컬러 철학
- ambient: 거의 무채색 blue/black
- interactable: 제한된 warm accent
- sonar: 설정 가능한 발광 계열
- critical alert: color + shape + motion 동시 사용

## 3. 환경 에셋 규격
- modular 2m grid
- texel density 통일
- collision mesh 별도
- LOD 0/1/2
- occluder proxy 제공
- materials: trim sheet 우선

## 4. 잠수정 내부
- cockpit
- sonar desk
- engineering bay
- cargo bay
- airlock

핵심 스테이션 사이 이동은 5초 이내.

## 5. 외부 환경
시야가 제한적이므로 silhouette와 sonar response가 중요.
- large landmark 1~2 per sector
- mid landmark 3~5
- clutter는 collision 없는 decorative 비중 증가

## 6. VFX
- sonar ring
- suspended particles
- cavitation bubbles
- hull spark
- leak jets
- pressure dust
- creature bioluminescence

파티클 overdraw 예산을 low/medium/high로 분리.

## 7. 오디오가 핵심 시스템
Bus:
- Master
- UI
- Voice
- Interior
- Exterior
- Creature
- Sonar
- Music

## 8. 소리 우선순위
1. hull breach/critical
2. creature attack tell
3. teammate voice
4. sonar result
5. machinery
6. ambience/music

## 9. Dynamic mix
ThreatLevel에 따라 음악 볼륨보다:
- low-frequency rumble
- hull creak density
- distant biological calls
- silence gap
을 변화시킨다.

## 10. 에셋 제작 전략
공개 배포 시에는 모든 에셋을 독창적으로 제작하거나 상업 라이선스가 명확한 에셋만 사용.
Third-party asset마다:
- source
- license
- receipt
- modification notes
를 `asset_registry.csv`로 보관.

## 11. 그래픽 에셋 우선순위
P0:
- submarine interior/exterior
- sonar material/shader
- 3 biome kit
- 8 creature silhouettes
- 35 room prefabs
- core VFX/UI glyphs
P1:
- cosmetics
- additional relic meshes
- narrative props
