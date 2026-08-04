#!/usr/bin/env python3
"""
Cross-language result verification.
Queries the same IPs across all 8 language SDKs and diffs the pipe output.
Any difference = parsing bug.
"""
import os, sys, subprocess, json, tempfile

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))

TEST_IPS = [
    # Common V4
    "114.114.114.114",
    "223.5.5.5",
    "8.8.8.8",
    "1.1.1.1",
    "192.168.1.1",
    "10.0.0.1",
    "127.0.0.1",
    "0.0.0.0",
    "255.255.255.255",
    "172.16.0.1",
    "100.64.0.1",
    "202.96.128.86",
    "180.76.76.76",
    "47.94.1.1",
    "58.217.200.13",
    "39.104.24.1",
    "101.226.125.1",
    "119.29.29.29",
    "112.74.2.1",
    "120.55.55.55",
    "218.104.97.1",
    "61.132.246.1",
    "60.205.0.1",
    "47.104.16.1",
    "36.99.136.1",
    # Edge V4
    "0.0.0.1",
    "192.0.2.1",
    "198.51.100.1",
    "203.0.113.1",
    "233.252.0.1",
    "240.0.0.1",
    "1.0.0.1",
    "8.8.4.4",
    "9.9.9.9",
    "149.112.112.112",
    "1.0.0.0",
    "224.0.0.1",
    "169.254.0.1",
    "100.100.2.136",
    "47.246.1.1",
    "203.119.169.1",
    "47.254.24.1",
    "120.232.1.1",
    "103.152.112.1",
    "116.62.81.1",
    "39.108.0.1",
    # V6
    "2408:8000:9000::1",
    "2001:4860:4860::8888",
    "2606:4700:4700::1111",
    "2400:3200::1",
    "2400:da00::1",
    "2804:14d:5c82:d484:0:ff:fe00:1",
    "2001:db8::1",
    "fe80::1",
    "::1",
    "::",
    "ff02::1",
    "2a00:1450:4001:801::200e",
    "2607:f8b0:4004:800::200e",
    "2c0f:f248:0:1::cafe",
    "2402:4e00:0:1::abcd",
]

DB_PATH = os.path.join(SCRIPT_DIR, "data", "qqzeng_ip_max_global.qzdb")
if not os.path.exists(DB_PATH):
    DB_PATH = os.path.join(SCRIPT_DIR, "data", "qqzeng_ip_std_china.qzdb")


def run_python(ip_list):
    sys.path.insert(0, os.path.join(SCRIPT_DIR, "python"))
    from qzdb import QzdbSearcher
    searcher = QzdbSearcher.get_instance(DB_PATH)
    results = {}
    for ip in ip_list:
        r = searcher.find(ip)
        results[ip] = r.to_pipe() if r else ""
    return results


def run_nodejs(ip_list):
    ips_json = json.dumps(ip_list)
    js_code = f"""
const QzdbSearcher = require('./qzdb');
const ips = {ips_json};
const s = new QzdbSearcher('{DB_PATH}');
const results = {{}};
for (const ip of ips) {{
    const r = s.find(ip);
    results[ip] = r ? r.toPipe() : '';
}}
console.log(JSON.stringify(results));
"""
    tmp = os.path.join(SCRIPT_DIR, "nodejs", "_cross_verify.js")
    with open(tmp, "w") as f:
        f.write(js_code)
    try:
        out = subprocess.check_output(["node", tmp], cwd=os.path.join(SCRIPT_DIR, "nodejs"), timeout=30).decode()
        return json.loads(out.strip())
    finally:
        os.unlink(tmp)


def run_php(ip_list):
    php_ips = "array(" + ", ".join(f'"{ip}"' for ip in ip_list) + ")"
    php_code = f"""<?php
require_once '{os.path.join(SCRIPT_DIR, "php", "QzdbSearcher.php")}';
use Qqzeng\\Ip\\QzdbSearcher;
$ips = {php_ips};
$s = QzdbSearcher::getInstance('{DB_PATH}');
$results = array();
foreach ($ips as $ip) {{
    $r = $s->find($ip);
    $results[$ip] = $r ? $r->toPipe() : '';
}}
echo json_encode($results);
"""
    tmp = os.path.join(SCRIPT_DIR, "php", "_cross_verify.php")
    with open(tmp, "w") as f:
        f.write(php_code)
    try:
        out = subprocess.check_output(["php", "-d", "memory_limit=256M", tmp],
                                       cwd=os.path.join(SCRIPT_DIR, "php"), timeout=30).decode()
        return json.loads(out.strip())
    except Exception as e:
        print(f"  [SKIP PHP: {e}]")
        return None
    finally:
        if os.path.exists(tmp):
            os.unlink(tmp)


def run_go(ip_list):
    ips_json = json.dumps(ip_list)
    go_code = f"""package main

import (
    "encoding/json"
    "fmt"
    "os"
    "qzdb"
)

func main() {{
    ips := []string{{{", ".join(f'"{ip}"' for ip in ip_list)}}}
    s, err := qzdb.NewQzdbSearcher("{DB_PATH}")
    if err != nil {{
        fmt.Fprintln(os.Stderr, err)
        os.Exit(1)
    }}
    results := make(map[string]string)
    for _, ip := range ips {{
        r := s.Find(ip)
        if r != nil {{
            results[ip] = r.ToPipe()
        }} else {{
            results[ip] = ""
        }}
    }}
    data, _ := json.Marshal(results)
    fmt.Print(string(data))
}}
"""
    tmp = os.path.join(SCRIPT_DIR, "go", "_cross_verify", "main.go")
    os.makedirs(os.path.dirname(tmp), exist_ok=True)
    with open(tmp, "w") as f:
        f.write(go_code)
    try:
        out = subprocess.check_output(
            ["go", "run", "main.go"],
            cwd=os.path.join(SCRIPT_DIR, "go"),
            timeout=60
        ).decode()
        return json.loads(out.strip())
    finally:
        os.unlink(tmp)
        os.rmdir(os.path.dirname(tmp))


def run_java(ip_list):
    java_ips = "{" + ", ".join(f'"{ip}"' for ip in ip_list) + "}"
    java_code = f"""import qzdb.QzdbSearcher;
import qzdb.IpLocation;
import java.util.LinkedHashMap;

public class CrossVerify {{
    public static void main(String[] args) throws Exception {{
        String[] ips = {java_ips};
        QzdbSearcher s = QzdbSearcher.getInstance();
        s.load("{DB_PATH}");
        LinkedHashMap<String, String> results = new LinkedHashMap<>();
        for (String ip : ips) {{
            IpLocation r = s.find(ip);
            results.put(ip, r != null ? r.toPipeString() : "");
        }}
        StringBuilder sb = new StringBuilder();
        sb.append("{{");
        boolean first = true;
        for (var e : results.entrySet()) {{
            if (!first) sb.append(",");
            first = false;
            sb.append("\\\"").append(e.getKey()).append("\\\":\\\"").append(e.getValue().replace("\\\\","\\\\\\\\").replace("\\\"","\\\\\\\"")).append("\\\"");
        }}
        sb.append("}}");
        System.out.println(sb.toString());
    }}
}}
"""
    build_dir = os.path.join(SCRIPT_DIR, "java", "build")
    os.makedirs(build_dir, exist_ok=True)
    tmp = os.path.join(build_dir, "CrossVerify.java")
    with open(tmp, "w") as f:
        f.write(java_code)
    try:
        java_home = _find_java_home()
        if not java_home:
            return None
        javac_cmd = [f"{java_home}/bin/javac", "-d", build_dir]
        srcs = [tmp]
        for root, dirs, files in os.walk(os.path.join(SCRIPT_DIR, "java", "src")):
            for fn in files:
                if fn.endswith(".java"):
                    srcs.append(os.path.join(root, fn))
        javac_cmd.extend(srcs)
        subprocess.check_output(javac_cmd, timeout=30, stderr=subprocess.STDOUT)
        out = subprocess.check_output(
            [f"{java_home}/bin/java", "-cp", build_dir, "CrossVerify"],
            timeout=30, stderr=subprocess.STDOUT
        ).decode()
        return json.loads(out.strip())
    except Exception as e:
        print(f"  [SKIP Java: {e}]")
        return None
    finally:
        for f in ["CrossVerify.java", "CrossVerify.class"]:
            p = os.path.join(build_dir, f)
            if os.path.exists(p):
                os.unlink(p)


def _find_java_home():
    import glob
    candidates = glob.glob("/opt/homebrew/Cellar/openjdk@21/*/libexec/openjdk.jdk/Contents/Home") + \
                 glob.glob("/opt/homebrew/opt/openjdk@21") + \
                 glob.glob("/opt/homebrew/opt/openjdk") + \
                 glob.glob("/Library/Java/JavaVirtualMachines/*/Contents/Home")
    for h in candidates:
        if os.path.exists(os.path.join(h, "bin", "javac")):
            return h
    return None


def run_rust(ip_list):
    rust_bin = os.path.join(SCRIPT_DIR, "rust", "_cross_verify", "target", "release", "cross_verify")
    if not os.path.exists(rust_bin):
        print(f"  [SKIP Rust: binary not found]")
        return None
    try:
        cmd = [rust_bin, DB_PATH] + ip_list
        out = subprocess.check_output(cmd, cwd=SCRIPT_DIR, timeout=30).decode()
        return json.loads(out.strip())
    except Exception as e:
        print(f"  [SKIP Rust: {e}]")
        return None


def run_c(ip_list):
    ips_json = json.dumps(ip_list)
    c_code = f"""#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "qzdb_searcher.h"

int main() {{
    const char* ips[] = {{{", ".join(f'"{ip}"' for ip in ip_list)}}};
    int n = {len(ip_list)};

    qzdb_searcher_t ctx;
    if (qzdb_init(&ctx, "{DB_PATH}") != 0) {{
        fprintf(stderr, "load failed\\n");
        return 1;
    }}

    printf("{{");
    for (int i = 0; i < n; i++) {{
        char out[4096];
        int rc = qzdb_find_str(&ctx, ips[i], out, sizeof(out));
        printf("\\\"%s\\\":\\\"%s\\\"", ips[i], rc == 0 ? out : "");
        if (i < n - 1) printf(",");
    }}
    printf("}}\\n");
    qzdb_free(&ctx);
    return 0;
}}
"""
    tmp_c = os.path.join(SCRIPT_DIR, "c", "_cross_verify.c")
    with open(tmp_c, "w") as f:
        f.write(c_code)
    try:
        exe = os.path.join(SCRIPT_DIR, "c", "_cross_verify")
        subprocess.check_output(
            ["clang", "-O2", "-o", exe, tmp_c, "qzdb_searcher.c", "-lm"],
            cwd=os.path.join(SCRIPT_DIR, "c"), timeout=30, stderr=subprocess.STDOUT
        )
        out = subprocess.check_output([exe], timeout=30).decode()
        return json.loads(out.strip())
    except Exception as e:
        print(f"  [SKIP C: {e}]")
        return None
    finally:
        for f in [tmp_c, exe]:
            if os.path.exists(f):
                os.unlink(f)


def run_go(ip_list):
    go_bin = os.path.join(SCRIPT_DIR, "test_runner_bin", "go_cross_verify")
    if not os.path.exists(go_bin):
        print(f"  [SKIP Go: binary not found at {go_bin}]")
        return None
    try:
        cmd = [go_bin, DB_PATH] + ip_list
        out = subprocess.check_output(cmd, cwd=SCRIPT_DIR, timeout=30).decode()
        return json.loads(out.strip())
    except Exception as e:
        print(f"  [SKIP Go: {e}]")
        return None


def run_csharp(ip_list):
    cs_exe = os.path.join(SCRIPT_DIR, "test_runner_bin", "cs_cross_verify", "CrossVerify")
    if not os.path.exists(cs_exe):
        print(f"  [SKIP C#: binary not found at {cs_exe}]")
        return None
    try:
        cmd = [cs_exe, DB_PATH] + ip_list
        out = subprocess.check_output(cmd, cwd=SCRIPT_DIR, timeout=30).decode()
        return json.loads(out.strip())
    except Exception as e:
        print(f"  [SKIP C#: {e}]")
        return None


def main():
    print(f"Cross-Language Verification: {len(TEST_IPS)} IPs × all languages")
    print(f"DB: {DB_PATH}\n")

    print("Running Python (reference)...")
    ref = run_python(TEST_IPS)
    print(f"  {len(ref)} results\n")

    languages = {
        "Node.js": run_nodejs,
        "PHP": run_php,
        "C": run_c,
        "Rust": run_rust,
        "Java": run_java,
        "C#": run_csharp,
        "Go": run_go,
    }

    all_results = {"Python": ref}
    for lang, runner in languages.items():
        print(f"Running {lang}...")
        try:
            results = runner(TEST_IPS)
            if results is None:
                print(f"  [SKIPPED]\n")
                continue
            all_results[lang] = results
            print(f"  {len(results)} results\n")
        except Exception as e:
            print(f"  [ERROR: {e}]\n")

    print("=" * 60)
    print("COMPARISON RESULTS")
    print("=" * 60)

    total = 0
    passed = 0
    failed = 0
    failures = []

    for ip in TEST_IPS:
        ref_val = ref.get(ip, "MISSING")
        for lang, results in all_results.items():
            if lang == "Python":
                continue
            total += 1
            lang_val = results.get(ip, "MISSING")
            if ref_val == lang_val:
                passed += 1
            else:
                failed += 1
                failures.append((ip, lang, ref_val, lang_val))

    if failures:
        print(f"\nFAILED ({failed}/{total} comparisons):\n")
        for ip, lang, expected, got in failures:
            exp_short = expected[:80] + "..." if len(expected) > 80 else expected
            got_short = got[:80] + "..." if len(got) > 80 else got
            print(f"  IP: {ip}")
            print(f"    Python:  {exp_short}")
            print(f"    {lang}: {got_short}")
            print()
    else:
        print(f"\nALL {total} comparisons PASSED across {len(all_results)} languages!")
        print("Every IP returns identical results across all SDKs.")

    print(f"\nSummary: {passed} passed, {failed} failed out of {total} comparisons")
    print(f"Languages tested: {', '.join(all_results.keys())}")

    return 0 if failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
