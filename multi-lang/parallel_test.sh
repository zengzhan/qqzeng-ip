#!/bin/bash
set -Euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

# Parallel Test Runner for qzdb SDK
# Runs L1 smoke tests across all 8 languages in parallel,
# then runs L2-L4 sequentially for cross-language consistency.

DATA_DIR="$SCRIPT_DIR/data"
RESULTS_DIR="$SCRIPT_DIR/.test_results"
mkdir -p "$RESULTS_DIR"

if [ ! -d "$DATA_DIR" ]; then
    echo "ERROR: Data directory not found: $DATA_DIR"
    exit 1
fi

DB_FILES=("$DATA_DIR"/*.qzdb)
if [ ${#DB_FILES[@]} -eq 0 ]; then
    echo "ERROR: No .qzdb files found in $DATA_DIR"
    exit 1
fi

DB_PATH="${DB_FILES[0]}"
echo "Using DB: $DB_PATH"
echo ""

# --- Parallel L1 Smoke Tests per Language ---
echo "=========================================="
echo "  L1: Parallel Smoke Tests"
echo "=========================================="
echo ""

L1_PIDS=()
L1_NAMES=()

run_l1_test() {
    local lang="$1"
    local cmd="$2"
    local result_file="$RESULTS_DIR/L1_${lang}.result"
    local status_file="$RESULTS_DIR/L1_${lang}.status"

    (
        eval "$cmd" > "$result_file" 2>&1
        ec=$?
        if [ "$ec" -eq 0 ]; then
            echo "PASS" > "$status_file"
        else
            echo "FAIL" > "$status_file"
        fi
    ) &
    L1_NAMES+=("$lang")
    L1_PIDS+=($!)
}

# C smoke test
run_l1_test "c" "./qzdb_test"

# Go smoke test
run_l1_test "go" "go run ./cmd/main.go"

# Rust smoke test
run_l1_test "rust" "cargo run --bin main"

# Python smoke test
run_l1_test "python" "python3 qzdb.py --test"

# Node.js smoke test
run_l1_test "nodejs" "node qzdb.js --test"

# PHP smoke test
run_l1_test "php" "php -r 'require_once \"QzdbSearcher.php\"; echo \"OK\";'"

# Java smoke test
run_l1_test "java" "javac -d build src/*.java && java -cp build com.qqzeng.ip.QzdbSearcher"

# C# smoke test
run_l1_test "csharp" "dotnet run --project netcore/"

# Wait for all L1 tests
echo "Waiting for L1 smoke tests..."
for i in "${!L1_PIDS[@]}"; do
    wait "${L1_PIDS[$i]}" 2>/dev/null || true
done

# Collect L1 results
L1_PASSED=0
L1_FAILED=0
for lang in "${L1_NAMES[@]}"; do
    status_file="$RESULTS_DIR/L1_${lang}.status"
    if [ -f "$status_file" ] && [ "$(cat "$status_file")" = "PASS" ]; then
        echo "  ✓ L1 ${lang} passed"
        L1_PASSED=$((L1_PASSED + 1))
    else
        echo "  ✗ L1 ${lang} FAILED"
        L1_FAILED=$((L1_FAILED + 1))
    fi
done
echo "L1 Results: $L1_PASSED passed, $L1_FAILED failed"

if [ "$L1_FAILED" -gt 0 ]; then
    echo "L1 smoke tests failed. Skipping L2-L4."
    exit 1
fi

# --- Sequential L2-L4 Verification ---
echo ""
echo "=========================================="
echo "  L2: Cross-Language Verification"
echo "=========================================="
cd "$SCRIPT_DIR" && python3 cross_lang_verify.py

echo ""
echo "=========================================="
echo "  L3: Batch Regression"
echo "=========================================="
CSV_FILES=("$DATA_DIR"/*.csv)
if [ ${#CSV_FILES[@]} -gt 0 ] && [ -f "${CSV_FILES[0]}" ]; then
    python3 run_batch_test_suite.py --db "$DB_PATH" --csv "${CSV_FILES[0]}"
else
    echo "L3 SKIP (no CSV ground truth file)"
fi

echo ""
echo "=========================================="
echo "  L4: Deep Accuracy Analysis"
echo "=========================================="
python3 accuracy_analysis.py

echo ""
echo "=========================================="
echo "  All Tests Complete"
echo "=========================================="
