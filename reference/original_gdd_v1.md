# [GDD] 바이럴 심해 잠수정 협동 호러 분석 및 유물 인양 로그라이크 기획·설계서

- **문서 버전**: v1.0.0
- **작성 일자**: 2026-09-17
- **프로젝트 코드명**: AbyssScav: Submarine Salvage & Sonar (어비스 스캐브: 심해 인양반)
- **장르**: 1인칭 심해 음파탐지 잠수정 조타 & 미지의 잔해 인양 로그라이트 (First-Person Sonar Submarine Salvage Roguelite)
- **타깃 플랫폼**: PC (Steam)

---

## 1. 벤치마크 바이럴 게임 심층 분석

### 1.1 분석 대상
- **대표 레퍼런스**: 《Murky Divers (머키 다이버스)》, 《Barotrauma》, 《Lethal Company》
- **장르 특성**: 1~4인 협동 잠수정 운항, 블라인드 음파탐지(Sonar) 항법, 버려진 심해 연구소 잔해 인양 및 심해 공포(Thalassophobia)

### 1.2 바이럴 핵심 요인 (Viral Drivers)
1. **눈먼 잠수함의 비명과 혼돈의 소통 (Frantic Co-op Shouting)**:
   - 전방 시야가 0인 암흑 속에서 한 명은 소나 스크린의 점(Blip)만 보며 방향을 고함치고, 조타수는 계기판만 보며 조종하는 절체절명의 협동 상황극이 틱톡/유튜브에서 폭발적 바이럴 형성.
2. **청각적 서스펜스와 심해 거대 괴수 공포**:
   - 핑(Ping) 소리가 벽에 부딪쳐 울리는 반향음, 잠수정 외벽을 긁고 지나가는 거대 크라켄의 소름 끼치는 울음소리가 극도의 긴장감 유발.

### 1.3 나쁜 요소 및 기존 게임의 결함 (Pain Points)
1. **솔로 플레이의 원천적 불가능 (Unplayable Solo)**:
   - 한 명이 조타기를 잡고 다른 한 명이 소나를 봐야만 운항이 가능한 구조라, 1인 플레이 시 잠수정 운전석과 소나실을 정신없이 뛰어다니다 침몰하는 치명적 한계.
2. **반복적인 인양 목표와 얕은 메타 프로그래션**:
   - 연구소 쓰레기를 주워 파는 행위가 3~4판 후 지루해지며, 획득한 자금으로 잠수함을 커스텀 튜닝하는 심도 있는 빌드 요소 부재.
3. **심각한 지형 충돌 버그**:
   - 잠수정이 해저 협곡 텍스처 사이에 끼어 회수 불가능 상태가 되는 물리 엔진 결함.

---

## 2. +α 혁신 프로젝트: 《AbyssScav: Submarine Salvage & Sonar》

> **"시야는 0m, 수압은 500기압. 소나의 핑 하나에 의지해 심해 유적의 잔해를 인양하고 탈출하세요."**

### 2.1 핵심 차별화 시스템 (+α Innovations)

| 혁신 시스템 | 상세 설명 및 해결 과제 |
| :--- | :--- |
| **+α 1. [솔로 친화형 AI 음파 항법 버디 (Sonar HUD Sync)]** | 1인 플레이 지원: 잠수정 조타석 앞 유리에 음파 반향 신호가 홀로그램으로 증강 투사되는 '스마트 소나 헤드업 디스플레이' 탑재. 혼자서도 완벽한 조타와 탐색 가능. |
| **+α 2. [모듈형 잠수정 현장 용접 (Modular Sub Upgrade)]** | 인양한 외계 고대 유물 부품을 잠수정 외벽에 현장 용접(Welding)하여 즉시 성능 확장: 고압 EMP 방출기, 음향 유인 미끼(Decoy), 암석 분쇄 드릴 암 등. |
| **+α 3. [음향 스텔스 & 수압 밸브 퍼즐]** | 엔진을 풀가동하면 소음으로 인해 심해 포식자가 돌진. 엔진을 끄고 무소음 관성 항행을 하거나, 수압 게이지를 분산 조절하는 전술적 밸브 미니 퍼즐. |
| **+α 4. [절차적 심해 해구 & 심해 보스 레이드]** | 매 런마다 무작위로 생성되는 심해 참호, 난파선, 외계 연구 시설과 구역별 보스 생명체 회피 미션. |

---

## 3. 코어 게임플레이 루프

```
[인양 계약 수락: 심해 해구 투하] ──► (수심 3,000m 강하)
       │
       ▼
[블라인드 소나 항법 & 협곡 탐색] ──► (소음 제어: 크리처 어그로 관리)
       │
       ├─► [연구소 도킹 & 선체 진입] ──► 희귀 유물 및 코어 회수
       │
       ├─► [현장 잠수정 부품 개조] ──► 인양한 파츠로 잠수정 스펙 업
       │
       ▼
[급격한 수압 상승 / 괴수 추격] ──► (EMP 방출 및 감압 부표 전개)
       │
       ▼
[모선 귀환 & 유물 암시장 경매] ──► 영구 잠수정 슬롯 해금
```

---

## 4. 게임 상태 머신 (FSM) 상세 설계

```
+-------------------------------------------------------------+
|                      SUB_STATE_SURFACE                      |
|           (Contract Briefing, Gear Selection, Drop Pod)     |
+-------------------------------------------------------------+
                              │
                              ▼
+-------------------------------------------------------------+
|                      SUB_STATE_NAVIGATE                     | <--------------------+
|  - Passive/Active Sonar Pulse Engine                        |                      |
|  - Noise Level Meter (Engine RPM + Cavitation)              |                      |
|  - Hull Integrity & Water Pressure Balance                 |                      |
+-------------------------------------------------------------+                      |
                              │                                                      |
                  [Creature Aggro Triggered!]                                        |
                              │                                                      |
                              ▼                                                      |
+-------------------------------------------------------------+                      |
|                     SUB_STATE_EVASION                       |                      |
|  - Choice: Silent Drift (Silent Running) or Fire Decoy Flare|                      |
|  - Hull Breached? -> Quick Time Event (Weld Pipe Leaks)     |                      |
+-------------------------------------------------------------+                      |
                              │                                                      |
                              ▼                                                      |
                 (Reach Docking Facility) ───────────────────────────────────────────+
                              │
                              ▼
+-------------------------------------------------------------+
|                     SUB_STATE_EXTRACTION                    |
|       (Surface Ascent, Decompression Chamber, Settlement)   |
+-------------------------------------------------------------+
```

---

## 5. 핵심 기술 아키텍처 및 데이터 모델

### 5.1 음파탐지(Sonar) 렌더러 아키텍처
- **엔진**: **Unity (HDRP) or Godot 4.3 3D**
- **소나 셰이더**: 화면 공간 거리 버퍼(Depth Buffer)를 샘플링하여, 핑(Ping)이 확산될 때마다 지형과 오브젝트의 외곽선(Wireframe)을 0.5초간 녹색 형광으로 밝히는 포스트 프로세싱 셰이더.

### 5.2 핵심 데이터 모델 (C#)

```csharp
public enum SubmarineModuleType { SonarArray, EngineThruster, HullArmor, UtilityArm }

[System.Serializable]
public class SubmarineStatus {
    public float currentDepthMeters;
    public float hullPressurePercent; // 0.0f ~ 100.0f
    public float noiseEmissionDb;
    public bool isSilentRunningMode;
    public List<SubmarineModuleType> installedModules;
}

[System.Serializable]
public class SonarContactBlip {
    public string contactId;
    public Vector3 worldPosition;
    public float signalIntensity;
    public bool isHostileLeviathan;
    public float timeSinceLastPing;
}
```

---

## 6. 6주 완성 MVP 로드맵

- **Week 1~2: 음파탐지 포스트프로세싱 셰이더 & 잠수정 3D 물리 제어**
  - 핑 확산 와이어프레임 셰이더 및 부력/추진력 잠수정 조타 프로토타입.
- **Week 3~4: 솔로 소나 HUD & 소음 감지 크리처 AI**
  - 조타석 윈드실드 증강 소나 UI 및 소음 반응형 심해 괴수 FSM 구축.
- **Week 5: 연구소 도킹 & 잠수정 모듈 장착 시스템**
  - 선체 수리 QTE 미니게임 및 잠수정 부품 용접/강화 시스템.
- **Week 6: 사운드 믹싱(ASMR 수중 음향) & 스팀 데모 빌드 패키징**
