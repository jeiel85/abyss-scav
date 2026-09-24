# Changelog

All notable changes to AbyssScav will be documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- **Co-op ship replication** (protocol v2): clients send their own ship pose at 15 Hz; the host validates each pose (finite values, speed cap, world bounds, reachable displacement, one winch teleport per player) and republishes every player's pose inside the world snapshot. Teammates render as collider-less proxy submarines with name tags (120 ms interpolation buffer, bounded extrapolation, large-correction blend, teleport snap) and as ring markers on the sonar scope.
- **Per-player co-op extraction**: any player can extract once the shared objectives are complete (clients are host-authorized with a reliable verdict); a host extraction is a team extraction. Each player settles their own draft exactly once through the settlement ledger.
- **Host-loss settlement**: a lost host (disconnect or 10 s silence) opens an 8 s reconnect window; if it expires, clients settle only the last host-confirmed secured cargo at the insurance retention (host-confirmed survey research kept, counted as neither completed nor failed).
- **Reconnect takeover**: the session token now rides in the handshake, mid-run seats are held for the 120 s grace (measured from when the peer was last heard), and the reconnecting player takes over its seat and ship entity and receives the manifest + full world snapshot. Clients reconnect automatically inside the host-loss window.
- **Join-in-progress**: the host can open a running dive to late joiners from the lobby (off by default); admission closes at the extraction final sequence (shared objectives complete).
- Two-process in-engine co-op run smoke (`tests/Net/coop_run_smoke.tscn`) and managed `CoopSession` / `CoopShips` suites.

### Fixed
- Run manifests could not be encoded with real `sha256:` layout/catalog hashes (71 bytes over a 64-byte cap), so a co-op run could never start.
- `GodotNetworkSession` emitted its signals under snake_case names that do not exist for C# signals, so lobby/run/snapshot/intent signals never reached subscribers.
- Reliable frames could be dropped as replays behind newer unreliable frames from another ENet channel; replay windows are now per channel.
- Clients now ignore host-only messages (snapshots, events, manifests, lobby, tokens) that did not come from the host.
- A client-side ENet server loss is now always reported (once) as host loss.

### Changed
- Network protocol version 1 → 2 (handshake, snapshot, lobby admission bits, manifest hash caps, new `ShipPose` message); v1 and v2 builds cannot join each other.

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
