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
# The repository's public demo database is sufficient for the independent
# cross-language oracle, but not for tiered tests that require edition/scope
# fixtures such as std_china and max_global. Do not turn that expected local
# fixture absence into a wall of misleading test failures.
DB_FILES=()
if [ -d "$DATA_DIR" ]; then
    DB_FILES=("$DATA_DIR"/*.qzdb)
fi
if [ ${#DB_FILES[@]} -eq 0 ] || [ ! -f "${DB_FILES[0]}" ]; then
    DEMO_DB="$SCRIPT_DIR/../demo/qqzeng-ip-ult.qzdb"
    if [ -f "$DEMO_DB" ]; then
        echo "No ip-qzdb-sdk/data fixtures found; running the public demo oracle instead."
        if "$PYTHON_BIN" "$SCRIPT_DIR/tools/demo_sample_check.py"; then
            echo "Full fixture-dependent suite skipped (no private edition/scope fixtures)."
            exit 0
        fi
        echo "Public demo oracle FAILED."
        exit 1
    fi
    echo "ERROR: No QZDB test fixtures found in $DATA_DIR and public demo is absent."
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
# It only ever pairs a .qzdb with a _range.csv from the SAME snapshot directory,
# so a database build can never be judged against another build's ground truth.
run_test "CSV Oracle" "$PYTHON_BIN test_csv_oracle.py" "python" "CSV_ORACLE_OK"

# Test-data provenance gate: every repo .qzdb must trace to a release build
# (matched by canonical CRC32) and every frozen snapshot must be single-build
# and ground-truthed. Without this, refreshing data/ independently of
# test_data_<period>/ silently invalidates the oracle above.
run_test "Data-Sync" "$PYTHON_BIN tools/verify_data_sync.py" "." "OK: every repo" "0"

# Hostile-vector conformance (consumes tools/hostile_vectors.json; skips
# gracefully when the base DB is absent).
run_test "Python-HostileVectors" "$PYTHON_BIN test_hostile_vectors.py" "python" "HOSTILE_VECTORS_OK"

# FORMAT §10.5 原生浮点统一契约边界（P0-2，DB-free）
run_test "Python-NativeFloat" "$PYTHON_BIN test_native_float.py" "python" "NATIVE_FLOAT_OK"

# Node.js
run_test "Node.js" "node test.js" "nodejs"

# FORMAT §10.5 原生浮点统一契约边界（P0-2，DB-free）
run_test "Node-NativeFloat" "node native_float_test.js" "nodejs" "NATIVE_FLOAT_OK"

# 严格 IP 解析一致性（DB-free）：IPv4 解析改单遍扫描 + 删除冗余空白预扫描后，
# 严格性必须由该表驱动测试钉死（对齐 C-IpStrict / PHP-IpStrict）。
run_test "Node-IpStrict" "node ip_strict_test.js" "nodejs" "IP_STRICT_OK"

# 32 位 Trie 节点 + typed-array 极速通道（DB-free，自建合成库）：仓库内所有 .qzdb
# 的 v4Node24 都置位，_v4NodesU32 永不构建，V4 极速通道在真实库上无法被执行。
# 该测试在内存里造 32 位节点库把它与 V6 一并拉起来跑。
run_test "Node-TrieTypedArray" "node trie_typedarray_test.js" "nodejs" "TRIE_TYPEDARRAY_OK"

# PHP
run_test "PHP" "php test.php" "php"

# PHP hostile-vector conformance (skips gracefully without base DB).
run_test "PHP-HostileVectors" "php tier2_hostile.php" "php" "HOSTILE_VECTORS_OK"

# 严格 IPv4 解析一致性（DB-free）：fastParseIpv4 改用 filter_var + ip2long 后，
# 严格性不再由手写循环保证，必须由该表驱动测试钉死（对齐 C-IpStrict）。
run_test "PHP-IpStrict" "php ip_strict_test.php" "php" "IP_STRICT_OK"

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
#
# JDK 解析顺序（与 cross_lang_verify.py:_find_java_home 保持一致）：
#   1) 显式且有效的 JAVA_HOME —— CI / 容器 / 需要钉住 JDK 版本时用这个；
#   2) 否则按常见安装路径探测，LTS(21) 优先于最新版。
#
# 三点不要"顺手简化"，每一条都有具体的坏掉方式：
#   ① 本函数在本文件里**故意逐字重复**，不要抽成 `source tools/java_env.sh`。
#      sync_to_github.py:266 把 run_all_tests.sh 原样拷进发布仓库，但不同步
#      ip-qzdb-sdk/tools/ 下的辅助脚本 —— 抽出去会让发布仓那份直接 source 失败。
#      tools/build_all.sh:56 里有第二份副本，改一处必须同时改另一处。
#   ② 原来写的是 `JAVA_HOME=$(find_java_home)`（无条件覆盖），把调用方显式设置的
#      JAVA_HOME 静默丢掉 —— 于是 `JAVA_HOME=<jdk28> ./run_all_tests.sh` 跑的其实是
#      探测到的 JDK 27，而日志里根本看不出来。显式设置必须优先。
#   ③ 路径必须加引号。原来的 `for f in $h/bin/javac` 没加引号，任何**含空格的
#      JDK 路径**（macOS 上很常见）会被 word split 成几个不存在的片段，探测恒失败。
find_java_home() {
    if [ -n "${JAVA_HOME:-}" ]; then
        if [ -x "${JAVA_HOME}/bin/javac" ]; then
            (cd "${JAVA_HOME}" && pwd)
            return 0
        fi
        # 设了却不可用属于调用方错误：明确告警，而不是静默换一个 JDK，
        # 否则"以为自己钉住了版本"的人永远不知道自己没测到。
        echo "WARN: JAVA_HOME=$JAVA_HOME 下没有 bin/javac，回退到安装路径探测" >&2
    fi
    local h
    # 未匹配的 glob 在 bash 下保持字面量，后续 -x 判定自然为假，无需 nullglob。
    for h in \
        /opt/homebrew/Cellar/openjdk@21/*/libexec/openjdk.jdk/Contents/Home \
        /opt/homebrew/opt/openjdk@21 \
        /opt/homebrew/opt/openjdk \
        /Library/Java/JavaVirtualMachines/*/Contents/Home \
        "${HOME}/.sdkman/candidates/java/current" \
        /usr/lib/jvm/default-java
    do
        if [ -x "${h}/bin/javac" ]; then
            (cd "${h}" && pwd)
            return 0
        fi
    done
    return 1
}
JAVA_HOME="$(find_java_home || true)"

# Maven 只用于跑数据无关的 JUnit 5 层（Java-Unit）。它不在 PATH 里时不该让整层消失，
# 但也不该被当成通过 —— 下面会写 SKIP 状态并在汇总里显式列出。
find_mvn() {
    if command -v mvn >/dev/null 2>&1; then
        command -v mvn
        return 0
    fi
    local candidates=(
        /opt/homebrew/bin/mvn
        /usr/local/bin/mvn
        /opt/maven/bin/mvn
        "$HOME/.sdkman/candidates/maven/current/bin/mvn"
    )
    for m in "${candidates[@]}"; do
        if [ -x "$m" ]; then
            echo "$m"
            return 0
        fi
    done
    return 1
}
MVN_BIN=$(find_mvn || true)

if [ -n "$JAVA_HOME" ]; then
    export JAVA_HOME
    # 把实际用的 JDK 打出来：CI 日志里能一眼看出"想测的版本"与"真跑的版本"是否一致，
    # 避免探测逻辑悄悄换掉版本而无人察觉（这正是 ② 那个 bug 能长期潜伏的原因）。
    echo "  Java JDK: $($JAVA_HOME/bin/javac -version 2>&1) [$JAVA_HOME]"
    mkdir -p java/build
    # 编译整个源码树（main + test），v2.4 包名为 com.qqzeng.qzdb。
    # 排除 *UnitTest.java：那是 JUnit 5 用例，裸 javac 没有 JUnit classpath，
    # 由下面的 Java-Unit 层经 Maven/surefire 编译并执行（两条路径共用 DataFreeCases 定义）。
    if ! $JAVA_HOME/bin/javac -encoding UTF-8 -d java/build \
            $(find java/src -name '*.java' -not -name '*UnitTest.java'); then
        echo "✗ Java (compile failed)" > "$RESULTS_DIR/Java.result.status"
        TEST_NAMES+=("Java")
        TEST_PIDS+=(0)
    else
        # QzdbReaderTest 覆盖 Tier 1 全场景，成功时打印 TEST_PASS；
        # 以 ip-qzdb-sdk/ 为 CWD 运行时按相对路径候选自动定位 test_data_202608 数据。
        run_test "Java" "$JAVA_HOME/bin/java -cp java/build com.qqzeng.qzdb.QzdbReaderTest" ""
        run_test "Java-Tier2" "$JAVA_HOME/bin/java -cp java/build com.qqzeng.qzdb.FullAccuracyAndPerfTester" ""
        run_test "Java-Tier3" "$JAVA_HOME/bin/java -Xmx4g -cp java/build com.qqzeng.qzdb.DualStackBenchmark" ""
        # Gate passes only when the suite prints FAILCLOSED_OK (29/29, no genuine SDK bug).
        run_test "Java-FailClosed" "$JAVA_HOME/bin/java -Xmx2g -cp java/build com.qqzeng.qzdb.FailClosedHostileTest" "" "FAILCLOSED_OK" "0"
    fi
    # 数据无关层：不依赖私有数据，任何环境都应执行（此前 surefire 被 skipTests 关掉，
    # `mvn test` 是空转，这一层等于不存在）。
    if [ -n "$MVN_BIN" ]; then
        run_test "Java-Unit" "$MVN_BIN -B --no-transfer-progress -f java/pom.xml test" "" "BUILD SUCCESS" "0"
    else
        echo "[SKIP] Java-Unit：未找到 mvn，数据无关的 JUnit 5 用例**未执行**（不是通过）"
        echo "SKIP" > "$RESULTS_DIR/Java-Unit.result.status"
        TEST_NAMES+=("Java-Unit")
        TEST_PIDS+=(0)
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
    run_test "C#" "dotnet build netcore.Tests/netcore.Tests.csproj -c Release -p:TargetFramework=net10.0 -p:TargetFrameworks=net10.0 -v:q && dotnet run --project netcore.Tests/netcore.Tests.csproj -c Release -f net10.0 --no-build" "." "ALL TIERS PASSED" "0"
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
        elif [ "$status" = "SKIP" ]; then
            # SKIP 不判失败（如缺 mvn），但必须显式列出，不能伪装成通过。
            echo "  - $name SKIPPED"
            SKIPPED=$((SKIPPED + 1))
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
echo "Results: $PASSED passed, $FAILED failed, $SKIPPED skipped"

# --- Cleanup ---
# 若作为 run_all.sh 的子层运行（RUN_AS_LAYER=1），不清理共享结果目录，
# 否则会删掉其他并行验证层（L1b/L2/L3/L3b/L4/L4b）写入的状态文件，导致父脚本误判。
if [ -z "${RUN_AS_LAYER:-}" ]; then
    rm -rf "$RESULTS_DIR"
fi

if [ "$FAILED" -eq 0 ]; then
    if [ "$SKIPPED" -gt 0 ]; then
        echo "All executed tests passed ($SKIPPED layer(s) skipped — see the summary above)."
    else
        echo "All tests passed!"
    fi
    exit 0
else
    echo "Some tests FAILED!"
    exit 1
fi
