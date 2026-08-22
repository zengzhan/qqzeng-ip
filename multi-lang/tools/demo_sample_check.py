#!/usr/bin/env python3
"""Demo-sample cross-language oracle.

Verifies SDKs against the PUBLIC demo dataset (demo/qqzeng-ip-ult.qzdb +
qqzeng-ip-ult.csv; 360 ranges = 220 IPv4 + 140 IPv6, ult edition). The sample
is published upstream at zengzhan/qqzeng-ip, so CI can exercise real data
without proprietary files or secrets.

Layers:
  anchor - Python SDK output must equal the CSV ground truth. The expected
           pipe string is reconstructed positionally from the .qzdb's own
           field-name metadata, filling values BY COLUMN NAME from the CSV.
  parity - enabled language adapters must reproduce the Python find_str
           output line-by-line on the same sampled IPs.

Exit codes: 0 = pass, 1 = mismatch, 2 = setup error. Prints SAMPLE_ORACLE_OK
or SAMPLE_ORACLE_FAIL for CI gating.
"""
import argparse
import csv
import ipaddress
import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
MULTI_LANG = os.path.dirname(HERE)
REPO_ROOT = os.path.dirname(MULTI_LANG)
sys.path.insert(0, os.path.join(MULTI_LANG, "python"))

DEFAULT_DB = os.path.join(REPO_ROOT, "demo", "qqzeng-ip-ult.qzdb")
DEFAULT_CSV = os.path.join(REPO_ROOT, "demo", "qqzeng-ip-ult.csv")
META_COLS = {"family", "start_ip", "end_ip", "cidr"}
MAX_REPORT = 20


def load_csv(path):
    """Return (data_columns, rows). Header row and '#' comments are skipped."""
    with open(path, newline="", encoding="utf-8") as f:
        reader = csv.reader(f)
        header = None
        rows = []
        for row in reader:
            if not row or not row[0].strip() or row[0].lstrip().startswith("#"):
                continue
            if header is None:
                header = [h.strip() for h in row]
                continue
            rows.append({h: (row[i].strip() if i < len(row) else "")
                         for i, h in enumerate(header)})
    return [c for c in header if c and c not in META_COLS], rows


def values_equal(a, b):
    a = (a or "").strip()
    b = (b or "").strip()
    if a == b:
        return True
    try:
        fa, fb = float(a), float(b)
        return abs(fa - fb) <= max(1e-6, abs(fb) * 1e-9)
    except ValueError:
        return False


def build_expected(names, row):
    """Reconstruct the ult pipe string in the db's own field order."""
    return "|".join(row.get(n, "") for n in names)


def field_diff(actual_pipe, expected_pipe, names):
    """Per-field comparison with numeric tolerance; returns list of diffs."""
    a_seg = actual_pipe.split("|")
    e_seg = expected_pipe.split("|")
    diffs = []
    for i, name in enumerate(names):
        av = a_seg[i] if i < len(a_seg) else ""
        ev = e_seg[i] if i < len(e_seg) else ""
        if not values_equal(av, ev):
            diffs.append((name, ev, av))
    return diffs


def run_adapter(cmd, db_path, ips):
    """Feed IPs via stdin; return stdout lines. Raises on non-zero exit."""
    p = subprocess.run(cmd + [db_path], input="\n".join(ips) + "\n",
                       capture_output=True, text=True, timeout=120)
    if p.returncode != 0:
        raise RuntimeError(f"{' '.join(cmd)} exited {p.returncode}: {p.stderr[:300]}")
    out = p.stdout.splitlines()
    if len(out) != len(ips):
        raise RuntimeError(f"{' '.join(cmd)} returned {len(out)} lines, expected {len(ips)}")
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--db", default=DEFAULT_DB)
    ap.add_argument("--csv", default=DEFAULT_CSV)
    ap.add_argument("--parity", default="nodejs,php",
                    help="comma list from: nodejs,php")
    args = ap.parse_args()

    for path in (args.db, args.csv):
        if not os.path.isfile(path):
            print(f"SAMPLE_ORACLE_SETUP_ERROR: file not found: {path}", file=sys.stderr)
            return 2
    try:
        from qzdb import QzdbReader
    except ImportError as exc:
        print(f"SAMPLE_ORACLE_SETUP_ERROR: python SDK import failed: {exc}", file=sys.stderr)
        return 2

    columns, rows = load_csv(args.csv)
    if not rows:
        print("SAMPLE_ORACLE_SETUP_ERROR: no data rows in csv", file=sys.stderr)
        return 2

    reader = QzdbReader(args.db)
    names = reader.get_field_names()
    missing_cols = [n for n in names if n not in columns]
    checked_cols = [n for n in names if n in columns]
    print(f"=== Demo sample oracle: {len(rows)} ranges, "
          f"{2 * len(rows)} probe IPs, {len(checked_cols)}/{len(names)} fields anchored ===")
    if missing_cols:
        print(f"  note: db fields without a CSV column (filled as empty): {missing_cols}")

    failures = []

    # ---- anchor layer: python vs authoritative CSV ----
    baseline = {}
    ips = []
    for r in rows:
        net = ipaddress.ip_network(r["cidr"], strict=False)
        for ip in (str(net.network_address), str(net.broadcast_address)):
            ips.append(ip)
            expected = build_expected(names, r)
            actual = reader.find_str(ip)
            baseline[ip] = actual
            if not values_equal(actual, expected):
                # Pipe-string inequality may be pure number formatting
                # (e.g. "-47.211900" vs "-47.2119"); the authoritative
                # decision is the tolerant per-field comparison.
                diffs = field_diff(actual, expected, names)
                if diffs:
                    failures.append(("anchor", ip, r["cidr"], diffs))

    # ---- parity layer: adapters must match python baseline ----
    adapter_cmds = {
        "nodejs": ["node", os.path.join(MULTI_LANG, "nodejs", "batch_cli.js")],
        "php": ["php", os.path.join(MULTI_LANG, "php", "batch_cli.php")],
    }
    enabled = []
    for name in [s.strip() for s in args.parity.split(",") if s.strip()]:
        cmd = adapter_cmds.get(name)
        if cmd is None:
            print(f"  notice: unknown parity adapter '{name}' ignored")
            continue
        if shutil.which(cmd[0]) is None:
            print(f"  notice: parity '{name}' skipped ({cmd[0]} not installed)")
            continue
        enabled.append((name, cmd))
    for name, cmd in enabled:
        try:
            out = run_adapter(cmd, args.db, ips)
        except Exception as exc:
            failures.append((f"parity:{name}", "-", "-", [(str(exc), "", "")]))
            continue
        for ip, got in zip(ips, out):
            want = baseline[ip]
            if not values_equal(got, want):
                diffs = field_diff(got, want, names)
                failures.append((f"parity:{name}", ip, "-", diffs))

    # ---- report ----
    for kind, ip, cidr, diffs in failures[:MAX_REPORT]:
        head = f"  [FAIL] {kind} {ip} {cidr}"
        if len(diffs) == 1 and not diffs[0][1] and not diffs[0][2]:
            print(f"{head} {diffs[0][0]}")
        else:
            print(head)
            for fname, ev, av in diffs[:8]:
                print(f"         {fname}: expected={ev!r} got={av!r}")
    if len(failures) > MAX_REPORT:
        print(f"  ... and {len(failures) - MAX_REPORT} more failures")

    total_checks = len(ips) * (1 + len(enabled))
    print(f"\nDemo oracle: {total_checks - len(failures)}/{total_checks} checks passed "
          f"(anchor={len(ips)}, parity={'+'.join(n for n, _ in enabled) or 'none'})")
    if failures:
        print("SAMPLE_ORACLE_FAIL")
        return 1
    print("SAMPLE_ORACLE_OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
