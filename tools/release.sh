#!/bin/bash
set -Euo pipefail

# qzdb SDK Release Automation
# Usage: ./tools/release.sh [patch|minor|major]

VERSION_TYPE="${1:-patch}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
BASE_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
MULTI_LANG="$BASE_DIR/multi-lang"

echo "=========================================="
echo "  qzdb SDK Release Automation"
echo "=========================================="
echo ""

# ── Step 1: Run production readiness checks ──
echo "[1/6] Running production readiness checks..."
python3 "$BASE_DIR/tools/production_check.py"
if [ $? -ne 0 ]; then
    echo "ERROR: Production readiness checks failed. Aborting release."
    exit 1
fi
echo "✓ Production readiness checks passed"
echo ""

# ── Step 2: Run full verification suite ──
echo "[2/6] Running full verification suite (L1-L4)..."
cd "$MULTI_LANG"
chmod +x run_all.sh
./run_all.sh
if [ $? -ne 0 ]; then
    echo "ERROR: Verification suite failed. Aborting release."
    exit 1
fi
echo "✓ Full verification suite passed"
echo ""

# ── Step 3: Run cross-language verification ──
echo "[3/6] Running cross-language verification..."
python3 "$MULTI_LANG/cross_lang_verify.py"
if [ $? -ne 0 ]; then
    echo "ERROR: Cross-language verification failed. Aborting release."
    exit 1
fi
echo "✓ Cross-language verification passed"
echo ""

# ── Step 4: Run accuracy analysis ──
echo "[4/6] Running accuracy analysis..."
python3 "$MULTI_LANG/accuracy_analysis.py"
echo "✓ Accuracy analysis completed"
echo ""

# ── Step 5: Update version ──
echo "[5/6] Updating version..."
CURRENT_VERSION=$(grep -oP '(?<=version: ")[^"]+' "$BASE_DIR/FORMAT.md" 2>/dev/null || echo "0.0.0")
echo "Current version: $CURRENT_VERSION"

# Bump version based on type
IFS='.' read -r MAJOR MINOR PATCH <<< "$CURRENT_VERSION"
case "$VERSION_TYPE" in
    patch) PATCH=$((PATCH + 1)) ;;
    minor) MINOR=$((MINOR + 1)); PATCH=0 ;;
    major) MAJOR=$((MAJOR + 1)); MINOR=0; PATCH=0 ;;
esac
NEW_VERSION="$MAJOR.$MINOR.$PATCH"
echo "New version: $NEW_VERSION"

# ── Step 6: Create release commit ──
echo "[6/6] Creating release commit..."
cd "$BASE_DIR"
git add -A
git commit -m "release: v$NEW_VERSION

- Full verification suite passed (L1-L4)
- Cross-language consistency verified
- Accuracy analysis completed
- Production readiness checks passed

Co-authored-by: qzdb-searcher"

echo ""
echo "=========================================="
echo "  Release v$NEW_VERSION ready!"
echo "  Run 'git push' and create a PR to finalize."
echo "=========================================="
