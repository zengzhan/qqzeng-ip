#!/usr/bin/env bash
set -euo pipefail

# -----------------------------------------------------------------------------
# selftest_require.sh: 负向与容错自测脚本
# 断言:
# 1. 必需工具链缺失 + --require 时，verify_demo_parity.py 必须以非 0 退出
# 2. 缺少工具链但未指定 --require 时，verify_demo_parity.py 必须以 0 退出，且绝不输出 "100%" 或 "全部通过"
# 3. 指定了不支持的语言作为 --require 时，必须以非 0 退出
# -----------------------------------------------------------------------------

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "=== [Selftest 1/3] 模拟缺失 Go 工具链且带 --require go (预期: 非 0 失败) ==="
set +e
OUT=$(QZDB_MOCK_MISSING_TOOLCHAIN="go" python3 "${SCRIPT_DIR}/verify_demo_parity.py" --langs "c,go" --require "c,go" 2>&1)
EXIT_CODE=$?
set -e

if [[ ${EXIT_CODE} -eq 0 ]]; then
    echo "❌ 失败: 缺失必需工具链时脚本仍以 0 退出 (假绿)!" >&2
    echo "${OUT}" >&2
    exit 1
fi
if ! echo "${OUT}" | grep -q "必需语言 go 未能执行"; then
    echo "❌ 失败: 未能捕获预期的错误提示信息!" >&2
    echo "${OUT}" >&2
    exit 1
fi
echo "  ✅ 通过: 必需工具链缺失时成功拦截并返回非 0 (退出码: ${EXIT_CODE})"

echo "=== [Selftest 2/3] 模拟缺失 Go 工具链但不带 --require (预期: 退出码 0 且无假绿文案) ==="
set +e
OUT=$(QZDB_MOCK_MISSING_TOOLCHAIN="go" python3 "${SCRIPT_DIR}/verify_demo_parity.py" --langs "c,go" 2>&1)
EXIT_CODE=$?
set -e

if [[ ${EXIT_CODE} -ne 0 ]]; then
    echo "❌ 失败: 未要求必需语言时，允许本地跳过的场景非 0 退出!" >&2
    echo "${OUT}" >&2
    exit 1
fi
if echo "${OUT}" | grep -q "100%"; then
    echo "❌ 失败: 存在跳过语言时违规输出了 '100%' 假绿文案!" >&2
    echo "${OUT}" >&2
    exit 1
fi
if echo "${OUT}" | grep -q "passed completely"; then
    echo "❌ 失败: 存在跳过语言时违规输出了 'passed completely' 假绿文案!" >&2
    echo "${OUT}" >&2
    exit 1
fi
if ! echo "${OUT}" | grep -q "跳过了 1 种语言"; then
    echo "❌ 失败: 未如实输出跳过汇总!" >&2
    echo "${OUT}" >&2
    exit 1
fi
echo "  ✅ 通过: 本地跳过时退出码为 0，且如实输出跳过原因、杜绝假绿"

echo "=== [Selftest 3/3] 指定非法/不支持的语言作为 --require (预期: 非 0 失败) ==="
set +e
OUT=$(python3 "${SCRIPT_DIR}/verify_demo_parity.py" --langs "c" --require "nonexistent_lang" 2>&1)
EXIT_CODE=$?
set -e

if [[ ${EXIT_CODE} -eq 0 ]]; then
    echo "❌ 失败: 不支持语言作为 --require 时仍以 0 退出!" >&2
    exit 1
fi
echo "  ✅ 通过: 不支持语言作为 --require 时成功以非 0 拦截 (退出码: ${EXIT_CODE})"

echo ""
echo "🎉 selftest_require.sh: 全部负向与容错自测用例验证通过！"
