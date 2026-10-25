<?php
/**
 * 兼容占位 (薄包装): 历史上本目录的 IpSearch.php 是 Redis ZSET 查询示例脚本,
 * 现由标准版实现统一提供, 本文件仅保留目录结构向后兼容:
 *
 *   require '../qqzeng-ip-redis/IpSearch.php';  // 等价于 require 标准版
 *   $finder = new IpSearch();                   // 自动探测路径, 或 new IpSearch($path)
 *   echo $finder->find('8.8.8.8');
 *
 * Redis 导入 (ZSET 写入) 请使用本目录的 ip-import.php。
 */

require_once __DIR__ . '/../qqzeng-ip-php/IpSearch.php';
