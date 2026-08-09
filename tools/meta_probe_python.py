#!/usr/bin/env python3
"""元信息探针（Python）：输出与 meta_probe_node.js 完全同构的 JSON。"""
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                '..', 'multi-lang', 'python'))
from qzdb import QzdbReader  # noqa: E402

out = []
for f in sys.argv[1:]:
    r = QzdbReader(f)
    out.append({
        'file': os.path.basename(f),
        'lang': 'python',
        'edition': r.get_edition(),
        'edition_source': r.get_edition_source(),
        'version_mask': r.get_version_mask(),
        'field_names_source': r.get_field_names_source(),
        'field_names': r.get_field_names(),
        'group_count': r.get_group_count(),
        'pool_count': r.get_pool_count(),
        'data_month': r.get_data_month(),
    })
    r.close()
sys.stdout.write(json.dumps(out, ensure_ascii=False))
