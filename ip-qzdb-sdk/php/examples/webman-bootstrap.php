<?php
// examples/webman-bootstrap.php —— Webman（Workerman 常驻）集成示例（QZDB PHP SDK）
//
// 本文件把几个类 / 函数放在一起只是为了便于阅读和 `php -l`；落地时按下面的路径拆：
//   App\Qzdb\Qzdb            -> app/Qzdb/Qzdb.php            （全局调用封装）
//   App\Bootstrap\QzdbBootstrap -> app/Bootstrap/QzdbBootstrap.php
//   App\Middleware\IpRiskMiddleware -> app/Middleware/IpRiskMiddleware.php
//   ip_geo() / ip_risk()     -> app/helper.php
//
// 注册步骤（webman 2.x / workerman 5.x 骨架）：
//   1. composer require qqzeng/qzdb（命名空间 Qqzeng\Ip，单文件零依赖）
//   2. .env：QZDB_PATH=/data/qqzeng_ip_max_global.qzdb
//   3. config/qzdb.php（新建，webman 里 config() 助手可直接读到）：
//        return [
//            'path'        => env('QZDB_PATH', '/data/qqzeng_ip_max_global.qzdb'),
//            'warmup'      => true,
//            'blocked'     => ['DataCenter', 'Cloud', 'VPN', 'AICrawler'],
//            'fpm_verify_crc' => false,
//        ];
//   4. config/bootstrap.php 追加一行（Webman 在 onWorkerStart 里逐个调 Bootstrap 接口）：
//        \App\Bootstrap\QzdbBootstrap::class,
//   5. config/middleware.php 追加全局中间件：
//        return [ \App\Middleware\IpRiskMiddleware::class ];
//   6. composer.json 的 autoload.files 里加上 "app/helper.php"，全局函数即自动可用。
//   7. 热更新：库文件先写临时名、校验完整后 rename() 覆盖正式路径（契约 §四.2 禁止
//      覆写正在被打开的 .qzdb）。之后二选一：
//        a) 进程内 reload：workerman/crontab 定时任务里调 Qzdb::reload($newPath)；
//        b) 进程级重启：php webman reload（优雅重启全部 worker，重新走 onWorkerStart）。
//      每个 worker 各自持有一份库（README 第 4.7 节），所以定时 reload 要每个 worker
//      都触发；workerman 的定时任务在 master/各 worker 行为不同，稳妥做法是把 reload
//      挂在「每个 worker 都跑」的进程内定时器上，或者干脆走 b)。
//
// 为什么这样写：
// - Workerman 是常驻多进程，onWorkerStart 之后进程不退出，库只加载一次；
//   总内存 ≈ worker 进程数 × 单 worker 常驻（README 第 4.6 节的公式），
//   不是「全机共享一份」，上线前用 `count($reader->getFieldNames())` 之外的方式
//   实测：memory_get_usage(true) 打点，或直接看 ps 的 RSS。
// - 中间件只解码请求需要的 4~6 列（findFields），不把 toPipe() 的整串再手工切割；
//   分类判断走 getUsageType() 的强类型对象（README 第 6.4 节，21 个官方分类）。
// - close() 必须显式调：Workerman 进程退出不会跑你的请求收尾逻辑，
//   SDK 的 __destruct 在长驻进程里也可能拖到很晚。
//
// 双运行时自适应（README 第 4.6 / 4.7 节）：
// - 常驻（webman / workerman，PHP_SAPI=cli）：MEMORY_BALANCED + verifyCrc(true) +
//   onWorkerStart 里 warmup()，跨请求零 I/O。
// - 同一份 Qzdb 封装也可能被 FPM 项目或 php -S 内置服务器复用（本地起 webman
//   之外的调试场景）：这时每个请求都要重建 reader，改用 MEMORY_LOW + verifyCrc(false)，
//   CRC 放部署脚本离线跑一次（php -r 里 build 一遍就够，见 README 第 15.3 节）。
//
// 注意：
// - 本文件不 implements 任何 Webman/Workerman 接口（本机没装框架类时也能 php -l 通过）。
//   真实项目里把注释里的 implements 补上：目标接口分别是
//   Webman\Bootstrap::onWorkerStart(?Worker $worker = null) 与
//   Webman\MiddlewareInterface::process(Request $request, callable $handler): Response，
//   方法名与参数已按这两个接口写好，加上 implements 就能直接用。
// - Workerman 的 worker 不跑协程，请求处理是单线程串行的，因此这里用原地 reload()
//   是安全的；如果你换到协程运行时（Hyperf/Swoole），请改用
//   examples/hyperf-service.php 里的「整实例切换」写法。

declare(strict_types=1);

namespace App\Qzdb {

    use Qqzeng\Ip\GeoInfo;
    use Qqzeng\Ip\QzdbBuilder;
    use Qqzeng\Ip\QzdbException;
    use Qqzeng\Ip\QzdbReader;

    /**
     * 全局调用封装：Qzdb::reader() / Qzdb::geo() / Qzdb::reload()。
     */
    final class Qzdb
    {
        /** @var QzdbReader|null 本 worker 进程内唯一的 reader */
        private static $reader = null;

        /** 常驻运行时判定：Workerman/Webman 是 cli，FPM/CGI/内置服务器不是。 */
        public static function isResident(): bool
        {
            $override = getenv('QZDB_RESIDENT');
            if ($override !== false && $override !== '') {
                return $override === '1' || strtolower((string) $override) === 'true';
            }
            if (in_array(PHP_SAPI, ['fpm-fcgi', 'cgi-fcgi', 'cgi', 'litespeed', 'cli-server'], true)) {
                return false;
            }
            return class_exists('Workerman\Worker', false) || class_exists('Webman\App', false);
        }

        /**
         * 装配 Builder：常驻 = balanced + CRC + 预热；FPM = low + 免 CRC。
         *
         * @param array $cfg config('qzdb') 的数组
         */
        public static function builderFor(string $path, array $cfg): QzdbBuilder
        {
            $resident = self::isResident();
            return QzdbBuilder::path($path)
                ->memoryMode($resident ? QzdbReader::MEMORY_BALANCED : QzdbReader::MEMORY_LOW)
                // 常驻：worker 生命周期里只校验一次。FPM：默认不校验（部署期已校验过）。
                ->verifyCrc($resident ? true : (bool) ($cfg['fpm_verify_crc'] ?? false));
        }

        /**
         * 启动期初始化（QzdbBootstrap 调）。库缺失 / 损坏时抛 QzdbException：
         * worker 启动失败并退出，别让半死的实例对外服务。
         */
        public static function init(array $cfg): QzdbReader
        {
            $env = getenv('QZDB_PATH');
            $path = (string) ($cfg['path'] ?? ($env !== false && $env !== '' ? $env : '/data/qqzeng_ip_max_global.qzdb'));
            $reader = self::builderFor($path, $cfg)->build();
            if (self::isResident() && (bool) ($cfg['warmup'] ?? true)) {
                $reader->warmup(); // 只在缓冲模式下生效，预触 Trie 跳表 / 节点表所在页
            }
            self::$reader = $reader;
            return $reader;
        }

        /** 业务侧取值：FPM 场景下没跑 init()，这里惰性建一次（每请求一次）。 */
        public static function reader(): QzdbReader
        {
            if (!self::$reader instanceof QzdbReader) {
                $cfg = function_exists('config') ? (array) config('qzdb') : [];
                self::init($cfg);
            }
            return self::$reader;
        }

        /**
         * 只解码需要的列。未命中 / 非法 IP 返回 null（README 第 3 节 Fail-Soft）。
         *
         * @param string[] $fields
         */
        public static function geo(string $ip, array $fields = ['country_code', 'province', 'city', 'isp', 'asn', 'usage_type']): ?GeoInfo
        {
            return self::reader()->findFields($ip, $fields);
        }

        /**
         * 进程内热更新：沿用当前内存模式、强制 CRC；失败抛异常且旧快照继续服务。
         * Workerman 单线程处理请求，原地 reload() 不会与查询交错（协程运行时请走整实例切换）。
         */
        public static function reload(string $newPath): bool
        {
            if (self::$reader === null) {
                return false;
            }
            self::$reader->reload($newPath);
            return true;
        }

        /** worker 退出前显式释放文件句柄与缓存。 */
        public static function shutdown(): void
        {
            if (self::$reader instanceof QzdbReader) {
                try { self::$reader->close(); } catch (\Throwable $e) {}
                self::$reader = null;
            }
        }

        /** 供中间件打印 edition / 数据月份，确认线上加载的是哪一版库。 */
        public static function describe(): string
        {
            if (self::$reader === null) {
                return 'not_loaded';
            }
            return sprintf('edition=%s dataMonth=%s mode=%s fields=%d',
                self::$reader->getEdition(), self::$reader->getDataMonth(),
                self::$reader->getMemoryMode(), count(self::$reader->getFieldNames()));
        }
    }
}

namespace App\Bootstrap {

    use App\Qzdb\Qzdb;

    /**
     * Webman 启动引导。
     *
     * 真实项目补上接口（本文件为无框架机器也能 php -l 通过，不写 implements）：
     *   use Webman\Bootstrap;
     *   use Workerman\Worker;
     *   final class QzdbBootstrap implements Bootstrap
     * 接口要求的方法就是 onWorkerStart(?Worker $worker = null): void，签名已对齐。
     */
    final class QzdbBootstrap
    {
        /**
         * @param object|null $worker Workerman\Worker 实例（真实项目里类型写 ?Worker）
         */
        public function onWorkerStart($worker = null): void
        {
            $cfg = function_exists('config') ? (array) config('qzdb') : [];
            try {
                Qzdb::init($cfg);
            } catch (\Throwable $e) {
                // 启动期失败要 loud：抛出去让 worker 退出，Workerman 会按策略重启。
                // 吞掉异常 = 用「查不到」冒充「没有风险 IP」，风控场景不可接受。
                error_log('[qzdb] worker start load FAILED: [' . $e->getCode() . '] ' . $e->getMessage());
                throw $e;
            }
            error_log('[qzdb] worker #' . ($worker !== null && isset($worker->id) ? $worker->id : '?')
                . ' ready, ' . Qzdb::describe());
        }
    }
}

namespace App\Middleware {

    use App\Qzdb\Qzdb;
    use Closure;
    use Qqzeng\Ip\GeoInfo;
    use Qqzeng\Ip\QzdbException;

    /**
     * IP 风控中间件：按 usage_type 分类放行 / 拦截。
     *
     * 真实项目补上接口：
     *   use Webman\MiddlewareInterface;
     *   use Webman\Http\Request;
     *   use Webman\Http\Response;
     *   final class IpRiskMiddleware implements MiddlewareInterface
     *   {
     *       public function process(Request $request, callable $handler): Response { ... }
     *   }
     * 本文件用 object / 无返回类型声明，保证没有框架类时依旧能解析。
     *
     * 注册：config/middleware.php 返回 [ \App\Middleware\IpRiskMiddleware::class ]（全局中间件）。
     */
    final class IpRiskMiddleware
    {
        /** 只解码风控需要的列：国家 / 用途 / 机房属性，一次查询不到 10 个字段 */
        private const FIELDS = ['country_code', 'province', 'isp', 'usage_type'];

        /** 默认拦截的网络类型（可被 config('qzdb.blocked') 覆盖） */
        private const DEFAULT_BLOCKED = ['DataCenter', 'Cloud', 'VPN', 'AICrawler'];

        public function process(object $request, callable $handler)
        {
            $ip = self::clientIp($request);

            try {
                // findFields 只解码请求列；未命中 / 非法 IP 返回 null，不抛异常。
                $info = Qzdb::geo($ip, self::FIELDS);
            } catch (QzdbException $e) {
                // 策略：fail-open（放行 + 告警）。风控要求严格时改成直接 503。
                error_log('[qzdb] lookup FAILED, fail-open ip=' . $ip . ' [' . $e->getCode() . '] ' . $e->getMessage());
                return $handler($request);
            }

            if ($info instanceof GeoInfo) {
                $usage = $info->getUsageType();          // UsageType 对象，不是字符串切割
                $blocked = (array) (function_exists('config') ? config('qzdb.blocked') : self::DEFAULT_BLOCKED);
                if (!$blocked) {
                    $blocked = self::DEFAULT_BLOCKED;
                }
                if ($usage->isKnown() && in_array($usage->rawValue(), $blocked, true)) {
                    return self::deny($usage->rawValue(), $usage->getDisplayZh(), $info);
                }
            }

            return $handler($request);
        }

        /**
         * 取真实客户端 IP。
         *
         * Webman 的 Request 由 Symfony HttpFoundation 派生：优先框架自带的取 IP 方法，
         * 再退到 getClientIp()（依赖 proxy_trusted_headers 配置），最后退 REMOTE_ADDR。
         * 没有声明可信代理时，伪造的 X-Forwarded-For 不该被采信——这里刻意不手工读头。
         */
        private static function clientIp(object $request): string
        {
            foreach (['getRealIp', 'getClientIp', 'ip'] as $m) {
                if (method_exists($request, $m)) {
                    $ip = (string) $request->$m();
                    if ($ip !== '') {
                        return $ip;
                    }
                }
            }
            return (string) ($_SERVER['REMOTE_ADDR'] ?? '');
        }

        /**
         * @param GeoInfo|null $info
         * @return object 真实项目里返回 Webman\Http\Response
         */
        private static function deny(string $usage, string $usageZh, ?GeoInfo $info)
        {
            // webman 助手函数 json() 返回 Response；这里加守卫，方便本机单测调用本方法。
            $body = [
                'error'        => 'forbidden_network_type',
                'usage_type'   => $usage,
                'usage_label'  => $usageZh,
                'country_code' => $info instanceof GeoInfo ? $info->getCountryCode() : '',
                'isp'          => $info instanceof GeoInfo ? $info->getIsp() : '',
            ];
            if (function_exists('json')) {
                $response = json($body);
                if (is_object($response) && method_exists($response, 'withStatus')) {
                    return $response->withStatus(403);
                }
                return $response;
            }
            if (!headers_sent()) {
                http_response_code(403);
            }
            echo json_encode($body);
            return null;
        }
    }
}

namespace {

    use App\Qzdb\Qzdb;
    use Qqzeng\Ip\GeoInfo;

    if (!function_exists('ip_geo')) {
        /**
         * 全局辅助：一行拿到结构化地理信息（controller / view / 命令行通用）。
         * 只解码传入的列，未命中返回 null。
         *
         *   $geo = ip_geo($ip);                              // 默认 6 列
         *   $geo = ip_geo($ip, ['country_code', 'asn']);     // 只取两列
         *
         * @param string[] $fields
         * @return array<string, mixed>|null
         */
        function ip_geo(string $ip, ?array $fields = null): ?array
        {
            $cols = $fields === null
                ? ['country_code', 'province', 'city', 'isp', 'asn', 'usage_type']
                : $fields;
            // usage_label 是派生列，真身是库里的 usage_type
            if (in_array('usage_label', $cols, true) && !in_array('usage_type', $cols, true)) {
                $cols[] = 'usage_type';
            }
            $info = Qzdb::geo($ip, $cols);
            if (!$info instanceof GeoInfo) {
                return null;
            }
            $out = [];
            foreach ($cols as $col) {
                // 强类型 getter 优先；库里没有的列退回 GeoInfo::get()（未收录返回 ""，不抛）
                switch ($col) {
                    case 'country_code': $out[$col] = $info->getCountryCode(); break;
                    case 'province':     $out[$col] = $info->getProvince(); break;
                    case 'city':         $out[$col] = $info->getCity(); break;
                    case 'isp':          $out[$col] = $info->getIsp(); break;
                    case 'asn':          $out[$col] = $info->getAsn(); break;            // ?int
                    case 'usage_type':   $out[$col] = $info->getUsageType()->rawValue(); break;
                    case 'usage_label':  $out[$col] = $info->getUsageType()->getDisplayZh(); break;
                    default:             $out[$col] = $info->get($col);
                }
            }
            return $out;
        }
    }

    if (!function_exists('ip_risk')) {
        /** 全局辅助：只关心「这条 IP 是什么网络类型」，返回原始分类字符串。 */
        function ip_risk(string $ip): string
        {
            $info = Qzdb::geo($ip, ['usage_type']);
            return $info instanceof GeoInfo ? $info->getUsageType()->rawValue() : '';
        }
    }
}
