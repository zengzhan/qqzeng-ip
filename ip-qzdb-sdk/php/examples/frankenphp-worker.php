<?php
// examples/frankenphp-worker.php —— FrankenPHP worker 模式示例（QZDB PHP SDK）
//
// 用法（启动时只加载一次数据库，之后每个请求只做一次查询）：
//   FRANKENPHP_CONFIG="worker ./ip-qzdb-sdk/php/examples/frankenphp-worker.php 8" \
//     QZDB_PATH=/data/qqzeng_ip_ult_global.qzdb \
//     MAX_REQUESTS=500 \
//     frankenphp php-server
//
// 也可以写进 Caddyfile（worker 数量写在脚本路径后面，默认是每 CPU 2 个）：
//   frankenphp {
//       worker ./ip-qzdb-sdk/php/examples/frankenphp-worker.php 8
//   }
//
// 为什么这样写：QzdbReader 启动时把库文件读进内存并校验 CRC，之后查询只读
// 不可变快照。FPM 下每次请求都要重走这一遍；worker 模式下这一步每个 worker
// 生命周期只跑一次。内存按 README 第 4.6 节的公式估算：总量 = worker 数 ×
// 单 worker 峰值（当前 data/ 的 ult_global 约 66 MB；第 4.6 节对旧版约
// 116 MB 文件给的是单 worker 125～150 MB，8 核默认 16 个 worker 就要
// 约 2.0～2.4 GB）。上线前务必在目标机上实测单 worker 峰值，不要只按
// 文件大小留内存。
//
// 注意：
// - 内存上限要够。memory_limit 太小的话，SDK 会按 README 第 4.5 节自动切到
//   流式模式（fseek/fread 分页读），那就把“只加载一次”的好处又丢回去了。
// - $_GET / $_POST / $_SERVER 每个请求会自动重置，但 $_ENV 不会重置，static
//   变量、全局变量、类静态属性也会跨请求保留（FrankenPHP 官方文档原话）。
//   所以请求相关的值不要往 $_ENV 和 static 里放。
// - MAX_REQUESTS 是兜底回收：每个 worker 处理够 500 个请求就退出重建，
//   防慢泄漏。重启全部 worker 用 Caddy 管理接口：
//   curl -X POST http://localhost:2019/frankenphp/workers/restart
// - 常驻进程退出前必须显式 close()（README 第 4.6 节），否则文件句柄和
//   分页缓存会一直占着。

declare(strict_types=1);

require __DIR__ . '/../QzdbReader.php';

use Qqzeng\Ip\QzdbReader;

if (!function_exists('frankenphp_handle_request')) {
    fwrite(STDERR, "This script must run under FrankenPHP worker mode.\n");
    exit(1);
}

// 启动期（每个 worker 只跑一次）：加载数据库 + CRC 校验。
// 构造签名已按 QzdbReader.php 核对：__construct($dbPath, $groupIndex, $verifyCrc)。
// 这里 groupIndex 用 0（默认分组），verifyCrc 用 true（启动时校验一次即可）。
// 库文件不存在或损坏时这里直接抛 QzdbException，worker 退出，FrankenPHP
// 会按退避策略重启；这是故意的——启动期失败就该 loud，不要带病服务。
$dbPath = $_SERVER['QZDB_PATH'] ?? '/data/qqzeng_ip_ult_global.qzdb';
$reader = new QzdbReader($dbPath, 0, true);

$handler = static function () use ($reader): void {
    header('Content-Type: application/json; charset=utf-8');
    try {
        $ip = $_GET['ip'] ?? '';
        if ($ip === '') {
            http_response_code(400);
            echo '{"error":"missing_ip"}';
            return;
        }
        // find() 查不到（非法 IP 或库里没有）返回 null，不抛异常。
        $info = $reader->find($ip);
        if ($info === null) {
            http_response_code(404);
            echo '{"error":"not_found"}';
            return;
        }
        echo $info->toJson();
    } catch (\Throwable $e) {
        http_response_code(500);
        echo json_encode(['error' => $e->getMessage()]);
    }
};

// MAX_REQUESTS 在第一次处理请求前读：这时 $_SERVER 还是 worker 自身的环境变量；
// 一旦开始处理请求，$_SERVER 会被每次请求的值覆盖，所以不能放在循环里读。
// 官方示例默认 0（不限）；这里默认 500，作兜底回收。
$maxRequests = (int)($_SERVER['MAX_REQUESTS'] ?? 500);

for ($i = 0; !$maxRequests || $i < $maxRequests; ++$i) {
    $keep = \frankenphp_handle_request($handler);
    gc_collect_cycles();
    error_clear_last();
    if (!$keep) {
        break;
    }
}

$reader->close();
