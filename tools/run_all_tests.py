#!/usr/bin/env python3
"""
AbyssScav Unified Test Runner
Runs managed test suites, design package validation, and headless in-engine tests.
"""
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def run_command(cmd, desc, cwd=ROOT):
    print(f"\n[RUN] {desc}...")
    print(f"      Command: {' '.join(cmd)}")
    res = subprocess.run(cmd, cwd=cwd)
    if res.returncode != 0:
        print(f"[FAIL] {desc} failed with returncode {res.returncode}")
        return False
    print(f"[PASS] {desc}")
    return True

def find_godot_mono():
    # Check common environment variables or local paths
    candidates = [
        os.environ.get("GODOT4_MONO_BIN"),
        r"D:\Users\jeiel\Temp\opencode\godot-mono-4.7.2\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe",
    ]
    for c in candidates:
        if c and Path(c).is_file():
            return c
    return None

def main():
    failures = 0

    # 1. Build solution
    if not run_command(["dotnet", "build", "AbyssScav.sln", "--configuration", "Release"], "Build AbyssScav.sln"):
        sys.exit(1)

    # 2. Managed test suites
    test_projects = [
        ("tests/Domain/AbyssScav.Domain.Tests.csproj", "Domain Test Suite"),
        ("tests/Foundation/AbyssScav.Foundation.Tests.csproj", "Foundation Test Suite"),
        ("tests/Net/AbyssScav.Net.Tests.csproj", "Net Test Suite"),
        ("tests/Persistence/AbyssScav.Persistence.Tests.csproj", "Persistence Test Suite"),
    ]

    for proj, name in test_projects:
        if not run_command(["dotnet", "run", "--project", proj, "-c", "Release", "--no-build"], name):
            failures += 1

    # 3. Design package validation
    if not run_command([sys.executable, "tools/validate_design_package.py"], "Design Package Validation"):
        failures += 1

    # 3.5 i18n coverage validation (ko.json keys + format placeholder integrity)
    if not run_command([sys.executable, "tools/validate_i18n_coverage.py"], "i18n Coverage Validation"):
        failures += 1

    # 3.6 Version consistency validation (in-game vs package identity)
    if not run_command([sys.executable, "tools/validate_version_consistency.py"], "Version Consistency Validation"):
        failures += 1

    # 4. In-engine headless test (if Godot Mono is present)
    godot_bin = find_godot_mono()
    if godot_bin:
        print(f"\nFound Godot Mono at: {godot_bin}")
        if not run_command([godot_bin, "--headless", "--run-smoke"], "Godot Headless Smoke Test"):
            failures += 1
    else:
        print("\n[SKIP] Godot Mono console binary not detected in default paths; skipping headless smoke.")

    if failures > 0:
        print(f"\n[SUMMARY] {failures} test suite(s) failed.")
        sys.exit(1)

    print("\n[SUMMARY] ALL SUITES PASSED CLEANLY.")
    sys.exit(0)

if __name__ == "__main__":
    main()
