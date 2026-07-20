#!/bin/bash
# Build all compiled batch runners for cross-language verification
set -e
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
BASE_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

echo "=========================================="
echo "  Building Cross-Verification Runners"
echo "=========================================="

# ── C ──
echo ""
echo "--- C ---"
if command -v clang &> /dev/null || command -v gcc &> /dev/null; then
    CC=$(command -v clang || command -v gcc)
    # Copy the C batch runner source to the c/ directory for correct relative includes
    cp "$SCRIPT_DIR/batch_query.c" "$BASE_DIR/c/batch_query.c"
    $CC -O3 -o "$SCRIPT_DIR/batch_c" "$BASE_DIR/c/batch_query.c" "$BASE_DIR/c/qzdb_searcher.c" -lm
    echo "  -> tools/batch_c"
else
    echo "  SKIP (no C compiler)"
fi

# ── Go ──
echo ""
echo "--- Go ---"
if command -v go &> /dev/null; then
    # Create Go batch runner in go/cmd/batch_go/
    mkdir -p "$BASE_DIR/go/cmd/batch_go"
    cp "$SCRIPT_DIR/batch_query.go" "$BASE_DIR/go/cmd/batch_go/main.go"
    cd "$BASE_DIR/go"
    go build -o "$SCRIPT_DIR/batch_go" ./cmd/batch_go
    echo "  -> tools/batch_go"
else
    echo "  SKIP (no Go)"
fi

# ── Rust ──
echo ""
echo "--- Rust ---"
if command -v cargo &> /dev/null; then
    cd "$BASE_DIR/rust"
    cargo build --release --bin batch_rust 2>&1 | tail -3
    cp "target/release/batch_rust" "$SCRIPT_DIR/batch_rust"
    echo "  -> tools/batch_rust"
else
    echo "  SKIP (no Cargo/Rust)"
fi

# ── Java ──
echo ""
echo "--- Java ---"
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
    BUILD_DIR="$SCRIPT_DIR/java_build"
    rm -rf "$BUILD_DIR"
    mkdir -p "$BUILD_DIR"
    SDK_DIR="$BASE_DIR/java/src/main/java"
    "$JAVA_HOME/bin/javac" -d "$BUILD_DIR" \
        "$SDK_DIR/qzdb/QzdbSearcher.java" \
        "$SDK_DIR/qzdb/IpLocation.java" \
        "$SCRIPT_DIR/BatchQuery.java"
    # Create wrapper script
    cat > "$SCRIPT_DIR/batch_java.sh" << 'JAVAEOF'
#!/bin/bash
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
JAVA_HOME=$(ls -d /opt/homebrew/Cellar/openjdk@21/*/libexec/openjdk.jdk/Contents/Home /opt/homebrew/opt/openjdk@21 /opt/homebrew/opt/openjdk /Library/Java/JavaVirtualMachines/*/Contents/Home 2>/dev/null | head -1)
if [ -z "$JAVA_HOME" ]; then
    JAVA_HOME=$(find / -name "javac" -type f 2>/dev/null | head -1 | xargs dirname | xargs dirname)
fi
exec "$JAVA_HOME/bin/java" -cp "$SCRIPT_DIR/java_build" qzdb.BatchQuery "$@"
JAVAEOF
    chmod +x "$SCRIPT_DIR/batch_java.sh"
    echo "  -> tools/batch_java.sh"
else
    echo "  SKIP (no Java)"
fi

# ── C# (.NET) ──
echo ""
echo "--- C# (.NET) ---"
if command -v dotnet &> /dev/null; then
    # Create separate project for batch runner
    BATCH_CS_DIR="$SCRIPT_DIR/batch_csharp"
    if [ -d "$BATCH_CS_DIR" ]; then
        cd "$BATCH_CS_DIR"
        dotnet build --configuration Release -o "$SCRIPT_DIR/batch_csharp_out" 2>&1 | tail -3
        # Create wrapper script
        cat > "$SCRIPT_DIR/batch_csharp.sh" << 'CSEOF'
#!/bin/bash
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
exec dotnet run --project "$SCRIPT_DIR/batch_csharp" --configuration Release -- "$@"
CSEOF
        chmod +x "$SCRIPT_DIR/batch_csharp.sh"
        echo "  -> tools/batch_csharp.sh"
    else
        echo "  SKIP (batch_csharp project not found)"
    fi
else
    echo "  SKIP (no .NET)"
fi

echo ""
echo "=========================================="
echo "  Build Complete"
echo "=========================================="
ls -la "$SCRIPT_DIR/batch_c" "$SCRIPT_DIR/batch_go" "$SCRIPT_DIR/batch_rust" "$SCRIPT_DIR/batch_java.sh" "$SCRIPT_DIR/batch_csharp.sh" 2>/dev/null || true
