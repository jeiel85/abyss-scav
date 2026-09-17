# 15. Implementation Backlog — GitHub Track

## EPIC A — Project Foundation
### A-01 Bootstrap
- boot scene
- dependency registry
- config loader
- log writer
- safe-mode
Acceptance: clean Windows PC에서 network 없이 main menu 진입.

### A-02 Data Catalog
Acceptance: duplicate/unknown ID가 CI에서 실패.

### A-03 Save Core
- atomic write
- 3 backups
- migration
Acceptance: force-close/truncate/old-version fuzz 통과.

## EPIC B — Submarine
### B-01 6DOF Controller
### B-02 Collision Safety
### B-03 Power/Pressure/Flood
Acceptance: automated tunnel corpus에서 unrecoverable stuck 0.

## EPIC C — Sonar
### C-01 Active Pulse
### C-02 Passive Contacts
### C-03 Classification Confidence
### C-04 Accessibility palettes/glyphs
Acceptance: 신규 tester가 3분 내 terrain/biological/salvage 구분.

## EPIC D — World Generation
### D-01 Trench Graph
### D-02 POI Placement
### D-03 Facility Grammar
### D-04 Validation/Fallback
### D-05 Seed Debugger
Acceptance: 10,000 seeds에서 primary objective unreachable 0.

## EPIC E — Creatures
### E-01 Perception framework
### E-02 AI FSM
### E-03 Base creature roster
### E-04 Advanced creatures
### E-05 Apex framework
Acceptance: AI state host-only, client는 visualization replication.

## EPIC F — Salvage / Inventory
### F-01 Interactable
### F-02 Cargo rules
### F-03 Relic traits
### F-04 Field module welding
Acceptance: duplicate pickup/negative inventory 불가.

## EPIC G — Contracts / Progression
### G-01 Contract runtime
### G-02 Settlement
### G-03 Insurance
### G-04 Research tree
### G-05 8 contract archetypes
Acceptance: success/failure/host-loss 정산 idempotent.

## EPIC H — Multiplayer
### H-01 `INetworkTransport` + ENet
### H-02 Host/create session
### H-03 Direct-IP join
### H-04 LAN discovery
### H-05 Protocol/catalog handshake
### H-06 Snapshot replication
### H-07 Reliable domain events
### H-08 Join in progress
### H-09 Reconnect token/grace
### H-10 Host-loss settlement
### H-11 UPnP port mapping helper
Acceptance: latency/loss matrix + 4-player soak 통과.

## EPIC I — UI / UX
### I-01 Main menu
### I-02 Host/Join browser
### I-03 Lobby
### I-04 Loadout
### I-05 Cockpit HUD
### I-06 Sonar station
### I-07 Engineering UI
### I-08 Settlement/research
### I-09 Settings/accessibility
### I-10 Network troubleshooting
Acceptance: keyboard/controller 모두 주요 flow 완료.

## EPIC J — GitHub Public Build
### J-01 Git repository conventions
### J-02 CI build/test
### J-03 Windows export
### J-04 ZIP packager
### J-05 SHA-256 manifest
### J-06 tag/version validation
### J-07 GitHub Release template
### J-08 bug/support bundle flow
### J-09 third-party license registry
Acceptance: tag build가 clean Windows PC에서 압축 해제 후 실행되고 README_FIRST 및 license files 포함.
