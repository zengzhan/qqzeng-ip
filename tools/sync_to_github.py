#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
QZDB 官方一键同步脚本 (Dev -> GitHub)
=====================================
功能：
1. 精准同步 multi-lang 下各语言 SDK 源码到 GitHub 仓库 (ip-qzdb-sdk)
2. 自动过滤所有测试文件、基准测试、二进制 *.qzdb 库文件与内部配置
3. 严格按照拓扑顺序（先子目录、再顶级目录）逐一提交专属 Commit 描述
4. 自动推送到远程 GitHub 仓库

用法：
    python3 tools/sync_to_github.py [--push]
"""

import os
import sys
import time
import shutil
import subprocess

DEV_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "multi-lang"))
GITHUB_REPO = "/Users/zengxiangzhan/ZengData/网站/GitHub/qqzeng-ip/qqzeng-ip"
GITHUB_SDK = os.path.join(GITHUB_REPO, "ip-qzdb-sdk")

# 目录与 Commit 描述映射（严格先子目录后顶级目录）
FOLDER_SEQUENCE = [
    # 1. 先刷 ip-qzdb-sdk 8 个子语言
    ("ip-qzdb-sdk/rust", "rust: ⚡ Rust 极速解析引擎 (内存安全 mmap 零拷贝, 微秒级响应)"),
    ("ip-qzdb-sdk/c", "c: ⚡ C/C++ 语言极速解析引擎 (mmap 零拷贝, 微秒级响应, 零堆内存分配)"),
    ("ip-qzdb-sdk/go", "go: ⚡ Go 语言极速解析引擎 (跨平台 mmap 零拷贝, 无锁并发, 极致低延迟)"),
    ("ip-qzdb-sdk/netcore", "netcore: ⚡ C# .NET 极速解析引擎 (内存映射优化, 高并发 760 万+ QPS 零分配)"),
    ("ip-qzdb-sdk/java", "java: ⚡ Java 极速解析引擎 (堆外内存优化, 极致并发性能)"),
    ("ip-qzdb-sdk/nodejs", "nodejs: ⚡ Node.js 极速解析引擎 (V8 原生 BigInt 优化, 异步高效检索)"),
    ("ip-qzdb-sdk/php", "php: ⚡ PHP 极速解析引擎 (高性能内存解析, 开箱即用)"),
    ("ip-qzdb-sdk/python", "python: ⚡ Python 极速解析引擎 (二进制轻量解析, 极简集成)"),
    # 2. 顶级产品目录
    ("ip-qzdb-sdk", "ip-qzdb-sdk: 👑 下一代 QZDB 极速 IP 解析引擎多语言 SDK (支持 Rust/C/Go/Java/C#/Node/PHP/Python)"),
    ("ip-classic-sdk", "ip-classic-sdk: 📦 IP 数据库经典版 SDK (经典 6.0 .db 与 2.0 .dat 多语言源码)"),
    ("ip-history-sdk", "ip-history-sdk: 🗂️ IP 数据库历史版本与工具 (3.0~5.0 历史演进与桌面查询工具)"),
    ("phone-location-sdk", "phone-location-sdk: 📱 50万+ 手机号段归属地 2.0~6.0 全版本多语言 DAT 解析 SDK 与 Redis 方案"),
    ("database-sql", "database-sql: 🗄️ MySQL / PostgreSQL / SQL Server IP 与号段数据库建表与批量入库 DDL"),
    ("demo", "demo: 📋 IP 归属地及手机号段 CSV/TXT 与 QZDB 演示样本数据"),
    ("docs", "docs: 📚 项目核心设计文档、多格式性能基准对比报告与维护指南"),
]

def run(cmd, cwd=None, check=True):
    print(f"  [RUN] {cmd}")
    res = subprocess.run(cmd, shell=True, cwd=cwd, text=True, capture_output=True)
    if check and res.returncode != 0:
        print(f"❌ 命令执行失败:\nSTDOUT:\n{res.stdout}\nSTDERR:\n{res.stderr}")
        sys.exit(res.returncode)
    return res.stdout.strip()

def sync_sdk_files():
    print("\n📦 Step 1: 精准同步 SDK 源码及文档...")
    if not os.path.exists(GITHUB_SDK):
        print(f"❌ 目标 GitHub 目录不存在: {GITHUB_SDK}")
        sys.exit(1)

    # 1. C
    shutil.copy2(os.path.join(DEV_DIR, "c", "qzdb_reader.c"), os.path.join(GITHUB_SDK, "c"))
    shutil.copy2(os.path.join(DEV_DIR, "c", "qzdb_reader.h"), os.path.join(GITHUB_SDK, "c"))
    shutil.copy2(os.path.join(DEV_DIR, "c", "README.md"), os.path.join(GITHUB_SDK, "c"))

    # 2. Go
    shutil.copy2(os.path.join(DEV_DIR, "go", "go.mod"), os.path.join(GITHUB_SDK, "go"))
    if os.path.exists(os.path.join(DEV_DIR, "go", "go.sum")):
        shutil.copy2(os.path.join(DEV_DIR, "go", "go.sum"), os.path.join(GITHUB_SDK, "go"))
    shutil.copy2(os.path.join(DEV_DIR, "go", "README.md"), os.path.join(GITHUB_SDK, "go"))
    go_target = os.path.join(GITHUB_SDK, "go", "qzdb")
    os.makedirs(go_target, exist_ok=True)
    for f in os.listdir(os.path.join(DEV_DIR, "go", "qzdb")):
        if f.endswith(".go") and not f.endswith("_test.go"):
            shutil.copy2(os.path.join(DEV_DIR, "go", "qzdb", f), go_target)

    # 3. Java
    shutil.copy2(os.path.join(DEV_DIR, "java", "pom.xml"), os.path.join(GITHUB_SDK, "java"))
    shutil.copy2(os.path.join(DEV_DIR, "java", "README.md"), os.path.join(GITHUB_SDK, "java"))
    java_target = os.path.join(GITHUB_SDK, "java", "src", "main", "java", "com", "qqzeng", "qzdb")
    os.makedirs(java_target, exist_ok=True)
    for f in os.listdir(os.path.join(DEV_DIR, "java", "src", "main", "java", "com", "qqzeng", "qzdb")):
        if f.endswith(".java") and f != "Main.java":
            shutil.copy2(os.path.join(DEV_DIR, "java", "src", "main", "java", "com", "qqzeng", "qzdb", f), java_target)

    # 4. .NET
    shutil.copy2(os.path.join(DEV_DIR, "netcore", "QQZeng.Qzdb.csproj"), os.path.join(GITHUB_SDK, "netcore"))
    shutil.copy2(os.path.join(DEV_DIR, "netcore", "README.md"), os.path.join(GITHUB_SDK, "netcore"))
    for f in os.listdir(os.path.join(DEV_DIR, "netcore")):
        if f.endswith(".cs"):
            shutil.copy2(os.path.join(DEV_DIR, "netcore", f), os.path.join(GITHUB_SDK, "netcore"))

    # 5. Node.js
    shutil.copy2(os.path.join(DEV_DIR, "nodejs", "package.json"), os.path.join(GITHUB_SDK, "nodejs"))
    shutil.copy2(os.path.join(DEV_DIR, "nodejs", "qzdb.js"), os.path.join(GITHUB_SDK, "nodejs"))
    shutil.copy2(os.path.join(DEV_DIR, "nodejs", "README.md"), os.path.join(GITHUB_SDK, "nodejs"))

    # 6. PHP
    shutil.copy2(os.path.join(DEV_DIR, "php", "QzdbReader.php"), os.path.join(GITHUB_SDK, "php"))
    shutil.copy2(os.path.join(DEV_DIR, "php", "README.md"), os.path.join(GITHUB_SDK, "php"))

    # 7. Python
    shutil.copy2(os.path.join(DEV_DIR, "python", "qzdb.py"), os.path.join(GITHUB_SDK, "python"))
    shutil.copy2(os.path.join(DEV_DIR, "python", "pyproject.toml"), os.path.join(GITHUB_SDK, "python"))
    shutil.copy2(os.path.join(DEV_DIR, "python", "README.md"), os.path.join(GITHUB_SDK, "python"))

    # 8. Rust
    shutil.copy2(os.path.join(DEV_DIR, "rust", "Cargo.toml"), os.path.join(GITHUB_SDK, "rust"))
    shutil.copy2(os.path.join(DEV_DIR, "rust", "README.md"), os.path.join(GITHUB_SDK, "rust"))
    rust_target = os.path.join(GITHUB_SDK, "rust", "src")
    os.makedirs(rust_target, exist_ok=True)
    shutil.copy2(os.path.join(DEV_DIR, "rust", "src", "lib.rs"), rust_target)

    # 9. 跑测脚本与规范文档
    shutil.copy2(os.path.join(DEV_DIR, "run_all_tests.sh"), GITHUB_SDK)
    shutil.copy2(os.path.join(DEV_DIR, "FORMAT.md"), GITHUB_SDK)
    shutil.copy2(os.path.join(DEV_DIR, "API_CONTRACT.md"), GITHUB_SDK)
    print("✅ 源码与文档同步拷贝完成。")

def clean_and_verify():
    print("\n🧹 Step 2: 严格清理多余临时文件与测试用例...")
    # 清理编译产物
    for root, dirs, files in os.walk(GITHUB_SDK):
        for d in list(dirs):
            if d in ["bin", "obj", "target", "nupkg", ".claude", ".omc", ".omo", ".workbuddy"]:
                shutil.rmtree(os.path.join(root, d), ignore_errors=True)
        for f in files:
            if f.endswith(".qzdb") or f.endswith(".o") or f.endswith(".so") or f.endswith(".a") or f.endswith(".dll"):
                os.remove(os.path.join(root, f))
    print("✅ 临时文件与数据库文件检查清理完毕。")

def refresh_descriptions_and_commit(do_push=False):
    print("\n📝 Step 3: 按严格拓扑层级生成 Commit 描述...")
    ts = int(time.time())
    
    # 先把基础代码变动 commit 掉
    run("git add .", cwd=GITHUB_REPO, check=False)
    run('git commit -m "feat(sdk): multi-language optimizations and documentation update"', cwd=GITHUB_REPO, check=False)

    for path, commit_msg in FOLDER_SEQUENCE:
        readme_path = os.path.join(GITHUB_REPO, path, "README.md")
        if os.path.exists(readme_path):
            with open(readme_path, "r", encoding="utf-8") as f:
                content = f.read()
            lines = [l for l in content.splitlines() if not l.startswith("<!-- commit:")]
            new_content = "\n".join(lines).strip() + f"\n\n<!-- commit: {commit_msg} sync={ts} -->\n"
            with open(readme_path, "w", encoding="utf-8") as f:
                f.write(new_content)
            run(f'git add "{readme_path}"', cwd=GITHUB_REPO)
            run(f'git commit -m "{commit_msg}"', cwd=GITHUB_REPO)
            print(f"  ✅ [Commit 成功] {path} -> {commit_msg}")

    if do_push:
        print("\n🚀 Step 4: 推送到远程 GitHub origin/main...")
        run("git push origin main", cwd=GITHUB_REPO)
        print("🎉 全部同步并推送到远程成功！")
    else:
        print("\n💡 提示: 本地已完成提交。若需自动推送，请加参数: --push")

if __name__ == "__main__":
    should_push = "--push" in sys.argv
    sync_sdk_files()
    clean_and_verify()
    refresh_descriptions_and_commit(do_push=should_push)
