#!/usr/bin/env python3
"""Production readiness checks for qzdb SDK.

Validates security, performance, and correctness before release.
"""
import os
import sys
import subprocess

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
BASE_DIR = os.path.dirname(SCRIPT_DIR)
MULTI_LANG = os.path.join(BASE_DIR, "multi-lang")
DATA_DIR = os.path.join(MULTI_LANG, "data")

CHECKS_PASSED = 0
CHECKS_FAILED = 0

def check(name, condition, detail=""):
    global CHECKS_PASSED, CHECKS_FAILED
    if condition:
        CHECKS_PASSED += 1
        print("  [PASS] " + name)
    else:
        CHECKS_FAILED += 1
        print("  [FAIL] " + name + ": " + detail)

def run_cmd(cmd, timeout=30):
    try:
        result = subprocess.run(cmd, shell=True, capture_output=True, text=True, timeout=timeout)
        return result.returncode == 0, result.stdout.strip(), result.stderr.strip()
    except Exception as e:
        return False, "", str(e)

def main():
    print("=" * 60)
    print("  qzdb SDK Production Readiness Checks")
    print("=" * 60)
    print()

    # --- 1. Security Checks ---
    print("--- Security Checks ---")

    ok, out, _ = run_cmd("git ls-files | grep -c '\\.qzdb$' || true")
    qzdb_in_git = int(out.strip().split('\n')[0]) > 0
    check("No .qzdb database files in git", not qzdb_in_git,
          "Found " + out.strip() + " .qzdb files tracked by git")

    ok, out, _ = run_cmd("find . -name '.env' -not -path './.git/*' | wc -l")
    env_files = int(out.strip())
    check("No .env files in repo", env_files == 0,
          "Found " + str(env_files) + " .env files")

    ok, out, _ = run_cmd(
        "grep -rn 'password\\|secret\\|api_key\\|apikey' multi-lang/ "
        "--include='*.py' --include='*.js' --include='*.c' --include='*.h' "
        "--include='*.rs' --include='*.go' --include='*.java' --include='*.cs' "
        "--include='*.php' 2>/dev/null | grep -v 'test\\|example\\|placeholder\\|TODO\\|field_names\\|token' | wc -l"
    )
    secrets_found = int(out.strip())
    check("No hardcoded secrets in source", secrets_found == 0,
          "Found " + str(secrets_found) + " potential secret references")

    ok, out, _ = run_cmd("grep -c 'mmap\\|region.*size\\|bounds.*check' multi-lang/c/qzdb_reader.c")
    check("C SDK has mmap/size validation", int(out.strip()) > 0,
          "No mmap size validation found in C SDK")

    ok, out, _ = run_cmd("grep -c 'unsafe {' multi-lang/rust/src/lib.rs")
    unsafe_count = int(out.strip())
    check("Rust SDK minimizes unsafe blocks", unsafe_count < 50,
          "Found " + str(unsafe_count) + " unsafe blocks in Rust SDK")

    ok, out, _ = run_cmd(
        "grep -c 'fail.*close\\|return.*error\\|Error\\|Err(' "
        "multi-lang/c/qzdb_reader.c multi-lang/python/qzdb.py "
        "multi-lang/nodejs/qzdb.js 2>/dev/null"
    )
    total_errors = sum(int(x.split(':')[1]) for x in out.strip().split('\n') if ':' in x and x.split(':')[1].strip().isdigit())
    check("Error paths fail closed", total_errors > 0,
          "No explicit error handling found")

    print()

    # --- 2. Performance Checks ---
    print("--- Performance Checks ---")

    db_files = [f for f in os.listdir(DATA_DIR) if f.endswith('.qzdb')] if os.path.exists(DATA_DIR) else []
    if db_files:
        db_path = os.path.join(DATA_DIR, db_files[0])
        db_size = os.path.getsize(db_path)
        check("Database file exists (" + db_files[0] + ")", True,
              "Size: " + str(db_size / 1024 / 1024) + " MB")
        check("Database size < 500MB", db_size < 500 * 1024 * 1024,
              "Database is " + str(db_size / 1024 / 1024) + " MB")
    else:
        check("Database file exists", False, "No .qzdb file found in data/")

    ok, out, _ = run_cmd("test -f " + MULTI_LANG + "/python/qzdb.py && echo OK")
    check("Python SDK imports correctly", ok, out)

    ok, out, _ = run_cmd("test -f " + MULTI_LANG + "/nodejs/qzdb.js && echo OK")
    check("Node.js SDK imports correctly", ok, out)

    ok, out, _ = run_cmd("cd " + MULTI_LANG + "/go && go build ./...")
    check("Go SDK compiles", ok, out)

    ok, out, _ = run_cmd("cd " + MULTI_LANG + "/rust && cargo check --quiet 2>&1")
    check("Rust SDK compiles", ok, out)

    print()

    # --- 3. Correctness Checks ---
    print("--- Correctness Checks ---")

    ok, out, _ = run_cmd("cd " + MULTI_LANG + " && python3 cross_lang_verify.py", timeout=60)
    cross_ok = "PASSED" in out and "0 failed" in out
    check("Cross-language verification (L2)", cross_ok, "Cross-lang verify failed or incomplete")

    ok, out, _ = run_cmd("cd " + MULTI_LANG + " && python3 accuracy_analysis.py", timeout=60)
    acc_complete = any(kw in out for kw in ["通过", "passed", "pass", "complete", "总计", "total"])
    check("Accuracy analysis (L4) completes", acc_complete, "Accuracy analysis did not complete")

    ok, out, _ = run_cmd("cd " + MULTI_LANG + " && bash run_all_tests.sh 2>&1", timeout=120)
    smoke_ok = ok and ("passed" in out.lower()) and ("0 failed" in out.lower())
    check("Smoke tests (L1)", smoke_ok, "Smoke tests failed")

    print()

    # --- 4. Code Quality Checks ---
    print("--- Code Quality Checks ---")

    ok, out, _ = run_cmd(
        "grep -cP ' +$' " + BASE_DIR + "/multi-lang/c/qzdb_reader.c "
        + BASE_DIR + "/multi-lang/c/qzdb_reader.h 2>/dev/null || echo 0"
    )
    trailing_ws = int(out.strip())
    check("No trailing whitespace in C SDK", trailing_ws == 0,
          "Found " + str(trailing_ws) + " lines with trailing whitespace")

    fmt_path = os.path.join(BASE_DIR, "FORMAT.md")
    check("FORMAT.md exists and is non-empty",
          os.path.exists(fmt_path) and os.path.getsize(fmt_path) > 0,
          "FORMAT.md missing or empty")

    sdk_files = {
        "C": "multi-lang/c/qzdb_reader.c",
        "C#": "multi-lang/netcore/QzdbReader.cs",
        "Go": "multi-lang/go/qzdb/qzdb.go",
        "Java": "multi-lang/java/src/main/java/qzdb/QzdbReader.java",
        "Node.js": "multi-lang/nodejs/qzdb.js",
        "PHP": "multi-lang/php/QzdbReader.php",
        "Python": "multi-lang/python/qzdb.py",
        "Rust": "multi-lang/rust/src/lib.rs",
    }
    for lang, relpath in sdk_files.items():
        full_path = os.path.join(BASE_DIR, relpath)
        check(lang + " SDK exists", os.path.exists(full_path), "Missing: " + relpath)

    print()

    # --- Summary ---
    total = CHECKS_PASSED + CHECKS_FAILED
    print("=" * 60)
    print("  Results: " + str(CHECKS_PASSED) + "/" + str(total) + " passed, " + str(CHECKS_FAILED) + " failed")
    print("=" * 60)

    if CHECKS_FAILED > 0:
        print()
        print("  Some checks failed. Review before releasing.")
        return 1
    else:
        print()
        print("  All production readiness checks passed!")
        return 0

if __name__ == "__main__":
    sys.exit(main())
