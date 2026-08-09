#!/usr/bin/env node
// 元信息探针（Node.js）：把每个库的自描述判定结果打成一行 JSON，
// 供 cross_verify_meta.py 做 8 语言逐字段对拍。
'use strict';

const path = require('path');
const QzdbReader = require(path.join(__dirname, '..', 'multi-lang', 'nodejs', 'qzdb.js'));

const files = process.argv.slice(2);
const out = [];
for (const f of files) {
  const r = new QzdbReader();
  r.load(f);
  out.push({
    file: path.basename(f),
    lang: 'node',
    edition: r.getEdition(),
    edition_source: r.getEditionSource(),
    version_mask: r.getVersionMask(),
    field_names_source: r.getFieldNamesSource(),
    field_names: r.getFieldNames(),
    group_count: r.getGroupCount(),
    pool_count: r.getPoolCount(),
    data_month: r.getDataMonth(),
  });
  r.close();
}
process.stdout.write(JSON.stringify(out, null, 0));
