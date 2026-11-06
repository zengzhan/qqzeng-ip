<?php
// examples/hyperf-service.php —— Hyperf 3.x（Swoole 协程）集成示例（QZDB PHP SDK）
//
// 本文件把几个类放在一起只是为了便于阅读和 `php -l`；落地时按下面的路径拆文件：
//   App\Qzdb\QzdbRuntime          -> app/Qzdb/QzdbRuntime.php
//   App\Qzdb\QzdbSnapshotHolder   -> app/Qzdb/QzdbSnapshotHolder.php
//   App\Qzdb\QzdbReaderFactory    -> app/Qzdb/QzdbReaderFactory.php
//   App\Qzdb\QzdbReloadService    -> app/Qzdb/QzdbReloadService.php
//   App\Qzdb\IpGeoService         -> app/Qzdb/IpGeoService.php
//   App\Listener\QzdbBootListener -> app/Listener/QzdbBootListener.php
//   App\Task\QzdbReloadTask       -> app/Task/QzdbReloadTask.php
//
// 注册步骤（Hyperf 3.x 骨架；SDK 侧用法与框架版本无关）：
//   1. composer require qqzeng/qzdb（命名空间 Qqzeng\Ip，单文件零依赖）
//   2. .env：QZDB_PATH=/data/qqzeng_ip_max_global.qzdb
//   3. config/autoload/qzdb.php（新建）：
//        return [
//            'path'          => env('QZDB_PATH', '/data/qqzeng_ip_max_global.qzdb'),
//            'warmup'        => (bool) env('QZDB_WARMUP', true),
//            'fpm_verify_crc'=> (bool) env('QZDB_FPM_VERIFY_CRC', false),
//            'grace_seconds' => (int) env('QZDB_GRACE_SECONDS', 30), // 旧快照延迟释放的宽限期
//        ];
//   4. config/autoload/dependencies.php 追加一行，让 make(QzdbReader::class) 走工厂：
//        \Qqzeng\Ip\QzdbReader::class => \App\Qzdb\QzdbReaderFactory::class,
//   5. config/autoload/listeners.php 追加 \App\Listener\QzdbBootListener::class
//   6. 定时热更新二选一：
//        a) 注解：在 QzdbReloadTask 类上写 @Crontab(name="QzdbReload", rule="0 4 * * *", callback="execute")
//           并在 config/autoload/crontab.php 打开 enable => true；
//        b) 信号：Swoole 侧给 master 进程发 SIGUSR2 自定义信号，在 sign 回调里调 reload。
//      注解/接口的确切参数名以你安装的 hyperf/crontab 源码为准（本文件不写注解，避免版本漂移）。
//
// 协程安全为什么成立（对应 README 第 11.2 / 14.1 节）：
// - 只读快照安全：QzdbReader 加载完成后，查询路径只读快照指向的不可变状态；
//   Swoole 协程只在系统调用 / 网络 / sleep 这些「挂起点」交错，find* 在缓冲模式下
//   没有挂起点，一个协程从进入到返回是原子的，两个协程不可能读到「半个」快照。
//   per-snapshot 的 GeoInfo 缓存（GEO_CACHE_LIMIT 槽）即便被两个协程各填一次，
//   写的也是同一个 entryId 对应的同一个值——快照不可变 → 重复计算幂等，不会串数据。
// - 写路径不允许原地做：reload() 是先构建新快照、再 assign() 把约三十个属性逐个
//   拷进当前实例。属性拷贝中间若被别的协程切进来，它读到的是「data 已置空、
//   偏移表还是旧的」这种半提交状态，可能直接拿到错值。所以本示例在协程环境下
//   一律走「整实例切换」：新 reader 建好 → 一次赋值换掉 holder 里的引用 →
//   旧 reader 等宽限期后再 close()。契约 §四.2 对 C 语言那条「严禁交换后立即 free」
//   是同一个道理，PHP 里 GC 会兜住引用计数，但文件句柄与分页缓存仍要靠 close()。
// - 协程环境请用 MEMORY_BALANCED，别用 MEMORY_LOW：流式模式每次未命中缓存都要
//   fseek/fread，Swoole 挂钩（SWOOLE_HOOK_ALL）会把 fread 变成协程挂起点，
//   共享实例的分页 LRU 会在交错中被反复重建，结果依旧正确但白丢 I/O。
//
// 双运行时自适应（README 第 4.6 / 4.7 节）：
// - 常驻（Swoole / Swow / RoadRunner，Hyperf 的默认形态）：MEMORY_BALANCED +
//   verifyCrc(true) + 启动期 warmup()，整份库常驻，跨请求零 I/O，退出时 close()。
// - Hyperf 也支持不带 Swoole 扩展跑在 PHP-FPM 上（官方文档里的 FPM / Swowless 形态）：
//   这时每个请求都要重建 reader，改用 MEMORY_LOW + verifyCrc(false)，CRC 放部署脚本
//   （php bin/hyperf.php qzdb:verify 之类的一次性命令）离线校验一次，别在请求里读两遍文件。
// - 判断依据就是 QzdbRuntime::isResident()：检测 Swoole/Swow/RoadRunner/FrankenPHP
//   这些常驻信号，可用 QZDB_RESIDENT=0/1 手工覆盖（单测、灰度时很好用）。
//
// 注意：
// - Hyperf 3.x 自身要求 PHP 8.0+，本文件仍写成 PHP 7.4~8.6 兼容语法，
//   与 SDK 的最低版本口径保持一致，方便你把同一份代码放到别的框架里复用。
// - reload() 在 balanced 模式下峰值约为「旧库 + 新库」两份（README 第 11 节），
//   整实例切换同样是两份；容器 memory_limit 要按两份留，宽限期越长旧库滞留越久。
// - 本文件不 implements 任何框架接口（本机没装框架类时照样能 php -l 通过）。
//   真实项目里把注释里的 implements 补上即可，方法签名已按框架要求写好。

declare(strict_types=1);

namespace App\Qzdb {

    use Qqzeng\Ip\GeoInfo;
    use Qqzeng\Ip\QzdbBuilder;
    use Qqzeng\Ip\QzdbException;
    use Qqzeng\Ip\QzdbReader;

    /**
     * 运行形态判定 + 加载参数装配：全部示例共用的「双运行时自适应」入口。
     */
    final class QzdbRuntime
    {
        /** 常驻运行时：worker 进程活多久，库就在内存里待多久 */
        public static function isResident(): bool
        {
            $override = getenv('QZDB_RESIDENT');
            if ($override !== false && $override !== '') {
                return $override === '1' || strtolower((string) $override) === 'true';
            }
            if (in_array(PHP_SAPI, ['fpm-fcgi', 'cgi-fcgi', 'cgi', 'litespeed'], true)) {
                return false; // PHP-FPM / CGI：每请求重建，走低内存分支
            }
            // 注意：`php bin/hyperf.php` 的命令行进程同样加载了 Swoole，这里会被判成常驻，
            // 多做一次 warmup（几十毫秒，结果不受影响）。介意的话在命令入口给
            // QZDB_RESIDENT=0，或 config 里 warmup => false。
            return extension_loaded('swoole')
                || extension_loaded('swow')
                || isset($_SERVER['RR_MODE'])          // RoadRunner
                || function_exists('frankenphp_handle_request') // FrankenPHP worker
                || class_exists('Workerman\Worker', false);      // Webman / Workerman
        }

        /**
         * 按运行形态装配 Builder：常驻 = balanced + CRC + 预热；FPM = low + 免 CRC。
         *
         * @param array $cfg config('qzdb') 之类的面包屑数组
         */
        public static function builderFor(string $path, array $cfg): QzdbBuilder
        {
            $resident = self::isResident();
            $builder = QzdbBuilder::path($path)
                ->memoryMode($resident ? QzdbReader::MEMORY_BALANCED : QzdbReader::MEMORY_LOW)
                // 常驻：启动期校验一次 CRC，成本摊薄到 0。
                // FPM：默认关掉整库 CRC（部署脚本已离线校验过），可用配置打开。
                ->verifyCrc($resident ? true : (bool) ($cfg['fpm_verify_crc'] ?? false));

            $groupIndex = (int) ($cfg['group_index'] ?? 0);
            if ($groupIndex !== 0) {
                $builder->groupIndex($groupIndex);
            }
            return $builder;
        }

        /** FPM 形态下的每请求成本提示：不要在这里 warmup()。 */
        public static function shouldWarmup(array $cfg): bool
        {
            return self::isResident() && (bool) ($cfg['warmup'] ?? true);
        }
    }

    /**
     * 进程内快照持有者。协程 / worker 全部读这里，写只有 swapTo 一处。
     *
     * 为什么不用 QzdbRegistry：注册表按名字管理多份 reader，替换时会立即 close() 旧实例
     * （源码 register() 里 $old->close()），不给你宽限期；协程下要「旧快照再服务一会儿」，
     * 所以自己持有引用更可控。单库场景两者行为等价。
     */
    final class QzdbSnapshotHolder
    {
        /** @var QzdbReader|null */
        private static $current = null;
        /** @var array<int, array{0: QzdbReader, 1: int}> 待退役的旧快照 + 退役时间戳 */
        private static $retired = [];

        public static function current(): ?QzdbReader
        {
            return self::$current;
        }

        public static function set(QzdbReader $reader): void
        {
            self::$current = $reader;
        }

        /**
         * 原子换到新快照，旧快照进退役队列（不立刻 close，避免在途查询踩到已释放实例）。
         *
         * 这里只有一次赋值语句，PHP 层没有比它更小的写单位，协程不会看到中间态。
         */
        public static function swapTo(QzdbReader $next): void
        {
            $old = self::$current;
            self::$current = $next;
            if ($old !== null) {
                self::$retired[] = [$old, time()];
            }
        }

        /** 释放过了宽限期的旧快照；常驻进程里由定时器或每次热更新后顺手调一次。 */
        public static function gcRetired(int $graceSeconds = 30): int
        {
            $now = time();
            $keep = [];
            $closed = 0;
            foreach (self::$retired as $item) {
                if ($now - $item[1] >= $graceSeconds) {
                    try { $item[0]->close(); } catch (\Throwable $e) {}
                    ++$closed;
                } else {
                    $keep[] = $item;
                }
            }
            self::$retired = $keep;
            return $closed;
        }

        /** worker 退出：当前快照 + 退役队列全部显式 close()（README 第 4.6 节要求）。 */
        public static function shutdown(): void
        {
            if (self::$current !== null) {
                try { self::$current->close(); } catch (\Throwable $e) {}
                self::$current = null;
            }
            foreach (self::$retired as $item) {
                try { $item[0]->close(); } catch (\Throwable $e) {}
            }
            self::$retired = [];
        }
    }

    /**
     * Hyperf 容器工厂：dependencies.php 里 \Qqzeng\Ip\QzdbReader::class => 本类。
     *
     * 真实项目请补上接口：
     *   use Hyperf\Contract\FactoryInterface;
     *   final class QzdbReaderFactory implements FactoryInterface
     * create(...) 的参数用可变参数、不带类型，兼容 hyperf/contract 里
     * create(array|object ...$dependencies): object 的签名（参数类型放宽是合法的）。
     */
    final class QzdbReaderFactory
    {
        /** @var array */
        private $cfg;

        public function __construct(array $cfg = [])
        {
            // Hyperf 里用 $this->container->get(ConfigInterface::class)->get('qzdb') 取；
            // 这里允许无参构造，方便 FPM 场景 / 单测直接 new。
            $this->cfg = $cfg;
        }

        /**
         * @param mixed ...$dependencies 容器注入的依赖（本工厂不消费）
         */
        public function create(...$dependencies): QzdbReader
        {
            $reader = QzdbRuntime::builderFor(self::path(), $this->cfg)->build();
            if (QzdbRuntime::shouldWarmup($this->cfg)) {
                $reader->warmup(); // 只在缓冲模式下生效：预触 Trie 跳表 / 节点表页
            }
            // 首次解析即写进 holder，后续协程都读同一份；工厂只在进程生命周期里跑一次。
            if (QzdbSnapshotHolder::current() === null) {
                QzdbSnapshotHolder::set($reader);
            }
            return $reader;
        }

        public static function path(): string
        {
            $env = getenv('QZDB_PATH');
            return (string) ($env !== false && $env !== '' ? $env : '/data/qqzeng_ip_max_global.qzdb');
        }
    }

    /**
     * 原子热更新：新文件 → 新实例 → 换引用 → 旧实例延迟释放。
     *
     * 前提：候选文件先写到与目标同盘的临时名，校验完整后用 rename() 原子覆盖
     * （契约 §四.2：严禁覆写 / 截断正在被进程打开的 .qzdb，否则 mmap 语言会 SIGBUS）。
     */
    final class QzdbReloadService
    {
        /**
         * @return array{ok: bool, edition: string, dataMonth: string, error: string}
         */
        public static function reload(string $newPath, array $cfg = []): array
        {
            $grace = (int) ($cfg['grace_seconds'] ?? 30);
            try {
                // 新实例自己走一遍 CRC + 预热；这一步不影响正在服务的旧快照。
                $next = QzdbRuntime::builderFor($newPath, $cfg + ['fpm_verify_crc' => true])->build();
                if (QzdbRuntime::shouldWarmup($cfg)) {
                    $next->warmup();
                }
            } catch (QzdbException $e) {
                // 加载失败：旧快照继续服务，绝不 swapTo。
                return ['ok' => false, 'edition' => '', 'dataMonth' => '', 'error' => '[' . $e->getCode() . '] ' . $e->getMessage()];
            }

            QzdbSnapshotHolder::swapTo($next);
            QzdbSnapshotHolder::gcRetired($grace);

            return ['ok' => true, 'edition' => $next->getEdition(), 'dataMonth' => $next->getDataMonth(), 'error' => ''];
        }

        /**
         * 只想更新当前这一个实例、不需要换引用时，用 SDK 自带的原地 reload()。
         *
         * 警告：仅适用于「查询与 reload 不会交错」的运行时（Octane 的 Swoole 驱动默认
         * enable_coroutine=false、单线程请求；FPM 单请求）。协程环境请用上面的 reload()。
         * reload() 内部强制 CRC、沿用当前内存模式，失败抛异常且旧快照继续服务。
         */
        public static function reloadInPlace(string $newPath): void
        {
            $reader = QzdbSnapshotHolder::current();
            if ($reader === null) {
                throw new QzdbException('qzdb reader not initialized', QzdbReader::ERROR_INVALID_PARAM);
            }
            $reader->reload($newPath);
        }
    }

    /**
     * 业务侧查询封装：只用强类型 getter + findFields 投影，不碰管道串。
     */
    final class IpGeoService
    {
        /** 只解码业务真正要的列；字段名与 GeoInfo 的 getter 一一对应 */
        private const FIELDS = ['country_code', 'province', 'city', 'isp', 'asn', 'usage_type'];

        public static function reader(): QzdbReader
        {
            $reader = QzdbSnapshotHolder::current();
            if ($reader === null) {
                // FPM 形态下没有 boot listener，这里兜底建一次（每请求一次，
                // QzdbRuntime 会自动选 MEMORY_LOW + 免 CRC 分支）。
                $reader = (new QzdbReaderFactory([]))->create();
            }
            return $reader;
        }

        /**
         * @return array{country_code: string, province: string, city: string, isp: string, asn: int|null, usage_type: string}
         */
        public static function locate(string $ip): array
        {
            $info = self::reader()->findFields($ip, self::FIELDS);
            if (!$info instanceof GeoInfo) {
                // 未命中 / 非法 IP：find* 系列返回 null，不抛（README 第 3 节）
                return ['country_code' => '', 'province' => '', 'city' => '', 'isp' => '', 'asn' => null, 'usage_type' => ''];
            }
            return [
                'country_code' => $info->getCountryCode(),
                'province'     => $info->getProvince(),
                'city'         => $info->getCity(),
                'isp'          => $info->getIsp(),
                'asn'          => $info->getAsn(),          // ?int
                'usage_type'   => $info->getUsageType()->rawValue(),
            ];
        }

        /** 批量入口：逐条容错，用 BatchResult 区分「未命中」与「IP 非法」。 */
        public static function locateBatch(array $ips): array
        {
            $out = [];
            foreach (self::reader()->findBatch($ips) as $r) {
                if ($r->isSuccess()) {
                    $info = $r->info;
                    $out[$r->input] = $info instanceof GeoInfo ? $info->getUsageType()->rawValue() : '';
                } else {
                    $out[$r->input] = $r->isNotFound() ? null : false; // null=未命中，false=输入非法
                }
            }
            return $out;
        }
    }
}

namespace App\Listener {

    use App\Qzdb\QzdbRuntime;
    use App\Qzdb\QzdbSnapshotHolder;
    use App\Qzdb\QzdbReaderFactory;

    /**
     * worker 启动期加载 + 预热 + 退出释放。
     *
     * 真实项目补上接口（本文件为了在无框架机器上也能 php -l，不写 implements）：
     *   use Hyperf\Event\Contract\ListenerInterface;
     *   final class QzdbBootListener implements ListenerInterface
     * 并在 config/autoload/listeners.php 注册本类。
     *
     * 关心的事件（类名以你安装的 hyperf/framework 版本为准）：
     *   Hyperf\Framework\Event\OnWorkerStart        每个 worker 进程启动后：加载 + warmup
     *   Hyperf\Framework\Event\BootApplicationShutdown / OnWorkerStop：显式 close()
     * process() 里不做类型判断的分支请直接 return，别把库加载放进请求路径。
     */
    final class QzdbBootListener
    {
        /** @var callable|null 框架注入的容器取值函数，便于脱离框架也能实例化 */
        private $configProvider;

        public function __construct(?callable $configProvider = null)
        {
            $this->configProvider = $configProvider;
        }

        /** Hyperf 的 ListenerInterface 只要求这一个方法，事件对象用 object 接。 */
        public function process(object $event): void
        {
            $name = get_class($event);

            if (strpos($name, 'OnWorkerStop') !== false || strpos($name, 'Shutdown') !== false) {
                QzdbSnapshotHolder::shutdown();
                return;
            }
            if (strpos($name, 'OnWorkerStart') === false && strpos($name, 'Boot') === false) {
                return; // 其它事件不关心
            }

            if (!QzdbRuntime::isResident()) {
                return; // FPM 形态：没有「启动期」可言，交给 IpGeoService::reader() 惰性加载
            }
            if (QzdbSnapshotHolder::current() !== null) {
                return; // 同一个 worker 里别加载第二遍
            }
            $cfg = $this->configProvider !== null ? (array) call_user_func($this->configProvider) : [];
            $reader = (new QzdbReaderFactory($cfg))->create();
            QzdbSnapshotHolder::set($reader);
            error_log(sprintf('[qzdb] loaded edition=%s dataMonth=%s fields=%d mode=%s',
                $reader->getEdition(), $reader->getDataMonth(),
                count($reader->getFieldNames()), $reader->getMemoryMode()));
        }
    }
}

namespace App\Task {

    use App\Qzdb\QzdbReloadService;
    use App\Qzdb\QzdbSnapshotHolder;

    /**
     * 定时热更新任务。
     *
     * 真实项目挂注解（hyperf/crontab，参数名以框架源码为准）：
     *   use Hyperf\Crontab\Annotation\Crontab;
     *   / **
     *   *  @Crontab(name="QzdbReload", rule="0 4 * * *", callback="execute", memo="原子替换 IP 库")
     *   * /
     *   final class QzdbReloadTask
     * 注意 crontab 任务在 worker 进程内执行，所以整实例切换对同进程的协程立即生效；
     * 其它 worker 进程要各自跑一遍（每个 worker 都是一份独立内存，README 第 4.7 节）。
     */
    final class QzdbReloadTask
    {
        public function execute(): void
        {
            $candidate = getenv('QZDB_CANDIDATE');
            if ($candidate === false || $candidate === '') {
                return;
            }
            $result = QzdbReloadService::reload((string) $candidate);
            error_log('[qzdb] reload ' . json_encode($result));
        }

        /** 顺手回收退役快照：常驻进程里建议每轮任务都调一次。 */
        public function gc(): void
        {
            QzdbSnapshotHolder::gcRetired(30);
        }
    }
}
