#!/usr/bin/env python3
"""
QZDB 零幻觉多语言长效批量测试框架 (High-Performance Batch Process Stream Engine)
"""

import argparse
import csv
import ipaddress
import json
import os
import random
import subprocess
import sys
import time
from dataclasses import dataclass
from typing import Dict, List, Optional


# ----------------------------------------------------------------------------
# 1. 独立 Ground Truth 层 (Zero-SDK Direct Reading)
# ----------------------------------------------------------------------------

class CsvGroundTruth:
    def __init__(self, csv_path: str):
        self.csv_path = csv_path
        self.rows: List[Dict] = []
        self._load()

    def _load(self):
        with open(self.csv_path, encoding="utf-8") as f:
            reader = csv.reader(f)
            row_id = 0
            for row in reader:
                if not row or row[0].startswith("#"):
                    continue
                cidr_str = row[0].strip()
                try:
                    net = ipaddress.ip_network(cidr_str)
                except ValueError:
                    continue

                start_ip = str(net.network_address)
                end_ip = str(net.broadcast_address)
                fields = [f.strip() for f in row[1:]]
                raw_expected = "|".join(fields)

                self.rows.append({
                    "row_id": row_id,
                    "cidr": cidr_str,
                    "start_ip": start_ip,
                    "end_ip": end_ip,
                    "start_int": int(net.network_address),
                    "end_int": int(net.broadcast_address),
                    "fields": fields,
                    "expected_str": raw_expected
                })
                row_id += 1

    def row_count(self) -> int:
        return len(self.rows)

    def get_row(self, idx: int) -> Dict:
        return self.rows[idx]


# ----------------------------------------------------------------------------
# 2. 长效 Process Stream 批量 Adapter
# ----------------------------------------------------------------------------

class BatchProcessAdapter:
    def __init__(self, name: str, cmd_args: List[str], db_path: str):
        self.name = name
        self.cmd_args = cmd_args
        self.db_path = db_path
        self.proc: Optional[subprocess.Popen] = None

    def start(self):
        cmd = self.cmd_args + [self.db_path]
        self.proc = subprocess.Popen(
            cmd,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            bufsize=1
        )

    def batch_query(self, ip_list: List[str]) -> List[str]:
        if not self.proc or not self.proc.stdin or not self.proc.stdout:
            raise RuntimeError(f"Process {self.name} is not running")

        input_data = "\n".join(ip_list) + "\n"
        self.proc.stdin.write(input_data)
        self.proc.stdin.flush()

        results = []
        for _ in range(len(ip_list)):
            line = self.proc.stdout.readline()
            if not line and self.proc.poll() is not None:
                err = self.proc.stderr.read() if self.proc.stderr else ""
                raise RuntimeError(f"Process {self.name} terminated unexpectedly: {err}")
            results.append(line.rstrip("\r\n"))
        return results

    def stop(self):
        if self.proc:
            try:
                if self.proc.stdin:
                    self.proc.stdin.close()
                self.proc.terminate()
                self.proc.wait(timeout=2)
            except Exception:
                pass


# ----------------------------------------------------------------------------
# 3. 覆盖率用例生成器
# ----------------------------------------------------------------------------

@dataclass
class TestCase:
    row_id: int
    test_type: str
    ip: str
    expected_str: str


def generate_test_cases(gt: CsvGroundTruth, samples_per_row: int, seed: int) -> List[TestCase]:
    rng = random.Random(seed)
    cases: List[TestCase] = []
    n = gt.row_count()

    for i in range(n):
        row = gt.get_row(i)
        s_int = row["start_int"]
        e_int = row["end_int"]
        exp = row["expected_str"]

        cases.append(TestCase(i, "start_ip", row["start_ip"], exp))
        cases.append(TestCase(i, "end_ip", row["end_ip"], exp))

        span = e_int - s_int
        if span > 1:
            mid_int = (s_int + e_int) // 2
            cases.append(TestCase(i, "mid_ip", str(ipaddress.ip_address(mid_int)), exp))
            for _ in range(samples_per_row - 1):
                rand_int = rng.randint(s_int, e_int)
                cases.append(TestCase(i, "random_ip", str(ipaddress.ip_address(rand_int)), exp))

    return cases


# ----------------------------------------------------------------------------
# 4. 批量运行与对比主引擎
# ----------------------------------------------------------------------------

def main():
    parser = argparse.ArgumentParser(description="QZDB 多语言零幻觉长效批量测试框架")
    parser.add_argument("--db", required=True, help="待测 .qzdb 文件")
    parser.add_argument("--csv", required=True, help="基准 CSV 文件 (Ground Truth)")
    parser.add_argument("--seed", type=int, default=20260803, help="随机数种子")
    parser.add_argument("--samples", type=int, default=2, help="每行采样点数")
    args = parser.parse_args()

    gt = CsvGroundTruth(args.csv)
    print(f"[+] Ground Truth CSV 加载成功，共 {gt.row_count()} 行记录")

    cases = generate_test_cases(gt, args.samples, args.seed)
    ip_list = [c.ip for c in cases]
    print(f"[+] 成功构建 {len(cases)} 条全维度测试用例 (Start IP, End IP, Mid IP, Random IP)")

    SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))

    def _find_java_home():
        import glob
        candidates = glob.glob("/opt/homebrew/Cellar/openjdk@21/*/libexec/openjdk.jdk/Contents/Home") + \
                     glob.glob("/opt/homebrew/opt/openjdk@21") + \
                     glob.glob("/opt/homebrew/opt/openjdk") + \
                     glob.glob("/Library/Java/JavaVirtualMachines/*/Contents/Home")
        for h in candidates:
            if os.path.exists(os.path.join(h, "bin", "javac")):
                return h
        return None

    adapters = [
        BatchProcessAdapter("Python", ["python3", os.path.join(SCRIPT_DIR, "python", "batch_cli.py")], args.db),
        BatchProcessAdapter("Java", [os.path.join(_find_java_home(), "bin", "java"), "-cp", os.path.join(SCRIPT_DIR, "java_build"), "BatchMain"], args.db) if _find_java_home() else None,
        BatchProcessAdapter("Node.js", ["node", os.path.join(SCRIPT_DIR, "nodejs", "batch_cli.js")], args.db),
        BatchProcessAdapter("PHP", ["php", os.path.join(SCRIPT_DIR, "php", "batch_cli.php")], args.db),
        BatchProcessAdapter("C", [os.path.join(SCRIPT_DIR, "test_runner_bin", "c_batch")], args.db),
        BatchProcessAdapter("C#", ["dotnet", os.path.join(SCRIPT_DIR, "test_runner_bin", "netcore_bin", "qzdb-searcher.dll")], args.db),
        BatchProcessAdapter("Go", [os.path.join(SCRIPT_DIR, "test_runner_bin", "go_batch")], args.db),
        BatchProcessAdapter("Rust", [os.path.join(SCRIPT_DIR, "test_runner_bin", "rust_batch")], args.db),
    ]
    adapters = [a for a in adapters if a is not None]

    print("\n[+] 正在预启动 8 种语言 SDK 常驻批量子进程 (Stream Process)...")
    for a in adapters:
        t0 = time.time()
        a.start()
        print(f"  └─ [{a.name}] 进程启动成功 (耗时: {(time.time()-t0)*1000:.1f}ms)")

    print(f"\n[*] 开始通过长效 Process Stream 并行推送 {len(cases)} 条用例...")
    
    t_start = time.time()
    results_by_adapter = {}

    for a in adapters:
        t0 = time.time()
        res = a.batch_query(ip_list)
        results_by_adapter[a.name] = res
        print(f"  └─ [{a.name}] 完成 {len(ip_list)} 次查询比对，耗时: {time.time()-t0:.3f} 秒 (QPS: {len(ip_list)/(time.time()-t0):.0f})")
        a.stop()

    t_total = time.time() - t_start
    print(f"\n[✓] 8 大语言全量查询比对全部完成！总耗时: {t_total:.3f} 秒")

    print("\n[+] 正在进行逐字段一致性校验...")
    total_checks = len(cases) * len(adapters)
    passed_checks = 0
    failed_details = []

    for i, c in enumerate(cases):
        expected = c.expected_str
        for a in adapters:
            actual = results_by_adapter[a.name][i]
            if actual == expected:
                passed_checks += 1
            else:
                failed_details.append({
                    "case_idx": i,
                    "ip": c.ip,
                    "test_type": c.test_type,
                    "lang": a.name,
                    "expected": expected,
                    "actual": actual
                })

    print(f"\n=========================================================================")
    print(f"                      零幻觉批量测试报告汇总                              ")
    print(f"=========================================================================")
    print(f" 总 CIDR 行数: {gt.row_count()}")
    print(f" 总测试用例数: {len(cases)}")
    print(f" 跨 8 语言总校验点数: {total_checks}")
    print(f" 成功匹配点数: {passed_checks}")
    print(f" 匹配失败点数: {len(failed_details)}")
    print(f" 整体正确率: {(passed_checks / total_checks) * 100:.2f}%")
    print(f"=========================================================================")

    if failed_details:
        print("\n❌ 发现解析不一致条目（前 5 条）：")
        for f in failed_details[:5]:
            print(f"  IP: {f['ip']} ({f['test_type']}) | 语言: {f['lang']}")
            print(f"    - 期望(CSV): {f['expected']}")
            print(f"    - 实际(SDK): {f['actual']}")
            print("-" * 50)
    else:
        print("\n🎉 全语言 100.00% 完美通过测试！")


if __name__ == "__main__":
    main()
