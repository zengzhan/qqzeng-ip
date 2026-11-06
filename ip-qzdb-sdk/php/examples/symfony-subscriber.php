<?php
// examples/symfony-subscriber.php —— Symfony 7.x 集成示例（QZDB PHP SDK）
//
// 本文件把几个类放在一起只是为了便于阅读和 `php -l`；落地时按下面的路径拆：
//   App\Qzdb\QzdbManager                -> src/Qzdb/QzdbManager.php
//   App\Qzdb\QzdbReaderFactory          -> src/Qzdb/QzdbReaderFactory.php
//   App\EventListener\GeoInfoSubscriber -> src/EventListener/GeoInfoSubscriber.php
//
// config/services.yaml —— 工厂配置（整段可用，注释行按部署形态二选一）：
//   parameters:
//       qzdb.path: '%env(resolve:QZDB_PATH)%'
//       # 常驻运行时（FrankenPHP / RoadRunner）填 true，传统 PHP-FPM 填 false
//       qzdb.resident: '%env(bool:QZDB_RESIDENT)%'
//       qzdb.fields: ['country_code', 'province', 'city', 'isp', 'asn', 'usage_type']
//   services:
//       _defaults:
//           autowire: true
//           autoconfigure: true
//           public: false
//       App\:
//           resource: '../src/'
//           exclude: '../src/Kernel.php'
//       App\Qzdb\QzdbManager:
//           arguments:
//               $dbPath: '%qzdb.path%'
//               $resident: '%qzdb.resident%'
//       # reader 走工厂方法创建；Symfony 服务默认 shared，容器里只 create() 一次。
//       Qqzeng\Ip\QzdbReader:
//           factory: ['@App\Qzdb\QzdbManager', 'createReader']
//       App\EventListener\GeoInfoSubscriber:
//           tags: [{ name: kernel.event_subscriber }]
//
//   也可以用注解挂载监听器（Symfony 6.3+），此时不需要上面那行 tags：
//       use Symfony\Component\EventDispatcher\Attribute\AsEventListener;
//       #[AsEventListener(event: 'kernel.request', method: 'onKernelRequest')]
//
// 其它注册步骤：
//   1. composer require qqzeng/qzdb（命名空间 Qqzeng\Ip，单文件零依赖）
//   2. .env：QZDB_PATH=/data/qqzeng_ip_max_global.qzdb、QZDB_RESIDENT=1
//   3. 可信代理：public/index.php 里（Symfony 6/7 的写法，常量集合按你的版本核对）
//        Request::setTrustedProxies(['10.0.0.0/8', '172.16.0.0/12'],
//            Request::HEADER_X_FORWARDED_FOR | Request::HEADER_X_FORWARDED_PROTO);
//      没声明可信代理时 getClientIp() 只会给 REMOTE_ADDR，伪造的 X-Forwarded-For 不会被采信。
//   4. 控制器取值：
//        #[Route('/api/order', methods: ['POST'])]
//        public function order(Request $request): JsonResponse
//        {
//            $geo = $request->attributes->get('geo_info');   // ?GeoInfo，未命中为 null
//            ...
//        }
//
// 部署形态差异（这是本示例最重要的一段）：
// - FPM（传统）：每个请求重新构建容器，也重新构建 reader。`shared` 帮不上忙。
//   配 QZDB_RESIDENT=0 → MEMORY_LOW + verifyCrc(false) + 不 warmup：
//   整库 CRC 交给部署脚本跑一次（php -r 里 build 一遍，或 bin/console 自定义命令），
//   请求期不再为校验把 62.6 MB 的库多读一遍。
// - FrankenPHP（Runtime：`runtime.php` 里 new FrankenPHP\Runtime()，配合
//   FRANKENPHP_CONFIG="adapter=frankenphp worker=public/index.php" 之类的 worker 配置，
//   原生 worker 写法见 examples/frankenphp-worker.php）与 RoadRunner（`rr serve`，
//   .rr.yaml 里 command 指向 php-worker / 你的 Symfony worker 脚本）：
//   容器随 worker 进程只引导一次，之后 `$app->handle($request)` 复用同一批服务。
//   配 QZDB_RESIDENT=1 → MEMORY_BALANCED + verifyCrc(true) + 启动期 warmup()，
//   跨请求零 I/O。内存预算按 README 第 4.7 节：worker 数 × 单 worker 常驻，
//   大库要先把 `pool.max` / worker 数压下来再上线。
//   这两种常驻形态下退出前必须显式 close()：FrankenPHP 在 worker 循环结束时、
//   RoadRunner 在 stop 信号回调里调 QzdbManager::shutdown()。
// - 常驻下热更新：`bin/console` 跑在独立 CLI 进程里，改不了 worker 内存。
//   流程同 README 第 11.1 节：候选文件写临时名 → 校验 → rename() 原子覆盖 →
//   worker 侧按节流检查 mtime 自行 reload()（下面 QzdbManager 实现了），
//   或者直接重启 worker（FrankenPHP：POST /frankenphp/workers/restart；RoadRunner：rr reload）。
// - reload() 在 balanced 模式下峰值约「旧库 + 新库」两份，容器 memory_limit 按两份留。
//
// 注意：
// - 本文件不 implements Symfony 的接口（本机没装 symfony/event-dispatcher 时也能
//   php -l 通过）。真实项目里补上一行即可，方法签名已按接口要求写好：
//     use Symfony\Component\EventDispatcher\EventSubscriberInterface;
//     final class GeoInfoSubscriber implements EventSubscriberInterface
// - 事件参数在 Symfony 5+ 叫 RequestEvent（旧的 GetRequestEvent 已不存在），
//   这里用 object 接收并靠 method_exists 取 request，是为了让骨架在无框架环境里也不炸；
//   真实项目请直接写 `public function onKernelRequest(RequestEvent $event): void`。

declare(strict_types=1);

namespace App\Qzdb {

    use Qqzeng\Ip\QzdbBuilder;
    use Qqzeng\Ip\QzdbException;
    use Qqzeng\Ip\QzdbReader;

    /**
     * reader 的持有者 + 工厂 + 热更新，三者放一个类里方便 services.yaml 只用一个服务。
     */
    final class QzdbManager
    {
        /** @var QzdbReader|null */
        private static $reader = null;

        /** @var string */
        private $dbPath;

        /** @var bool */
        private $resident;

        /** @var int 上次检查 mtime 的时间戳（常驻热更新节流用） */
        private $lastCheckAt = 0;

        /** @var int 当前已加载库文件的 mtime */
        private $loadedMtime = 0;

        /** @var int 热更新检查间隔（秒）；0 = 关闭请求路径里的 mtime 检查 */
        private $reloadInterval;

        public function __construct(string $dbPath, bool $resident = true, int $reloadInterval = 0)
        {
            $this->dbPath = $dbPath;
            $this->resident = $resident;
            $this->reloadInterval = $reloadInterval;
        }

        /**
         * services.yaml 里 factory: ['@App\Qzdb\QzdbManager', 'createReader'] 指向的方法。
         *
         * 常驻：MEMORY_BALANCED + verifyCrc(true) + warmup()，进程生命周期内一次。
         * FPM ：MEMORY_LOW + verifyCrc(false)，每请求一次，成本压在分页读上。
         */
        public function createReader(): QzdbReader
        {
            if (self::$reader instanceof QzdbReader) {
                return self::$reader; // 常驻下容器重建（子请求/手动 reset）也不重复加载
            }
            $builder = QzdbBuilder::path($this->dbPath)
                ->memoryMode($this->resident ? QzdbReader::MEMORY_BALANCED : QzdbReader::MEMORY_LOW)
                ->verifyCrc($this->resident ? true : false);

            // 库文件缺失 / 损坏：这里抛 QzdbException，容器引导直接失败。
            // 启动期就该 loud——不要带病服务（README 第 4.4 节的错误码表）。
            $reader = $builder->build();
            if ($this->resident) {
                $reader->warmup();
            }
            $this->loadedMtime = (int) @filemtime($this->dbPath);
            self::$reader = $reader;
            return $reader;
        }

        public static function current(): ?QzdbReader
        {
            return self::$reader;
        }

        /**
         * 请求路径上的节流 mtime 检查：文件被 rename() 换掉了就原地 reload()。
         *
         * reload() 强制 CRC、沿用当前内存模式；失败抛 QzdbException 且旧快照继续服务
         * （README 第 11.1 节）。FPM 形态不需要这里——每请求本来就重新加载。
         */
        public function maybeReload(int $now): void
        {
            if (!$this->resident || $this->reloadInterval <= 0) {
                return;
            }
            if ($this->lastCheckAt !== 0 && $now - $this->lastCheckAt < $this->reloadInterval) {
                return;
            }
            $this->lastCheckAt = $now;
            clearstatcache(true, $this->dbPath);
            $mtime = (int) @filemtime($this->dbPath);
            if ($mtime === 0 || $mtime === $this->loadedMtime) {
                return;
            }
            $reader = self::$reader;
            if (!$reader instanceof QzdbReader) {
                return;
            }
            try {
                $reader->reload($this->dbPath);
                $this->loadedMtime = $mtime;
                error_log('[qzdb] reloaded path=' . $this->dbPath . ' mtime=' . $mtime);
            } catch (QzdbException $e) {
                error_log('[qzdb] reload FAILED, old snapshot still serving: [' . $e->getCode() . '] ' . $e->getMessage());
            }
        }

        /** 常驻进程退出前显式释放（FrankenPHP worker 循环后 / RoadRunner stop 回调里调）。 */
        public static function shutdown(): void
        {
            if (self::$reader instanceof QzdbReader) {
                try { self::$reader->close(); } catch (\Throwable $e) {}
                self::$reader = null;
            }
        }

        public function isResident(): bool
        {
            return $this->resident;
        }
    }
}

namespace App\EventListener {

    use App\Qzdb\QzdbManager;
    use Qqzeng\Ip\GeoInfo;
    use Qqzeng\Ip\QzdbException;
    use Qqzeng\Ip\QzdbReader;

    /**
     * REQUEST 监听器：解析客户端 IP，把结果挂到 $request->attributes。
     *
     * 真实项目补上接口（本文件为无框架机器也能 php -l 通过，不写 implements）：
     *   use Symfony\Component\EventDispatcher\EventSubscriberInterface;
     *   final class GeoInfoSubscriber implements EventSubscriberInterface
     * 接口只要求 static getSubscribedEvents(): array，本类已实现。
     */
    final class GeoInfoSubscriber
    {
        /** 请求属性名：控制器里 $request->attributes->get('geo_info') 取 ?GeoInfo */
        public const ATTR = 'geo_info';

        /** 只解码业务需要的列——不拼 toPipe() 再手工切割 */
        private const FIELDS = ['country_code', 'province', 'city', 'isp', 'asn', 'usage_type'];

        /**
         * KernelEvents::REQUEST 的字符串值是 'kernel.request'；
         * 这里直接用字符串，避免本机没有 symfony/http-kernel 时连类常量都解析不了。
         * 真实项目请写：KernelEvents::REQUEST => [['onKernelRequest', 8]]
         */
        public static function getSubscribedEvents(): array
        {
            return ['kernel.request' => [['onKernelRequest', 8]]];
        }

        /**
         * @param object $event 真实项目里类型是 Symfony\Component\HttpKernel\Event\RequestEvent
         */
        public function onKernelRequest(object $event): void
        {
            // 只处理主请求：子请求（ESI / 模板片段）不重复查库。
            if (method_exists($event, 'isMainRequest') && !$event->isMainRequest()) {
                return;
            }
            if (method_exists($event, 'isMasterRequest') && !$event->isMasterRequest()) {
                return; // Symfony 4.x 及更早的主请求判据
            }
            if (!method_exists($event, 'getRequest')) {
                return;
            }
            $request = $event->getRequest();

            /** @var QzdbReader|null $reader */
            $reader = QzdbManager::current();
            if (!$reader instanceof QzdbReader) {
                return; // 没有注入 reader（例如 CLI 命令场景），直接放行
            }
            $ip = self::clientIp($request);
            if ($ip === '') {
                return;
            }

            try {
                // 未命中 / 非法 IP 返回 null，不抛（README 第 3 节 Fail-Soft 口径）。
                $info = $reader->findFields($ip, self::FIELDS);
            } catch (QzdbException $e) {
                // 策略：fail-open（放行 + 日志）。风控严格的业务可在这里改成抛 503。
                error_log('[qzdb] lookup FAILED, fail-open ip=' . $ip . ' [' . $e->getCode() . '] ' . $e->getMessage());
                return;
            }

            // 未命中也写入 null，下游统一用 ?? 兜底，省掉「属性存在与否」的分支。
            $request->attributes->set(self::ATTR, $info);

            if ($info instanceof GeoInfo) {
                // 顺手放几个标量，模板 / 校验器里取用更方便（强类型 getter，不是切割字符串）
                $request->attributes->set('geo_country_code', $info->getCountryCode());
                $request->attributes->set('geo_usage_type', $info->getUsageType()->rawValue());
            }
        }

        /** 取客户端 IP：Symfony 的可信代理配置决定 getClientIp() 是否会解析 X-Forwarded-For。 */
        private static function clientIp(object $request): string
        {
            if (method_exists($request, 'getClientIp')) {
                return (string) $request->getClientIp();
            }
            return (string) ($_SERVER['REMOTE_ADDR'] ?? '');
        }
    }
}

namespace App\Command {

    use Qqzeng\Ip\QzdbBuilder;
    use Qqzeng\Ip\QzdbException;
    use Qqzeng\Ip\QzdbReader;

    /**
     * 部署期离线校验候选库（FPM 形态用它替掉请求里的整库 CRC）。
     *
     * 真实项目：`final class QzdbVerifyCommand extends Command`（symfony/console），
     * 并 `php bin/console qzdb:verify /data/incoming/qqzeng_ip_max_global.qzdb`。
     * 校验通过后再 rename() 到正式路径；常驻运行时随后重启 worker，
     * 或让 GeoInfoSubscriber 注入的 QzdbManager 按 mtime 自己 reload()。
     */
    final class QzdbVerifyCommand
    {
        /**
         * @param string      $candidate 新库文件路径（建议先放到与目标同盘的目录）
         * @param string|null $target    正式路径；给了就在校验通过后 rename() 原子覆盖
         */
        public function execute(string $candidate, ?string $target = null): int
        {
            if (!is_file($candidate)) {
                fwrite(STDERR, '候选文件不存在: ' . $candidate . PHP_EOL);
                return 1;
            }
            try {
                // 流式模式校验：不把整库读进 CLI 进程内存，CRC 依旧是全文件校验。
                $probe = QzdbBuilder::path($candidate)
                    ->memoryMode(QzdbReader::MEMORY_LOW)
                    ->verifyCrc(true)
                    ->build();
                fwrite(STDOUT, sprintf('校验通过: edition=%s dataMonth=%s fields=%d usage_type=%s%s',
                    $probe->getEdition(), $probe->getDataMonth(), count($probe->getFieldNames()),
                    $probe->hasField('usage_type') ? 'yes' : 'no', PHP_EOL));
                $probe->close();
            } catch (QzdbException $e) {
                fwrite(STDERR, sprintf('校验失败 [%d]: %s%s', $e->getCode(), $e->getMessage(), PHP_EOL));
                return 1;
            }
            if ($target === null || $target === $candidate) {
                return 0;
            }
            // 同一文件系统内 rename() 是原子的（契约 §四.2：严禁覆写 / 截断正在被打开的文件）。
            // 替换完：FPM 无需其它动作；常驻运行时重启 worker，或等 QzdbManager 的 mtime 检查。
            if (!@rename($candidate, $target)) {
                fwrite(STDERR, 'rename 失败: ' . $candidate . ' -> ' . $target . '（跨文件系统？权限？）' . PHP_EOL);
                return 1;
            }
            fwrite(STDOUT, '已替换: ' . $target . PHP_EOL);
            return 0;
        }
    }
}
