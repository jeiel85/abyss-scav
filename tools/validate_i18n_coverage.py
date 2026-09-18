#!/usr/bin/env python3
"""
AbyssScav i18n Coverage Validation

Checks that every Localization.T() key used in source exists in ko.json and
that format placeholders ({0}, {1:F0}, ...) match between each key and its
Korean value. A missing key silently falls back to English at runtime, so
this gate keeps the Korean UI from regressing unnoticed.

Static keys: Localization.T("...") literals across src/**/*.cs.
Dynamic keys: player-facing message strings in RunSimulation.cs (Raise
events, result reasons, CheckObjective notes, FailRun reasons). Technical
diagnostics (CONTENT-xxx / GEN-xxx) are intentionally excluded.
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
KO_PATH = ROOT / "src" / "Foundation" / "lang" / "ko.json"


def load_ko():
    try:
        return json.loads(KO_PATH.read_text(encoding="utf-8"))
    except Exception as e:  # noqa: BLE001 - gate must fail loudly
        print(f"FATAL: ko.json unreadable: {e}")
        sys.exit(1)


def extract_literal_keys(src):
    """Localization.T("...") literal keys, decoding \\n / \\t escapes."""
    keys = set()
    for m in re.finditer(r'Localization\.T\("((?:[^"\\]|\\.)*)"', src):
        keys.add(m.group(1).replace("\\n", "\n").replace("\\t", "\t"))
    return keys


def extract_dynamic_keys(src):
    """Player-facing message strings from RunSimulation.cs."""
    keys = set()
    for m in re.finditer(r'Raise\("[^"]+", "((?:[^"\\]|\\.)*)"', src):
        keys.add(m.group(1))
    for m in re.finditer(r'new \w+Result\((?:false|true), "((?:[^"\\]|\\.)*)"', src):
        keys.add(m.group(1))
    for m in re.finditer(r'CheckObjective\("((?:[^"\\]|\\.)*)"', src):
        keys.add(m.group(1))
    for m in re.finditer(r'FailRun\("((?:[^"\\]|\\.)*)"', src):
        keys.add(m.group(1))
    return keys


def placeholder_specs(s):
    """Set of (index, format-specifier) tuples, e.g. {('0', ':F0')}."""
    return set(re.findall(r"\{(\d+)(:[^}]*)?\}", s))


def main():
    ko = load_ko()

    static = set()
    for p in (ROOT / "src").rglob("*.cs"):
        static |= extract_literal_keys(p.read_text(encoding="utf-8"))

    sim_src = (ROOT / "src" / "Domain" / "RunSimulation.cs").read_text(encoding="utf-8")
    dynamic = extract_dynamic_keys(sim_src)

    errors = []
    for k in sorted(k for k in (static | dynamic) if k not in ko):
        errors.append(f"missing ko.json key: {k!r}")
    for k, v in ko.items():
        if placeholder_specs(k) != placeholder_specs(v):
            errors.append(f"placeholder mismatch: key={k!r} value={v!r}")

    print(f"i18n: {len(static)} static keys, {len(dynamic)} dynamic keys, "
          f"{len(ko)} ko.json entries, {len(errors)} problem(s)")
    if errors:
        print("\n".join(errors))
        sys.exit(1)
    print("PASS")


if __name__ == "__main__":
    main()