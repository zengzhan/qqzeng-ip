#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
QZDB 官方一键同步脚本 (Dev -> GitHub)
=====================================
功能：
1. 精准同步 multi-lang 下各语言 SDK 源码到 GitHub 仓库 (ip-qzdb-sdk)
2. 同步「发布仓库根级元数据」(tools/publish_meta/ -> 发布仓库根)：composer.json /
   .gitattributes / LICENSE。这些文件活在**发布仓库根目录**，其路径语境是
   `ip-qzdb-sdk/`，与开发仓库的 `multi-lang/` 不同，因此单独放在 publish_meta/ 下维护
3. 自动过滤所有测试文件、基准测试、二进制 *.qzdb 库文件与内部配置
4. 严格按照拓扑顺序（先子目录、再顶级目录）逐一提交专属 Commit 描述
5. 自动推送到远程 GitHub 仓库

用法：
    python3 tools/sync_to_github.py [--push]
"""

import os
import sys
import time
import shutil
import subprocess

DEV_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "multi-lang"))
GITHUB_REPO = os.environ.get("QZDB_GITHUB_REPO",
            "/Users/zengxiangzhan/ZengData/网站/GitHub/qqzeng-ip/qqzeng-ip")
DEV_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
GITHUB_SDK = os.path.join(GITHUB_REPO, "ip-qzdb-sdk")

# 目录与 Commit 描述映射（严格先子目录后顶级目录）
FOLDER_SEQUENCE = [
    # 1. 先刷 ip-qzdb-sdk 8 个子语言
    ("ip-qzdb-sdk/rust", "rust: Rust SDK（mmap 只读映射，内存安全）"),
    ("ip-qzdb-sdk/c", "c: C SDK（零拷贝 mmap 读取，单文件集成）"),
    ("ip-qzdb-sdk/go", "go: Go SDK（跨平台 mmap，无锁并发查询）"),
    ("ip-qzdb-sdk/netcore", "netcore: C# .NET SDK（内存映射与高并发查询）"),
    ("ip-qzdb-sdk/java", "java: Java SDK（堆外内存与 Builder API）"),
    ("ip-qzdb-sdk/nodejs", "nodejs: Node.js SDK（BigInt 偏移解析）"),
    ("ip-qzdb-sdk/php", "php: PHP SDK（纯 PHP 实现，缓冲与流式双模式）"),
    ("ip-qzdb-sdk/python", "python: Python SDK（mmap 轻量读取）"),
    # 2. 顶级产品目录
    ("ip-qzdb-sdk", "ip-qzdb-sdk: QZDB 多语言 SDK（Rust/C/Go/Java/C#/Node.js/PHP/Python）"),
    ("ip-classic-sdk", "ip-classic-sdk: 经典版 IP 数据库 SDK（6.0 .db 与 2.0 .dat 多语言源码）"),
    ("ip-history-sdk", "ip-history-sdk: 历史版本与工具（3.0~5.0 演进与桌面查询工具）"),
    ("phone-location-sdk", "phone-location-sdk: 手机号段归属地 DAT 解析 SDK（2.0~6.0 全版本多语言）"),
    ("database-sql", "database-sql: MySQL / PostgreSQL / SQL Server 建表与入库 DDL"),
    ("demo", "demo: 归属地与号段 CSV/TXT 及 QZDB 演示样本数据"),
    ("docs", "docs: 设计文档、性能基准对比与维护指南"),
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
    # .editorconfig 必须随 SDK 走：netcore 开了 AnalysisMode=All + TreatWarningsAsErrors，
    # 每个 CA 告警都必须显式修复或带理由抑制，而这些抑制全写在 .editorconfig 里。
    # 缺它时发布仓库的 C# SDK 会把 50 条已裁决的 CA 规则（如 CA1031）重新报成 error。
    if os.path.exists(os.path.join(DEV_DIR, "netcore", ".editorconfig")):
        shutil.copy2(os.path.join(DEV_DIR, "netcore", ".editorconfig"), os.path.join(GITHUB_SDK, "netcore"))

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
    # LICENSE 必须随 crate 走：cargo 只打包 package 目录内的 LICENSE，不会向上查找仓库根，
    # 缺它 crates.io 页面会显示无许可证文件
    if os.path.exists(os.path.join(DEV_DIR, "rust", "LICENSE")):
        shutil.copy2(os.path.join(DEV_DIR, "rust", "LICENSE"), os.path.join(GITHUB_SDK, "rust"))
    rust_target = os.path.join(GITHUB_SDK, "rust", "src")
    os.makedirs(rust_target, exist_ok=True)
    shutil.copy2(os.path.join(DEV_DIR, "rust", "src", "lib.rs"), rust_target)

    # 9. 跑测脚本与规范文档
    shutil.copy2(os.path.join(DEV_DIR, "run_all_tests.sh"), GITHUB_SDK)
    shutil.copy2(os.path.join(DEV_DIR, "FORMAT.md"), GITHUB_SDK)
    shutil.copy2(os.path.join(DEV_DIR, "API_CONTRACT.md"), GITHUB_SDK)
    shutil.copy2(os.path.join(DEV_ROOT, "CHANGELOG.md"), GITHUB_SDK)
    print("✅ 源码与文档同步拷贝完成。")

def sync_publish_meta():
    """同步「发布仓库根级元数据」。

    这些文件不属于任何单一语言 SDK，而是整个 GitHub 发布仓库的根级配置：
      - composer.json   Packagist 只认仓库根的 composer.json（不支持子目录），
                        classmap 指向 `ip-qzdb-sdk/php/QzdbReader.php`
      - .gitattributes  export-ignore 裁剪 dist，路径同样是 `ip-qzdb-sdk/`
      - LICENSE         Packagist / crates.io 都要求包内带许可证文件

    关键坑：两个仓库目录结构不同（开发 `multi-lang/` vs 发布 `ip-qzdb-sdk/`），
    所以这些文件的内容**必须按发布仓库语境书写**。放在 tools/publish_meta/ 下
    作为单一事实来源，由此函数拷到发布仓库根；在发布仓库里直接改会被下次同步覆盖。
    """
    print("\n📋 Step 1b: 同步发布仓库根级元数据 (composer.json / .gitattributes / LICENSE)...")
    meta_dir = os.path.join(DEV_ROOT, "tools", "publish_meta")
    if not os.path.isdir(meta_dir):
        print(f"  ⚠️ 跳过：{meta_dir} 不存在")
        return
    for name in ("composer.json", ".gitattributes"):
        src = os.path.join(meta_dir, name)
        if os.path.exists(src):
            shutil.copy2(src, os.path.join(GITHUB_REPO, name))
            print(f"  ✅ {name} -> 发布仓库根")
    lic = os.path.join(DEV_ROOT, "LICENSE")
    if os.path.exists(lic):
        shutil.copy2(lic, os.path.join(GITHUB_REPO, "LICENSE"))
        print("  ✅ LICENSE -> 发布仓库根")

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
    sync_publish_meta()
    clean_and_verify()
    refresh_descriptions_and_commit(do_push=should_push)
