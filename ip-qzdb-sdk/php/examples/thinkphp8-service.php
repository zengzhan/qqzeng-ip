<?php
// examples/thinkphp8-service.php —— ThinkPHP 8.0 集成示例（QZDB PHP SDK）
//
// 本文件把几个类 / 函数放在一起只是为了便于阅读和 `php -l`；落地时按下面的路径拆：
//   app\service\QzdbService            -> app/service/QzdbService.php   （系统服务类）
//   app\middleware\IpGeoMiddleware     -> app/middleware/IpGeoMiddleware.php
//   ip_geo() / ip_usage()              -> app/common.php                （辅助函数）
//
// 注册步骤（ThinkPHP 8.0 骨架）：
//   1. composer require qqzeng/qzdb（命名空间 Qqzeng\Ip，单文件零依赖）
//   2. .env：QZDB_PATH=/data/qqzeng_ip_max_global.qzdb
//   3. config/qzdb.php（新建，TP 里 config('qzdb.path') 直接读）：
//        return [
//            'path'              => env('qzdb.path', '/data/qqzeng_ip_max_global.qzdb'),
//            'resident'          => null,   // null = 自动判定；true/false 手工覆盖
//            'warmup'            => true,
//            'fpm_verify_crc'    => false,
//            'blocked_usage_types' => ['DataCenter', 'Cloud', 'VPN', 'AICrawler'],
//        ];
//   4. app/services.php 追加一行，把系统服务类注册进去：
//        return [ \app\service\QzdbService::class ];
//   5. app/provider.php —— 容器绑定风格（把抽象绑到我们的工厂闭包）：
//        return [
//            \Qqzeng\Ip\QzdbReader::class => function ($app) {
//                return \app\service\QzdbService::reader();
//            },
//        ];
//      注意：TP 的 provider 绑定不保证进程级单例（bind 每次都会构建），
//      所以闭包里只是转发到 QzdbService 自己的 static，真正的「只加载一次」
//      由 QzdbService::$reader 兜住。控制器里 `use Qqzeng\Ip\QzdbReader;`
//      + 构造器注入照样可用，拿到的都是同一份 reader。
//   6. app/middleware.php 追加全局中间件：
//        return [ \app\middleware\IpGeoMiddleware::class ];
//   7. 辅助函数写在 app/common.php（TP 自动加载该文件），本文件底部给了同名实现。
//
// 双运行时自适应（README 第 4.6 / 4.7 节）：
// - 传统 PHP-FPM（`php-cgi` / nginx + php-fpm，最常见的 TP 部署）：每个请求重新引导
//   应用、重新加载库，static 也活不过请求。所以默认走 MEMORY_LOW + verifyCrc(false)：
//   每请求只做一次分页读取加载，整库 CRC 放部署脚本离线校验一次
//   （`php -r` 或 `think` 命令里 build 一遍即可，见 README 第 15.3 节）。
// - think-swoole（`composer require topthink/think-swoole`，`php think swoole start`）：
//   worker 常驻、跨请求复用同一份 reader，走 MEMORY_BALANCED + verifyCrc(true) +
//   启动期 warmup()，并在 onWorkerStop / SIGTERM 里 close()。
//   判定见 QzdbService::isResident()：FPM / CGI 一律低内存；FrankenPHP / RoadRunner /
//   Webman 有明确运行时信号可自动判定；Swoole 不猜（`php think migrate:run` 这类一次性
//   命令和 worker 进程的 PHP_SAPI、argv 完全一样），所以 think-swoole 部署要显式声明：
//   .env 里 QZDB_RESIDENT=1，或 config/qzdb.php 里 'resident' => true。
//   判定不准时用 config('qzdb.resident') 覆盖。
// - 内存预算：think-swoole 的 worker 数（config/swoole.php 的 worker_num）×
//   单 worker 常驻量；62.6 MB 的库按 README 第 12.2 节的实测，单 worker findFields
//   口径约 68 MB、全字段 find() 口径约 100 MB，别按文件大小留。
//
// 热更新：
// - FPM：什么都不用做，下一个请求自然加载新文件（rename() 换掉即可）。
// - think-swoole 常驻：库文件先写临时名、校验完整后 rename() 覆盖正式路径
//   （契约 §四.2：严禁覆写 / 截断正在被进程打开的 .qzdb），再调
//   QzdbService::reload($path) —— 它走 SDK 的 reload()，强制 CRC、失败抛异常且
//   旧快照继续服务。think-swoole 默认不是协程并发处理同一请求，原地 reload() 安全；
//   自行开启协程并发的（Hyperf / Swoole\Runtime hook 全开），改用
//   examples/hyperf-service.php 里的「整实例切换」写法。
//
// 注意：
// - 本文件不 extends / implements 任何框架类（本机没装 ThinkPHP 时也能 php -l 通过）。
//   真实项目里补上：
//     final class QzdbService extends \think\Service      // 系统服务基类，boot() 由框架调用
//     final class IpGeoMiddleware                         // TP 中间件不要求接口，只要 handle($request, \Closure $next)
//   方法签名（boot / handle / ip_geo）都已按框架约定写好，加上 extends 即可用。
// - 全部示例都只用强类型 getter（getCountryCode / getProvince / getCity / getIsp /
//   getAsn / getUsageType）+ findFields() 按需取列，没有任何 explode('|') 手工切割。

declare(strict_types=1);

namespace app\service {

    use Qqzeng\Ip\GeoInfo;
    use Qqzeng\Ip\QzdbBuilder;
    use Qqzeng\Ip\QzdbException;
    use Qqzeng\Ip\QzdbReader;

    /**
     * 系统服务类：进程级持有 reader，并对外提供查询 / 热更新 / 释放。
     *
     * 真实项目：final class QzdbService extends \think\Service
     * think\Service 提供 protected $app 与 boot()，本类不依赖它也能跑（方便单测）。
     */
    class QzdbService
    {
        /** 默认只取这 6 列：够用且解码量最小 */
        const DEFAULT_FIELDS = ['country_code', 'province', 'city', 'isp', 'asn', 'usage_type'];

        /** @var QzdbReader|null 本进程唯一 reader（常驻时跨请求复用；FPM 时=本请求一份） */
        private static $reader = null;

        /** @var array 配置快照，避免每次查询都去读 config() */
        private static $cfg = [];

        /** @var int 已加载库文件的 mtime，供常驻侧比对 */
        private static $loadedMtime = 0;

        /**
         * 框架在应用引导阶段调用（TP 会反射注入 $this->app，这里不依赖它）。
         */
        public function boot(): void
        {
            self::$cfg = self::readConfig();
            // 常驻形态：启动期就加载 + 预热，把 47ms~数百 ms 的加载成本挪出请求路径。
            // FPM 形态：留给第一次真正查询时惰性加载（本请求反正只跑一次）。
            if (self::isResident()) {
                self::reader();
            }
        }

        /** 读 config/qzdb.php；脱离框架时（单测、CLI 脚本）退回环境变量与默认值。 */
        public static function readConfig(): array
        {
            $cfg = [];
            if (function_exists('config')) {
                try {
                    $cfg = (array) config('qzdb');
                } catch (\Throwable $e) {
                    $cfg = [];
                }
            }
            if (!isset($cfg['path']) || $cfg['path'] === '') {
                $env = getenv('QZDB_PATH');
                $cfg['path'] = $env !== false && $env !== ''
                    ? $env
                    : '/data/qqzeng_ip_max_global.qzdb';
            }
            return $cfg;
        }

        /** 常驻运行时判定：见文件头「双运行时自适应」。 */
        public static function isResident(): bool
        {
            $cfg = self::$cfg ? self::$cfg : self::readConfig();
            if (isset($cfg['resident']) && $cfg['resident'] !== null) {
                return (bool) $cfg['resident'];
            }
            $override = getenv('QZDB_RESIDENT');
            if ($override !== false && $override !== '') {
                return $override === '1' || strtolower((string) $override) === 'true';
            }
            if (in_array(PHP_SAPI, ['fpm-fcgi', 'cgi-fcgi', 'cgi', 'litespeed', 'cli-server'], true)) {
                return false; // PHP-FPM / CGI：每请求重建，走低内存分支
            }
            // CLI 下明确是常驻服务器的信号
            if (function_exists('frankenphp_handle_request')   // FrankenPHP worker
                || isset($_SERVER['RR_MODE'])                  // RoadRunner
                || class_exists('Workerman\Worker', false)) {  // Webman / Workerman
                return true;
            }
            // Swoole（think-swoole）不猜：`php think migrate:run` 这类一次性命令和
            // think-swoole 的 worker 进程，PHP_SAPI / argv 完全一样，靠自动判定不可靠。
            // 部署时在 .env 写 QZDB_RESIDENT=1，或 config/qzdb.php 里 'resident' => true。
            return false;
        }

        /**
         * 装配 Builder：常驻 = balanced + CRC + 预热；FPM = low + 免 CRC。
         */
        public static function builder(): QzdbBuilder
        {
            $cfg = self::$cfg ? self::$cfg : self::readConfig();
            $resident = self::isResident();
            return QzdbBuilder::path((string) $cfg['path'])
                ->memoryMode($resident ? QzdbReader::MEMORY_BALANCED : QzdbReader::MEMORY_LOW)
                // 常驻：worker 生命周期只校验一次，摊薄为零。
                // FPM：默认不校验（部署脚本已离线校验），可用 config 打开。
                ->verifyCrc($resident ? true : (bool) ($cfg['fpm_verify_crc'] ?? false));
        }

        /**
         * 取 reader（首次调用时加载）。
         *
         * 库文件缺失 / 损坏时这里抛 QzdbException：常驻下 worker 启动失败退出，
         * FPM 下由 TP 的全局异常处理接管。两种情况都不要吞掉——不要带病服务。
         */
        public static function reader(): QzdbReader
        {
            if (self::$reader instanceof QzdbReader) {
                return self::$reader;
            }
            $cfg = self::$cfg ? self::$cfg : self::readConfig();
            self::$cfg = $cfg;
            $reader = self::builder()->build();
            if (self::isResident() && !empty($cfg['warmup'])) {
                $reader->warmup(); // 只在缓冲模式下生效：预触 Trie 跳表 / 节点表所在页
            }
            self::$loadedMtime = (int) @filemtime((string) $cfg['path']);
            self::$reader = $reader;
            return $reader;
        }

        /**
         * 只解码需要的列，并只返回请求的列（SDK 字段投影，README 第 5 节 / 契约 §二.3）。
         * 未命中 / 非法 IP 返回 null，不抛（README 第 3 节 Fail-Soft）。
         *
         * @param string[]|null $fields 需要的列；不给用默认 6 列
         * @return array|null
         */
        public static function geo(string $ip, ?array $fields = null): ?array
        {
            $cols = $fields === null ? self::DEFAULT_FIELDS : $fields;
            // usage_label 是派生列（中文展示名），它的真身是库里的 usage_type 列
            if (in_array('usage_label', $cols, true) && !in_array('usage_type', $cols, true)) {
                $cols[] = 'usage_type';
            }
            $info = self::reader()->findFields($ip, $cols);
            if (!$info instanceof GeoInfo) {
                return null;
            }
            $out = [];
            foreach ($cols as $col) {
                // 全部走强类型 getter；库里没有的列交给 GeoInfo::get()，它返回 "" 不抛。
                switch ($col) {
                    case 'country_code': $out[$col] = $info->getCountryCode(); break;
                    case 'province':     $out[$col] = $info->getProvince(); break;
                    case 'province_en':  $out[$col] = $info->getProvinceEn(); break;
                    case 'city':         $out[$col] = $info->getCity(); break;
                    case 'city_en':      $out[$col] = $info->getCityEn(); break;
                    case 'isp':          $out[$col] = $info->getIsp(); break;
                    case 'asn':          $out[$col] = $info->getAsn(); break;            // ?int
                    case 'usage_type':   $out[$col] = $info->getUsageType()->rawValue(); break;
                    case 'usage_label':  $out[$col] = $info->getUsageType()->getDisplayZh(); break; // 「云服务」
                    default:             $out[$col] = $info->get($col);
                }
            }
            return $out;
        }

        /** 只要网络类型（风控判定的那一列），一次查询 1 个字段，最省。 */
        public static function usage(string $ip): string
        {
            $info = self::reader()->findFields($ip, ['usage_type']);
            return $info instanceof GeoInfo ? $info->getUsageType()->rawValue() : '';
        }

        /**
         * 进程内热更新（常驻形态用）。沿用当前内存模式、强制 CRC；
         * 失败抛 QzdbException 且旧快照继续服务。
         */
        public static function reload(?string $newPath = null): bool
        {
            if (!self::$reader instanceof QzdbReader) {
                return false;
            }
            $path = $newPath !== null && $newPath !== '' ? $newPath : (string) (self::$cfg['path'] ?? '');
            self::$reader->reload($path);
            self::$loadedMtime = (int) @filemtime($path);
            return true;
        }

        /** 库文件是否已被外部替换（rename 覆盖），常驻形态可在定时任务里检查。 */
        public static function isStale(): bool
        {
            $path = (string) (self::$cfg['path'] ?? '');
            if ($path === '' || self::$loadedMtime === 0) {
                return false;
            }
            clearstatcache(true, $path);
            $mtime = (int) @filemtime($path);
            return $mtime !== 0 && $mtime !== self::$loadedMtime;
        }

        /** 常驻进程退出前显式释放文件句柄与缓存（onWorkerStop / SIGTERM 里调）。 */
        public static function shutdown(): void
        {
            if (self::$reader instanceof QzdbReader) {
                try { self::$reader->close(); } catch (\Throwable $e) {}
                self::$reader = null;
            }
        }

        /** 打印当前加载的版本，排查「线上到底是哪一版库」很有用。 */
        public static function describe(): string
        {
            if (!self::$reader instanceof QzdbReader) {
                return 'not_loaded';
            }
            return sprintf('edition=%s dataMonth=%s mode=%s fields=%d',
                self::$reader->getEdition(), self::$reader->getDataMonth(),
                self::$reader->getMemoryMode(), count(self::$reader->getFieldNames()));
        }
    }
}

namespace app\middleware {

    use app\service\QzdbService;
    use Closure;
    use Qqzeng\Ip\GeoInfo;
    use Qqzeng\Ip\QzdbException;

    /**
     * IP 风控全局中间件。
     *
     * TP 的中间件不要求 implements 任何接口，只要有一个
     *   public function handle($request, Closure $next, ...$params)
     * 方法，本类已按该签名写好。注册在 app/middleware.php（全局中间件数组）。
     */
    class IpGeoMiddleware
    {
        /** 只解码风控需要的列 */
        private const FIELDS = ['country_code', 'province', 'isp', 'usage_type'];

        /**
         * @param object $request think\Request（TP6/8 的 Request 对象）
         * @return mixed
         */
        public function handle($request, Closure $next)
        {
            // $request->ip() 已按框架的代理信任配置解析过；伪造的 X-Forwarded-For 不会被采信。
            $ip = (string) (method_exists($request, 'ip') ? $request->ip() : ($_SERVER['REMOTE_ADDR'] ?? ''));

            try {
                $info = QzdbService::reader()->findFields($ip, self::FIELDS);
            } catch (QzdbException $e) {
                // 策略：fail-open（放行 + 日志）。风控要求严格时改成直接返回 503。
                if (class_exists('think\\facade\\Log')) {
                    \think\facade\Log::error('[qzdb] lookup failed, fail-open ip=' . $ip
                        . ' [' . $e->getCode() . '] ' . $e->getMessage());
                }
                return $next($request);
            }

            if ($info instanceof GeoInfo) {
                $usage = $info->getUsageType(); // UsageType 对象，不是字符串
                // 传给控制器：推荐的做法是控制器里再调 ip_geo($request->ip())，
                // reader 已在进程内，重复调用只多一次微秒级查询，不重复加载库。
                // 若一定要在中间件传值，可在 think\Request 子类里声明 public $geoInfo。
                if ($this->isBlocked($usage->rawValue(), $usage->isKnown())) {
                    // TP6/8 的全局 json() 助手返回 think\response\Json，链式 ->code() 可用。
                    return json([
                        'error'       => 'forbidden_network_type',
                        'usage_type'  => $usage->rawValue(),
                        'usage_label' => $usage->getDisplayZh(),
                    ])->code(403);
                }
            }

            return $next($request);
        }

        private function isBlocked(string $raw, bool $known): bool
        {
            if (!$known) {
                return false; // 未知分类不拦（策略：未知 = 放行）
            }
            $blocked = (array) (function_exists('config') ? config('qzdb.blocked_usage_types') : []);
            if (!$blocked) {
                $blocked = ['DataCenter', 'Cloud', 'VPN', 'AICrawler'];
            }
            return in_array($raw, $blocked, true);
        }
    }
}

namespace {

    use app\service\QzdbService;

    if (!function_exists('ip_geo')) {
        /**
         * 全局辅助函数：模板 / 控制器 / 命令行一处调用。
         *
         *   {$ip_geo|默认文案} 之外更常用的是控制器里：
         *     $geo = ip_geo($request->ip());          // 全字段
         *     $geo = ip_geo($request->ip(), ['country_code', 'usage_type']); // 只取两列
         *
         * 未命中或 IP 非法返回 null（SDK 的 Fail-Soft 口径，README 第 3 节）。
         *
         * @param string[]|null $fields 需要的列，不给用默认 6 列
         * @return array|null
         */
        function ip_geo(string $ip, ?array $fields = null): ?array
        {
            // 只解码请求列（SDK 的字段投影），返回的键与请求的列一一对应
            return QzdbService::geo($ip, $fields);
        }
    }

    if (!function_exists('ip_usage')) {
        /** 全局辅助函数：这条 IP 是什么网络场景（Cloud / DataCenter / VPN / Broadband ...）。 */
        function ip_usage(string $ip): string
        {
            return QzdbService::usage($ip);
        }
    }
}
