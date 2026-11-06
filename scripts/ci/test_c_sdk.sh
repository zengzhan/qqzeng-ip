#!/usr/bin/env bash
set -euo pipefail

# -----------------------------------------------------------------------------
# C SDK 严格编译与 ASan/UBSan 全量查询内存安全验证脚本
# -----------------------------------------------------------------------------

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/../.." && pwd)"

DEMO_QZDB="${ROOT_DIR}/demo/qqzeng-ip-ult.qzdb"
DEMO_CSV="${ROOT_DIR}/demo/qqzeng-ip-ult.csv"
C_SRC="${ROOT_DIR}/ip-qzdb-sdk/c/qzdb_reader.c"
C_HDR="${ROOT_DIR}/ip-qzdb-sdk/c/qzdb_reader.h"
RUNNER_SRC="${SCRIPT_DIR}/c_smoke_runner.c"

if [[ ! -f "${DEMO_QZDB}" || ! -f "${DEMO_CSV}" ]]; then
    echo "❌ Demo files missing in ${ROOT_DIR}/demo" >&2
    exit 1
fi

CC="${CC:-cc}"
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "${TMP_DIR}"' EXIT

IP_LIST="${TMP_DIR}/demo_ips.txt"

# 提取 demo CSV 中全部 360 条 IP，并追加边界值
python3 -c "
import csv
with open('${DEMO_CSV}', 'r', encoding='utf-8') as f:
    r = csv.reader([l for l in f if not l.startswith('#')])
    header = next(r)
    s_idx = header.index('start_ip')
    e_idx = header.index('end_ip')
    ips = []
    for row in r:
        ips.append(row[s_idx])
        ips.append(row[e_idx])
ips = list(dict.fromkeys(ips))
boundaries = ['10.0.0.1', '172.16.0.1', '192.168.1.1', '127.0.0.1', '0.0.0.0', '224.0.0.1', '255.255.255.255', '::1', '::', 'fd00::1', 'abc', '', '999.999.999.999']
ips.extend(boundaries)
with open('${IP_LIST}', 'w', encoding='utf-8') as out:
    for ip in ips:
        out.write(ip + '\n')
"

echo "=== [1/2] 编译与运行 ASan + UBSan 内存安全检查 ==="
BIN_SAN="${TMP_DIR}/c_runner_san"
# 编译参数：严格 C11、全部告警视同错误、启用 AddressSanitizer 与 UndefinedBehaviorSanitizer
"${CC}" -std=c11 -Wall -Wextra -Werror \
    -fsanitize=address,undefined -fno-omit-frame-pointer -g \
    -I"${ROOT_DIR}/ip-qzdb-sdk/c" \
    "${RUNNER_SRC}" "${C_SRC}" \
    -o "${BIN_SAN}" -lm -lpthread

if [[ "$(uname -s)" == "Darwin" ]]; then
    export ASAN_OPTIONS="abort_on_error=1"
else
    export ASAN_OPTIONS="detect_leaks=1:abort_on_error=1"
fi
export UBSAN_OPTIONS="print_stacktrace=1:halt_on_error=1"

"${BIN_SAN}" "${DEMO_QZDB}" "${IP_LIST}"
echo "✅ ASan/UBSan 检查通过：0 内存泄漏、0 未定义行为报告。"

echo "=== [2/2] 编译与运行 Release (-O2 -Wall -Wextra -Werror) 构建 ==="
BIN_OPT="${TMP_DIR}/c_runner_opt"
"${CC}" -std=c11 -Wall -Wextra -Werror -O2 \
    -I"${ROOT_DIR}/ip-qzdb-sdk/c" \
    "${RUNNER_SRC}" "${C_SRC}" \
    -o "${BIN_OPT}" -lm -lpthread

"${BIN_OPT}" "${DEMO_QZDB}" "${IP_LIST}"
echo "✅ Release 优化构建通过。"

echo "🎉 C SDK 全流程质量门禁验证通过！"
