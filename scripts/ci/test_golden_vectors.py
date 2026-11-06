#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
执行基于公开 demo 样本库的 27 组 Golden Vectors 跨语言一致性验证。
期望值源自 demo/qqzeng-ip-ult.csv 独立数据源，覆盖 IPv4/IPv6、各类 usage_type、有无 district 及边界负例。
用于 CI 自动化门禁。
"""

import os
import sys
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
DEMO_QZDB = ROOT / "demo" / "qqzeng-ip-ult.qzdb"
GOLDEN_JSON = ROOT / "golden_vectors.json"
PY_SDK = ROOT / "ip-qzdb-sdk" / "python"

sys.path.insert(0, str(PY_SDK))
from qzdb import QzdbReader, QzdbError

def main():
    if not DEMO_QZDB.exists():
        print(f"Error: Demo database not found: {DEMO_QZDB}", file=sys.stderr)
        return 1
    if not GOLDEN_JSON.exists():
        print(f"Error: Golden vectors JSON not found: {GOLDEN_JSON}", file=sys.stderr)
        return 1

    with open(GOLDEN_JSON, "r", encoding="utf-8") as f:
        data = json.load(f)

    vectors = data.get("vectors", [])
    if not vectors:
        print("Error: No vectors found in golden_vectors.json", file=sys.stderr)
        return 1

    reader = QzdbReader(str(DEMO_QZDB))
    failed = 0

    print(f"Running {len(vectors)} Golden Vectors against demo database...")
    for idx, v in enumerate(vectors, 1):
        desc = v.get("desc", "")
        ip = v["ip"]
        exp = v["expected"]

        actual = None
        try:
            actual = reader.find(ip)
        except QzdbError:
            actual = None

        if exp.get("hit", True):
            if actual is None:
                print(f"[{idx}] FAIL: {ip} ({desc}) -> Expected hit, got None", file=sys.stderr)
                failed += 1
                continue

            mismatches = []
            for field, exp_val in exp.items():
                if field == "hit":
                    continue
                act_val = getattr(actual, field, None)
                if act_val != exp_val:
                    mismatches.append(f"{field}: expected={exp_val!r}, actual={act_val!r}")

            if mismatches:
                print(f"[{idx}] FAIL: {ip} ({desc}) -> Mismatch: {'; '.join(mismatches)}", file=sys.stderr)
                failed += 1
            else:
                print(f"[{idx}] OK: {ip} ({desc}) -> {actual.country} {actual.city} ({actual.isp})")
        else:
            if actual is not None:
                print(f"[{idx}] FAIL: {ip} ({desc}) -> Expected miss, got {actual}", file=sys.stderr)
                failed += 1
            else:
                print(f"[{idx}] OK: {ip} ({desc}) -> not hit (as expected)")

    if failed > 0:
        print(f"\nFAILED: {failed}/{len(vectors)} vectors failed verification.", file=sys.stderr)
        return 1

    print(f"\nPASSED: All {len(vectors)} golden vectors verified successfully against demo database.")
    return 0

if __name__ == "__main__":
    sys.exit(main())
