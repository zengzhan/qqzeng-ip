#!/bin/bash
set -uo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

DATA_DIR="$SCRIPT_DIR/data"
RESULTS_DIR="$SCRIPT_DIR/.test_results"
mkdir -p "$RESULTS_DIR"

# --- Data directory validation ---
if [ ! -d "$DATA_DIR" ]; then
    echo "ERROR: Data directory not found: $DATA_DIR"
    echo "Place .qzdb files in multi-lang/data/ before running tests."
    exit 1
fi

DB_FILES=("$DATA_DIR"/*.qzdb)
if [ ${#DB_FILES[@]} -eq 0 ]; then
    echo "ERROR: No .qzdb files found in $DATA_DIR"
    echo "Download a database from qqzeng.com and place it here."
    exit 1
fi

DB_PATH="${DB_FILES[0]}"
echo "Using DB: $DB_PATH"
echo ""

# --- Unified Test Orchestrator ---
# Runs all 4 verification layers:
#   L1: Smoke tests (per-language SDK basic queries)
#   L2: Cross-language verification (same IPs across all SDKs)
#   L3: Batch regression (CSV ground truth comparison)
#   L4: Deep accuracy analysis (trie traversal + IPRow validation)

# Plain arrays + indexed loops instead of `declare -A`: macOS ships bash 3.2,
# which does not support associative arrays (bash 4+ only).
LAYER_PIDS=()
LAYER_NAMES=()
TIMEOUT_SECS=120
FAILED_LAYERS=()

run_layer() {
    local layer="$1"
    local cmd="$2"
    local result_file="$RESULTS_DIR/${layer}.result"
    local status_file="$RESULTS_DIR/${layer}.status"

    (
        eval "$cmd" > "$result_file" 2>&1
        ec=$?
        if [ "$ec" -eq 0 ]; then
            mkdir -p "$RESULTS_DIR" && echo "PASS" > "$status_file"
        else
            mkdir -p "$RESULTS_DIR" && echo "FAIL" > "$status_file"
        fi
    ) &
    LAYER_NAMES+=("$layer")
    LAYER_PIDS+=($!)
}

echo "=========================================="
echo "  QZDB Unified Verification Orchestrator"
echo "=========================================="
echo ""
echo "DB: $DB_PATH"
echo ""

# --- L1: Smoke Tests (parallel with L2) ---
echo "[L1] Running smoke tests..."
run_layer "L1_smoke" "./run_all_tests.sh"

# --- L1b: IPv6 Smoke Tests ---
echo "[L1b] Running IPv6 smoke tests..."
run_layer "L1b_ipv6_smoke" "python3 -c \"
import sys
sys.path.insert(0, 'python')
from qzdb import QzdbSearcher
searcher = QzdbSearcher.get_instance('$DB_PATH')
v6_ips = ['2408:8000:9000::1', '2001:4860:4860::8888', '2606:4700:4700::1111', '2400:3200::1', '2400:da00::1', '2a00:1450:4001:801::200e', '2607:f8b0:4004:800::200e', '2c0f:f248:0:1::cafe', '2402:4e00:0:1::abcd', '::1', '::', 'ff02::1']
passed = 0
failed = 0
for ip in v6_ips:
    r = searcher.find(ip)
    if r is not None:
        passed += 1
    else:
        failed += 1
print(f'IPv6 smoke: {passed} passed, {failed} failed')
sys.exit(0 if failed == 0 else 1)
\""

# --- L2: Cross-Language Verification (IPv4 + IPv6) ---
echo "[L2] Running cross-language verification..."
run_layer "L2_cross_lang" "python3 cross_lang_verify.py && python3 cross_lang_verify_v6.py"

# --- L3: Batch Regression (needs CSV ground truth) ---
echo "[L3] Running batch regression..."
CSV_FILES=("$DATA_DIR"/*.csv)
if [ ${#CSV_FILES[@]} -gt 0 ] && [ -f "${CSV_FILES[0]}" ]; then
    run_layer "L3_batch" "python3 run_batch_test_suite.py --db '$DB_PATH' --csv '${CSV_FILES[0]}'"
else
    echo "[L3] SKIP (no CSV ground truth file found in data/)"
    echo "SKIP" > "$RESULTS_DIR/L3_batch.status"
fi

# --- L3b: IPv6 Batch Regression ---
echo "[L3b] Running IPv6 batch regression..."
run_layer "L3b_ipv6_batch" "python3 -c \"
import csv, sys
sys.path.insert(0, 'python')
from qzdb import QzdbSearcher

DB_PATH = '$DB_PATH'
CSV_PATH = '$DATA_DIR/qqzeng_ip_std_china_range.csv'

searcher = QzdbSearcher.get_instance(DB_PATH)

v6_rows = []
with open(CSV_PATH, 'r', encoding='utf-8') as f:
    reader = csv.DictReader(f)
    for row in reader:
        if ':' in row['start_ip']:
            v6_rows.append(row)

print(f'IPv6 CSV ranges: {len(v6_rows)}')

passed = 0
failed = 0
no_result = 0
for row in v6_rows[:500]:
    ip = row['start_ip']
    result = searcher.find(ip)
    if result is None:
        no_result += 1
        continue
    sdk_pipe = result.to_pipe()
    csv_pipe = '|'.join([
        row.get('continent', ''),
        row.get('country_code', ''),
        row.get('country', ''),
        row.get('province', ''),
        row.get('city', ''),
        row.get('isp', ''),
    ])
    if sdk_pipe == csv_pipe:
        passed += 1
    else:
        failed += 1

print(f'IPv6 batch: {passed} passed, {failed} failed, {no_result} no-result')
sys.exit(0 if failed == 0 else 1)
\""

# --- L4: Deep Accuracy Analysis (IPv4 + IPv6) ---
echo "[L4] Running deep accuracy analysis..."
run_layer "L4_accuracy" "python3 accuracy_analysis.py"

# --- L4b: IPv6 Deep Accuracy Analysis ---
echo "[L4b] Running IPv6 deep accuracy analysis..."
run_layer "L4b_ipv6_accuracy" "python3 -c \"
import sys
sys.path.insert(0, 'python')
from qzdb import QzdbSearcher

DB_PATH = '$DB_PATH'
searcher = QzdbSearcher.get_instance(DB_PATH)

v6_ips = [
    '2408:8000:9000::1', '2001:4860:4860::8888', '2606:4700:4700::1111',
    '2400:3200::1', '2400:da00::1', '2a00:1450:4001:801::200e',
    '2607:f8b0:4004:800::200e', '2c0f:f248:0:1::cafe', '2402:4e00:0:1::abcd',
    '::1', '::', 'ff02::1',
]

passed = 0
failed = 0
for ip in v6_ips:
    r = searcher.find(ip)
    if r is not None:
        passed += 1
    else:
        failed += 1

print(f'IPv6 deep accuracy: {passed} found, {failed} not found')
sys.exit(0)
\""

# --- Wait for all layers ---
echo ""
echo "Waiting for all layers to complete..."
for i in "${!LAYER_PIDS[@]}"; do
    wait "${LAYER_PIDS[$i]}" 2>/dev/null || true
done

# --- Collect Results ---
echo ""
echo "=========================================="
echo "  Verification Summary"
echo "=========================================="

PASSED=0
FAILED=0
SKIPPED=0

for layer in L1_smoke L1b_ipv6_smoke L2_cross_lang L3_batch L4_accuracy; do
    status_file="$RESULTS_DIR/${layer}.status"
    if [ -f "$status_file" ]; then
        status=$(cat "$status_file")
        if [ "$status" = "PASS" ]; then
            echo "  ✓ $layer passed"
            PASSED=$((PASSED + 1))
        elif [ "$status" = "SKIP" ]; then
            echo "  · $layer skipped"
            SKIPPED=$((SKIPPED + 1))
        else
            echo "  ✗ $layer FAILED"
            FAILED=$((FAILED + 1))
        fi
    else
        echo "  ✗ $layer FAILED (no result file)"
        FAILED=$((FAILED + 1))
    fi
done

echo ""
echo "Results: $PASSED passed, $FAILED failed, $SKIPPED skipped"

# --- Cleanup ---
rm -rf "$RESULTS_DIR"

if [ "$FAILED" -eq 0 ]; then
    echo "All verification layers passed!"
    exit 0
else
    echo "Some verification layers FAILED!"
    exit 1
fi