"""
Extreme edge-case testing for qzdb IP SDKs across all languages.
Tests IP parsing edge cases that are likely to find inconsistencies.
"""
import sys, os, subprocess, tempfile, json

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'python'))
from qzdb import QzdbSearcher

DATA = os.path.join(os.path.dirname(__file__), '..', 'data')

# ============================================================
# Edge case IP categories
# ============================================================

# V4: These MUST be syntactically valid and parse to the same internal uint32
V4_VALID = [
    # Normal IPs
    "114.114.114.114",
    "223.5.5.5",
    "8.8.8.8",
    "1.1.1.1",
    "0.0.0.0",
    "255.255.255.255",

    # Private ranges (should return data on global DB)
    "10.0.0.1",
    "10.255.255.255",
    "172.16.0.1",
    "172.31.255.255",
    "192.168.1.1",
    "192.168.0.0",

    # Special purpose
    "127.0.0.1",
    "127.255.255.255",
    "169.254.1.1",
    "169.254.255.255",

    # Documentation / Test
    "192.0.2.1",
    "198.51.100.1",
    "203.0.113.1",

    # Multicast
    "224.0.0.1",
    "239.255.255.255",

    # Reserved
    "240.0.0.1",
    "250.5.5.5",
    "255.0.0.0",

    # Boundary
    "0.0.0.1",
    "255.255.255.254",
    "128.0.0.0",
    "191.255.255.255",
    "192.0.0.0",
    "223.255.255.255",
]

# V4: These must be REJECTED (return None/empty)
V4_INVALID = [
    # Missing octets
    "",
    "1",
    "1.2",
    "1.2.3",

    # Extra octets
    "1.2.3.4.5",
    "1.2.3.4.5.6",

    # Double dots
    "114..114.114.114",
    "1..2.3.4",

    # Leading/trailing dots
    ".114.114.114.114",
    "114.114.114.114.",

    # Overflow
    "256.1.2.3",
    "1.256.2.3",
    "1.2.256.3",
    "1.2.3.256",
    "999.999.999.999",

    # Negative
    "-1.2.3.4",
    "1.-2.3.4",
    "1.2.3.-4",

    # Plus
    "+1.2.3.4",
    "1.+2.3.4",

    # Hex (not standard dotted-decimal)
    "0x72.0x72.0x72.0x72",
    "0x7f.0x00.0x00.0x01",

    # Octal-like (not standard)
    "0162.0162.0162.0162",

    # Non-decimal characters
    "1.2.3.a",
    "a.b.c.d",
    "1.2.3.4f",

    # Whitespace
    " 114.114.114.114",
    "114.114.114.114 ",
    " 114.114.114.114 ",
    "\t114.114.114.114",
    "114.114.114.114\n",

    # IP with port
    "127.0.0.1:8080",

    # Unicode/fullwidth
    "１１４．１１４．１１４．１１４",  # fullwidth digits

    # Empty octets
    "1.2.3.",
    ".1.2.3",
    "1..2.3",
]

# V6: These MUST be valid
V6_VALID = [
    # Loopback
    "::1",
    "0:0:0:0:0:0:0:1",

    # Unspecified
    "::",
    "0:0:0:0:0:0:0:0",

    # Google DNS V6
    "2001:4860:4860::8888",
    "2001:4860:4860:0:0:0:0:8888",

    # China Unicom V6
    "2408:8000:9000::1",

    # Cloudflare DNS
    "2606:4700:4700::1111",
    "2606:4700:4700::1001",

    # Link-local
    "fe80::1",
    "fe80::224:68ff:fedb:2e83",

    # Documentation
    "2001:db8::1",
    "2001:db8:0:0:0:0:0:1",

    # 6to4
    "2002::1",

    # Unique local
    "fd00::1",
    "fc00::1",

    # Multicast
    "ff02::1",

    # Mixed case
    "2001:DB8::1",
    "2001:4860:4860::8888",

    # Embedded IPv4 — mapping (::ffff:x.x.x.x)
    "::ffff:114.114.114.114",
    "::ffff:8.8.8.8",
    "::ffff:192.168.1.1",

    # Embedded IPv4 — translated (::ffff:0:x.x.x.x)
    "::ffff:0:114.114.114.114",
    "::ffff:0:8.8.8.8",
]

# V6: These must be REJECTED
V6_INVALID = [
    # Wrong
    "",
    ":",
    ":::",
    "1:::1",

    # Too many groups
    "1:2:3:4:5:6:7:8:9",

    # Double compression
    "2001::1::",

    # Non-hex
    "2001:gggg::1",
    "2001:zzzz::1",

    # IPv4-mapped with invalid IPv4
    "::ffff:256.256.256.256",
    "::ffff:1.2.3.4.5",

    # Whitespace
    " 2001:db8::1",
    "2001:db8::1 ",
    "\t2001:db8::1\n",

    # With zone ID (depends on parser — most reject)
    "fe80::1%eth0",
    "fe80::1%25eth0",

    # Bracketed (URL-style) — most reject in pure IP context
    "[::1]",
    "[2001:db8::1]",

    # With port
    "[::1]:80",
    "[2001:db8::1]:443",
]

def test_db(db_name, db_path, label):
    """Test a single database against all edge cases."""
    if not os.path.exists(db_path):
        return None
    searcher = QzdbSearcher(db_path)
    
    results = {}
    
    # Test V4 valid
    for ip in V4_VALID:
        r = searcher.find(ip)
        results[ip] = r.to_pipe() if r else "(empty)"
    
    # Test V4 invalid
    for ip in V4_INVALID:
        r = searcher.find(ip)
        results[ip] = "(empty)" if not r else r.to_pipe()  # must be empty
    
    # Test V6 valid
    for ip in V6_VALID:
        r = searcher.find(ip)
        results[ip] = r.to_pipe() if r else "(empty)"
    
    # Test V6 invalid  
    for ip in V6_INVALID:
        r = searcher.find(ip)
        results[ip] = "(empty)" if not r else r.to_pipe()  # must be empty
    
    return results


def print_category(name, results, ips, expected_nonempty):
    """Print category results with PASS/FAIL markers."""
    issues = []
    for ip in ips:
        result = results.get(ip, "NOT TESTED")
        should_be_empty = ip not in expected_nonempty
        is_empty = (result == "(empty)")
        
        if should_be_empty and not is_empty:
            issues.append(f"  ⚠ SHOULD BE EMPTY: {ip:40s} => {result}")
        elif not should_be_empty and is_empty:
            issues.append(f"  ⚠ SHOULD HAVE DATA: {ip:40s} => {result}")
    
    if issues:
        print(f"\n  [{name}] ISSUES ({len(issues)}):")
        for i in issues:
            print(i)
    else:
        print(f"\n  [{name}] All {len(ips)} cases OK")


def compare_results(name, results_by_lang, ips, ref_lang="python"):
    """Compare all languages against reference."""
    ref = results_by_lang.get(ref_lang, {})
    issues = []
    for ip in ips:
        ref_val = ref.get(ip, None)
        if ref_val is None:
            continue
        for lang, results in results_by_lang.items():
            if lang == ref_lang:
                continue
            val = results.get(ip, "N/A")
            if val != ref_val:
                issues.append(f"  MISMATCH: {ip:40s} {ref_lang}={ref_val:30s} {lang}={val}")
    
    if issues:
        print(f"\n  [{name}] CROSS-LANG MISMATCHES ({len(issues)}):")
        for i in issues[:20]:  # limit output
            print(i)
        if len(issues) > 20:
            print(f"  ... and {len(issues) - 20} more")
    else:
        print(f"\n  [{name}] All languages match ✓")


def main():
    dbs = {
        "std_china": ("qqzeng_ip_std_china.qzdb", "China-only (std)"),
        "max_china": ("qqzeng_ip_max_china.qzdb", "China-only (max)"),
        "max_global": ("qqzeng_ip_max_global.qzdb", "Global (max)"),
        "ult_china": ("qqzeng_ip_ult_china.qzdb", "China-only (ult)"),
    }
    
    all_results = {}
    
    for db_key, (fname, label) in dbs.items():
        db_path = os.path.join(DATA, fname)
        if not os.path.exists(db_path):
            print(f"SKIP {db_key}: {fname} not found")
            continue
        print(f"\n{'='*70}")
        print(f"Testing {label} ({fname})")
        print(f"{'='*70}")
        
        results = test_db(db_key, db_path, label)
        all_results[db_key] = results
        
        # V4 valid: most should have data on china DBs
        # On china DB, only Chinese IPs have data; foreign IPs are empty
        # On global DB, almost all have data
        if "china" in db_key:
            china_v4_ips = [
                "114.114.114.114", "223.5.5.5",
                "10.0.0.1", "10.255.255.255",
                "172.16.0.1", "172.31.255.255",
                "192.168.1.1", "192.168.0.0",
                "127.0.0.1", "127.255.255.255",
            ]
            foreign_v4_ips = [
                "8.8.8.8", "1.1.1.1",
                "169.254.1.1", "169.254.255.255",
                "224.0.0.1", "239.255.255.255",
                "240.0.0.1", "250.5.5.5",
            ]
            print_category("China V4 (should have data)", results, china_v4_ips, china_v4_ips)
            print_category("China V4 foreign (should be empty)", results, foreign_v4_ips, [])
        else:
            print_category("Global V4 valid", results, V4_VALID, V4_VALID)
        
        # V4 invalid — all should be empty
        print_category("V4 invalid (should be empty)", results, V4_INVALID, [])
        
        # V6 valid
        if "china" in db_key:
            china_v6_ips = ["2408:8000:9000::1"]
            foreign_v6_ips = [
                "::1", "::", "2001:4860:4860::8888",
                "2606:4700:4700::1111", "2001:db8::1",
                "fe80::1", "ff02::1",
                "2002::1", "fd00::1",
            ]
            print_category("China V6 (should have data)", results, china_v6_ips, china_v6_ips)
            print_category("China V6 foreign (should be empty)", results, foreign_v6_ips, [])
            
            # Embedded IPv4 on china DB — the V4-resolved IP should match
            print_category("China embedded IPv4 vs plain V4", results, 
                ["::ffff:114.114.114.114", "::ffff:8.8.8.8", "::ffff:0:114.114.114.114", "::ffff:0:8.8.8.8"],
                all_results.get("max_global", {}).get("::ffff:114.114.114.114", None) and 
                ["::ffff:114.114.114.114"] or [])
        else:
            print_category("Global V6 valid", results, V6_VALID, V6_VALID)
        
        # V6 invalid — all should be empty
        print_category("V6 invalid (should be empty)", results, V6_INVALID, [])
    
    # Cross-DB consistency: embedded IPv4 vs plain V4
    print(f"\n{'='*70}")
    print("Cross-reference: embedded IPv4 vs plain V4")
    print(f"{'='*70}")
    
    for db_key in dbs:
        r = all_results.get(db_key, {})
        v4 = r.get("114.114.114.114", "N/A")
        emb = r.get("::ffff:114.114.114.114", "N/A")
        emb_t = r.get("::ffff:0:114.114.114.114", "N/A")
        match = "✓" if v4 == emb else "✗ MISMATCH"
        match_t = "✓" if v4 == emb_t else ("✗ (expected diff)" if v4 != emb_t else "✓")
        print(f"  {db_key:12s}  plain={v4:40s}  mapped={emb:40s}  {match}")
        print(f"  {'':12s}  plain={v4:40s}  transl={emb_t:40s}  {match_t}")


    # 🔑 Key cross-consistency: embedded IPv4 should match plain V4 across ALL DBs
    print(f"\n{'='*70}")
    print("EMBEDDED IPv4 INTEGRITY CHECK (::ffff:x.x.x.x must == x.x.x.x)")
    print(f"{'='*70}")
    embed_issues = []
    for db_key in dbs:
        r = all_results.get(db_key, {})
        for v4_ip in V4_VALID:
            v4_res = r.get(v4_ip)
            embedded = f"::ffff:{v4_ip}"
            emb_res = r.get(embedded)
            if v4_res is not None and emb_res is not None and v4_res != emb_res:
                embed_issues.append(f"  {db_key:12s} {v4_ip:20s} V4={v4_res:40s} ::ffff:={emb_res}")
    if embed_issues:
        for i in embed_issues:
            print(i)
    else:
        print("  ALL EMBEDDED IPv4 MATCH PLAIN V4 ✓")

    return all_results


if __name__ == '__main__':
    results = main()
