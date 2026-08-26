#!/bin/bash
set -Euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

DATA_DIR="$SCRIPT_DIR/data"
RESULTS_DIR="$SCRIPT_DIR/.test_results"
if [ -z "${RUN_AS_LAYER:-}" ]; then
    rm -rf "$RESULTS_DIR"
fi
mkdir -p "$RESULTS_DIR"

# Prefer a supported interpreter over a macOS system python3 that may be 3.9.
PYTHON_BIN="${PYTHON_BIN:-}"
if [ -z "$PYTHON_BIN" ]; then
    for candidate in python3.14 python3.13 python3.12 python3.11 python3; do
        if command -v "$candidate" >/dev/null 2>&1 && "$candidate" -c 'import sys; raise SystemExit(0 if sys.version_info >= (3, 10) else 1)' >/dev/null 2>&1; then
            PYTHON_BIN="$candidate"
            break
        fi
    done
fi
if [ -z "$PYTHON_BIN" ]; then
    echo "ERROR: Python 3.10+ is required for the Python SDK tests"
    exit 1
fi

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
    local pass_pattern="${4:-TEST_PASS}"
    local require_ec="${5:-0}"
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
        ok=1
        # "--" 防止以 "-" 开头的通过标记（如 Go-NativeFloat 的 "--- PASS"）被 grep 当作选项
        if grep -q -- "$pass_pattern" "$result_file" 2>/dev/null; then ok=0; fi
        # require_ec=0 时仍要求退出码为 0；require_ec=1 时仅看通过信号（容忍已知差异导致的非 0 退出）
        if [ "$require_ec" = "0" ] && [ "$ec" -ne 0 ]; then ok=1; fi
        if [ "$ok" -eq 0 ]; then
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
run_test "Python" "$PYTHON_BIN test.py" "python"

# Independent CSV oracle (the old verify_csv.py entrypoint was removed; this
# oracle compares the SDK with the authoritative range CSV directly).
run_test "CSV Oracle" "$PYTHON_BIN test_csv_oracle.py" "python" "CSV_ORACLE_OK"

# Hostile-vector conformance (consumes tools/hostile_vectors.json; skips
# gracefully when the base DB is absent).
run_test "Python-HostileVectors" "$PYTHON_BIN test_hostile_vectors.py" "python" "HOSTILE_VECTORS_OK"

# FORMAT §10.5 原生浮点统一契约边界（P0-2，DB-free）
run_test "Python-NativeFloat" "$PYTHON_BIN test_native_float.py" "python" "NATIVE_FLOAT_OK"

# Node.js
run_test "Node.js" "node test.js" "nodejs"

# FORMAT §10.5 原生浮点统一契约边界（P0-2，DB-free）
run_test "Node-NativeFloat" "node native_float_test.js" "nodejs" "NATIVE_FLOAT_OK"

# PHP
run_test "PHP" "php test.php" "php"

# PHP hostile-vector conformance (skips gracefully without base DB).
run_test "PHP-HostileVectors" "php tier2_hostile.php" "php" "HOSTILE_VECTORS_OK"

# Go
if command -v go &> /dev/null; then
    run_test "Go" "go run ./cmd/demo" "go"
    # Hostile-vector conformance (skips gracefully without base DB).
    run_test "Go-HostileVectors" "go test ./qzdb/ -run 'TestHostileVectors' -count=1 -v" "go" "HOSTILE_VECTORS_OK"
    # FORMAT §10.5 原生浮点统一契约边界 + Metadata TLV type=5/6 权威（P0-2 / T7，DB-free）
    run_test "Go-NativeFloat" "go test ./qzdb/ -run 'TestFormatFloat6Boundaries' -count=1 -v" "go" "--- PASS"
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
    # FORMAT §10.5 原生浮点统一契约边界（P0-2；include 源文件以触达静态格式化器）
    if ! (cd c && $CC -O2 -o native_float_boundary_test native_float_boundary_test.c -lm); then
        echo "✗ C-NativeFloat (compile failed)" > "$RESULTS_DIR/C-NativeFloat.result.status"
        TEST_NAMES+=("C-NativeFloat")
        TEST_PIDS+=(0)
    else
        run_test "C-NativeFloat" "./native_float_boundary_test" "c" "NATIVE_FLOAT_OK"
    fi
    # Metadata TLV type=5/6 权威语义 + scope 所有权（UAF 回归守卫；需真实库文件，缺失时自跳过）
    if ! (cd c && $CC -O2 -o tlv_meta_test tlv_meta_test.c qzdb_reader.c -lm); then
        echo "✗ C-TlvMeta (compile failed)" > "$RESULTS_DIR/C-TlvMeta.result.status"
        TEST_NAMES+=("C-TlvMeta")
        TEST_PIDS+=(0)
    else
        run_test "C-TlvMeta" "./tlv_meta_test" "c" "TLV_META_C_OK"
    fi
    # IP 解析严格性契约（缺陷审计 + 回归守卫；include 源文件以触达静态 fast_parse_ip）
    if ! (cd c && $CC -O2 -o ip_strict_test ip_strict_test.c -lm); then
        echo "✗ C-IpStrict (compile failed)" > "$RESULTS_DIR/C-IpStrict.result.status"
        TEST_NAMES+=("C-IpStrict")
        TEST_PIDS+=(0)
    else
        run_test "C-IpStrict" "./ip_strict_test" "c" "IP_STRICT_OK"
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
        # Gate passes only when the suite prints FAILCLOSED_OK (29/29, no genuine SDK bug).
        run_test "Java-FailClosed" "$JAVA_HOME/bin/java -Xmx2g -cp java/build com.qqzeng.qzdb.FailClosedHostileTest" "" "FAILCLOSED_OK" "0"
    fi
else
    echo "[SKIP] Java (JDK not found)"
fi

# .NET/C#
if command -v dotnet &> /dev/null; then
    # C# 门禁以 Tier 1（功能/边界/并发）全过为准：测试打印 "Tier 1: N pass, 0 fail"
    # 当且仅当 tier1Fail==0（无任何功能/安全/并发回归）。Tier 2 的 52 个错误为已知跨数据集
    # 差异（保留地址语义/字段映射），不计入门禁失败，故该层仍打印 SOME TIERS FAILED 且退出码
    # 非 0；用 require_ec=1 容忍非 0 退出码，并匹配 "Tier 1 ... 0 fail" 信号，既不误判 C# 失败，
    # 也不会掩盖真实的 Tier 1 回归（一旦 tier1Fail>0，该行不再含 "0 fail"，门禁会 FAIL）。
    # Pin the executable test target to net10.0; the library itself remains
    # multi-targeted, while an unqualified dotnet run can trigger a slow or
    # ambiguous cross-target build on macOS.
    run_test "C#" "dotnet build netcore.Tests/netcore.Tests.csproj -c Release -p:TargetFramework=net10.0 -p:TargetFrameworks=net10.0 --no-restore -v:q && dotnet run --project netcore.Tests/netcore.Tests.csproj -c Release -f net10.0 --no-build --no-restore" "." "ALL TIERS PASSED" "0"
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
# 若作为 run_all.sh 的子层运行（RUN_AS_LAYER=1），不清理共享结果目录，
# 否则会删掉其他并行验证层（L1b/L2/L3/L3b/L4/L4b）写入的状态文件，导致父脚本误判。
if [ -z "${RUN_AS_LAYER:-}" ]; then
    rm -rf "$RESULTS_DIR"
fi

if [ "$FAILED" -eq 0 ]; then
    echo "All tests passed!"
    exit 0
else
    echo "Some tests FAILED!"
    exit 1
fi
