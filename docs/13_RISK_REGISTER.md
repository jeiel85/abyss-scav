# 13. Risk Register — GitHub Track

| Risk | Prob. | Impact | Mitigation | Gate |
|---|---:|---:|---|---|
| Sonar는 멋있지만 공간 인식이 어려움 | M | H | usability test, glyph/history | VS |
| 잠수정 지형 끼임 | H | H | clearance + stuck recovery | Alpha |
| 4인 desync | M | H | host authority from M0, soak | Beta |
| 절차 생성 objective unreachable | M | H | deterministic validation/fallback | Alpha |
| 콘텐츠 반복 | H | M | modifiers/prefab variety/history bias | Beta |
| solo 난이도 과도 | M | H | AI Buddy + solo tuning | Beta |
| save corruption | L | H | atomic save + backups + fuzz | Stable |
| 인터넷 Direct-IP가 NAT/CGNAT로 실패 | H | M | UPnP + 명확한 troubleshooting + optional VPN | Beta |
| host disconnect frustration | M | M | reconnect + safe partial settlement | Beta |
| GitHub Release에 잘못된 빌드 업로드 | M | M | CI hash/version verification | Stable |
| 외부 에셋 라이선스 누락 | M | H | asset registry + third-party notice gate | All |
| 개발 범위 과대 | H | H | feature lock + P2 cut list | M2 |
| 아트 생산량 부족 | H | M | modular kits/trim sheet | M1 |
| 성능 저하 | M | H | profiler budget from VS | All |

## Cut List
일정 초과 시 아래 순서로 제거한다.
1. advanced cosmetics
2. codex 3D viewer
3. optional EVA free-swim
4. apex #3 variation
5. complex facility puzzle variants

절대 자르지 않는다.
- solo path
- base 4-player stability
- save safety
- sonar readability
- generation validation
- network limitation UX
