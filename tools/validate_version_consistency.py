#!/usr/bin/env python3
"""
AbyssScav Version Consistency Validation

RELEASE_GATE requires "tag = in-game version". This gate keeps every
version source aligned so a tagged build cannot ship a binary that
reports a different version than its artifact name.

Sources checked:
  - src/Foundation/GameVersion.cs        (in-game identity, source of truth)
  - src/Net/NetworkSmoke.cs              (protocol smoke identity)
  - tools/package_windows.py VERSION     (artifact naming / local packaging)
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def extract_version(path: Path, pattern: str, label: str):
    text = path.read_text(encoding="utf-8")
    m = re.search(pattern, text)
    if not m:
        print(f"FATAL: cannot find version in {path.relative_to(ROOT)} ({label})")
        sys.exit(1)
    return m.group(1)


def main():
    game_version = extract_version(
        ROOT / "src" / "Foundation" / "GameVersion.cs",
        r'Current = "([^"]+)"',
        "GameVersion.Current",
    )
    net_version = extract_version(
        ROOT / "src" / "Net" / "NetworkSmoke.cs",
        r'GameVersion = "([^"]+)"',
        "NetworkSmoke.GameVersion",
    )
    package_version = extract_version(
        ROOT / "tools" / "package_windows.py",
        r'VERSION = "([^"]+)"',
        "package_windows.VERSION",
    )

    sources = {
        "GameVersion.Current": game_version,
        "NetworkSmoke.GameVersion": net_version,
        "package_windows.VERSION": package_version,
    }

    errors = []
    for label, ver in sources.items():
        if ver != game_version:
            errors.append(f"version mismatch: {label}={ver!r} != GameVersion.Current={game_version!r}")

    print(f"versions: GameVersion.Current={game_version}, NetworkSmoke={net_version}, "
          f"package={package_version}, {len(errors)} problem(s)")
    if errors:
        print("\n".join(errors))
        sys.exit(1)
    print("PASS")


if __name__ == "__main__":
    main()