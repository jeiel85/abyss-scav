# GitHub Public Build Gate Checklist

## Code / Stability
- [ ] P0 = 0
- [ ] P1 = 0
- [ ] save migration tests pass
- [ ] corrupted-save recovery pass
- [ ] clean install/portable run pass
- [ ] solo 45-minute run pass
- [ ] 4-player LAN 90-minute soak pass
- [ ] WAN direct-IP smoke pass
- [ ] host-loss settlement pass

## Generation / Content
- [ ] 10,000 seed reachability corpus pass
- [ ] all contract archetypes completable
- [ ] no blocker room/POI placement regression
- [ ] content IDs/catalog hash valid

## Networking
- [ ] LAN discovery
- [ ] direct-IP join
- [ ] protocol mismatch message
- [ ] catalog mismatch message
- [ ] reconnect grace
- [ ] UPnP success path
- [ ] UPnP failure/manual-guide path
- [ ] CGNAT limitation documented

## UX / Accessibility
- [ ] tutorial complete
- [ ] keyboard-only flow
- [ ] controller-only flow
- [ ] ultrawide smoke
- [ ] subtitle/accessibility options
- [ ] network troubleshooting screen

## GitHub Artifact
- [ ] tag = in-game version
- [ ] Windows export boots
- [ ] ZIP integrity pass
- [ ] SHA256SUMS generated
- [ ] README_FIRST included
- [ ] CHANGELOG updated
- [ ] LICENSE present
- [ ] THIRD_PARTY_NOTICES complete
- [ ] no credentials/secrets in artifact
- [ ] support bundle privacy review pass
