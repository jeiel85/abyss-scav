#!/usr/bin/env python3
"""
AbyssScav Windows Portable Packaging Script
Creates standalone release ZIP and SHA256SUMS.txt matching docs/10 Section 6.
"""
import hashlib
import os
import shutil
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
VERSION = "0.1.0-a01"

def sha256_file(filepath: Path) -> str:
    h = hashlib.sha256()
    with open(filepath, "rb") as f:
        while chunk := f.read(65536):
            h.update(chunk)
    return h.hexdigest()

def package_release(version: str = VERSION):
    build_dir = ROOT / "build" / "windows"
    dist_dir = ROOT / "dist"
    stage_name = f"AbyssScav-v{version}-win-x64"
    stage_dir = dist_dir / stage_name
    zip_path = dist_dir / f"{stage_name}.zip"
    sha_path = dist_dir / "SHA256SUMS.txt"

    print(f"=== AbyssScav v{version} Windows Release Packaging ===")

    if not (build_dir / "AbyssScav.exe").is_file():
        print(f"[ERROR] Missing {build_dir / 'AbyssScav.exe'}. Please run export first.", file=sys.stderr)
        return False

    # Clean dist / staging
    if stage_dir.exists():
        shutil.rmtree(stage_dir)
    stage_dir.mkdir(parents=True, exist_ok=True)

    # 1. Copy binary and pck
    shutil.copy2(build_dir / "AbyssScav.exe", stage_dir / "AbyssScav.exe")
    if (build_dir / "AbyssScav.pck").is_file():
        shutil.copy2(build_dir / "AbyssScav.pck", stage_dir / "AbyssScav.pck")

    # Copy data folder if present (contains .NET mono assemblies)
    data_dir = build_dir / "data_AbyssScav_windows_x86_64"
    if data_dir.is_dir():
        shutil.copytree(data_dir, stage_dir / "data_AbyssScav_windows_x86_64")

    # 2. Copy documentation & licenses
    docs_to_copy = [
        "README_FIRST.txt",
        "THIRD_PARTY_NOTICES.md",
        "CHANGELOG.md",
        "LICENSE",
    ]
    for doc in docs_to_copy:
        src = ROOT / doc
        if src.is_file():
            shutil.copy2(src, stage_dir / doc)

    licenses_src = ROOT / "LICENSES"
    if licenses_src.is_dir():
        shutil.copytree(licenses_src, stage_dir / "LICENSES")

    # 3. Create portable ZIP
    print(f"Creating portable archive: {zip_path.name}...")
    if zip_path.exists():
        zip_path.unlink()

    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
        for file_path in stage_dir.rglob("*"):
            if file_path.is_file():
                arcname = file_path.relative_to(stage_dir)
                zf.write(file_path, arcname)

    # 4. Generate SHA256SUMS.txt
    zip_hash = sha256_file(zip_path)
    sha_content = f"{zip_hash}  {zip_path.name}\n"
    sha_path.write_text(sha_content, encoding="utf-8")

    print("\n[SUCCESS] Package completed successfully.")
    print(f"  ZIP: {zip_path} ({zip_path.stat().st_size:,} bytes)")
    print(f"  SHA256: {zip_hash}")
    print(f"  Manifest: {sha_path}")
    return True

if __name__ == "__main__":
    ver = sys.argv[1] if len(sys.argv) > 1 else VERSION
    if not package_release(ver):
        sys.exit(1)
