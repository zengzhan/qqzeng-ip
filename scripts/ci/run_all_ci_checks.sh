#!/usr/bin/env bash
set -euo pipefail

# -----------------------------------------------------------------------------
# QZDB 公开仓库本地一键全量 CI 门禁复现脚本
# -----------------------------------------------------------------------------

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/../.." && pwd)"

echo "================================================================="
echo "  QZDB CI 质量门禁与跨语言一致性验证 (本地一键复现)"
echo "================================================================="

echo ""
echo "[Step 1/6] Markdown 全仓相对链接有效性检查..."
python3 "${SCRIPT_DIR}/check_markdown_links.py" "${ROOT_DIR}"

echo ""
echo "[Step 2/6] SDK 清单与文档版本号一致性校验..."
python3 "${SCRIPT_DIR}/check_versions.py"

echo ""
echo "[Step 3/6] 25 维全要素 Schema 与 CSV 表头对齐校验..."
python3 "${SCRIPT_DIR}/verify_schema_parity.py"

echo ""
echo "[Step 4/6] 27 组 Golden Vectors 跨语言基准向量校验..."
python3 "${SCRIPT_DIR}/test_golden_vectors.py"

echo ""
echo "[Step 5/6] C SDK 严格编译与 ASan/UBSan 全量查询内存安全验证..."
"${SCRIPT_DIR}/test_c_sdk.sh"

echo ""
echo "[Step 6/6] 多语言 SDK (C/Node/Go/PHP) 374 条全量网段逐字节对拍..."
STATUS_FILE="$(mktemp)"
trap 'rm -f "${STATUS_FILE}"' EXIT
python3 "${SCRIPT_DIR}/verify_demo_parity.py" --langs "c,node,go,php" --status-file "${STATUS_FILE}"

FULLY_COVERED=$(python3 -c "import json; d=json.load(open('${STATUS_FILE}')); print('yes' if d.get('fully_covered') else 'no')")
if [[ "${FULLY_COVERED}" == "yes" ]]; then
    echo ""
    echo "================================================================="
    echo "🎉 恭喜！全部 CI 门禁检查验证通过（覆盖全量指定语言：C/Node/Go/PHP）！"
    echo "================================================================="
else
    echo ""
    echo "================================================================="
    echo "⚠️  本地 CI 门禁检查结束：部分语言工具链缺失已跳过，未达全量覆盖。"
    python3 -c "
import json
d = json.load(open('${STATUS_FILE}'))
print(f\"  已验证：{len(d.get('verified', []))} 种（{', '.join(d.get('verified', []))}）\")
if d.get('skipped'):
    sk_str = '; '.join([f\"{item['lang']}: {item['reason']}\" for item in d['skipped']])
    print(f\"  跳过：{len(d['skipped'])} 种（{sk_str}）\")
"
    echo "  若需严格全量验证，请安装缺失环境或运行带 --require 的门禁命令。"
    echo "================================================================="
fi
