#!/bin/bash
set -Euo pipefail
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

echo "Using DB: ${DB_FILES[0]}"
echo ""

# --- Parallel test runner ---
# Plain arrays + indexed loops instead of `declare -A`: macOS ships bash 3.2,
# which does not support associative arrays (bash 4+ only).
TEST_PIDS=()
TEST_NAMES=()

run_test() {
    local name="$1"
    local cmd="$2"
    local dir="$3"
    local result_file="$RESULTS_DIR/${name}.result"

    (
        if [ -n "$dir" ]; then
            pushd "$dir" > /dev/null
        fi
        eval "$cmd" > "$result_file" 2>&1
        ec=$?
        if [ -n "$dir" ]; then
            popd > /dev/null
        fi
        if [ "$ec" -eq 0 ] && grep -q "TEST_PASS" "$result_file" 2>/dev/null; then
            echo "PASS" > "${result_file}.status"
        else
            echo "FAIL" > "${result_file}.status"
        fi
    ) &
    TEST_NAMES+=("$name")
    TEST_PIDS+=($!)
}

# --- Run all tests in parallel ---
echo "Running tests in parallel..."
echo ""

# Python
run_test "Python" "python3 test.py" "python"

# CSV Verify
run_test "CSV Verify" "python3 ../python/verify_csv.py" "python"

# Node.js
run_test "Node.js" "node test.js" "nodejs"

# PHP
run_test "PHP" "php test.php" "php"

# Go
if command -v go &> /dev/null; then
    run_test "Go" "go run ./cmd/demo" "go"
fi

# Rust
if command -v cargo &> /dev/null; then
    run_test "Rust" "cargo run --release --bin demo --quiet" "rust"
fi

# C
if command -v gcc &> /dev/null || command -v clang &> /dev/null; then
    CC="gcc"
    command -v clang &> /dev/null && CC="clang"
    if ! (cd c && $CC -O3 -o qzdb_test qzdb_reader.c main.c -lm); then
        echo "✗ C (compile failed)" > "$RESULTS_DIR/C.result.status"
        TEST_NAMES+=("C")
        TEST_PIDS+=(0)
    else
        run_test "C" "./qzdb_test" "c"
    fi
fi

# Java (v2.4 API: com.qqzeng.qzdb.QzdbReader + QzdbReaderTest)
find_java_home() {
    local homes=(
        /opt/homebrew/Cellar/openjdk@21/*/libexec/openjdk.jdk/Contents/Home
        /opt/homebrew/opt/openjdk@21
        /opt/homebrew/opt/openjdk
        /Library/Java/JavaVirtualMachines/*/Contents/Home
    )
    for h in "${homes[@]}"; do
        for f in $h/bin/javac; do
            if [ -x "$f" ]; then
                echo "$(cd "$h" && pwd)"
                return 0
            fi
        done
    done
    return 1
}
JAVA_HOME=$(find_java_home)
if [ -n "$JAVA_HOME" ]; then
    export JAVA_HOME
    mkdir -p java/build
    # 编译整个源码树（main + test），v2.4 包名为 com.qqzeng.qzdb
    if ! $JAVA_HOME/bin/javac -encoding UTF-8 -d java/build $(find java/src -name '*.java'); then
        echo "✗ Java (compile failed)" > "$RESULTS_DIR/Java.result.status"
        TEST_NAMES+=("Java")
        TEST_PIDS+=(0)
    else
        # QzdbReaderTest 覆盖 Tier 1 全场景，成功时打印 TEST_PASS；
        # 以 multi-lang/ 为 CWD 运行时按相对路径候选自动定位 test_data_202608 数据。
        run_test "Java" "$JAVA_HOME/bin/java -cp java/build com.qqzeng.qzdb.QzdbReaderTest" ""
        run_test "Java-Tier2" "$JAVA_HOME/bin/java -cp java/build com.qqzeng.qzdb.FullAccuracyAndPerfTester" ""
        run_test "Java-Tier3" "$JAVA_HOME/bin/java -Xmx4g -cp java/build com.qqzeng.qzdb.DualStackBenchmark" ""
    fi
else
    echo "[SKIP] Java (JDK not found)"
fi

# .NET/C#
if command -v dotnet &> /dev/null; then
    run_test "C#" "dotnet run --project netcore.Tests/netcore.Tests.csproj -c Release" "."
else
    echo "[SKIP] C# (.NET SDK not found)"
fi

# --- Wait for all tests ---
echo ""
echo "Waiting for tests to complete..."
for i in "${!TEST_PIDS[@]}"; do
    wait "${TEST_PIDS[$i]}" 2>/dev/null || true
done

# --- Collect results ---
echo ""
echo "=========================================="
echo "  Test Summary"
echo "=========================================="

PASSED=0
FAILED=0
SKIPPED=0

for i in "${!TEST_NAMES[@]}"; do
    name="${TEST_NAMES[$i]}"
    status_file="$RESULTS_DIR/${name}.result.status"
    if [ -f "$status_file" ]; then
        status=$(cat "$status_file")
        if [ "$status" = "PASS" ]; then
            echo "  ✓ $name passed"
            PASSED=$((PASSED + 1))
        else
            echo "  ✗ $name FAILED"
            FAILED=$((FAILED + 1))
        fi
    else
        echo "  ✗ $name FAILED (no result file)"
        FAILED=$((FAILED + 1))
    fi
done

echo ""
echo "Results: $PASSED passed, $FAILED failed"

# --- Cleanup ---
rm -rf "$RESULTS_DIR"

if [ "$FAILED" -eq 0 ]; then
    echo "All tests passed!"
    exit 0
else
    echo "Some tests FAILED!"
    exit 1
fi
