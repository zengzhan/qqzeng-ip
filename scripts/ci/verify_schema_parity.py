#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
校验 demo/README.md 字段列表 == CSV 表头 == SDK schema (25 字段全等)。
用于 CI 自动化门禁。
"""

import re
import csv
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
CSV_PATH = ROOT / "demo" / "qqzeng-ip-ult.csv"
DEMO_README = ROOT / "demo" / "README.md"
MAIN_README = ROOT / "README.md"
DEMO_QZDB = ROOT / "demo" / "qqzeng-ip-ult.qzdb"
PY_SDK = ROOT / "ip-qzdb-sdk" / "python"

sys.path.insert(0, str(PY_SDK))
from qzdb import QzdbReader

def main():
    # 1. CSV 表头
    with open(CSV_PATH, "r", encoding="utf-8") as f:
        for line in f:
            if line.startswith("#"):
                continue
            cols = list(csv.reader([line.strip()]))[0]
            break
    csv_payload = cols[4:]
    if len(csv_payload) != 25:
        print(f"Error: CSV payload has {len(csv_payload)} columns, expected 25", file=sys.stderr)
        return 1

    # 2. demo/README.md
    content = DEMO_README.read_text(encoding="utf-8")
    m = re.search(r"`(continent\|[^`]+)`", content)
    if not m:
        print("Error: Could not find pipe-separated field list in demo/README.md", file=sys.stderr)
        return 1
    demo_fields = m.group(1).strip().split("|")

    # 3. SDK schema
    with QzdbReader(str(DEMO_QZDB)) as r:
        fn = getattr(r, "field_names")
        sdk_fields = list(fn() if callable(fn) else fn)

    # 4. Check equality
    if csv_payload != demo_fields:
        print(f"Error: CSV payload != demo/README.md:\n  CSV:  {csv_payload}\n  DEMO: {demo_fields}", file=sys.stderr)
        return 1

    if csv_payload != sdk_fields:
        print(f"Error: CSV payload != SDK schema:\n  CSV: {csv_payload}\n  SDK: {sdk_fields}", file=sys.stderr)
        return 1

    # 5. Check main README 25 fields in table
    main_readme_text = MAIN_README.read_text(encoding="utf-8")
    table_keys = re.findall(r"\|\s*\d+\s*\|\s*`([a-z0-9_]+)`\s*\|", main_readme_text)
    # The first 25 keys in table
    table_25 = table_keys[:25]
    if len(table_25) == 25:
        # Check set equality
        diff = set(csv_payload) - set(table_25)
        if diff:
            print(f"Warning: Main README table missing fields: {diff}", file=sys.stderr)

    print(f"PASSED: All 25 fields strictly match across CSV header, demo/README.md, and SDK metadata.")
    print("  Field list:", ", ".join(csv_payload))
    return 0

if __name__ == "__main__":
    sys.exit(main())
