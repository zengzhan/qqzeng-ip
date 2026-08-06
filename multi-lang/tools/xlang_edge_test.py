"""
Cross-language edge case test.
Generates test cases → runs each language → compares results.
"""
import json, os, subprocess, sys, tempfile

DATA_DIR = os.path.join(os.path.dirname(__file__), '..', 'data')
SRC_DIR = os.path.join(os.path.dirname(__file__), '..')
sys.path.insert(0, os.path.join(SRC_DIR, 'python'))
from qzdb import QzdbReader

# Edge-case IPs: (label, ip, is_valid, db_key)
# db_key: 'std_china' or 'max_global'
TEST_CASES = [
    # ---- V4 boundary ----
    ("V4-normal", "114.114.114.114", True),
    ("V4-private-A", "10.0.0.1", True),
    ("V4-private-B", "172.16.0.1", True),
    ("V4-private-C", "192.168.1.1", True),
    ("V4-loopback", "127.0.0.1", True),
    ("V4-linklocal", "169.254.1.1", True),
    ("V4-documentation", "192.0.2.1", True),
    ("V4-reserved-240", "240.0.0.1", True),
    ("V4-multicast", "224.0.0.1", True),
    ("V4-broadcast", "255.255.255.255", True),
    ("V4-zero", "0.0.0.0", True),
    ("V4-ones", "255.255.255.254", True),

    # ---- V4 invalid ----
    ("V4-leading-zero", "0162.0162.0162.0162", False),  # Python treats as 162.162.162.162
    ("V4-empty", "", False),
    ("V4-too-few", "1.2.3", False),
    ("V4-too-many", "1.2.3.4.5", False),
    ("V4-double-dot", "114..114.114.114", False),
    ("V4-leading-dot", ".1.2.3.4", False),
    ("V4-trailing-dot", "1.2.3.4.", False),
    ("V4-overflow", "256.1.2.3", False),
    ("V4-negative", "-1.2.3.4", False),
    ("V4-hex", "0x72.0x72.0x72.0x72", False),
    ("V4-alpha", "a.b.c.d", False),
    ("V4-whitespace-left", " 114.114.114.114", False),
    ("V4-whitespace-right", "114.114.114.114 ", False),
    ("V4-with-port", "127.0.0.1:8080", False),
    ("V4-fullwidth", "１１４．１１４．１１４．１１４", False),

    # ---- V6 valid ----
    ("V6-loopback", "::1", True),
    ("V6-unspecified", "::", True),
    ("V6-google", "2001:4860:4860::8888", True),
    ("V6-china-unicom", "2408:8000:9000::1", True),
    ("V6-cloudflare", "2606:4700:4700::1111", True),
    ("V6-documentation", "2001:db8::1", True),
    ("V6-linklocal", "fe80::1", True),
    ("V6-multicast", "ff02::1", True),
    ("V6-6to4", "2002::1", True),
    ("V6-ula", "fd00::1", True),
    ("V6-mixed-case", "2001:DB8::1", True),
    ("V6-embedded-v4-mapped", "::ffff:114.114.114.114", True),
    ("V6-embedded-v4-google", "::ffff:8.8.8.8", True),
    ("V6-full-form", "0000:0000:0000:0000:0000:0000:0000:0001", True),
    ("V6-google-full", "2001:4860:4860:0000:0000:0000:0000:8888", True),

    # ---- V6 invalid ----
    ("V6-empty", "", False),
    ("V6-too-many-groups", "1:2:3:4:5:6:7:8:9", False),
    ("V6-double-compress", "2001::1::", False),
    ("V6-non-hex", "2001:gggg::1", False),
    ("V6-zone-id", "fe80::1%eth0", False),
    ("V6-bracketed", "[::1]", False),
    ("V6-with-port", "[::1]:80", False),
    ("V6-ipv4-translated", "::ffff:0:114.114.114.114", False),
]

def run_python(db_path):
    """Run all tests via Python and return results."""
    s = QzdbReader(db_path)
    results = {}
    for label, ip, is_valid in TEST_CASES:
        r = s.find(ip)
        results[label] = r.to_pipe() if r else "(empty)"
    return results

def run_go(db_path, label_filter=None):
    """Compile and run Go test binary."""
    # Write Go test program
    go_src = '''package main

import (
    "fmt"
    "os"
    "qzdb_reader/qzdb"
)

func main() {
    searcher, err := qzdb.Instance("%s")
    if err != nil { os.Exit(1) }
    cases := []struct{label, ip string}{
%s
    }
    for _, c := range cases {
        r := searcher.FindStr(c.ip)
        if r == "" { r = "(empty)" }
        fmt.Printf("%%s\\t%%s\\n", c.label, r)
    }
}
'''
    case_entries = '\n'.join(f'        {{"{l}", "{ip}"}},' for l, ip, _ in TEST_CASES)
    code = go_src % (db_path, case_entries)
    
    tmpdir = tempfile.mkdtemp()
    gofile = os.path.join(tmpdir, 'test_edge.go')
    with open(gofile, 'w') as f:
        f.write(code)
    
    # Build with proper module context
    mod_dir = os.path.join(SRC_DIR, 'go')
    try:
        r = subprocess.run(['go', 'run', gofile], cwd=mod_dir, 
                         capture_output=True, text=True, timeout=30)
        results = {}
        for line in r.stdout.strip().split('\n'):
            if '\t' in line:
                label, val = line.split('\t', 1)
                results[label] = val
        return results
    except Exception as e:
        print(f"  [Go error] {e}")
        return {}
    finally:
        subprocess.run(['rm', '-rf', tmpdir])


def run_c(db_path, label_filter=None):
    """Compile and run C test binary."""
    # Build C test source. Use {dbpath} and {calls} as placeholders to avoid printf %s conflict.
    c_src = '''
#include "qzdb_reader.h"
#include <stdio.h>
#include <string.h>

static void test(qzdb_reader_t* ctx, const char* label, const char* ip) {
    qzdb_geo_info_t info;
    char buf[1024];
    if (qzdb_find(ctx, ip, &info) != 0) {
        printf("%s\\t(empty)\\n", label);
        return;
    }
    buf[0] = '\\0';
    size_t pos = 0;
    for (uint32_t i = 0; i < ctx->pool_count && pos < sizeof(buf) - 1; i++) {
        if (i > 0) buf[pos++] = '|';
        if (info.values[i]) {
            size_t len = strlen(info.values[i]);
            if (pos + len < sizeof(buf)) {
                memcpy(buf + pos, info.values[i], len);
                pos += len;
            }
        }
    }
    buf[pos] = '\\0';
    printf("%s\\t%s\\n", label, buf);
}

int main() {
    qzdb_reader_t ctx;
    if (qzdb_init(&ctx, "{dbpath}") != 0) return 1;
{calls}
    qzdb_free(&ctx);
    return 0;
}
'''
    calls = '\n'.join(f'    test(&ctx, "{l}", "{ip}");' for l, ip, _ in TEST_CASES)
    code = c_src.replace('{dbpath}', db_path).replace('{calls}', calls)
    
    tmpdir = tempfile.mkdtemp()
    cfile = os.path.join(tmpdir, 'test_edge.c')
    binfile = os.path.join(tmpdir, 'test_edge')
    with open(cfile, 'w') as f:
        f.write(code)
    
    c_dir = os.path.join(SRC_DIR, 'c')
    try:
        subprocess.run(['cc', '-O2', '-Wall', '-I', c_dir, '-o', binfile, cfile,
                       os.path.join(c_dir, 'qzdb_reader.c')],
                      capture_output=True, text=True, timeout=30)
        r = subprocess.run([binfile], capture_output=True, text=True, timeout=30)
        results = {}
        for line in r.stdout.strip().split('\n'):
            if '\t' in line:
                label, val = line.split('\t', 1)
                results[label] = val
        return results
    except Exception as e:
        print(f"  [C error] {e}")
        return {}
    finally:
        subprocess.run(['rm', '-rf', tmpdir])


def run_rust(db_path):
    """Create and run a Rust test binary."""
    case_entries = '\n'.join(
        f'        ("{l}", "{ip}"),' for l, ip, _ in TEST_CASES
    )
    # Build Rust source with template substitution to avoid format-string conflicts
    rs_src = '''
fn main() {{
    let searcher = qzdb_reader::from_file("{dbpath}");
    let cases: [(&str, &str); {n}] = [
{cases}
    ];
    for &(label, ip) in &cases {{
        let r = searcher.find_str(ip);
        if r.is_empty() {{
            println!("{{}}\\t(empty)", label);
        }} else {{
            println!("{{}}\\t{{}}", label, r);
        }}
    }}
}}
'''
    code = rs_src.replace('{dbpath}', db_path)
    code = code.replace('{n}', str(len(TEST_CASES)))
    code = code.replace('{cases}', case_entries)
    # Fix double-braces: {{ -> { and }} -> }
    code = code.replace('{{', '{').replace('}}', '}')
    
    tmpdir = tempfile.mkdtemp()
    cargo_dir = os.path.join(tmpdir, 'test_edge')
    os.makedirs(os.path.join(cargo_dir, 'src'))
    with open(os.path.join(cargo_dir, 'Cargo.toml'), 'w') as f:
        f.write('[package]\nname = "test_edge"\nversion = "0.1.0"\nedition = "2021"\n')
        f.write(f'[dependencies]\nqzdb_reader = {{ path = "{os.path.join(SRC_DIR, "rust")}" }}\n')
    with open(os.path.join(cargo_dir, 'src', 'main.rs'), 'w') as f:
        f.write(code)
    
    try:
        r = subprocess.run(['cargo', 'run', '--release', '--manifest-path', 
                          os.path.join(cargo_dir, 'Cargo.toml')],
                         capture_output=True, text=True, timeout=120)
        results = {}
        for line in r.stdout.strip().split('\n'):
            if '\t' in line:
                label, val = line.split('\t', 1)
                results[label] = val
        if r.stderr:
            for line in r.stderr.strip().split('\n'):
                if 'error' in line.lower():
                    print(f"  [Rust stderr] {line}")
        return results
    except subprocess.TimeoutExpired:
        print("  [Rust timeout]")
        return {}
    except Exception as e:
        print(f"  [Rust error] {e}")
        return {}
    finally:
        subprocess.run(['rm', '-rf', tmpdir])


def compare(db_key, label, results_by_lang):
    """Compare one test case across all languages."""
    ref_results = {}
    for lang, r in results_by_lang.items():
        val = r.get(label)
        if val is not None:
            ref_results[lang] = val
    
    if not ref_results:
        return None
    
    # Find reference (Python preferred)
    ref_lang = 'python' if 'python' in ref_results else list(ref_results.keys())[0]
    ref_val = ref_results[ref_lang]
    
    mismatches = []
    for lang, val in ref_results.items():
        if val != ref_val:
            mismatches.append(f"{lang}={val}")
    
    return {
        'reference': f"{ref_lang}={ref_val}",
        'mismatches': mismatches,
        'all_match': len(mismatches) == 0,
        'results': ref_results,
    }


def main():
    import argparse
    parser = argparse.ArgumentParser()
    parser.add_argument('--db', default='std_china', choices=['std_china', 'max_global'])
    parser.add_argument('--langs', nargs='+', default=['python', 'go', 'c', 'rust'])
    args = parser.parse_args()
    
    db_map = {'std_china': 'qqzeng_ip_std_china.qzdb', 'max_global': 'qqzeng_ip_max_global.qzdb'}
    db_path = os.path.join(DATA_DIR, db_map[args.db])
    if not os.path.exists(db_path):
        print(f"DB not found: {db_path}")
        return
    
    print(f"\n{'='*70}")
    print(f"Cross-language edge case test: {args.db}")
    print(f"Languages: {', '.join(args.langs)}")
    print(f"{'='*70}")
    
    runners = {
        'python': run_python,
        'go': run_go,
        'c': run_c,
        'rust': run_rust,
    }
    
    results_by_lang = {}
    for lang in args.langs:
        runner = runners.get(lang)
        if not runner:
            print(f"  No runner for {lang}")
            continue
        print(f"\n  Running {lang}...")
        results_by_lang[lang] = runner(db_path)
        print(f"    Got {len(results_by_lang[lang])} results")
    
    # Compare
    all_labels = set()
    for r in results_by_lang.values():
        all_labels.update(r.keys())
    
    # Results grouped by category
    categories = {
        'V4 valid': [(l, ip, v) for l, ip, v in TEST_CASES if l.startswith('V4-') and v and not l.startswith('V4-leading')],
        'V4 leading zero': [(l, ip, v) for l, ip, v in TEST_CASES if l.startswith('V4-leading')],
        'V4 invalid': [(l, ip, v) for l, ip, v in TEST_CASES if l.startswith('V4-') and not v and not l.startswith('V4-leading')],
        'V6 valid': [(l, ip, v) for l, ip, v in TEST_CASES if l.startswith('V6-') and v],
        'V6 invalid': [(l, ip, v) for l, ip, v in TEST_CASES if l.startswith('V6-') and not v],
    }
    
    total_mismatches = 0
    total_issues = 0
    
    for cat_name, cases in categories.items():
        cat_mismatches = 0
        cat_issues = 0
        
        for label, ip, is_valid in cases:
            cmp = compare(args.db, label, results_by_lang)
            if cmp is None:
                continue
            
            if not cmp['all_match']:
                cat_mismatches += 1
                total_mismatches += 1
        
        # Also check: valid IPs should all have same empty/non-empty status
        for label, ip, is_valid in cases:
            vals = {}
            for lang, r in results_by_lang.items():
                v = r.get(label, "N/A")
                vals[lang] = v
            
            # Are empty status consistent?
            empty_statuses = set()
            for lang, v in vals.items():
                empty_statuses.add(v == "(empty)")
            
            if len(empty_statuses) > 1:
                cat_issues += 1
                total_issues += 1
                detail = '  '.join(f'{l}={v}' for l, v in vals.items())
                print(f"\n  ⚠ [{cat_name}] {label} ({ip}): EMPTY STATUS INCONSISTENT!")
                print(f"    {detail}")
        
        if cat_mismatches == 0 and cat_issues == 0:
            print(f"  [{cat_name}] {len(cases)} cases: all consistent ✓")
    
    # Key cross-consistency: embedded IPv4 must match plain V4
    print(f"\n{'─'*70}")
    print("Embedded IPv4 consistency (::ffff:x.x.x.x MUST == x.x.x.x):")
    embed_issues = 0
    for label, ip, _ in TEST_CASES:
        if ip.startswith("::ffff:") and not ip.startswith("::ffff:0:"):
            v4_ip = ip.split("::ffff:")[1]
            v4_label = None
            for l2, ip2, _ in TEST_CASES:
                if ip2 == v4_ip:
                    v4_label = l2
                    break
            if v4_label:
                vals = {}
                for lang, r in results_by_lang.items():
                    v4_v = r.get(v4_label, "N/A")
                    emb_v = r.get(label, "N/A")
                    if v4_v != emb_v:
                        embed_issues += 1
                        vals[lang] = f"V4={v4_v} emb={emb_v}"
                if vals:
                    for lang, v in vals.items():
                        print(f"  ✗ {ip}: {v}")
    
    if embed_issues == 0:
        print("  ✓ All ::ffff:x.x.x.x match plain x.x.x.x")
    
    print(f"\n{'='*70}")
    print(f"SUMMARY: {total_mismatches} mismatches, {total_issues} consistency issues")
    print(f"{'='*70}")
    
    # Print all results for manual inspection
    print(f"\n{'─'*70}")
    print("Full results matrix:")
    print(f"{'─'*70}")
    for label, ip, _ in TEST_CASES:
        parts = [f"{label:35s}  ({ip:30s})"]
        for lang in args.langs:
            r = results_by_lang.get(lang, {}).get(label, "?")
            parts.append(f"  {lang[:3]}={r}")
        print(''.join(parts))


if __name__ == '__main__':
    main()
