#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
遍历仓库内所有 Markdown 文件，检查相对链接的有效性，确保零断链。
用于 CI 自动化门禁。
"""

import re
import sys
from pathlib import Path

ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parent.parent

EXCLUDED_PARTS = {"node_modules", ".git", "target", "build", "dist", ".gemini", ".claude", ".bsk", "Library", "vendor"}

def main():
    md_files = list(ROOT.glob("**/*.md"))
    md_files = [f for f in md_files if not any(p in EXCLUDED_PARTS for p in f.relative_to(ROOT).parts)]

    link_pattern = re.compile(r"\[([^\]]+)\]\(([^)]+)\)")
    broken = []

    for md in md_files:
        try:
            content = md.read_text(encoding="utf-8", errors="ignore")
        except Exception as e:
            print(f"Warning: could not read {md}: {e}", file=sys.stderr)
            continue

        for match in link_pattern.finditer(content):
            text, url = match.groups()
            # Ignore absolute URLs, protocol schemes, and anchor-only links
            if url.startswith(("http://", "https://", "mailto:", "#")):
                continue
            # Strip query params and anchor
            clean_url = url.split("#")[0].split("?")[0]
            if not clean_url:
                continue

            # Check relative target
            target = (md.parent / clean_url).resolve()
            if not target.exists():
                broken.append((str(md.relative_to(ROOT)), url, str(target)))

    if broken:
        print(f"FAILED: Found {len(broken)} broken Markdown relative links:", file=sys.stderr)
        for src, url, full_target in broken:
            print(f"  * {src} -> '{url}' (target does not exist: {full_target})", file=sys.stderr)
        return 1

    print(f"PASSED: Verified {len(md_files)} Markdown files. 0 broken relative links found.")
    return 0

if __name__ == "__main__":
    sys.exit(main())
