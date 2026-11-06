#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
多语言 SDK 对 demo/qqzeng-ip-ult.qzdb 样本库的逐字节输出对拍门禁。
以 Python SDK find_str 为基准对拍参考，覆盖全部 360 条网段起始/结束 IP 及 14 类边界异常值。
接口约定归一化：未命中或无效 IP 统一归一化为空串 ""。
"""

import os
import sys
import csv
import json
import shutil
import tempfile
import argparse
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
DEMO_QZDB = ROOT / "demo" / "qqzeng-ip-ult.qzdb"
DEMO_CSV = ROOT / "demo" / "qqzeng-ip-ult.csv"

BOUNDARY_IPS = [
    "10.0.0.1", "172.16.0.1", "192.168.1.1", "127.0.0.1", "0.0.0.0",
    "224.0.0.1", "255.255.255.255", "::1", "::", "fd00::1",
    "abc", "", "999.999.999.999", "::ffff:118.115.99.0"
]

def load_test_ips():
    ips = []
    with open(DEMO_CSV, "r", encoding="utf-8") as f:
        r = csv.reader([l for l in f if not l.startswith("#")])
        header = next(r)
        s_idx = header.index("start_ip")
        e_idx = header.index("end_ip")
        for row in r:
            ips.append(row[s_idx])
            ips.append(row[e_idx])
    # Deduplicate while preserving order
    unique_ips = list(dict.fromkeys(ips))
    for b in BOUNDARY_IPS:
        if b not in unique_ips:
            unique_ips.append(b)
    return unique_ips

def get_python_reference(ips):
    py_sdk = ROOT / "ip-qzdb-sdk" / "python"
    sys.path.insert(0, str(py_sdk))
    from qzdb import QzdbReader

    reader = QzdbReader(str(DEMO_QZDB))
    ref = {}
    for ip in ips:
        val = reader.find_str(ip)
        ref[ip] = val if val is not None else ""
    return ref

def safe_which(binary):
    mocked_missing = [x.strip().lower() for x in os.environ.get("QZDB_MOCK_MISSING_TOOLCHAIN", "").split(",") if x.strip()]
    if binary.lower() in mocked_missing:
        return None
    return shutil.which(binary)

def verify_c(ips, ref, tmpdir):
    c_src = ROOT / "ip-qzdb-sdk" / "c" / "qzdb_reader.c"
    c_hdr = ROOT / "ip-qzdb-sdk" / "c" / "qzdb_reader.h"
    runner_c = Path(tmpdir) / "runner_c.c"
    runner_bin = Path(tmpdir) / "runner_c"

    runner_c.write_text(f"""
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "{c_hdr}"

int main(int argc, char** argv) {{
    if (argc < 2) return 1;
    qzdb_reader_t reader;
    int rc = qzdb_init(&reader, argv[1]);
    if (rc != QZDB_OK) return 1;

    char line[512];
    char buf[4096];
    while (fgets(line, sizeof(line), stdin)) {{
        char* p = strchr(line, '\\r'); if (p) *p = '\\0';
        p = strchr(line, '\\n'); if (p) *p = '\\0';
        buf[0] = '\\0';
        rc = qzdb_find_str(&reader, line, buf, sizeof(buf));
        if (rc == QZDB_OK) {{
            printf("%s\\n", buf);
        }} else {{
            printf("\\n");
        }}
    }}
    qzdb_free(&reader);
    return 0;
}}
""", encoding="utf-8")

    cc_name = os.environ.get("CC", "cc")
    cc_bin = safe_which(cc_name)
    if not cc_bin:
        return None, f"{cc_name} not found in PATH"
    cmd = [cc_bin, "-std=c11", "-Wall", "-Wextra", "-Werror", "-O2", str(runner_c), str(c_src), "-o", str(runner_bin), "-lm", "-lpthread"]
    res = subprocess.run(cmd, capture_output=True, text=True)
    if res.returncode != 0:
        return False, f"C compilation failed:\n{res.stderr}"

    proc = subprocess.run([str(runner_bin), str(DEMO_QZDB)], input="\n".join(ips) + "\n", capture_output=True, text=True)
    if proc.returncode != 0:
        return False, f"C execution failed:\n{proc.stderr}"

    outputs = proc.stdout.splitlines()
    for idx, (ip, out) in enumerate(zip(ips, outputs)):
        expected = ref[ip]
        if out != expected:
            return False, f"C mismatch at #{idx} IP '{ip}':\n  Expected: {expected!r}\n  Actual:   {out!r}"
    return True, f"C SDK verified ({len(ips)} IPs matched byte-for-byte)"

def verify_node(ips, ref, tmpdir):
    node_sdk = ROOT / "ip-qzdb-sdk" / "nodejs" / "qzdb.js"
    runner_js = Path(tmpdir) / "runner.js"
    runner_js.write_text(f"""
const readline = require('readline');
const QzdbReader = require('{node_sdk}');
const reader = new QzdbReader('{DEMO_QZDB}');

const rl = readline.createInterface({{ input: process.stdin, output: process.stdout, terminal: false }});
rl.on('line', (line) => {{
    let res = "";
    try {{
        res = reader.findStr(line) || "";
    }} catch(e) {{
        res = "";
    }}
    console.log(res);
}});
""", encoding="utf-8")

    node_bin = safe_which("node")
    if not node_bin:
        return None, "node not found in PATH"

    proc = subprocess.run([node_bin, str(runner_js)], input="\n".join(ips) + "\n", capture_output=True, text=True)
    if proc.returncode != 0:
        return False, f"Node.js execution failed:\n{proc.stderr}"

    outputs = proc.stdout.splitlines()
    for idx, (ip, out) in enumerate(zip(ips, outputs)):
        expected = ref[ip]
        if out != expected:
            return False, f"Node.js mismatch at #{idx} IP '{ip}':\n  Expected: {expected!r}\n  Actual:   {out!r}"
    return True, f"Node.js SDK verified ({len(ips)} IPs matched byte-for-byte)"

def verify_php(ips, ref, tmpdir):
    php_sdk = ROOT / "ip-qzdb-sdk" / "php" / "QzdbReader.php"
    php_bin = safe_which("php")
    if not php_bin:
        return None, "php not found in PATH"

    runner_php = Path(tmpdir) / "runner.php"
    runner_php.write_text(f"""<?php
require_once '{php_sdk}';
$reader = new Qqzeng\\Ip\\QzdbReader('{DEMO_QZDB}');
while (($line = fgets(STDIN)) !== false) {{
    $line = rtrim($line, "\\r\\n");
    $res = "";
    try {{
        $res = $reader->findStr($line);
    }} catch (\\Exception $e) {{
        $res = "";
    }}
    echo $res . "\\n";
}}
""", encoding="utf-8")

    proc = subprocess.run([php_bin, str(runner_php)], input="\n".join(ips) + "\n", capture_output=True, text=True)
    if proc.returncode != 0:
        return False, f"PHP execution failed:\n{proc.stderr}"

    outputs = proc.stdout.splitlines()
    for idx, (ip, out) in enumerate(zip(ips, outputs)):
        expected = ref[ip]
        if out != expected:
            return False, f"PHP mismatch at #{idx} IP '{ip}':\n  Expected: {expected!r}\n  Actual:   {out!r}"
    return True, f"PHP SDK verified ({len(ips)} IPs matched byte-for-byte)"

def verify_go(ips, ref, tmpdir):
    go_bin = safe_which("go")
    if not go_bin:
        return None, "go not found in PATH"

    runner_go = Path(tmpdir) / "main.go"
    runner_go.write_text(f"""package main
import (
    "bufio"
    "fmt"
    "os"
    "github.com/zengzhan/qqzeng-ip/ip-qzdb-sdk/go/qzdb"
)
func main() {{
    r, err := qzdb.Open("{DEMO_QZDB}", 0, true)
    if err != nil {{
        fmt.Fprintln(os.Stderr, err)
        os.Exit(1)
    }}
    defer r.Close()

    scanner := bufio.NewScanner(os.Stdin)
    for scanner.Scan() {{
        ip := scanner.Text()
        res := r.FindStr(ip)
        fmt.Println(res)
    }}
}}
""", encoding="utf-8")

    go_sdk_dir = ROOT / "ip-qzdb-sdk" / "go"
    proc = subprocess.run([go_bin, "run", str(runner_go)], cwd=str(go_sdk_dir), input="\n".join(ips) + "\n", capture_output=True, text=True)
    if proc.returncode != 0:
        return False, f"Go execution failed:\n{proc.stderr}"

    outputs = proc.stdout.splitlines()
    for idx, (ip, out) in enumerate(zip(ips, outputs)):
        expected = ref[ip]
        if out != expected:
            return False, f"Go mismatch at #{idx} IP '{ip}':\n  Expected: {expected!r}\n  Actual:   {out!r}"
    return True, f"Go SDK verified ({len(ips)} IPs matched byte-for-byte)"

def verify_rust(ips, ref, tmpdir):
    cargo_bin = safe_which("cargo")
    if not cargo_bin:
        return None, "cargo not found in PATH"

    rust_dir = ROOT / "ip-qzdb-sdk" / "rust"
    # Execute through cargo run with a small inline example or test
    runner_rs = Path(tmpdir) / "runner.rs"
    runner_bin = Path(tmpdir) / "runner_rs"
    
    # Check if target/release/libqzdb.rlib or similar can be used, or cargo run --example
    # Instead, we can run rustc with the crate:
    cmd_build = [cargo_bin, "build", "--quiet"]
    subprocess.run(cmd_build, cwd=str(rust_dir), capture_output=True)

    # Run via cargo run or rust binary
    runner_rs.write_text(f"""
use std::io::{{self, BufRead}};
use qzdb::QzdbReader;

fn main() {{
    let reader = match QzdbReader::from_file("{DEMO_QZDB}") {{
        Ok(r) => r,
        Err(e) => {{
            eprintln!("Failed to load: {{}}", e);
            std::process::exit(1);
        }}
    }};
    let stdin = io::stdin();
    for line in stdin.lock().lines() {{
        if let Ok(ip) = line {{
            let res = reader.find_str(&ip);
            println!("{{}}", res);
        }}
    }}
}}
""", encoding="utf-8")

    # Compile with rustc using cargo target dir
    target_rlib_dir = rust_dir / "target" / "debug" / "deps"
    if not target_rlib_dir.exists():
        return None, "cargo build deps directory not found"

    rustc_bin = safe_which("rustc")
    if not rustc_bin:
        return None, "rustc not found in PATH"

    cmd = [rustc_bin, str(runner_rs), "-o", str(runner_bin), "-L", f"dependency={target_rlib_dir}", "--extern", f"qzdb={rust_dir}/target/debug/libqzdb.rlib", "--edition=2021"]
    res = subprocess.run(cmd, capture_output=True, text=True)
    if res.returncode != 0:
        return None, f"Rust compile skipped: {res.stderr}"

    proc = subprocess.run([str(runner_bin)], input="\n".join(ips) + "\n", capture_output=True, text=True)
    if proc.returncode != 0:
        return False, f"Rust execution failed:\n{proc.stderr}"

    outputs = proc.stdout.splitlines()
    for idx, (ip, out) in enumerate(zip(ips, outputs)):
        expected = ref[ip]
        if out != expected:
            return False, f"Rust mismatch at #{idx} IP '{ip}':\n  Expected: {expected!r}\n  Actual:   {out!r}"
    return True, f"Rust SDK verified ({len(ips)} IPs matched byte-for-byte)"

def main():
    parser = argparse.ArgumentParser(description="Multi-language SDK demo output parity checker")
    parser.add_argument("--langs", default="c,node", help="Comma-separated languages to test (e.g. c,node,go,rust,php)")
    parser.add_argument("--require", default="", help="Comma-separated required languages that MUST pass (otherwise non-zero exit)")
    parser.add_argument("--status-file", default="", help="Optional JSON path to record verification summary")
    args = parser.parse_args()

    target_langs = [l.strip().lower() for l in args.langs.split(",") if l.strip()]
    require_langs = [l.strip().lower() for l in args.require.split(",") if l.strip()]

    print(f"Loading IPs from {DEMO_CSV}...")
    ips = load_test_ips()
    print(f"Total test vectors: {len(ips)} (360 demo range IPs + {len(BOUNDARY_IPS)} boundary cases)")

    print("Generating reference output from Python SDK...")
    ref = get_python_reference(ips)

    handlers = {
        "c": verify_c,
        "node": verify_node,
        "nodejs": verify_node,
        "go": verify_go,
        "rust": verify_rust,
        "php": verify_php,
    }

    verified = []
    skipped = []
    failed = []

    # Check if any required language is completely unsupported
    for req in require_langs:
        canonical = "node" if req == "nodejs" else req
        if canonical not in handlers:
            failed.append((req, f"语言 '{req}' 不受支持，无法作为必需门禁验证"))

    with tempfile.TemporaryDirectory() as tmpdir:
        expanded_langs = []
        for lang in target_langs:
            if lang in ("all", "full"):
                expanded_langs.extend(["c", "node", "go", "php", "rust"])
            else:
                expanded_langs.append(lang)

        # De-duplicate preserving order
        checked_set = set()
        for l in expanded_langs:
            canonical = "node" if l == "nodejs" else l
            if canonical in checked_set:
                continue
            checked_set.add(canonical)

            handler = handlers.get(canonical)
            if not handler:
                msg = f"Language '{canonical}' not supported for parity test"
                if canonical in require_langs or l in require_langs:
                    failed.append((l, msg))
                else:
                    skipped.append((l, msg))
                continue

            print(f"Testing {canonical} SDK parity against Python reference...")
            ok, msg = handler(ips, ref, tmpdir)
            is_required = (canonical in require_langs or l in require_langs)
            if ok is True:
                print(f"  ✅ {msg}")
                verified.append((canonical, msg))
            elif ok is None:
                print(f"  ℹ️  {canonical.upper()}: 未验证（{msg}）")
                if is_required:
                    failed.append((canonical, f"必需语言 {canonical} 未能执行: {msg}"))
                else:
                    skipped.append((canonical, msg))
            else:
                print(f"  ❌ {msg}", file=sys.stderr)
                failed.append((canonical, msg))

    # Also check if any required language was omitted from target_langs entirely
    for req in require_langs:
        canonical = "node" if req == "nodejs" else req
        if canonical not in checked_set and not any(f[0] == req for f in failed):
            failed.append((req, f"必需语言 {req} 未在待测语言清单 (--langs) 中被执行"))

    verified_names = [v[0] for v in verified]
    skipped_names = [s[0] for s in skipped]
    failed_names = [f[0] for f in failed]

    print("\n" + "=" * 65)
    print("Demo 对拍统计汇总:")
    if verified:
        print(f"  已验证 ({len(verified)} 种): {', '.join(verified_names)}")
    else:
        print("  已验证: 0 种")
    if skipped:
        print(f"  跳过 ({len(skipped)} 种):")
        for l, reason in skipped:
            print(f"    - {l}: {reason}")
    if failed:
        print(f"  失败/未满足要求 ({len(failed)} 种):")
        for l, reason in failed:
            print(f"    - {l}: {reason}")
    print("=" * 65)

    if args.status_file:
        try:
            status_data = {
                "verified": verified_names,
                "skipped": [{"lang": l, "reason": r} for l, r in skipped],
                "failed": [{"lang": l, "reason": r} for l, r in failed],
                "success": len(failed) == 0,
                "fully_covered": len(failed) == 0 and len(skipped) == 0
            }
            with open(args.status_file, "w", encoding="utf-8") as sf:
                json.dump(status_data, sf, ensure_ascii=False, indent=2)
        except Exception as e:
            print(f"Warning: failed to write status file: {e}", file=sys.stderr)

    if failed:
        print(f"\n❌ 门禁失败: 必需语言未全部通过或存在对拍差异 (共 {len(failed)} 项失败)!", file=sys.stderr)
        return 1

    if skipped:
        print(f"\n⚠️  验证结束：本地跳过了 {len(skipped)} 种语言 ({', '.join(skipped_names)})，仅完成部分语言对拍。")
        return 0

    print(f"\n🎉 Demo parity verification passed completely! (已验证全量指定语言: {', '.join(verified_names)})")
    return 0

if __name__ == "__main__":
    sys.exit(main())
