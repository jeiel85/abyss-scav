# Changelog

All notable changes to AbyssScav will be documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - 2026-09-17

### Added
- **Core Engine & Simulation**:
  - 6DOF submarine dynamics, ballast, power, flood, breach repair, and station docking mechanics.
  - Active sonar pinging and passive acoustic contact classification.
  - Procedural trench graph, facility grammar, and POI generation with deterministic seeds.
  - Sub-trench creature AI perception (hearing, sight, lure) and alert state machine.
  - Modular contract progression, salvage extraction, research skill tree, and codex screen.
- **Multiplayer Architecture**:
  - ENet host-authoritative listen server networking (LAN discovery & Direct-IP join).
  - Snapshot replication, catalog hash verification, reconnect grace period, and host-loss settlement protection.
- **Accessibility & Diagnostics**:
  - Colorblind-friendly sonar palettes, high-contrast HUD modes, and key remapping.
  - In-engine diagnostics, crash recovery, and 3-generation atomic save rollback.
  - **Localization**: full Korean (ko) UI support — menus, cockpit HUD, tutorial, sonar callouts, and dive runtime messages; language selection via settings; missing-key fallback to English with CI coverage validation.
- **Verification & Automation**:
  - Complete managed test suites across Domain, Foundation, Net, and Persistence layers.
  - Headless in-engine smoke verification and deterministic autopilot playback simulation.
  - GitHub Actions automated validation workflow and release packaging scripts.
  - i18n key coverage, format placeholder integrity, and version consistency gates in the unified test runner.
