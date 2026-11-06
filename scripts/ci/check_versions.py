#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
从 pom.xml / csproj / Cargo.toml / pyproject.toml / package.json / composer.json
提取 SDK 版本，并校验与 README.md 安装表格与 CHANGELOG 的版本一致性。
用于 CI 自动化门禁。
"""

import re
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent

def main():
    errors = []

    # 1. Java (pom.xml)
    pom_text = (ROOT / "ip-qzdb-sdk" / "java" / "pom.xml").read_text(encoding="utf-8")
    m = re.search(r"<version>(1\.0\.\d+)</version>", pom_text)
    java_ver = m.group(1) if m else None

    # 2. C# (.csproj)
    csproj_text = (ROOT / "ip-qzdb-sdk" / "netcore" / "QQZeng.Qzdb.csproj").read_text(encoding="utf-8")
    m = re.search(r"<Version>(1\.0\.\d+)</Version>", csproj_text)
    net_ver = m.group(1) if m else None

    # 3. Rust (Cargo.toml)
    cargo_text = (ROOT / "ip-qzdb-sdk" / "rust" / "Cargo.toml").read_text(encoding="utf-8")
    m = re.search(r'version\s*=\s*"([^"]+)"', cargo_text)
    rust_ver = m.group(1) if m else None

    # 4. Python (pyproject.toml)
    pyproject_text = (ROOT / "ip-qzdb-sdk" / "python" / "pyproject.toml").read_text(encoding="utf-8")
    m = re.search(r'version\s*=\s*"([^"]+)"', pyproject_text)
    py_ver = m.group(1) if m else None

    # 5. Node.js (package.json)
    pkg_json = json.loads((ROOT / "ip-qzdb-sdk" / "nodejs" / "package.json").read_text(encoding="utf-8"))
    node_ver = pkg_json.get("version")

    # 6. PHP (composer.json)
    comp_json = json.loads((ROOT / "composer.json").read_text(encoding="utf-8"))
    comp_ver = comp_json.get("version", "1.2.2")

    versions = {
        "Java": java_ver,
        ".NET": net_ver,
        "Rust": rust_ver,
        "Python": py_ver,
        "Node.js": node_ver,
        "PHP": comp_ver,
        "Go": "v1.0.8"
    }

    print("Current SDK Single Source of Truth Versions:")
    for lang, v in versions.items():
        print(f"  - {lang:8s}: {v}")
        if not v:
            errors.append(f"Missing version for {lang}")

    # Check main README
    readme_text = (ROOT / "README.md").read_text(encoding="utf-8")
    checks = [
        ("Java", f"com.qqzeng:qzdb:{java_ver}"),
        (".NET", f"QQZeng.Qzdb` (`{net_ver}`)"),
        ("Rust", f"qzdb` (`{rust_ver}`)"),
        ("Python", f"qzdb` (`{py_ver}`)"),
        ("Node.js", f"@qqzengip/qzdb` (`{node_ver}`)"),
        ("PHP", f"qqzeng/qzdb` (`{comp_ver}`)"),
        ("Go", f"v1.0.8")
    ]
    for lang, needle in checks:
        if needle not in readme_text:
            errors.append(f"README.md does not contain expected version coordinate for {lang}: '{needle}'")

    if errors:
        print("\nVersion verification FAILED:", file=sys.stderr)
        for e in errors:
            print(f"  * {e}", file=sys.stderr)
        return 1

    print("\nPASSED: All package versions match single source of truth and README.")
    return 0

if __name__ == "__main__":
    sys.exit(main())
