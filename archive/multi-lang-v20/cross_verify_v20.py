#!/usr/bin/env python3
"""
Cross-validate V20 SDK implementations across all 8 languages.
Tests each language SDK against V20 QZDB files using Python reference as baseline.

Usage:
    python3 cross_verify_v20.py [--versions std,ult,asn,max] [--region china,global] [--count 1000] [--langs all]

All language SDKs must be compiled/built before running this script.
"""

import argparse, csv, io, json, os, random, shutil, struct, subprocess, sys, tempfile, zipfile
from pathlib import Path

BASE_DIR = Path('/Users/zengxiangzhan/ZengData/发行版/2026-07')
MULTI_LANG = Path(__file__).resolve().parent.parent
PYTHON_DIR = MULTI_LANG / 'python'
NODEJS_DIR = MULTI_LANG / 'nodejs'
PHP_DIR = MULTI_LANG / 'php'
GO_DIR = MULTI_LANG / 'go'
RUST_DIR = MULTI_LANG / 'rust'
C_DIR = MULTI_LANG / 'c'
JAVA_DIR = MULTI_LANG / 'java'
CSHARP_DIR = MULTI_LANG / 'csharp' if (MULTI_LANG / 'csharp').exists() else MULTI_LANG / 'netcore'

ALL_VERSIONS = ['std', 'ult', 'asn', 'max']
ALL_REGIONS = ['china', 'global']

sys.path.insert(0, str(PYTHON_DIR))
from qzdb_v20 import QzdbSearcher as QzdbV20


def get_v20_file(ver, region):
    """Get path to V20 QZDB file."""
    return BASE_DIR / f'qqzeng_ip_{ver}' / f'qqzeng_ip_{ver}_{region}_v20.qzdb'


def get_csv_reference(ver, region, max_entries=1000):
    """Load CSV range data as reference. Returns {ip: pipe_string} dict."""
    geo_map = {}
    zip_path = BASE_DIR / f'qqzeng_ip_{ver}' / f'qqzeng_ip_{ver}_{region}_range.zip'
    if not zip_path.exists():
        print(f'  [WARN] CSV zip not found: {zip_path}')
        return geo_map

    with zipfile.ZipFile(str(zip_path)) as zf:
        for name in zf.namelist():
            if not name.endswith('.csv'):
                continue
            content = zf.read(name).decode('utf-8-sig')
            reader = csv.reader(io.StringIO(content))
            try:
                headers = next(reader)
            except StopIteration:
                continue
            geo_fields = headers[4:]
            for row in reader:
                ip = row[0].strip()
                csv_geo = row[4:4 + len(geo_fields)]
                geo_map[ip] = '|'.join(csv_geo)
                if len(geo_map) >= max_entries:
                    break
            break  # only first CSV
    return geo_map


def get_field_names(ver, region):
    """Read dynamic field names from V20 metadata."""
    db_path = get_v20_file(ver, region)
    if not db_path.exists():
        return []
    try:
        s = QzdbV20(str(db_path))
        return s.field_names
    except Exception as e:
        print(f'  [WARN] Could not read field names: {e}')
        return []


# ── Language test functions ──

def test_python(ver, region, ip_list):
    """Test Python V20 reference."""
    db_path = get_v20_file(ver, region)
    s = QzdbV20(str(db_path))
    results = {}
    for ip in ip_list:
        try:
            r = s.find(ip)
            results[ip] = r.to_pipe() if r else ''
        except Exception:
            results[ip] = ''
    return results


def test_nodejs(ver, region, ip_list):
    """Test Node.js V20 SDK."""
    db_path = get_v20_file(ver, region)
    script = f'''
const Q = require("{NODEJS_DIR}/qzdb_v20.js");
const s = new Q.QzdbSearcherV20("{db_path}");
const ips = {json.dumps(ip_list)};
for (const ip of ips) {{
    process.stdout.write(s.findStr(ip) + "\\n");
}}
'''
    runner = shutil.which('bun') or shutil.which('node')
    if not runner:
        print(f'  [WARN] No Bun/Node.js found')
        return {}

    try:
        # Bun's -e flag has issues with inline require; use temp file
        tmp = tempfile.NamedTemporaryFile(suffix='.js', mode='w', delete=False)
        tmp.write(script)
        tmp.close()
        r = subprocess.run([runner, tmp.name], capture_output=True, text=True, timeout=30)
        os.unlink(tmp.name)
        lines = r.stdout.strip().split('\n')
        results = {}
        for ip, line in zip(ip_list, lines):
            results[ip] = line.strip()
        return results
    except subprocess.TimeoutExpired:
        print(f'  [WARN] Node.js timeout')
        return {}
    except Exception as e:
        print(f'  [WARN] Node.js error: {e}')
        return {}


def test_php(ver, region, ip_list):
    """Test PHP V20 SDK."""
    db_path = get_v20_file(ver, region)
    script = f'''
require "{PHP_DIR}/QzdbSearcherV20.php";
use Qqzeng\\Ip\\QzdbSearcherV20;
$s = QzdbSearcherV20::getInstance("{db_path}");
$ips = {json.dumps(ip_list)};
foreach ($ips as $ip) {{
    echo $s->findStr($ip) . "\\n";
}}
'''
    try:
        r = subprocess.run(['php', '-r', script], capture_output=True, text=True, timeout=120)
        lines = r.stdout.strip().split('\n')
        results = {}
        for ip, line in zip(ip_list, lines):
            results[ip] = line.strip()
        return results
    except subprocess.TimeoutExpired:
        print(f'  [WARN] PHP timeout')
        return {}
    except Exception as e:
        print(f'  [WARN] PHP error: {e}')
        return {}


def _compile_go_test():
    """Compile Go V20 test helper if needed."""
    go_src_dir = GO_DIR / 'test_v20_helper'
    go_src = go_src_dir / 'main.go'
    go_bin = GO_DIR / 'v20_test_helper'
    go_src_dir.mkdir(parents=True, exist_ok=True)
    if not go_src.exists():
        go_src.write_text('''package main

import (
    "fmt"
    "os"
    "qzdb_searcher/qzdb"
)

func main() {
    if len(os.Args) < 3 {
        fmt.Fprintln(os.Stderr, "Usage: test_v20_helper <db_path> <ip1> [ip2 ...]")
        os.Exit(1)
    }
    dbPath := os.Args[1]
    ips := os.Args[2:]
    s, err := qzdb.NewSearcherV20(dbPath, 0)
    if err != nil {
        fmt.Fprintln(os.Stderr, "Failed to load DB:", err)
        os.Exit(1)
    }
    for _, ip := range ips {
        fmt.Println(s.LookupStr(ip))
    }
}
''')
    r = subprocess.run(['go', 'build', '-o', str(go_bin), '.'],
                       capture_output=True, text=True, timeout=60, cwd=str(go_src_dir))
    if r.returncode == 0:
        return str(go_bin)
    print(f'  [WARN] Go compile failed: {r.stderr[:200]}')
    return None


_GO_BINARY = None

def test_go(ver, region, ip_list):
    """Test Go V20 SDK."""
    global _GO_BINARY
    if _GO_BINARY is None:
        _GO_BINARY = _compile_go_test()
    if not _GO_BINARY:
        return {}
    db_path = get_v20_file(ver, region)
    results = {}
    for i in range(0, len(ip_list), 100):
        batch = ip_list[i:i + 100]
        try:
            r = subprocess.run([_GO_BINARY, str(db_path)] + batch,
                               capture_output=True, text=True, timeout=30)
            lines = r.stdout.strip().split('\n')
            for ip, line in zip(batch, lines):
                results[ip] = line.strip()
        except Exception as e:
            print(f'  [WARN] Go batch error: {e}')
            for ip in batch:
                results[ip] = ''
    return results


def test_csharp(ver, region, ip_list):
    """Test C# V20 SDK via dotnet run."""
    db_path = get_v20_file(ver, region)
    try:
        # Write a temp C# program that outputs results as pipe-separated lines
        script = f'''using System;
using System.IO;
class Program {{
    static void Main() {{
        var s = Qqzeng.QzdbSearcherV20.Instance;
        s.Load(@"{db_path}");
        var ips = new[] {{ {','.join(f'@"{ip}"' for ip in ip_list)} }};
        foreach (var ip in ips) {{
            Console.WriteLine(s.FindStr(ip));
        }}
    }}
}}
'''
        tmpdir = tempfile.mkdtemp()
        proj_path = Path(tmpdir) / 'test_v20.csproj'
        prog_path = Path(tmpdir) / 'Program.cs'
        dll_path = Path(tmpdir) / 'QzdbSearcherV20.cs'

        import shutil
        shutil.copy2(str(CSHARP_DIR / 'QzdbSearcherV20.cs'), str(dll_path))

        with open(proj_path, 'w') as f:
            f.write('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>')
        with open(prog_path, 'w') as f:
            f.write(script)

        r = subprocess.run(['dotnet', 'run', '--project', str(proj_path)],
                           capture_output=True, text=True, timeout=120, cwd=tmpdir)
        import shutil
        shutil.rmtree(tmpdir, ignore_errors=True)

        lines = r.stdout.strip().split('\n')
        results = {}
        for ip, line in zip(ip_list, lines):
            results[ip] = line.strip()
        return results
    except subprocess.TimeoutExpired:
        print(f'  [WARN] C# timeout')
        return {}
    except Exception as e:
        print(f'  [WARN] C# error: {e}')
        return {}


# ── Pre-compiled helpers ──

def _compile_c_test():
    """Compile C V20 test helper (main_v20_test) if needed."""
    c_test = C_DIR / 'main_v20_test'
    srcs = [C_DIR / 'qzdb_searcher_v20.c', C_DIR / 'main_v20_test.c']
    if c_test.exists() and all(s.stat().st_mtime < c_test.stat().st_mtime for s in srcs):
        return str(c_test)
    r = subprocess.run(['gcc', '-O2', '-o', str(c_test),
                        str(C_DIR / 'qzdb_searcher_v20.c'),
                        str(C_DIR / 'main_v20_test.c'),
                        '-I', str(C_DIR)], capture_output=True, text=True, timeout=30)
    if r.returncode == 0:
        return str(c_test)
    print(f'  [WARN] C compile failed: {r.stderr[:200]}')
    return None


_C_BINARY = None

def test_c(ver, region, ip_list):
    """Test C V20 SDK."""
    global _C_BINARY
    if _C_BINARY is None:
        _C_BINARY = _compile_c_test()
    if not _C_BINARY:
        return {}
    db_path = get_v20_file(ver, region)
    results = {}
    # Test in batches to avoid command line length issues
    for i in range(0, len(ip_list), 100):
        batch = ip_list[i:i + 100]
        try:
            r = subprocess.run([_C_BINARY, str(db_path)] + batch,
                               capture_output=True, text=True, timeout=30)
            lines = r.stdout.strip().split('\n')
            for ip, line in zip(batch, lines):
                results[ip] = line.strip()
        except Exception as e:
            print(f'  [WARN] C batch error: {e}')
            for ip in batch:
                results[ip] = ''
    return results


def _find_rust_test():
    """Return path to pre-compiled Rust V20 test binary."""
    candidates = [
        RUST_DIR / 'target' / 'release' / 'test_v20',
        RUST_DIR / 'target' / 'debug' / 'test_v20',
    ]
    for p in candidates:
        if p.exists():
            return str(p)
    return None


_RUST_BINARY = None

def test_rust(ver, region, ip_list):
    """Test Rust V20 SDK."""
    global _RUST_BINARY
    if _RUST_BINARY is None:
        _RUST_BINARY = _find_rust_test()
    if not _RUST_BINARY:
        return {}
    db_path = get_v20_file(ver, region)
    results = {}
    # Test in batches to avoid command line length issues
    for i in range(0, len(ip_list), 100):
        batch = ip_list[i:i + 100]
        try:
            r = subprocess.run([_RUST_BINARY, str(db_path)] + batch,
                               capture_output=True, text=True, timeout=30)
            lines = r.stdout.strip().split('\n')
            for ip, line in zip(batch, lines):
                results[ip] = line.strip()
        except Exception as e:
            print(f'  [WARN] Rust batch error: {e}')
            for ip in batch:
                results[ip] = ''
    return results


def test_java(ver, region, ip_list):
    """Test Java V20 SDK."""
    # Check if JDK is actually available (not just Apple stub)
    javac_ok = subprocess.run(['javac', '-version'],
                              capture_output=True, text=True).returncode == 0
    java_ok = subprocess.run(['java', '-version'],
                             capture_output=True, text=True).returncode == 0
    if not javac_ok or not java_ok:
        print('SKIP (no JDK)')
        return {}

    db_path = get_v20_file(ver, region)
    script = f'''
import com.qqzeng.ip.QzdbSearcherV20;
public class TestV20 {{
    public static void main(String[] args) {{
        QzdbSearcherV20 s = QzdbSearcherV20.getInstance();
        try {{
            s.load("{db_path}", 0);
        }} catch (Exception e) {{
            return;
        }}
        for (String ip : args) {{
            System.out.println(s.findStr(ip));
        }}
    }}
}}
'''
    try:
        tmpdir = tempfile.mkdtemp()
        src = Path(tmpdir) / 'TestV20.java'
        src.write_text(script)
        r = subprocess.run(['javac', '-cp', str(JAVA_DIR / 'build'),
                            '-d', tmpdir, str(src)],
                           capture_output=True, text=True, timeout=30)
        if r.returncode != 0:
            print(f'[WARN] Java compile error: {r.stderr[:200]}')
            shutil.rmtree(tmpdir, ignore_errors=True)
            return {}
        results = {}
        for i in range(0, len(ip_list), 100):
            batch = ip_list[i:i + 100]
            r = subprocess.run(['java', '-cp', f'{tmpdir}:{JAVA_DIR}/build',
                                'TestV20'] + batch,
                               capture_output=True, text=True, timeout=30)
            lines = r.stdout.strip().split('\n')
            for ip, line in zip(batch, lines):
                results[ip] = line.strip()
        shutil.rmtree(tmpdir, ignore_errors=True)
        return results
    except Exception as e:
        print(f'  [WARN] Java error: {e}')
        return {}


def compare_results(name, python_results, lang_results, ip_list):
    """Compare language results against Python reference."""
    total = 0
    errors = 0
    error_details = []
    for ip in ip_list:
        expected = python_results.get(ip, '')
        got = lang_results.get(ip, '')
        if expected == '':
            continue  # skip IPs Python couldn't resolve
        total += 1
        if got != expected:
            errors += 1
            if len(error_details) < 5:
                error_details.append(f'    {ip}: expected="{expected[:60]}" got="{got[:60]}"')
    return total, errors, error_details


def main():
    parser = argparse.ArgumentParser(description='Cross-validate V20 SDKs')
    parser.add_argument('--versions', default='all', help='Versions: std,ult,asn,max or all')
    parser.add_argument('--region', default='all', help='Region: china,global or all')
    parser.add_argument('--count', type=int, default=500, help='IPs per database')
    parser.add_argument('--langs', default='all',
                        help='Languages: py,node,php,go,rs,c,java,cs or all')
    args = parser.parse_args()

    versions = ALL_VERSIONS if args.versions == 'all' else args.versions.split(',')
    regions = ALL_REGIONS if args.region == 'all' else args.region.split(',')

    lang_map = {
        'py': ('Python', test_python),
        'node': ('Node.js', test_nodejs),
        'php': ('PHP', test_php),
        'go': ('Go', test_go),
        'cs': ('C#', test_csharp),
        'rs': ('Rust', test_rust),
        'c': ('C', test_c),
        'java': ('Java', test_java),
    }
    if args.langs != 'all':
        lang_map = {k: v for k, v in lang_map.items() if k in args.langs.split(',')}

    print('=' * 70)
    print('  V20 Cross-Language Verification')
    print('=' * 70)

    overall_ok = 0
    overall_errors = 0
    overall_total = 0

    for ver in versions:
        for region in regions:
            db_path = get_v20_file(ver, region)
            if not db_path.exists():
                print(f'\n  [SKIP] {ver}/{region}: QZDB file not found')
                continue

            print(f'\n  --- {ver}/{region} ---')

            # Get field names from Python
            field_names = get_field_names(ver, region)
            print(f'  Fields ({len(field_names)}): {field_names[:5]}...')

            # Load CSV reference
            csv_data = get_csv_reference(ver, region, args.count)
            if not csv_data:
                print(f'  [SKIP] No CSV reference data')
                continue

            ip_list = list(csv_data.keys())
            print(f'  IPs to test: {len(ip_list)}')

            # Python baseline
            python_results = test_python(ver, region, ip_list)

            # Test each language
            for lang_key, (lang_name, test_fn) in lang_map.items():
                if lang_key == 'py':
                    continue  # Python is baseline

                print(f'  Testing {lang_name}...', end=' ', flush=True)
                lang_results = test_fn(ver, region, ip_list)
                if not lang_results:
                    print('SKIP (no results)')
                    continue

                total, errors, details = compare_results(
                    lang_name, python_results, lang_results, ip_list
                )
                overall_total += total
                overall_errors += errors
                if errors == 0:
                    overall_ok += 1
                    print(f'OK ({total}/{total})')
                else:
                    pct = errors / total * 100 if total > 0 else 0
                    print(f'FAIL ({total - errors}/{total}, {pct:.1f}% errors)')
                    for d in details:
                        print(d)

    print(f'\n{"=" * 70}')
    if overall_total > 0:
        total_pct = (overall_total - overall_errors) / overall_total * 100
        print(f'  Overall: {overall_ok} languages OK | {overall_total - overall_errors}/{overall_total} ({total_pct:.2f}%)')
    else:
        print(f'  No tests executed (check file paths)')
    print(f'{"=" * 70}')
    return 0 if overall_errors == 0 else 1


if __name__ == '__main__':
    sys.exit(main())
