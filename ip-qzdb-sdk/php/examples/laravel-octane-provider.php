<?php
// examples/laravel-octane-provider.php —— Laravel 11 / 12 集成示例（QZDB PHP SDK）
//
// 本文件把三个类放在一起只是为了便于阅读和 `php -l`；落地时按下面的路径拆成三个文件：
//   App\Providers\QzdbServiceProvider        -> app/Providers/QzdbServiceProvider.php
//   App\Http\Middleware\IpGeoRiskMiddleware  -> app/Http/Middleware/IpGeoRiskMiddleware.php
//   App\Console\Commands\QzdbReloadCommand   -> app/Console/Commands/QzdbReloadCommand.php
//
// 注册步骤（Laravel 11/12 骨架）：
//   1. composer require qqzeng/qzdb
//   2. bootstrap/providers.php 里追加 App\Providers\QzdbServiceProvider::class
//   3. config/qzdb.php（新建）：
//        return [
//            'path'                  => env('QZDB_PATH', '/data/qqzeng_ip_ult_global.qzdb'),
//            'fpm_verify_crc'        => (bool) env('QZDB_FPM_VERIFY_CRC', false),
//            'reload_check_interval' => (int) env('QZDB_RELOAD_CHECK_INTERVAL', 0), // 秒；0 = 关闭
//            'blocked_usage_types'   => ['VPN', 'DataCenter'],
//        ];
//      只在 config/ 里用 env()，业务代码里不要直接 env()，否则 config:cache 后读到 null。
//   4. bootstrap/app.php：
//        ->withMiddleware(function (Middleware $middleware) {
//            // 可信代理：$request->ip() 只有在这里声明了代理，才会解析 X-Forwarded-For。
//            // 签名：trustProxies(array|string|null $at = null, ?int $headers = null)
//            $middleware->trustProxies(at: ['10.0.0.0/8', '172.16.0.0/12']);
//            $middleware->append(\App\Http\Middleware\IpGeoRiskMiddleware::class);
//        })
//      不配置 trustProxies 时，$request->ip() 返回 REMOTE_ADDR；伪造的 X-Forwarded-For 不会被采信。
//   5. Artisan 命令放到 app/Console/Commands/ 后 Laravel 11+ 自动发现：
//        php artisan qzdb:reload /data/incoming/qqzeng_ip_ult_global.qzdb [--octane-reload]
//
// 为什么这样写（对应 README 第 4.6 / 4.7 / 11 节）：
// - Octane（FrankenPHP / Swoole / RoadRunner）下 worker 常驻，reader 必须在 worker 启动期、
//   在「基础容器」里解析一次。Octane 每个请求都 clone 基础容器得到 sandbox，请求里第一次
//   解析的单例只活在 sandbox 里，下个请求又要重新加载整库。所以 boot() 里主动 make()。
//   （来源：laravel/octane src/Worker.php：$sandbox = clone $this->app; 请求结束 $sandbox->flush()）
// - 判断是否在 Octane worker 里：用 isset($_SERVER['LARAVEL_OCTANE'])。
//   laravel/octane 的 StartSwooleCommand / StartFrankenPhpCommand / StartRoadRunnerCommand 启动
//   服务器子进程时注入环境变量 LARAVEL_OCTANE=1；laravel/framework 自身也用同一判据
//   （Illuminate\Routing\ResponseFactory、Foundation\Exceptions\Renderer\Listener）。
//   不要用 app()->bound('octane')：OctaneServiceProvider::register() 无条件注册了 'octane'
//   单例，只要装了包，在 FPM 下它也是 true。
// - FPM 下每个请求都要重新加载，用 MEMORY_LOW 压低单次成本；整库 CRC 放到部署脚本里做一次
//   （php artisan qzdb:reload 就是这个用途），请求期 verifyCrc(false)。
// - 中间件只取需要的 6 个字段（findFields），不拼 toPipe() 再 explode。
// - 热更新：Artisan 命令跑在独立 CLI 进程里，它调 reload() 改不了 worker 里的实例。
//   命令负责「校验候选文件 → 原子 rename 到正式路径」；worker 侧通过 RequestReceived 事件
//   按 reload_check_interval 节流检查 mtime，变了就 $reader->reload()。
//   不想在请求路径里 reload 的，用 --octane-reload 让 Octane 重启全部 worker。
//
// 注意：
// - reload() 在 balanced 模式下峰值约为「旧库 + 新库」两份（README 第 11 节有实测），
//   Octane worker 的 memory_limit 要按两份库留；不够就用 --octane-reload 走重启路线。
// - Octane 的 Swoole 驱动默认 enable_coroutine = false（laravel/octane 默认配置），
//   worker 内没有协程交错，原地 reload() 是安全的。自行开启协程的，改用
//   examples/hyperf-service.php 里的「整实例切换」写法。
// - Swoole task worker 也会引导应用；下面在 task worker 里跳过预加载，省一份内存。
// - FPM 下 close() 由请求结束时的析构完成，不需要手工调；Octane 下在 WorkerStopping 里显式 close()。

declare(strict_types=1);

namespace App\Providers {

    use Illuminate\Support\Facades\Event;
    use Illuminate\Support\ServiceProvider;
    use Qqzeng\Ip\QzdbBuilder;
    use Qqzeng\Ip\QzdbException;
    use Qqzeng\Ip\QzdbReader;

    final class QzdbServiceProvider extends ServiceProvider
    {
        /** 上次检查库文件 mtime 的时间戳（仅 Octane 热更新用；provider 对象在 worker 内常驻） */
        private int $lastCheckAt = 0;
        private int $loadedMtime = 0;

        public function register(): void
        {
            $this->app->singleton(QzdbReader::class, function ($app): QzdbReader {
                $cfg = (array) $app['config']->get('qzdb', []);
                $path = (string) ($cfg['path'] ?? '/data/qqzeng_ip_ult_global.qzdb');
                $resident = self::runningUnderOctane();

                $builder = QzdbBuilder::path($path)
                    ->memoryMode($resident ? QzdbReader::MEMORY_BALANCED : QzdbReader::MEMORY_LOW)
                    // 常驻：启动期校验一次 CRC。FPM：默认不校验（部署期已经校验过），可用配置打开。
                    ->verifyCrc($resident ? true : (bool) ($cfg['fpm_verify_crc'] ?? false));

                // 库文件缺失 / 损坏时这里抛 QzdbException：Octane worker 启动失败并退出；
                // FPM 则由全局异常处理器接管。两种情况都不要吞掉——不要带病服务。
                $reader = $builder->build();
                if ($resident) {
                    $reader->warmup(); // 只在 balanced 模式下有效果，预热 trie / 池偏移表
                }
                $this->loadedMtime = (int) @filemtime($path);
                return $reader;
            });
        }

        public function boot(): void
        {
            if (!self::runningUnderOctane() || self::isSwooleTaskWorker()) {
                return; // FPM / CLI：按需惰性加载；task worker：不加载
            }

            // Octane worker 启动期：在基础容器里解析一次，之后所有请求的 sandbox 共享这一份。
            $this->app->make(QzdbReader::class);

            // worker 退出：显式 close()。类名用字符串，未装 octane 时本分支根本不会进来。
            Event::listen('Laravel\Octane\Events\WorkerStopping', function (): void {
                if ($this->app->resolved(QzdbReader::class)) {
                    $this->app->make(QzdbReader::class)->close();
                }
            });

            // 可选热更新：按 reload_check_interval 节流检查 mtime，变了就原地 reload()。
            $interval = (int) ($this->app['config']->get('qzdb.reload_check_interval') ?? 0);
            if ($interval > 0) {
                Event::listen('Laravel\Octane\Events\RequestReceived', function () use ($interval): void {
                    $this->maybeReload($interval);
                });
            }
        }

        private function maybeReload(int $interval): void
        {
            $now = time();
            if ($now - $this->lastCheckAt < $interval) {
                return;
            }
            $this->lastCheckAt = $now;

            $path = (string) $this->app['config']->get('qzdb.path');
            clearstatcache(true, $path);
            $mtime = (int) @filemtime($path);
            if ($mtime === 0 || $mtime === $this->loadedMtime) {
                return;
            }
            try {
                // reload() 强制 CRC、沿用当前内存模式；失败抛异常且旧快照继续服务（README 第 11 节）。
                $this->app->make(QzdbReader::class)->reload($path);
                $this->loadedMtime = $mtime;
                $this->app['log']->info('qzdb reloaded', ['path' => $path, 'mtime' => $mtime]);
            } catch (QzdbException $e) {
                $this->app['log']->error('qzdb reload failed, old snapshot still serving', [
                    'path' => $path, 'code' => $e->getCode(), 'error' => $e->getMessage(),
                ]);
            }
        }

        public static function runningUnderOctane(): bool
        {
            return isset($_SERVER['LARAVEL_OCTANE']) || getenv('LARAVEL_OCTANE') !== false;
        }

        private function isSwooleTaskWorker(): bool
        {
            if (!$this->app->bound('Swoole\Http\Server')) {
                return false;
            }
            try {
                return (bool) ($this->app->make('Swoole\Http\Server')->taskworker ?? false);
            } catch (\Throwable $e) {
                return false;
            }
        }
    }
}

namespace App\Http\Middleware {

    use Closure;
    use Illuminate\Http\Request;
    use Illuminate\Support\Facades\Log;
    use Qqzeng\Ip\GeoInfo;
    use Qqzeng\Ip\QzdbException;
    use Qqzeng\Ip\QzdbReader;
    use Symfony\Component\HttpFoundation\Response;

    final class IpGeoRiskMiddleware
    {
        /** 请求属性名：控制器里 $request->attributes->get('geo') 取 ?GeoInfo */
        public const ATTR = 'geo';

        /** 只取业务需要的字段；字段名与 GeoInfo 的 getter 一一对应 */
        private const FIELDS = ['country_code', 'province', 'city', 'isp', 'asn', 'usage_type'];

        public function __construct(private QzdbReader $reader)
        {
        }

        public function handle(Request $request, Closure $next): Response
        {
            // $request->ip()：按 bootstrap/app.php 的 trustProxies 解析；未声明代理时就是 REMOTE_ADDR。
            $ip = (string) $request->ip();

            $info = null;
            try {
                // 查不到 / IP 非法返回 null，不抛；只有底层数据异常才抛 QzdbException。
                $info = $this->reader->findFields($ip, self::FIELDS);
            } catch (QzdbException $e) {
                // 策略：fail-open（放行但记日志）。风控场景可改成 fail-closed：直接 return 503。
                Log::warning('qzdb lookup failed, fail-open', ['ip' => $ip, 'code' => $e->getCode(), 'error' => $e->getMessage()]);
            }

            $request->attributes->set(self::ATTR, $info); // 未命中也写入 null，下游用 ?? 处理

            if ($info instanceof GeoInfo && $this->isBlocked($info)) {
                return response()->json([
                    'error' => 'forbidden_network_type',
                    'usage_type' => $info->getUsageType()->rawValue(),
                ], 403);
            }

            return $next($request);
        }

        private function isBlocked(GeoInfo $info): bool
        {
            $usage = $info->getUsageType();
            if (!$usage->isKnown()) {
                return false; // 未知分类不拦（策略：未知 = 放行）
            }
            // 已知分类的 rawValue() 是规范值（如 "DataCenter"），直接按值比较；
            // 全部 21 个原始值见 README 第 6.4 节 / KnownUsageType::init()。
            $blocked = (array) config('qzdb.blocked_usage_types', ['VPN', 'DataCenter']);
            return in_array($usage->rawValue(), $blocked, true);
        }
    }
}

namespace App\Console\Commands {

    use Illuminate\Console\Command;
    use Qqzeng\Ip\QzdbBuilder;
    use Qqzeng\Ip\QzdbException;
    use Qqzeng\Ip\QzdbReader;

    /**
     * 校验候选库文件并原子替换到正式路径。
     *
     * 本命令跑在独立 CLI 进程里，改不了 Octane worker 里已加载的实例；worker 侧的
     * 更新靠 QzdbServiceProvider 的 mtime 检查（reload_check_interval > 0），
     * 或加 --octane-reload 让 Octane 重启全部 worker（octane:reload 要求服务器正在运行）。
     */
    final class QzdbReloadCommand extends Command
    {
        protected $signature = 'qzdb:reload {candidate : 新库文件路径} {--octane-reload : 替换后执行 octane:reload}';

        protected $description = '校验候选 .qzdb 文件（强制 CRC）并原子替换到 config(qzdb.path)';

        public function handle(): int
        {
            $candidate = (string) $this->argument('candidate');
            $target = (string) config('qzdb.path');

            if (!is_file($candidate)) {
                $this->error("候选文件不存在: {$candidate}");
                return self::FAILURE;
            }

            // 用流式模式校验：不把整库读进 CLI 进程内存，CRC 依旧是全文件校验。
            try {
                $probe = QzdbBuilder::path($candidate)
                    ->memoryMode(QzdbReader::MEMORY_LOW)
                    ->verifyCrc(true)
                    ->build();
                $this->info(sprintf('候选文件校验通过: edition=%s dataMonth=%s fields=%d usage_type=%s',
                    $probe->getEdition(), $probe->getDataMonth(), count($probe->getFieldNames()),
                    $probe->hasField('usage_type') ? 'yes' : 'no'));
                $probe->close();
            } catch (QzdbException $e) {
                $this->error("候选文件校验失败 [{$e->getCode()}]: {$e->getMessage()}");
                return self::FAILURE;
            }

            // 同一文件系统内 rename() 是原子的；流式 reader 打开的旧 inode 在 Linux/macOS 上
            // 继续有效，直到其 close()。候选文件请先放到与目标同盘的目录。
            if (!@rename($candidate, $target)) {
                $this->error("rename 失败: {$candidate} -> {$target}（跨文件系统？权限？）");
                return self::FAILURE;
            }
            $this->info("已替换: {$target}");

            if ($this->option('octane-reload')) {
                return $this->call('octane:reload');
            }
            return self::SUCCESS;
        }
    }
}
