#!/usr/bin/env python3
"""元信息跨语言对拍器。

用法:
    meta_compare.py <baseline.json> <other.json> [<other2.json> ...]

每个 JSON 是探针输出的数组，元素含 KEYS 中的字段。以第一个文件为基线，
逐文件、逐字段比对，输出简明差异报告；全绿返回 0，有差异返回 1。
"""
import json
import sys

KEYS = ('edition', 'edition_source', 'version_mask', 'field_names_source',
        'field_names', 'group_count', 'pool_count', 'data_month')


def load(path):
    with open(path, 'r', encoding='utf-8') as fh:
        rows = json.load(fh)
    return {r['file']: r for r in rows}, (rows[0]['lang'] if rows else path)


def main():
    if len(sys.argv) < 3:
        sys.stderr.write(__doc__)
        return 2

    base, base_lang = load(sys.argv[1])
    failed = False

    for path in sys.argv[2:]:
        other, lang = load(path)
        diffs = []

        only_base = sorted(set(base) - set(other))
        only_other = sorted(set(other) - set(base))
        for f in only_base:
            diffs.append(f'  {f}: MISSING in {lang}')
        for f in only_other:
            diffs.append(f'  {f}: EXTRA in {lang}')

        for f in sorted(set(base) & set(other)):
            for k in KEYS:
                a, b = base[f].get(k), other[f].get(k)
                if a != b:
                    diffs.append(f'  {f}.{k}: {base_lang}={a!r} {lang}={b!r}')

        n = len(set(base) & set(other))
        if diffs:
            failed = True
            print(f'[FAIL] {base_lang} <-> {lang}: {len(diffs)} diff(s) over {n} file(s)')
            for d in diffs[:40]:
                print(d)
            if len(diffs) > 40:
                print(f'  ... and {len(diffs) - 40} more')
        else:
            print(f'[ OK ] {base_lang} <-> {lang}: {n}/{n} files ALL MATCH')

    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main())
