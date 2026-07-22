#!/bin/bash
set -Euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

RESULTS=""
FAILED=0
SKIPPED=0

run_test() {
    local name="$1"
    local cmd="$2"
    local dir="$3"
    local tmp="/tmp/qzdb_test_$$.txt"
    echo "=========================================="
    echo "  Testing: $name"
    echo "=========================================="
    if [ -n "$dir" ]; then
        pushd "$dir" > /dev/null || { echo "ERROR: pushd $dir failed"; FAILED=1; return 1; }
    fi
    eval "$cmd" > "$tmp" 2>&1
    local ec=$?
    if [ -n "$dir" ]; then
        popd > /dev/null
    fi
    cat "$tmp"
    if [ "$ec" -eq 0 ] && grep -q "TEST_PASS" "$tmp" 2>/dev/null; then
        RESULTS="${RESULTS}✓ $name passed\n"
    else
        RESULTS="${RESULTS}✗ $name FAILED (exit=$ec)\n"
        FAILED=1
    fi
    rm -f "$tmp"
    echo ""
}

# Python
run_test "Python" "python3 test.py" "python"

# CSV Verify (Python reference against source CSV)
run_test "CSV Verify" "python3 ../python/verify_csv.py" "python"

# Node.js
run_test "Node.js" "node test.js" "nodejs"

# PHP
run_test "PHP" "php test.php" "php"

# Go
if command -v go &> /dev/null; then
    run_test "Go" "go run main.go" "go"
fi

# Rust
if command -v cargo &> /dev/null; then
    run_test "Rust" "cargo run --release --bin main --quiet" "rust"
fi

# C
if command -v gcc &> /dev/null || command -v clang &> /dev/null; then
    CC="gcc"
    command -v clang &> /dev/null && CC="clang"
    if ! (cd c && $CC -O3 -o qzdb_test qzdb_searcher.c main.c -lm); then
        RESULTS="${RESULTS}✗ C (compile failed)\n"
        FAILED=1
    else
        run_test "C" "./c/qzdb_test" ""
    fi
fi

# Java
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
    if ! $JAVA_HOME/bin/javac -d java/build java/src/main/java/qzdb/QzdbSearcher.java java/src/main/java/qzdb/IpLocation.java java/src/main/java/Main.java; then
        RESULTS="${RESULTS}✗ Java (compile failed)\n"
        FAILED=1
    else
        run_test "Java" "$JAVA_HOME/bin/java -cp java/build Main" ""
    fi
else
    echo "[SKIP] Java (JDK not found)"
    SKIPPED=$((SKIPPED + 1))
fi

# .NET/C#
if command -v dotnet &> /dev/null; then
    run_test "C#" "dotnet run --configuration Release" "netcore"
else
    echo "[SKIP] C# (.NET SDK not found)"
    SKIPPED=$((SKIPPED + 1))
fi

echo ""
echo "=========================================="
echo "  Summary"
echo "=========================================="
echo -e "$RESULTS"
echo "($SKIPPED skipped)"

if [ "$FAILED" -eq 0 ]; then
    echo "All tests passed!"
    exit 0
else
    echo "Some tests FAILED!"
    exit 1
fi
