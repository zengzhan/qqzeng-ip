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
    searcher = QzdbReader(DB_PATH)
    results = {}
    for ip in ip_list:
        r = searcher.find(ip)
        results[ip] = r.to_pipe() if r else ""
    return results

def run_nodejs_v6(ip_list):
    # NOTE: 生成文件必须写到 nodejs/ 目录、cwd 也要切到 nodejs/，
    # 否则 require('./qzdb') 会按多语言根目录解析而找不到模块。
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
    tmp = os.path.join(SCRIPT_DIR, "nodejs", "_cross_v6_tmp.js")
    with open(tmp, "w") as f:
        f.write(js_code)
    try:
        out = subprocess.check_output(["node", tmp], cwd=os.path.join(SCRIPT_DIR, "nodejs"), timeout=30).decode()
        return json.loads(out.strip())
    except Exception as e:
        print(f"  [SKIP Node.js IPv6: {e}]")
        return None
    finally:
        if os.path.exists(tmp):
            os.unlink(tmp)

def run_php_v6(ip_list):
    # NOTE: GeoInfo 不是数组，不能用 implode('|', $r)，必须用 $r->toPipe()。
    # require_once 使用绝对路径 + cwd 切到 php/，避免相对路径解析歧义。
    php_ips = "array(" + ", ".join(f'"{ip}"' for ip in ip_list) + ")"
    php_code = f"""<?php
require_once '{os.path.join(SCRIPT_DIR, "php", "QzdbReader.php")}';
use Qqzeng\\Ip\\QzdbReader;
$ips = {php_ips};
$s = new QzdbReader('{DB_PATH}');
$results = array();
foreach ($ips as $ip) {{
    $r = $s->find($ip);
    $results[$ip] = $r ? $r->toPipe() : '';
}}
echo json_encode($results);
"""
    tmp = os.path.join(SCRIPT_DIR, "php", "_cross_v6_tmp.php")
    with open(tmp, "w") as f:
        f.write(php_code)
    try:
        out = subprocess.check_output(["php", "-d", "memory_limit=256M", tmp],
                                       cwd=os.path.join(SCRIPT_DIR, "php"), timeout=30).decode()
        return json.loads(out.strip())
    except Exception as e:
        print(f"  [SKIP PHP IPv6: {e}]")
        return None
    finally:
        if os.path.exists(tmp):
            os.unlink(tmp)


def run_c_v6(ip_list):
    def _iprow(ip_str):
        ipv6 = ipaddress.IPv6Address(ip_str)
        return ipv6.packed.hex()

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

def _find_java_home():
    import glob as _glob
    candidates = []
    for pat in (
        "/opt/homebrew/Cellar/openjdk@21/*/libexec/openjdk.jdk/Contents/Home",
        "/opt/homebrew/opt/openjdk@21",
        "/opt/homebrew/opt/openjdk",
        "/Library/Java/JavaVirtualMachines/*/Contents/Home",
    ):
        candidates.extend(_glob.glob(pat))
    for h in candidates:
        if os.path.exists(os.path.join(h, "bin", "javac")):
            return h
    return None


def run_java_v6(ip_list):
    # NOTE: 不能用 json.dumps(ip_list)（生成 Python 风格 [".."]），
    # Java 数组必须用 {".."} 形式。javac 必须连同 SDK 源码一起编译。
    java_ips = "{" + ", ".join(f'"{ip}"' for ip in ip_list) + "}"
    java_code = f"""import com.qqzeng.qzdb.QzdbReader;
import com.qqzeng.qzdb.GeoInfo;
import java.io.File;
import java.util.LinkedHashMap;
import java.util.Optional;

public class CrossVerifyV6 {{
    public static void main(String[] args) throws Exception {{
        String[] ips = {java_ips};
        QzdbReader reader = new QzdbReader.Builder(new File("{DB_PATH}")).build();
        LinkedHashMap<String, String> results = new LinkedHashMap<>();
        for (String ip : ips) {{
            Optional<GeoInfo> r = reader.find(ip);
            results.put(ip, r.isPresent() ? r.get().toPipeString() : "");
        }}
        StringBuilder sb = new StringBuilder();
        sb.append("{{");
        boolean first = true;
        for (var e : results.entrySet()) {{
            if (!first) sb.append(",");
            first = false;
            sb.append("\\\"").append(e.getKey()).append("\\\":\\\"")
              .append(e.getValue().replace("\\\\", "\\\\\\\\").replace("\\\"", "\\\\\\\"")).append("\\\"");
        }}
        sb.append("}}");
        System.out.println(sb.toString());
        reader.close();
    }}
}}
"""
    build_dir = os.path.join(SCRIPT_DIR, "java", "build")
    os.makedirs(build_dir, exist_ok=True)
    tmp = os.path.join(build_dir, "CrossVerifyV6.java")
    with open(tmp, "w") as f:
        f.write(java_code)
    try:
        java_home = _find_java_home()
        if not java_home:
            print("  [SKIP Java IPv6: no JDK found]")
            return None
        javac_cmd = [f"{java_home}/bin/javac", "-encoding", "UTF-8", "-d", build_dir]
        srcs = [tmp]
        for root, dirs, files in os.walk(os.path.join(SCRIPT_DIR, "java", "src")):
            for fn in files:
                if fn.endswith(".java"):
                    srcs.append(os.path.join(root, fn))
        javac_cmd.extend(srcs)
        subprocess.check_output(javac_cmd, timeout=30, stderr=subprocess.STDOUT)
        out = subprocess.check_output(
            [f"{java_home}/bin/java", "-cp", build_dir, "CrossVerifyV6"],
            timeout=30, stderr=subprocess.STDOUT
        ).decode()
        return json.loads(out.strip())
    except Exception as e:
        print(f"  [SKIP Java IPv6: {e}]")
        return None
    finally:
        for f in ["CrossVerifyV6.java", "CrossVerifyV6.class"]:
            p = os.path.join(build_dir, f)
            if os.path.exists(p):
                os.unlink(p)

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
