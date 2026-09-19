# AbyssScav

Dark industrial submarine salvage — pilot a deep-sea submersible, hunt contracts in the abyss, and get back alive.

**Version:** 0.1.0-a01 · **Engine:** Godot 4.7.2 stable (.NET / C#) · **Target:** Windows 10/11

## Current State

This is a **solo-playable vertical slice** with a full host-authoritative networking layer underneath.

### Playable now
- **Solo dive loop**: contract & loadout selection → procedural trench generation → piloting, sonar, salvage, repair, docking, drilling, extraction → settlement (credits / research data / shards).
- **Tutorial**: "The First Ping" — 7 guided steps (steer, passive sonar, active ping, dock, salvage, repair, extract).
- **Content**: 3 biomes, 8 contract archetypes, 3 submarine frames, 24 modules (22 with implemented in-run effects), 8 consumables, 9 major random events, 4 difficulties, 11 creatures (8 + 3 apex), 8 relic traits, 8 contract modifiers.
- **Meta progression**: Research screen (4 branches; only modules with real effects are purchasable) and Codex (survey records: creatures, waters, relic traits).
- **Settings**: resolution, window mode, graphics quality, master volume, language (English / 한국어).
- **Save system**: local profile with 3-generation atomic rollback and an idempotent settlement ledger.
- **Diagnostics**: logs folder, support-bundle export, headless smoke tests, deterministic autopilot playback.

### Backend only (not reachable from the UI yet)
- **Multiplayer**: ENet host-authoritative listen server, LAN discovery, Direct-IP join, UPnP port mapping, reconnect grace, host-loss settlement protection — implemented and covered by tests, but **no lobby UI exists**, so the shipped build is solo-only.

### Not implemented
- Accessibility features (colorblind sonar palettes, high-contrast HUD, key remapping) — **not present**; do not expect them in this build.
- 2 of 24 modules have no in-run effect yet and are not purchasable/equippable (heat sink, vector fin).

## Quick Start

### Run from source (development)
```bat
run_game.bat
```
Requires Godot 4.7.2 stable Mono. The batch file points at a local Godot binary — edit `GODOT_BIN` if yours lives elsewhere.

### Build a release ZIP
```bat
python tools/package_windows.py
```
Produces `dist/AbyssScav-v0.1.0-a01-win-x64.zip` plus a SHA-256 manifest. Extract to a normal folder and run `AbyssScav.exe` (never run inside the archive).

### Run all tests
```bat
python tools/run_all_tests.py
```
Runs the managed test suites (Domain / Foundation / Net / Persistence), design-package validation, headless in-engine smoke + playback tests, i18n key-coverage, and version-consistency gates.

## How to Play

Full player guide (Korean): **[docs/17_PLAYER_GUIDE.md](docs/17_PLAYER_GUIDE.md)**

Short version:
1. Start with **Tutorial: The First Ping** from the main menu.
2. **Solo Dive — Contract & Loadout**: pick waters (biome), contract, frame, difficulty, seed, modifiers, modules, and optional consumables.
3. In the dive: **ping (F)** to reveal objectives/loot/creatures, close in, **salvage (E)**, complete the contract objectives shown top-center, then return to the start point and **extract (T)**. Use **1–8** for consumables and **B** to fire the emergency buoy. Watch for major random events (warning banner).
4. Settlement pays credits + research data + shards. Spend research data on module blueprints, then equip them (one slot per category) on the next dive.

### Controls

| Action | Key |
|---|---|
| Surge (forward/back) | W / S |
| Sway (left/right) | A / D |
| Heave (up/down) | Space / Ctrl |
| Yaw / pitch | Arrow keys |
| Boost | Shift |
| Quiet running | Z |
| Active sonar ping | F |
| Salvage | E |
| Survey contact | V |
| Service contract node | G |
| Repair hull | R |
| Dock / undock | J |
| Drill (hold) | H |
| Emergency winch | X |
| Consumable 1–8 | 1–8 |
| Fire emergency buoy | B |
| Extract | T |
| Pause | Esc |

## Content

| Category | Entries |
|---|---|
| Biomes | Continental Shelf Graveyard (1,500–3,000 m) · Black Trench (3,000–6,000 m) · Hadal Ruins (6,000–9,000 m) |
| Contracts | SalvageQuota · BlackBoxRecovery · FacilityCoreExtraction · BioSampleHunt · BeaconRepair · SurveyScan · RescuePodRecovery · ApexObservation |
| Frames | Skiff (agile) · Mule (heavy cargo) · Warden (armored) |
| Modules with effects | WhisperPulse · PassiveBooster · WideArray · FocusBeam · ResonanceClassifier · GhostFilter · QuietProp · OverdriveThruster · CavitationDampener · EmergencyReverse · ReinforcedRib · PressureSkin · AbyssPlating · FloodBulkhead · SelfSealingFoam · ShockBuffer · SalvageMagnet · EmergencyBuoy · DrillArm · RepairDrone |
| Consumables | Sealant Canister · Battery Pack · Hull Patch · Acoustic Decoy · Pressure Flare · Sonar Buoy · Stim · Antifreeze |
| Major events | Acoustic Disturbance · Facility Alarm · Anomaly · Current Shift · Migration · Collapsing Trench · False Distress Beacon · Relic Resonance · Extraction Ambush |
| Difficulties | Casual Dive · Standard · Blackwater · Custom |
| Creatures | 8 regular + 3 apex (Needle Eel, Bell Maw, Glass Ray, Silt Stalker, Lampreech, Chorus Colony, Hull Grazer, Warden Crab; The Long Choir, Pale Leviathan, Trench Mother) |

## Project Structure

```
src/            App · Domain · Foundation · Gameplay · Infra · Net · Persistence · Platform · Presentation
tests/          Managed suites: Domain · Foundation · Net · Persistence
tools/          package_windows.py · run_all_tests.py · validation gates
scenes/         Godot scenes (boot, main_menu, contract_select, run, research, codex, …)
docs/           Design & implementation docs (00–17)
checklists/     Release gate & development readiness
templates/      GitHub issue/release templates
```

## Documentation

- `docs/00_MASTER_GDD.md` — game design
- `docs/01_TECHNICAL_ARCHITECTURE.md` — engine/code/runtime architecture
- `docs/02_MULTIPLAYER_NETWORKING.md` — LAN/Direct-IP co-op, authority, RPC, reconnect
- `docs/03_GAMEPLAY_SYSTEMS.md` — submersible/sonar/pressure/noise/AI/salvage
- `docs/04_PROCEDURAL_GENERATION.md` — trench/facility/POI generation
- `docs/05_PROGRESSION_ECONOMY.md` — contracts/rewards/meta progression
- `docs/06_CONTENT_BIBLE.md` — public-build content targets
- `docs/07_UI_UX_ACCESSIBILITY.md` — HUD/menus/input/accessibility
- `docs/08_ART_AUDIO_PIPELINE.md` — graphics/sound/asset pipeline
- `docs/09_SAVE_CONFIG_TELEMETRY.md` — save/config/logs/crash recovery
- `docs/10_GITHUB_DISTRIBUTION.md` — repository/Actions/Releases
- `docs/11_QA_PERFORMANCE_SECURITY.md` — QA, performance budget, security
- `docs/12_PRODUCTION_PLAN.md` — milestones/work breakdown/DoD
- `docs/13_RISK_REGISTER.md` — product/technical/content risks
- `docs/14_VALIDATION_REPORT.md` — validation results
- `docs/15_IMPLEMENTATION_BACKLOG.md` — epics/acceptance criteria
- `docs/16_CODE_CONTRACTS.md` — core interfaces/error contracts
- `docs/17_PLAYER_GUIDE.md` — how to play (한국어)

## Save Data

Local profile, logs, and settings live under `%APPDATA%\Godot\app_userdata\AbyssScav\`. Saves use atomic writes with 3-generation rollback; a corrupted profile is recovered from backup or continued unsaved.

## License

See [LICENSE](LICENSE). Source and assets are published for playtesting, code review, and non-commercial community evaluation. Redistribution, commercial exploitation, or re-licensing without explicit written permission is prohibited.