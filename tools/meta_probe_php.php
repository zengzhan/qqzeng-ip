<?php
// 元信息探针（PHP）：输出与 meta_probe_node.js 同构的 JSON。
require_once __DIR__ . '/../multi-lang/php/QzdbReader.php';

use Qqzeng\Ip\QzdbReader;

$out = [];
foreach (array_slice($argv, 1) as $f) {
    $r = new QzdbReader($f);
    $out[] = [
        'file' => basename($f),
        'lang' => 'php',
        'edition' => $r->getEdition(),
        'edition_source' => $r->getEditionSource(),
        'version_mask' => $r->getVersionMask(),
        'field_names_source' => $r->getFieldNamesSource(),
        'field_names' => $r->getFieldNames(),
        'group_count' => $r->getGroupCount(),
        'pool_count' => $r->getPoolCount(),
        'data_month' => $r->getDataMonth(),
    ];
    $r->close();
}
echo json_encode($out, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
