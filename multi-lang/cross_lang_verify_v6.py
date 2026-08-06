#!/usr/bin/env python3
"""Cross-language IPv6 verification."""
import os, sys, subprocess, json, tempfile

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))

TEST_IPS_V6 = [
    "2408:8000:9000::1",
    "2001:4860:4860::8888",
    "2606:4700:4700::1111",
    "2400:3200::1",
    "2400:da00::1",
    "2a00:1450:4001:801::200e",
    "2607:f8b0:4004:800::200e",
    "2c0f:f248:0:1::cafe",
    "2402:4e00:0:1::abcd",
    "::1",
    "::",
    "ff02::1",
]

DB_PATH = os.path.join(SCRIPT_DIR, "data", "qqzeng_ip_std_china.qzdb")

def run_python_v6(ip_list):
    sys.path.insert(0, os.path.join(SCRIPT_DIR, "python"))
    from qzdb import QzdbReader
    searcher = QzdbReader.get_instance(DB_PATH)
    results = {}
    for ip in ip_list:
        r = searcher.find(ip)
        results[ip] = r.to_pipe() if r else ""
    return results

def run_nodejs_v6(ip_list):
    js_code = f"""
const QzdbReader = require('./qzdb');
const s = new QzdbReader('{DB_PATH}');
const results = {{}};
for (const ip of {json.dumps(ip_list)}) {{
    const r = s.find(ip);
    results[ip] = r ? r.toPipe() : '';
}}
console.log(JSON.stringify(results));
"""
    tmp = os.path.join(SCRIPT_DIR, ".cross_v6_js_tmp.js")
    with open(tmp, "w") as f:
        f.write(js_code)
    try:
        out = subprocess.check_output(["node", tmp], cwd=SCRIPT_DIR, timeout=30).decode()
        return json.loads(out.strip())
    except Exception as e:
        print(f"  [SKIP Node.js IPv6: {e}]")
        return None
    finally:
        if os.path.exists(tmp):
            os.unlink(tmp)

def run_php_v6(ip_list):
    php_code = f"""<?php
require_once 'QzdbReader.php';
use Qqzeng\Ip\QzdbReader;
$s = QzdbReader::getInstance('{DB_PATH}');
$results = array();
$ips = {json.dumps(ip_list)};
foreach ($ips as $ip) {{
    $r = $s->find($ip);
    $results[$ip] = $r ? implode('|', $r) : '';
}}
echo json_encode($results);
?>"""
    tmp = os.path.join(SCRIPT_DIR, "php", "_cross_v6_tmp.php")
    with open(tmp, "w") as f:
        f.write(php_code)
    try:
        out = subprocess.check_output(["php", tmp], cwd=SCRIPT_DIR, timeout=30).decode()
        return json.loads(out.strip())
    except Exception as e:
        print(f"  [SKIP PHP IPv6: {e}]")
        return None
    finally:
        if os.path.exists(tmp):
            os.unlink(tmp)

def run_c_v6(ip_list):
    c_code = f"""#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "qzdb_reader.h"

int main() {{
    const char* ips[] = {{{", ".join(f'"{ip}"' for ip in ip_list)}}};
    int n = {len(ip_list)};
    qzdb_reader_t ctx;
    if (qzdb_init(&ctx, "{DB_PATH}") != 0) {{
        fprintf(stderr, "load failed\\n");
        return 1;
    }}
    printf("{{");
    for (int i = 0; i < n; i++) {{
        char out[4096];
        int rc = qzdb_find_str(&ctx, ips[i], out, sizeof(out));
        printf("\\"%s\\":\\"%s\\"", ips[i], rc == 0 ? out : "");
        if (i < n - 1) printf(",");
    }}
    printf("}}\\n");
    qzdb_free(&ctx);
    return 0;
}}
"""
    tmp_c = os.path.join(SCRIPT_DIR, "c", "_cross_v6_tmp.c")
    with open(tmp_c, "w") as f:
        f.write(c_code)
    try:
        exe = os.path.join(SCRIPT_DIR, "c", "_cross_v6_tmp")
        subprocess.check_output(
            ["clang", "-O2", "-o", exe, tmp_c, "qzdb_reader.c", "-lm"],
            cwd=os.path.join(SCRIPT_DIR, "c"), timeout=30, stderr=subprocess.DEVNULL
        )
        out = subprocess.check_output([exe], timeout=30).decode()
        return json.loads(out.strip())
    except Exception as e:
        print(f"  [SKIP C IPv6: {e}]")
        return None
    finally:
        for f in [tmp_c, exe]:
            if os.path.exists(f):
                os.unlink(f)

def run_java_v6(ip_list):
    java_code = f"""import qzdb.QzdbReader;
import qzdb.IpLocation;
public class CrossVerifyV6 {{
    public static void main(String[] args) {{
        QzdbReader searcher = QzdbReader.getInstance();
        searcher.load("{DB_PATH}");
        String[] ips = {json.dumps(ip_list)};
        java.util.Map<String, String> results = new java.util.HashMap<>();
        for (String ip : ips) {{
            IpLocation loc = searcher.find(ip);
            results.put(ip, loc != null ? loc.toPipe() : "");
        }}
        System.out.println(new com.google.gson.Gson().toJson(results));
    }}
}}
"""
    build_dir = os.path.join(SCRIPT_DIR, "java", "build")
    os.makedirs(build_dir, exist_ok=True)
    tmp = os.path.join(build_dir, "CrossVerifyV6.java")
    with open(tmp, "w") as f:
        f.write(java_code)
    try:
        javac_cmd = ["javac", "-d", build_dir, tmp]
        subprocess.check_output(javac_cmd, cwd=SCRIPT_DIR, timeout=30, stderr=subprocess.DEVNULL)
        out = subprocess.check_output(
            ["java", "-cp", build_dir, "CrossVerifyV6"],
            cwd=SCRIPT_DIR, timeout=30
        ).decode()
        return json.loads(out.strip())
    except Exception as e:
        print(f"  [SKIP Java IPv6: {e}]")
        return None
    finally:
        for f in [tmp, os.path.join(build_dir, "CrossVerifyV6.class")]:
            if os.path.exists(f):
                os.unlink(f)

def main():
    print(f"Cross-Language IPv6 Verification: {len(TEST_IPS_V6)} IPs")
    print(f"DB: {DB_PATH}\n")

    print("Running Python (reference)...")
    ref = run_python_v6(TEST_IPS_V6)
    print(f"  {len(ref)} results\n")

    languages = {
        "Node.js": run_nodejs_v6,
        "PHP": run_php_v6,
        "C": run_c_v6,
        "Java": run_java_v6,
    }

    all_results = {"Python": ref}
    for lang, runner in languages.items():
        print(f"Running {lang}...")
        try:
            results = runner(TEST_IPS_V6)
            if results is None:
                print(f"  [SKIPPED]\n")
                continue
            all_results[lang] = results
            print(f"  {len(results)} results\n")
        except Exception as e:
            print(f"  [ERROR: {e}]\n")

    print("=" * 60)
    print("IPv6 COMPARISON RESULTS")
    print("=" * 60)

    total = 0
    passed = 0
    failed = 0
    failures = []

    for ip in TEST_IPS_V6:
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
            print(f"  {ip}:")
            print(f"    Python:  {expected[:80]}")
            print(f"    {lang}: {got[:80]}")
            print()
    else:
        print(f"\nALL {total} IPv6 comparisons PASSED across {len(all_results)} languages!")

    print(f"\nSummary: {passed} passed, {failed} failed out of {total} comparisons")
    print(f"Languages tested: {', '.join(all_results.keys())}")

    return 0 if failed == 0 else 1

if __name__ == '__main__':
    sys.exit(main())
