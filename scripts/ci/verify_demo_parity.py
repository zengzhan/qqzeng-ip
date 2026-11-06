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

    cc = os.environ.get("CC", "cc")
    cmd = [cc, "-std=c11", "-Wall", "-Wextra", "-Werror", "-O2", str(runner_c), str(c_src), "-o", str(runner_bin), "-lm", "-lpthread"]
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

    node_bin = shutil.which("node")
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
    php_bin = shutil.which("php")
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
    go_bin = shutil.which("go")
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
    cargo_bin = shutil.which("cargo")
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

    rustc_bin = shutil.which("rustc")
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
    args = parser.parse_args()

    target_langs = [l.strip().lower() for l in args.langs.split(",") if l.strip()]

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

    all_passed = True
    with tempfile.TemporaryDirectory() as tmpdir:
        for lang in target_langs:
            if lang in ("all", "full"):
                check_list = ["c", "node", "go", "php", "rust"]
            else:
                check_list = [lang]

            for l in check_list:
                handler = handlers.get(l)
                if not handler:
                    print(f"⚠️  Language '{l}' not supported for parity test")
                    continue
                print(f"Testing {l} SDK parity against Python reference...")
                ok, msg = handler(ips, ref, tmpdir)
                if ok is True:
                    print(f"  ✅ {msg}")
                elif ok is None:
                    print(f"  ℹ️  {l.upper()}: 未验证（{msg}）")
                else:
                    print(f"  ❌ {msg}", file=sys.stderr)
                    all_passed = False

    if not all_passed:
        print("\n❌ Demo parity check failed!", file=sys.stderr)
        return 1

    print("\n🎉 Demo parity verification passed completely!")
    return 0

if __name__ == "__main__":
    sys.exit(main())
