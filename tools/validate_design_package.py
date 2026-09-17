from pathlib import Path
import json, sys, re
root=Path(__file__).resolve().parents[1]
required=[
 'README.md','docs/00_MASTER_GDD.md','docs/01_TECHNICAL_ARCHITECTURE.md',
 'docs/02_MULTIPLAYER_NETWORKING.md','docs/10_GITHUB_DISTRIBUTION.md',
 'docs/14_VALIDATION_REPORT.md','docs/15_IMPLEMENTATION_BACKLOG.md',
 'docs/16_CODE_CONTRACTS.md','checklists/DEVELOPMENT_READINESS.md',
 'checklists/RELEASE_GATE.md'
]
errors=[]
for rel in required:
    if not (root/rel).exists(): errors.append(f'missing: {rel}')
if (root/'docs/10_STEAM_RELEASE_OPERATIONS.md').exists():
    errors.append('stale Steam operations document remains')
for p in (root/'schemas').glob('*.json'):
    try: json.loads(p.read_text(encoding='utf-8'))
    except Exception as e: errors.append(f'json: {p.name}: {e}')
# Operational docs must not depend on the old platform integration. Meta/history docs are excluded.
meta={Path('README.md'),Path('docs/14_VALIDATION_REPORT.md'),Path('reference/OFFICIAL_REFERENCES.md'),Path('reference/original_gdd_v1.md')}
for p in root.rglob('*'):
    if not p.is_file() or p.suffix.lower() not in {'.md','.txt','.json','.csv'}: continue
    rel=p.relative_to(root)
    if rel in meta or str(rel).startswith('tools/'): continue
    txt=p.read_text(encoding='utf-8',errors='ignore')
    for term in ['SteamTransport.cs','SteamPlatformService.cs','Steam Networking Sockets','Steam Datagram Relay','Steam test AppID','launch from Steam','STEAM-']:
        if term in txt: errors.append(f'stale platform dependency {term!r}: {rel}')
# Core GitHub networking terms should exist.
network=(root/'docs/02_MULTIPLAYER_NETWORKING.md').read_text(encoding='utf-8')
for term in ['ENetMultiplayerPeer','LAN','Direct IP','UPnP','CGNAT']:
    if term not in network: errors.append(f'network spec missing: {term}')
if errors:
    print('\n'.join(errors)); sys.exit(1)
print('PASS')
