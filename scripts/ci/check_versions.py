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

    # 7. Matrix SSOT verification: go.mod vs pyproject.toml vs CI matrix
    go_mod_text = (ROOT / "ip-qzdb-sdk" / "go" / "go.mod").read_text(encoding="utf-8")
    if "go 1.21" not in go_mod_text:
        errors.append("ip-qzdb-sdk/go/go.mod must declare 'go 1.21' as minimum Go baseline")

    if "requires-python = \">=3.10\"" not in pyproject_text:
        errors.append("ip-qzdb-sdk/python/pyproject.toml must declare 'requires-python = \">=3.10\"'")

    ci_path = ROOT / ".github" / "workflows" / "ci.yml"
    if not ci_path.exists():
        errors.append(f"CI workflow missing at: {ci_path}")
    else:
        # 结构化解析 YAML matrix.include 配置，去除注释与虚假 token 匹配
        entries = []
        current_entry = None
        in_include = False
        include_indent = None
        for raw_line in ci_path.read_text(encoding="utf-8").splitlines():
            line = raw_line.split("#", 1)[0].rstrip()
            stripped = line.strip()
            if not stripped:
                continue
            indent = len(raw_line) - len(raw_line.lstrip())
            if stripped == "include:":
                in_include = True
                include_indent = indent
                continue
            if in_include:
                if indent <= include_indent and not stripped.startswith("-"):
                    break
                if stripped.startswith("- "):
                    if current_entry:
                        entries.append(current_entry)
                    current_entry = {}
                    item = stripped[2:].strip()
                    if ":" in item:
                        k, v = item.split(":", 1)
                        current_entry[k.strip()] = v.strip().strip("\"'")
                elif ":" in stripped and current_entry is not None:
                    k, v = stripped.split(":", 1)
                    current_entry[k.strip()] = v.strip().strip("\"'")
        if current_entry:
            entries.append(current_entry)

        if not entries:
            errors.append(f"CI workflow ({ci_path.name}) missing or unparseable matrix.include block")
        else:
            if len(entries) < 6:
                errors.append(f"CI workflow ({ci_path.name}) expected >= 6 matrix combinations, got {len(entries)}")
            os_set = {e.get("os") for e in entries}
            py_set = {e.get("python") for e in entries}
            node_set = {e.get("node") for e in entries}
            go_set = {e.get("go") for e in entries}

            required_dims = [
                ("os", os_set, {"ubuntu-latest", "macos-latest"}),
                ("python", py_set, {"3.10", "3.13"}),
                ("node", node_set, {"20", "22"}),
                ("go", go_set, {"1.21", "stable"}),
            ]
            for dim, found, required in required_dims:
                missing = required - found
                if missing:
                    errors.append(f"CI matrix ({ci_path.name}) missing required {dim} targets: {missing}")

    if errors:
        print("\nVersion verification FAILED:", file=sys.stderr)
        for e in errors:
            print(f"  * {e}", file=sys.stderr)
        return 1

    print("\nPASSED: All package versions match single source of truth and README.")
    return 0

if __name__ == "__main__":
    sys.exit(main())
