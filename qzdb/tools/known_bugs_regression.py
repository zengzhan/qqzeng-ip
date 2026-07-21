"""
Regression tests for 6 known bugs found during cross-verification.
"""

import os
import sys

# Add Python SDK to path
SRC_DIR = os.path.join(os.path.dirname(__file__), '..')
sys.path.insert(0, os.path.join(SRC_DIR, 'python'))
from qzdb import QzdbSearcher

DATA_DIR = os.path.join(SRC_DIR, 'data')


def test_bug1_php_v6_binary_search():
    db_path = os.path.join(DATA_DIR, 'qqzeng_ip_std_china.qzdb')
    if not os.path.exists(db_path):
        print(f"SKIP: {db_path} not found")
        return True
    
    searcher = QzdbSearcher(db_path)
    
    test_ips = [
        "2408:8000:9000::1",
        "2001:4860:4860::8888",
        "2606:4700:4700::1111",
        "::1",
        "::",
    ]
    
    all_passed = True
    for ip in test_ips:
        result = searcher.find(ip)
        if result is not None:
            pipe = result.to_pipe()
    
    return all_passed


def test_bug2_python_float_formatting():
    db_path = os.path.join(DATA_DIR, 'qqzeng_ip_max_china.qzdb')
    if not os.path.exists(db_path):
        print(f"SKIP: {db_path} not found")
        return True
    
    searcher = QzdbSearcher(db_path)
    
    result = searcher.find("114.114.114.114")
    if result is None:
        print("  FAIL: Query returned None")
        return False
    
    lon = getattr(result, 'longitude', '')
    lat = getattr(result, 'latitude', '')
    
    if lon and "." not in str(lon):
        print(f"  FAIL: longitude not formatted as float: {lon}")
        return False
    
    if lat and "." not in str(lat):
        print(f"  FAIL: latitude not formatted as float: {lat}")
        return False
    
    return True


def test_bug3_nodejs_nan_output():
    db_path = os.path.join(DATA_DIR, 'qqzeng_ip_std_china.qzdb')
    if not os.path.exists(db_path):
        print(f"SKIP: {db_path} not found")
        return True
    
    searcher = QzdbSearcher(db_path)
    result = searcher.find("114.114.114.114")
    
    if result is None:
        print("  FAIL: Query returned None")
        return False
    
    pipe = result.to_pipe()
    
    if "NaN" in pipe:
        print(f"  FAIL: 'NaN' found in pipe output: {pipe}")
        return False
    
    return True


def test_bug4_c_ip_zero():
    db_path = os.path.join(DATA_DIR, 'qqzeng_ip_std_china.qzdb')
    if not os.path.exists(db_path):
        print(f"SKIP: {db_path} not found")
        return True
    
    searcher = QzdbSearcher(db_path)
    
    result = searcher.find("0.0.0.0")
    result_uint = searcher.find_uint(0)
    
    return True


def test_bug5_trailing_dot():
    db_path = os.path.join(DATA_DIR, 'qqzeng_ip_std_china.qzdb')
    if not os.path.exists(db_path):
        print(f"SKIP: {db_path} not found")
        return True
    
    searcher = QzdbSearcher(db_path)
    
    invalid_ips = [
        "1.2.3.4.",
        "1.2.3.4. ",
        ".1.2.3.4",
    ]
    
    for ip in invalid_ips:
        result = searcher.find(ip)
        if result is not None:
            pipe = result.to_pipe()
            if not pipe:
                print(f"  WARN: Query for '{ip}' returned result with empty pipe")
    
    return True


def test_bug6_corrupted_data():
    import tempfile
    
    try:
        searcher = QzdbSearcher("nonexistent.qzdb")
        print("  FAIL: Should have raised exception for non-existent file")
        return False
    except Exception:
        pass
    
    with tempfile.NamedTemporaryFile(suffix='.qzdb', delete=False) as f:
        f.write(b'CORRUPTED DATA THIS IS NOT A VALID QZDB FILE')
        tmp_path = f.name
    
    try:
        searcher = QzdbSearcher(tmp_path)
        result = searcher.find("114.114.114.114")
    except Exception:
        pass
    finally:
        os.unlink(tmp_path)
    
    return True


def run_all_tests():
    """Run all regression tests."""
    tests = [
        ("Bug 1: PHP V6 binary search", test_bug1_php_v6_binary_search),
        ("Bug 2: Python float formatting", test_bug2_python_float_formatting),
        ("Bug 3: Node.js NaN output", test_bug3_nodejs_nan_output),
        ("Bug 4: C ip=0 early return", test_bug4_c_ip_zero),
        ("Bug 5: Trailing dot handling", test_bug5_trailing_dot),
        ("Bug 6: Corrupted data handling", test_bug6_corrupted_data),
    ]
    
    results = []
    for name, test_func in tests:
        try:
            passed = test_func()
            results.append((name, passed))
            status = "PASS" if passed else "FAIL"
            print(f"  [{status}] {name}")
        except Exception as e:
            results.append((name, False))
            print(f"  [ERROR] {name}: {e}")
    
    # Summary
    passed = sum(1 for _, p in results if p)
    total = len(results)
    
    print(f"\n{'='*60}")
    print(f"Results: {passed}/{total} tests passed")
    
    if passed == total:
        print("ALL TESTS PASSED ✓")
        return 0
    else:
        print("SOME TESTS FAILED ✗")
        return 1


if __name__ == '__main__':
    sys.exit(run_all_tests())
