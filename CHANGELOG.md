# Changelog

All notable changes to AbyssScav will be documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- **Accessibility (actually implemented)**: the 0.1.0 notes listed these, but earlier builds did not ship them; they now exist and are player-usable from Settings.
  - Colour-vision sonar palettes: Standard, Red–green safe (deuteranopia/protanopia, Okabe–Ito based), Blue–yellow safe (tritanopia). Every contact kind also has its own glyph (salvage diamond, biological triangle, structure square, unknown "?", terrain dash) and a legend under the scope. Foundation tests verify contact-colour separation under Machado 2009 dichromacy simulation and ≥3:1 contrast against the scope.
  - High-contrast HUD: opaque panels, white outlined text, 2 px borders, larger outlined sonar glyphs (run HUD, sonar scope, tutorial checklist).
  - Keyboard remapping: press-to-rebind for all 32 movement / sonar-tool / consumable actions with swap-on-conflict, reset to defaults, and persistence. HUD controls line, instrument key tags, tutorial checklist, warnings, buddy callouts and contract descriptions show the bound keys (layout-aware letter labels).
- Settings schema v3 (`sonar_palette`, `high_contrast_hud`, `key_bindings`). v1/v2 files load with the new fields at defaults; bindings are whitelisted, completed and de-duplicated on load, and a malformed `key_bindings` section no longer marks the whole file as corrupt. A settings file without a `graphics` section no longer crashes boot.

### Changed
- Heave down no longer also answers to `C` (it was an undocumented second key); `Ctrl` stays the default and any key can be bound. Esc remains fixed to pause and doubles as the rebind-cancel key; gamepad buttons are unchanged.
- The settings panel is now a centred two-column layout (general + accessibility | key bindings); the language change applies only after a successful save.

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
