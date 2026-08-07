<?php
/**
 * QZDB — 离线 IP 地理定位数据库 PHP SDK（IPv4 / IPv6 双栈）
 *
 * 单一事实来源：multi-lang/API_CONTRACT.md (v2.4)
 * 认证参考实现：Java / C#（已通过 Tier1/2/3 全量验证）
 *
 * 本文件在命名空间 Qqzeng\Ip 下提供：
 *   QzdbException        异常类型（携带错误码）
 *   ErrorCode            错误码常量（见 QzdbReader 类常量）
 *   GeoInfo              查询结果实体（字段归一化 / 序列化 / 语义 Getter）
 *   RowIds               行号三元组 (geoId, asnId, usageId)
 *   BatchResult          批量查询三态结果
 *   UsageType            用途分类（21 已知 + 未知兜底）
 *   KnownUsageType       21 个官方场景枚举
 *   UnknownUsageType     未知场景安全兜底
 *   QzdbReader           核心读取器：加载 / Trie / 查询 / 热更新 / CRC / 元信息 / CIDR
 *   QzdbBuilder          Builder 模式加载入口
 *   QzdbRegistry         命名注册表（实例级 + 进程全局级）
 *   ChainedReader        多库链式组合（Fallback / Merge / MergeOverride）
 *
 * 设计要点（契约 §8 / §9）：
 *   - 不可变快照语义：reload 重建全部状态后原子替换；查询路径对快照只读。
 *   - per-snapshot 有界无锁 GeoInfo 缓存：以 groupIndex:entryId 为键，碰撞只重算不返回错值。
 *   - 浮点原生格式 = 6 位小数（整数值无小数点，NaN/Inf 返回 ""）。
 *   - SENTINEL 高位哨兵位在解析前剥离。
 *   - IPv4-Mapped IPv6 自动降级走 V4 Trie。
 *   - Fail-Closed：Magic / HeaderVersion / CRC / 截断 构造即拒绝。
 *   - 流式模式（文件过大走 fseek/fread）与缓冲模式共用 readBytes 单一读取入口，行为逐字节一致。
 */

namespace Qqzeng\Ip;

/* ===========================================================================
 * 异常类型
 * =========================================================================== */
class QzdbException extends \Exception
{
    public function __construct(string $message, int $code = 0, ?\Throwable $previous = null)
    {
        parent::__construct($message, $code, $previous);
    }
}

/* ===========================================================================
 * 用途分类 UsageType（密封接口语义；PHP 用抽象类模拟）
 * =========================================================================== */
abstract class UsageType
{
    /** 原始编码字符串（如 "Broadband" / "Cloud"） */
    abstract public function rawValue(): string;

    /** 中文显示名（如 "宽带" / "云服务"） */
    abstract public function getDisplayZh(): string;

    /** 英文显示名（如 "Broadband" / "Cloud"） */
    abstract public function getDisplayEn(): string;

    /** 详细描述 */
    abstract public function getDescription(): string;

    /** 是否为已知官方场景 */
    abstract public function isKnown(): bool;

    /**
     * 从原始文本解析 UsageType。
     * 已知场景返回 KnownUsageType；未知场景返回 UnknownUsageType（不崩溃）。
     */
    public static function fromString(string $raw): UsageType
    {
        $raw = trim($raw);
        if ($raw === '') {
            return KnownUsageType::fromRaw('Unknown') ?? new UnknownUsageType('');
        }
        $known = KnownUsageType::fromRaw($raw);
        if ($known !== null) {
            return $known;
        }
        return new UnknownUsageType($raw);
    }
}

class KnownUsageType extends UsageType
{
    public static $AI_CRAWLER, $BACKBONE, $BROADBAND, $BUSINESS, $CDN, $CLOUD, $DNS,
           $DATA_CENTER, $EDUCATION, $FINANCE, $GOVERNMENT, $ISP, $IXP, $IOT, $MOBILE,
           $RESERVED, $SATELLITE, $SPIDER, $STREAMING, $UNKNOWN, $VPN;

    private static $RAW_MAP = null;

    private $rawValue;
    private $displayZh;
    private $displayEn;
    private $description;

    private function __construct(string $rawValue, string $displayZh, string $displayEn, string $description)
    {
        $this->rawValue = $rawValue;
        $this->displayZh = $displayZh;
        $this->displayEn = $displayEn;
        $this->description = $description;
    }

    /** 一次性初始化全部 21 个枚举单例与 RAW_MAP。 */
    private static function init(): void
    {
        if (self::$RAW_MAP !== null) return;
        $defs = [
            ['AICrawler', 'AI 爬虫', 'AICrawler', 'AI 训练 / AI 搜索爬虫（GPTBot、ClaudeBot 等）'],
            ['Backbone', '骨干网', 'Backbone', '运营商骨干传输网 / 国际出口'],
            ['Broadband', '宽带', 'Broadband', '家庭/企业宽带接入（xDSL、光纤、Cable、拨号等）'],
            ['Business', '企业', 'Business', '企业专线 / 企业组网'],
            ['CDN', 'CDN', 'CDN', '内容分发网络'],
            ['Cloud', '云服务', 'Cloud', '公有云 / 托管云（AWS、阿里云、Azure 等）'],
            ['DNS', 'DNS', 'DNS', 'DNS 基础设施 / Anycast DNS'],
            ['DataCenter', '数据中心', 'DataCenter', 'IDC / 机房托管'],
            ['Education', '教育网', 'Education', '高校 / 科研网（CERNET 等）'],
            ['Finance', '金融', 'Finance', '银行 / 证券 / 保险等金融机构'],
            ['Government', '政府', 'Government', '政务 / 公共机构网络'],
            ['ISP', '互联网提供商', 'ISP', '未细分类型的通用 ISP 接入'],
            ['IXP', '交换中心', 'IXP', '互联网交换中心'],
            ['IoT', '物联网', 'IoT', '物联网设备接入网络'],
            ['Mobile', '移动网络', 'Mobile', '蜂窝移动网络（2G/3G/4G/5G）'],
            ['Reserved', '保留地址', 'Reserved', '保留 / 未分配地址'],
            ['Satellite', '卫星互联网', 'Satellite', '卫星 / 低轨星座接入（Starlink 等）'],
            ['Spider', '爬虫', 'Spider', '通用搜索引擎 / 通用网络爬虫'],
            ['Streaming', '流媒体', 'Streaming', '音视频 / 直播流媒体平台'],
            ['Unknown', '未知', 'Unknown', '无法判定用途'],
            ['VPN', 'VPN/代理', 'VPN', 'VPN / 代理 / 隐私网络出口'],
        ];
        $map = [];
        foreach ($defs as $d) {
            $inst = new KnownUsageType($d[0], $d[1], $d[2], $d[3]);
            $name = strtoupper(str_replace(['-', ' '], '_', $d[0]));
            // 用稳定属性名映射（与上面声明的静态属性一一对应）
            switch ($d[0]) {
                case 'AICrawler': self::$AI_CRAWLER = $inst; break;
                case 'Backbone': self::$BACKBONE = $inst; break;
                case 'Broadband': self::$BROADBAND = $inst; break;
                case 'Business': self::$BUSINESS = $inst; break;
                case 'CDN': self::$CDN = $inst; break;
                case 'Cloud': self::$CLOUD = $inst; break;
                case 'DNS': self::$DNS = $inst; break;
                case 'DataCenter': self::$DATA_CENTER = $inst; break;
                case 'Education': self::$EDUCATION = $inst; break;
                case 'Finance': self::$FINANCE = $inst; break;
                case 'Government': self::$GOVERNMENT = $inst; break;
                case 'ISP': self::$ISP = $inst; break;
                case 'IXP': self::$IXP = $inst; break;
                case 'IoT': self::$IOT = $inst; break;
                case 'Mobile': self::$MOBILE = $inst; break;
                case 'Reserved': self::$RESERVED = $inst; break;
                case 'Satellite': self::$SATELLITE = $inst; break;
                case 'Spider': self::$SPIDER = $inst; break;
                case 'Streaming': self::$STREAMING = $inst; break;
                case 'Unknown': self::$UNKNOWN = $inst; break;
                case 'VPN': self::$VPN = $inst; break;
            }
            $map[strtolower($inst->rawValue)] = $inst;
        }
        self::$RAW_MAP = $map;
    }

    public static function fromRaw(string $raw): ?KnownUsageType
    {
        self::init();
        return self::$RAW_MAP[strtolower(trim($raw))] ?? null;
    }

    public function rawValue(): string { return $this->rawValue; }
    public function getDisplayZh(): string { return $this->displayZh; }
    public function getDisplayEn(): string { return $this->displayEn; }
    public function getDescription(): string { return $this->description; }
    public function isKnown(): bool { return true; }
}

class UnknownUsageType extends UsageType
{
    private $rawValue;

    public function __construct(string $rawValue)
    {
        $this->rawValue = $rawValue;
    }

    public function rawValue(): string { return $this->rawValue ?? ''; }
    public function getDisplayZh(): string { return $this->rawValue !== null ? $this->rawValue : '未知'; }
    public function getDisplayEn(): string { return $this->rawValue !== null ? $this->rawValue : 'Unknown'; }
    public function getDescription(): string { return '未预期的细分场景分类: ' . ($this->rawValue ?? ''); }
    public function isKnown(): bool { return false; }
}

/* ===========================================================================
 * 行号三元组 RowIds
 * =========================================================================== */
class RowIds
{
    public $geoId;
    public $asnId;
    public $usageId;

    public function __construct(int $geoId, int $asnId, int $usageId)
    {
        $this->geoId = $geoId;
        $this->asnId = $asnId;
        $this->usageId = $usageId;
    }
}

/* ===========================================================================
 * 批量查询三态结果 BatchResult
 * =========================================================================== */
class BatchResult
{
    public $input;
    public $result;   // GeoInfo | null
    public $error;    // QzdbException | null

    public function __construct(string $input, ?GeoInfo $result, ?QzdbException $error)
    {
        $this->input = $input;
        $this->result = $result;
        $this->error = $error;
    }

    /** 查询成功且命中记录 */
    public function isSuccess(): bool
    {
        return $this->error === null && $this->result !== null;
    }

    /** 合法 IP 但未找到记录 */
    public function isNotFound(): bool
    {
        return $this->error === null && $this->result === null;
    }

    /** 输入格式错误或底层故障 */
    public function hasError(): bool
    {
        return $this->error !== null;
    }
}

/* ===========================================================================
 * 查询结果实体 GeoInfo
 * =========================================================================== */
class GeoInfo implements \ArrayAccess
{
    private $values;
    private $fieldNames;
    private $floatIndices;
    private $normalizedMap;

    public function __construct(array $values = [], array $fieldNames = [], array $floatIndices = [], ?array $normalizedMap = null)
    {
        $this->values = $values;
        $this->fieldNames = $fieldNames;
        $this->floatIndices = array_flip($floatIndices);
        $this->normalizedMap = $normalizedMap !== null ? $normalizedMap : self::buildNormalizedMap($fieldNames);
    }

    /** 归一化：小写化并移除 '_' 与 '-'（契约 §6.1）。 */
    public static function normalizeKey(string $key): string
    {
        $s = strtolower($key);
        return str_replace(['_', '-'], '', $s);
    }

    public static function buildNormalizedMap(array $fieldNames): array
    {
        $map = [];
        foreach ($fieldNames as $i => $name) {
            if ($name !== null) {
                $k = self::normalizeKey($name);
                if (!isset($map[$k])) {
                    $map[$k] = $i;
                }
            }
        }
        return $map;
    }

    /** toJson 数值字段判定：longitude / latitude / asn / geo_id 输出 JSON 数字（契约 §6.2）。 */
    public static function isNumericFieldName(string $name): bool
    {
        $n = self::normalizeKey($name);
        return $n === 'longitude' || $n === 'latitude' || $n === 'asn' || $n === 'geoid';
    }

    private function fieldIndex(string $name): ?int
    {
        if ($name === '') return null;
        $k = self::normalizeKey($name);
        return $this->normalizedMap[$k] ?? null;
    }

    /** 通用取字段（大小写 / 下划线 / 连字符不敏感）。缺失返回 ""，绝不抛异常（契约 §6）。 */
    public function get(string $name): string
    {
        $idx = $this->fieldIndex($name);
        if ($idx === null) return '';
        return $this->values[$idx] ?? '';
    }

    public function __get($name)
    {
        return $this->get($name);
    }

    public function offsetExists($offset): bool
    {
        if (is_int($offset)) {
            return $offset >= 0 && $offset < count($this->values);
        }
        return $this->fieldIndex((string)$offset) !== null;
    }

    #[\ReturnTypeWillChange]
    public function offsetGet($offset)
    {
        if (is_int($offset)) {
            return $this->values[$offset] ?? '';
        }
        return $this->get((string)$offset);
    }

    public function offsetSet($offset, $value): void
    {
        if (is_int($offset)) {
            $this->values[$offset] = (string)$value;
        } else {
            $idx = $this->fieldIndex((string)$offset);
            if ($idx !== null) {
                $this->values[$idx] = (string)$value;
            }
        }
    }

    public function offsetUnset($offset): void
    {
        if (is_int($offset)) {
            unset($this->values[$offset]);
        } else {
            $idx = $this->fieldIndex((string)$offset);
            if ($idx !== null) {
                $this->values[$idx] = '';
            }
        }
    }

    /**
     * 原生浮点格式 = 6 位小数（契约 §8 规则 2）。
     *   - 整数值（116.0）→ "116"（无小数点）
     *   - 非整数 → 固定 6 位小数（116.4 → "116.400000"）
     *   - NaN / Inf → ""
     * 使用 %F（非 locale 相关，恒为 "." 小数点）避免 setlocale 影响。
     */
    public static function formatFloatValue($val): string
    {
        if (!is_float($val) && !is_int($val)) {
            $val = (float)$val;
        }
        if (!is_finite($val)) {
            return '';
        }
        if ($val == (int)$val) {
            return (string)(int)$val;
        }
        return sprintf('%.6F', $val);
    }

    /** 管道符分隔：逐字拼接已解码字符串值（契约 §8 规则 3，禁止重新格式化）。 */
    public function toPipe(): string
    {
        return implode('|', $this->values);
    }

    public function toPipeString(): string
    {
        return $this->toPipe();
    }

    public function __toString(): string
    {
        return $this->toPipe();
    }

    /** Map<field, value>（全 string）。 */
    public function toMap(): array
    {
        $map = [];
        foreach ($this->fieldNames as $i => $name) {
            $map[$name] = $this->values[$i] ?? '';
        }
        return $map;
    }

    /** 手写 JSON；数值字段输出为数字或 null，键名保持原始 snake_case（契约 §6.2）。 */
    public function toJson(): string
    {
        $sb = '{';
        $first = true;
        foreach ($this->fieldNames as $i => $name) {
            if ($name === null) continue;
            $val = ($i < count($this->values)) ? $this->values[$i] : null;
            if (!$first) {
                $sb .= ',';
            }
            $first = false;
            $sb .= '"' . self::escapeJson($name) . '":';
            $numeric = self::isNumericFieldName($name);
            if ($val === null || $val === '') {
                $sb .= $numeric ? 'null' : '""';
            } elseif ($numeric) {
                $sb .= self::isJsonNumber($val) ? $val : 'null';
            } else {
                $sb .= '"' . self::escapeJson($val) . '"';
            }
        }
        return $sb . '}';
    }

    private static function isJsonNumber(string $val): bool
    {
        $n = strlen($val);
        if ($n === 0) return false;
        $i = 0;
        if ($val[0] === '-') {
            if ($n === 1) return false;
            $i = 1;
        }
        $digit = false;
        $dot = false;
        for (; $i < $n; $i++) {
            $c = $val[$i];
            if ($c >= '0' && $c <= '9') {
                $digit = true;
            } elseif ($c === '.' && !$dot) {
                $dot = true;
            } else {
                return false;
            }
        }
        return $digit;
    }

    private static function escapeJson(string $s): string
    {
        $out = '';
        $len = strlen($s);
        for ($i = 0; $i < $len; $i++) {
            $c = $s[$i];
            $o = ord($c);
            switch ($c) {
                case '"': $out .= '\\"'; break;
                case '\\': $out .= '\\\\'; break;
                case "\b": $out .= '\\b'; break;
                case "\f": $out .= '\\f'; break;
                case "\n": $out .= '\\n'; break;
                case "\r": $out .= '\\r'; break;
                case "\t": $out .= '\\t'; break;
                default:
                    if ($o < 0x20) {
                        $out .= sprintf('\\u%04x', $o);
                    } else {
                        $out .= $c;
                    }
            }
        }
        return $out;
    }

    // ----- 语义 Getter 全集（契约 §6.3；缺失返回 "" 或 null）-----

    public function getCidr(): string { return ''; } // CIDR 非数据库字段，恒 ""
    public function getCountry(): string { return $this->get('country'); }
    public function getCountryEn(): string { return $this->get('country_en'); }
    public function getProvince(): string { return $this->get('province'); }
    public function getProvinceEn(): string { return $this->get('province_en'); }
    public function getCity(): string { return $this->get('city'); }
    public function getCityEn(): string { return $this->get('city_en'); }
    public function getDistrict(): string { return $this->get('district'); }

    public function getGeoId(): ?int
    {
        $v = $this->get('geo_id');
        if ($v === '') return null;
        return is_numeric($v) ? (int)$v : null;
    }

    public function getLongitude(): ?float
    {
        $v = $this->get('longitude');
        if ($v === '') return null;
        return is_numeric($v) ? (float)$v : null;
    }

    public function getLatitude(): ?float
    {
        $v = $this->get('latitude');
        if ($v === '') return null;
        return is_numeric($v) ? (float)$v : null;
    }

    public function getTimezone(): string { return $this->get('timezone'); }
    public function getIsp(): string { return $this->get('isp'); }
    public function getIspEn(): string { return $this->get('isp_en'); }

    public function getAsn(): ?int
    {
        $v = $this->get('asn');
        if ($v === '') return null;
        return is_numeric($v) ? (int)$v : null;
    }

    public function getAsName(): string { return $this->get('as_name'); }
    public function getAsDomain(): string { return $this->get('as_domain'); }

    public function getUsageType(): UsageType
    {
        return UsageType::fromString($this->get('usage_type'));
    }

    public function getCountryAlpha2(): string { return $this->get('country_alpha2'); }
    public function getCountryAlpha3(): string { return $this->get('country_alpha3'); }
    public function getCurrencyCode(): string { return $this->get('currency_code'); }
    public function getCurrencyName(): string { return $this->get('currency_name'); }
    public function getPhonePrefix(): string { return $this->get('phone_prefix'); }
    public function getEmojiFlag(): string { return $this->get('emoji_flag'); }
    public function getLanguages(): string { return $this->get('languages'); }
}

/* ===========================================================================
 * 核心读取器 QzdbReader
 * =========================================================================== */
class QzdbReader
{
    // 错误码（契约 §7）
    public const ERROR_NOT_FOUND = 1;
    public const ERROR_CORRUPTED = 2;
    public const ERROR_OUT_OF_BOUNDS = 3;
    public const ERROR_INVALID_PARAM = 4;
    public const ERROR_BAD_HEADER = 5;
    public const ERROR_BAD_MAGIC = 6;
    public const ERROR_UNSUPPORTED = 7;

    const SENTINEL = 0x80000000;
    const SENTINEL_MASK_24 = 0x7FFFFF;
    const SENTINEL_MASK_31 = 0x7FFFFFFF;
    const FLOAT_FIELDS = ['longitude' => true, 'latitude' => true];
    const MAX_TRIE_WALK_STEPS = 1000;
    const MAX_POOL_COUNT = 1 << 26;
    const GEO_CACHE_LIMIT = 1 << 16; // 有界缓存上限（开放寻址 / 碰撞只重算）

    // 数据源
    private $data = null;        // 缓冲模式：完整文件字节
    private $stream = null;      // 流式模式：fopen 句柄（大文件）
    private $fileSize = 0;
    private $verifyCrc = true;
    private $closed = false;

    // 加载参数
    private $groupIndex = 0;

    // Header
    private $flags = 0;
    private $hasV4 = false;
    private $hasV6 = false;
    private $v4Node24 = false;
    private $v6Node24 = false;
    private $v6JumpBits = 16;
    private $poolCount = 0;
    private $poolIdxSize = 2;
    private $geoCount = 0;
    private $rowCount = 0;
    private $v4RecCount = 0;
    private $v6RecCount = 0;
    private $v4NodeCount = 0;
    private $v6NodeCount = 0;
    private $ipRowSize = 6;
    private $geoEntryGroupCount = 0;
    private $buildDate = 0;
    private $storedCrc = 0;

    // Offsets
    private $offV4Jump = 0;
    private $offV4Nodes = 0;
    private $offV6Jump = 0;
    private $offV6Nodes = 0;
    private $offIPRow = 0;
    private $offGeoEntries = 0;
    private $offPools = 0;
    private $offMeta = 0;
    private $offRowSchema = 0;
    private $offGroupSchema = 0;

    private $rowGeoWidth = 3;
    private $rowAsnWidth = 3;
    private $rowUsageWidth = 0;

    // Schema
    private $groupFieldCounts = [];
    private $groupEntryCounts = [];
    private $groupDimMasks = [];
    private $groupEntryOffsets = [];

    private $groupStrides = [];
    private $groupFieldWidths = [];
    private $groupFieldOffsets = [];
    private $groupFieldNative = [];
    private $groupFieldNativeType = [];
    private $groupFieldIds = [];
    private $groupPoolSectionIds = [];

    private $groupPoolDescs = null;
    private $poolsLoaded = false;

    // 元信息
    private $fieldNames = [];
    private $floatFieldIndices = [];
    private $normalizedFieldMap = [];
    private $versionName = '';
    private $description = '';
    private $edition = '';
    private $groupCount = 0;

    // per-snapshot 有界无锁 GeoInfo 缓存
    private $geoCache = [];
    private $geoCacheSize = 0;

    // 兼容旧用法的全局单例
    private static $instance = null;

    private static $HEX = null;
    private static $crc32bTable = null;
    private static $usageInit = false;

    public function __construct($dbPath = null, $groupIndex = 0, bool $verifyCrc = true)
    {
        $this->groupIndex = $groupIndex;
        $this->verifyCrc = $verifyCrc;
        setlocale(LC_NUMERIC, 'C'); // 浮点格式化与 locale 无关
        if ($dbPath !== null) {
            $this->load($dbPath, $verifyCrc);
        }
    }

    public function __destruct()
    {
        $this->close();
    }

    public function close(): void
    {
        if ($this->stream !== null && is_resource($this->stream)) {
            @fclose($this->stream);
        }
        $this->stream = null;
        $this->data = null;
        $this->closed = true;
        $this->geoCache = [];
        $this->geoCacheSize = 0;
    }

    public function isClosed(): bool
    {
        return $this->closed;
    }

    // ------------------------------------------------------------------
    // 加载入口
    // ------------------------------------------------------------------

    /** 兼容旧用法：从文件路径加载（默认校验 CRC）。 */
    public function load($dbPath, bool $verifyCrc = true): void
    {
        $this->verifyCrc = $verifyCrc;
        $size = @filesize($dbPath);
        if ($size === false) {
            throw new QzdbException("Cannot stat database file: " . $dbPath, self::ERROR_INVALID_PARAM);
        }
        $this->fileSize = $size;

        // 自适应存储：文件大于内存上限一半时走流式（fseek/fread，O(1) 内存）；
        // 否则缓冲到内存（速度更快）。两条路径都经 readBytes()，解析结果逐字节一致。
        $memLimit = $this->parseMemoryLimitBytes();
        if ($memLimit > 0 && $size > (int)($memLimit * 0.5)) {
            $this->stream = @fopen($dbPath, 'rb');
            if ($this->stream === false || $this->stream === null) {
                throw new QzdbException("Cannot open database file: " . $dbPath, self::ERROR_INVALID_PARAM);
            }
            $this->data = null;
        } else {
            $this->data = @file_get_contents($dbPath);
            if ($this->data === false) {
                throw new QzdbException("Cannot read database file: " . $dbPath, self::ERROR_INVALID_PARAM);
            }
            $this->stream = null;
        }

        $this->parseHeader();
        if ($this->verifyCrc && !$this->rawVerifyCrc()) {
            throw new QzdbException('CRC32 checksum mismatch — the .qzdb file is corrupted or truncated', self::ERROR_CORRUPTED);
        }
        $this->geoCache = [];
        $this->geoCacheSize = 0;
    }

    /** 从内存字节加载（拷贝语义）。 */
    public function loadBytes(string $bytes, bool $verifyCrc = true): void
    {
        $this->verifyCrc = $verifyCrc;
        $this->fileSize = strlen($bytes);
        $this->data = $bytes;
        $this->stream = null;
        $this->parseHeader();
        if ($this->verifyCrc && !$this->rawVerifyCrc()) {
            throw new QzdbException('CRC32 checksum mismatch — the .qzdb buffer is corrupted or truncated', self::ERROR_CORRUPTED);
        }
        $this->geoCache = [];
        $this->geoCacheSize = 0;
    }

    /** 从输入流句柄加载（读取全部字节到内存）。 */
    public function loadStream($handle, bool $verifyCrc = true): void
    {
        $this->verifyCrc = $verifyCrc;
        if (!is_resource($handle)) {
            throw new QzdbException('Invalid stream handle', self::ERROR_INVALID_PARAM);
        }
        $bytes = stream_get_contents($handle);
        if ($bytes === false) {
            throw new QzdbException('Failed to read from stream', self::ERROR_INVALID_PARAM);
        }
        $this->loadBytes($bytes, $verifyCrc);
    }

    // ------------------------------------------------------------------
    // 热更新（原子替换；reload 强制 CRC）
    // ------------------------------------------------------------------

    public function reload($dbPath): void
    {
        // 构造完整新快照；成功后再替换（本实现单线程，整体赋值即原子）。
        $snap = new QzdbReader($dbPath, $this->groupIndex, true); // 强制 CRC
        $this->assign($snap);
    }

    public function reloadBuffer(string $bytes): void
    {
        if ($bytes === '' || $bytes === null) {
            throw new QzdbException('Reload buffer cannot be null or empty', self::ERROR_INVALID_PARAM);
        }
        $snap = new QzdbReader(null, $this->groupIndex, true);
        $snap->loadBytes($bytes, true);
        $this->assign($snap);
    }

    /** 把另一实例的快照状态整体搬过来（reload 成功后调用）。 */
    private function assign(QzdbReader $src): void
    {
        foreach (get_object_vars($src) as $k => $v) {
            if ($k === 'instance' || $k === 'HEX' || $k === 'crc32bTable' || $k === 'usageInit') continue;
            $this->$k = $v;
        }
        // 关闭被替换实例持有的流，避免句柄泄漏
        if ($src->stream !== null && is_resource($src->stream)) {
            // src 仍持有流用于自身快照；不关闭，交由 src 析构/GC 处理
        }
    }

    // ------------------------------------------------------------------
    // 单条查询 API（契约 §3）
    // ------------------------------------------------------------------

    /** 字符串查询：IPv4 / IPv6 / IPv4-Mapped 均可。未命中或非法 IP 返回 null（契约 §4）。 */
    public function find($ipStr)
    {
        if ($this->closed) return null;
        if ($ipStr === null || $ipStr === '') return null;
        $result = self::fastParseIp($ipStr);
        if ($result === null) return null;
        list($v4, $v6) = $result;
        if ($v4 !== null) return $this->findUint($v4);
        if (!$this->hasV6) return null;
        return $this->findV6Bin($v6);
    }

    /** IPv4 整数（主机序，最高字节在前）查询。 */
    public function findUint(int $ipInt)
    {
        if ($this->closed) return null;
        if (!$this->hasV4) return null;
        $rowId = $this->trieWalkV4($ipInt);
        if ($rowId === 0) return null;
        return $this->resolveRowId($rowId, $this->groupIndex);
    }

    /** 原始 16 字节（IPv6）/ 4 字节（IPv4 网络序）查询。长度非法返回 null。 */
    public function findBytes(string $bytes)
    {
        if ($this->closed) return null;
        $len = strlen($bytes);
        if ($len === 4) {
            $ipInt = ((ord($bytes[0]) & 0xFF) << 24) | ((ord($bytes[1]) & 0xFF) << 16)
                   | ((ord($bytes[2]) & 0xFF) << 8) | (ord($bytes[3]) & 0xFF);
            return $this->findUint($ipInt);
        }
        if ($len === 16) {
            return $this->findV6Bin($bytes);
        }
        return null;
    }

    public function findV6Bin(string $ipBin)
    {
        if ($this->closed) return null;
        if (!$this->hasV6) return null;
        $rowId = $this->trieWalkV6($ipBin);
        if ($rowId === 0) return null;
        return $this->resolveRowId($rowId, $this->groupIndex);
    }

    /** 管道符字符串：未命中 / 非法返回 ""（契约 §3）。 */
    public function findStr($ipStr): string
    {
        $info = $this->find($ipStr);
        return $info === null ? '' : $info->toPipe();
    }

    /** 字段投影：只返回指定字段。fields 为空等价于 find（契约 §3）。 */
    public function findFields($ipStr, $fieldNames = null)
    {
        if ($fieldNames === null || (is_array($fieldNames) && count($fieldNames) === 0)) {
            return $this->find($ipStr);
        }
        $info = $this->find($ipStr);
        if ($info === null) return null;
        $projNames = [];
        $projValues = [];
        foreach ($fieldNames as $f) {
            $projNames[] = $f;
            $projValues[] = $info->get($f);
        }
        return new GeoInfo($projValues, $projNames);
    }

    // ------------------------------------------------------------------
    // 低级行号 API（契约 §5）
    // ------------------------------------------------------------------

    public function lookupRowId($ipStr): int
    {
        if ($this->closed) return 0;
        if ($ipStr === null || $ipStr === '') return 0;
        $result = self::fastParseIp($ipStr);
        if ($result === null) return 0;
        list($v4, $v6) = $result;
        if ($v4 !== null) return $this->lookupRowIdUint($v4);
        return $this->lookupRowIdV6($v6);
    }

    public function lookupRowIdUint(int $ipInt): int
    {
        if ($this->closed || !$this->hasV4) return 0;
        return $this->trieWalkV4($ipInt);
    }

    public function lookupRowIdBytes(string $bytes): int
    {
        if ($this->closed) return 0;
        $len = strlen($bytes);
        if ($len === 4) {
            $ipInt = ((ord($bytes[0]) & 0xFF) << 24) | ((ord($bytes[1]) & 0xFF) << 16)
                   | ((ord($bytes[2]) & 0xFF) << 8) | (ord($bytes[3]) & 0xFF);
            return $this->lookupRowIdUint($ipInt);
        }
        if ($len === 16) {
            return $this->lookupRowIdV6($bytes);
        }
        return 0;
    }

    public function lookupRowIdV6(string $ipBin): int
    {
        if ($this->closed || !$this->hasV6) return 0;
        return $this->trieWalkV6($ipBin);
    }

    /** 由行号反查 Geo / ASN / Usage 三类索引。越界返回 null。 */
    public function lookupIds(int $rowId): ?RowIds
    {
        if ($this->closed) return null;
        if ($rowId <= 0 || $rowId >= $this->rowCount) return null;
        $row = $this->readIPRow($rowId);
        return new RowIds($row[0], $row[1], $row[2]);
    }

    // ------------------------------------------------------------------
    // 批量 / 流式 API（契约 §5）
    // ------------------------------------------------------------------

    /** 批量字符串查询，逐条容错，保留三态。 */
    public function findBatch(array $ips): array
    {
        $out = [];
        foreach ($ips as $ip) {
            try {
                $info = $this->find($ip);
                $out[] = new BatchResult((string)$ip, $info, null);
            } catch (QzdbException $e) {
                $out[] = new BatchResult((string)$ip, null, $e);
            }
        }
        return $out;
    }

    public function findBatchFields(array $ips, $fields): array
    {
        $out = [];
        foreach ($ips as $ip) {
            try {
                $info = $this->findFields($ip, $fields);
                $out[] = new BatchResult((string)$ip, $info, null);
            } catch (QzdbException $e) {
                $out[] = new BatchResult((string)$ip, null, $e);
            }
        }
        return $out;
    }

    /** 流式查询：惰性产出，内存恒定（Generator）。 */
    public function findStream(iterable $ips): \Generator
    {
        foreach ($ips as $ip) {
            try {
                $info = $this->find($ip);
                yield new BatchResult((string)$ip, $info, null);
            } catch (QzdbException $e) {
                yield new BatchResult((string)$ip, null, $e);
            }
        }
    }

    // ------------------------------------------------------------------
    // CIDR 反查 API（契约 §5 / §8 规则 6）
    // ------------------------------------------------------------------

    /** 反查最具体网段（如 "1.0.1.0/24"、"2001:218::/32"）；未覆盖返回 null；非法 IP 返回 null（PHP 语义）。 */
    public function lookupCidr($ipStr)
    {
        if ($this->closed) return null;
        if ($ipStr === null || $ipStr === '') return null;
        $ip = trim($ipStr);
        if ($ip === '') return null;
        if (strpos($ip, ':') !== false) {
            $bytes = self::parseIpv6Raw($ip);
            if ($bytes === null) return null;
            if ($this->isV4MappedBytes($bytes)) {
                $v4 = $this->v4FromMappedBytes($bytes);
                $n = $this->lookupV4PrefixLen($v4);
                return $n < 0 ? null : $this->formatV4Cidr($v4, $n);
            }
            $n = $this->lookupV6PrefixLen($bytes);
            return $n < 0 ? null : $this->formatV6Cidr($bytes, $n);
        }
        $v4 = self::fastParseIpv4($ip);
        if ($v4 === null) return null;
        $n = $this->lookupV4PrefixLen($v4);
        return $n < 0 ? null : $this->formatV4Cidr($v4, $n);
    }

    public function lookupCidrUint(int $ipInt)
    {
        if ($this->closed || !$this->hasV4) return null;
        $n = $this->lookupV4PrefixLen($ipInt);
        return $n < 0 ? null : $this->formatV4Cidr($ipInt, $n);
    }

    public function lookupCidrBytes(string $bytes)
    {
        if ($this->closed) return null;
        $len = strlen($bytes);
        if ($len !== 4 && $len !== 16) return null;
        if ($len === 16) {
            if ($this->isV4MappedBytes($bytes)) {
                $v4 = $this->v4FromMappedBytes($bytes);
                $n = $this->lookupV4PrefixLen($v4);
                return $n < 0 ? null : $this->formatV4Cidr($v4, $n);
            }
            $n = $this->lookupV6PrefixLen($bytes);
            return $n < 0 ? null : $this->formatV6Cidr($bytes, $n);
        }
        $v4 = ((ord($bytes[0]) & 0xFF) << 24) | ((ord($bytes[1]) & 0xFF) << 16)
            | ((ord($bytes[2]) & 0xFF) << 8) | (ord($bytes[3]) & 0xFF);
        $n = $this->lookupV4PrefixLen($v4);
        return $n < 0 ? null : $this->formatV4Cidr($v4, $n);
    }

    // ------------------------------------------------------------------
    // 元信息自省 API（契约 §5）
    // ------------------------------------------------------------------

    public function getVersion(): string { return $this->versionName; }
    public function getDataMonth(): string
    {
        if ($this->buildDate <= 0) return '';
        $y = intdiv($this->buildDate, 10000);
        $m = intdiv($this->buildDate, 100) % 100;
        return sprintf('%04d-%02d', $y, $m);
    }
    public function getEdition(): string { return $this->edition; }
    public function getScope(): string { return ''; } // 当前格式无 scope 字段（契约 §5）
    public function getBuildTime(): string
    {
        if ($this->buildDate <= 0) return '';
        $y = intdiv($this->buildDate, 10000);
        $m = intdiv($this->buildDate, 100) % 100;
        $d = $this->buildDate % 100;
        return sprintf('%04d-%02d-%02d', $y, $m, $d);
    }
    public function getDescription(): string { return $this->description; }
    public function getVersionCode(): int
    {
        $pcMap = [6 => 1, 7 => 2, 25 => 3];
        return $pcMap[$this->poolCount] ?? 3;
    }
    public function getFileHash(): string { return sprintf('%08x', $this->storedCrc & 0xFFFFFFFF); }
    public function getFieldNames(): array { return $this->fieldNames; }
    public function hasField(string $name): bool
    {
        return isset($this->normalizedFieldMap[GeoInfo::normalizeKey($name)]);
    }
    public function getGroupCount(): int { return $this->groupCount; }
    public function getPoolCount(): int { return $this->poolCount; }

    public function verifyCrc(): bool
    {
        if ($this->fileSize < 20) return false;
        return $this->rawVerifyCrc();
    }

    private function rawVerifyCrc(): bool
    {
        if ($this->fileSize < 20) return false;
        $computed = self::crc32bComputeFile((string)$this->data, $this->stream, $this->fileSize);
        return (($this->storedCrc & 0xFFFFFFFF) === ($computed & 0xFFFFFFFF));
    }

    // ------------------------------------------------------------------
    // 解析：Header / Schema / Metadata
    // ------------------------------------------------------------------

    private function parseHeader(): void
    {
        if ($this->fileSize < 192) {
            throw new QzdbException('File too small for QZDB header', self::ERROR_CORRUPTED);
        }

        $magic = $this->readBytes(0, 4);
        if ($magic !== 'QZDB') {
            throw new QzdbException('Invalid magic, expected QZDB', self::ERROR_BAD_MAGIC);
        }

        $fmtVer = $this->readByte(4);
        if ($fmtVer !== 1) {
            throw new QzdbException("Unsupported format version: {$fmtVer} (only version 1 is supported)", self::ERROR_UNSUPPORTED);
        }

        $this->flags = $this->safeReadU16(8);
        $this->hasV4 = (bool)($this->flags & 1);
        $this->hasV6 = (bool)($this->flags & 2);
        $this->v4Node24 = (bool)($this->flags & 0x10);
        $this->v6Node24 = (bool)($this->flags & 0x20);

        $this->v6JumpBits = $this->readByte(11);
        if ($this->v6JumpBits === 0) {
            $this->v6JumpBits = 16;
        }
        if ($this->v6JumpBits < 8 || $this->v6JumpBits > 20) {
            throw new QzdbException("v6JumpBits out of range [8,20]: {$this->v6JumpBits}", self::ERROR_CORRUPTED);
        }

        $this->poolCount = $this->readByte(12);
        $this->poolIdxSize = $this->readByte(13);
        if ($this->poolIdxSize !== 2 && $this->poolIdxSize !== 3) {
            throw new QzdbException("poolIdxSize must be 2 or 3, got {$this->poolIdxSize}", self::ERROR_CORRUPTED);
        }
        $this->geoCount = $this->safeReadU16(14);
        $this->rowCount = $this->safeReadU32(20);
        $this->v4RecCount = $this->safeReadU32(24);
        $this->v6RecCount = $this->safeReadU32(28);
        $this->buildDate = $this->safeReadU32(32);
        $this->storedCrc = $this->safeReadU32(16);

        $hs = $this->safeReadU32(36);
        if ($hs !== 192) {
            throw new QzdbException("Unexpected header size: {$hs}", self::ERROR_CORRUPTED);
        }

        $this->offRowSchema = $this->safeReadU64(40);
        $this->offGroupSchema = $this->safeReadU64(48);
        $this->offV4Jump = $this->safeReadU64(64);
        $this->offV4Nodes = $this->safeReadU64(72);
        $this->offV6Jump = $this->safeReadU64(80);
        $this->offV6Nodes = $this->safeReadU64(88);
        $this->offIPRow = $this->safeReadU64(96);
        $this->offGeoEntries = $this->safeReadU64(104);
        $this->offPools = $this->safeReadU64(136);
        $this->offMeta = $this->safeReadU64(144);

        $this->v4NodeCount = $this->safeReadU32(152);
        $this->v6NodeCount = $this->safeReadU32(156);
        $this->ipRowSize = $this->safeReadU32(160);
        if ($this->ipRowSize < 1 || $this->ipRowSize > 64) {
            throw new QzdbException("ipRowSize out of range [1,64]: {$this->ipRowSize}", self::ERROR_CORRUPTED);
        }
        $this->geoEntryGroupCount = $this->safeReadU32(164);
        if ($this->geoEntryGroupCount < 1 || $this->geoEntryGroupCount > 255) {
            throw new QzdbException("geoEntryGroupCount out of range [1,255]: {$this->geoEntryGroupCount}", self::ERROR_CORRUPTED);
        }

        $this->parseRowSchema();

        $len = $this->fileSize;
        $v4NodeSize = $this->v4Node24 ? 6 : 8;
        $v6NodeSize = $this->v6Node24 ? 6 : 8;
        $v6JumpSize = (1 << $this->v6JumpBits) * 4;

        if ($this->offV4Jump > 0 && $this->offV4Jump + 65536 * 4 > $len) {
            throw new QzdbException('V4 jump table out of bounds', self::ERROR_OUT_OF_BOUNDS);
        }
        if ($this->offV4Nodes > 0 && $this->offV4Nodes + $this->v4NodeCount * $v4NodeSize > $len) {
            throw new QzdbException('V4 nodes table out of bounds', self::ERROR_OUT_OF_BOUNDS);
        }
        if ($this->offV6Jump > 0 && $this->offV6Jump + $v6JumpSize > $len) {
            throw new QzdbException('V6 jump table out of bounds', self::ERROR_OUT_OF_BOUNDS);
        }
        if ($this->offV6Nodes > 0 && $this->offV6Nodes + $this->v6NodeCount * $v6NodeSize > $len) {
            throw new QzdbException('V6 nodes table out of bounds', self::ERROR_OUT_OF_BOUNDS);
        }
        if ($this->offIPRow > 0 && $this->offIPRow + $this->rowCount * $this->ipRowSize > $len) {
            throw new QzdbException('IP row table out of bounds', self::ERROR_OUT_OF_BOUNDS);
        }

        $this->groupEntryOffsets = [];
        for ($i = 0; $i < 4; $i++) {
            $this->groupEntryOffsets[] = $this->safeReadU48(168 + $i * 6);
        }

        $gmOff = $this->offGeoEntries;
        $groupCount = $this->readByte($gmOff);
        $gmOff += 1;

        $actualGroups = min($groupCount, max(1, $this->geoEntryGroupCount));
        if ($actualGroups > 4) $actualGroups = 4;
        $this->groupCount = $actualGroups;
        $this->groupFieldCounts = array_fill(0, $actualGroups, 0);
        $this->groupEntryCounts = array_fill(0, $actualGroups, 0);
        $this->groupDimMasks = array_fill(0, $actualGroups, 0);

        for ($gi = 0; $gi < $actualGroups; $gi++) {
            $this->groupFieldCounts[$gi] = $this->readByte($gmOff);
            $gmOff += 1;
            $this->groupEntryCounts[$gi] = $this->safeReadU32($gmOff);
            $gmOff += 4;
            $this->groupDimMasks[$gi] = $this->safeReadU16($gmOff);
            $gmOff += 2;
        }

        $this->groupStrides = array_fill(0, $actualGroups, 0);
        $this->groupFieldWidths = array_fill(0, $actualGroups, null);
        $this->groupFieldOffsets = array_fill(0, $actualGroups, null);
        $this->groupFieldNative = array_fill(0, $actualGroups, null);
        $this->groupFieldNativeType = array_fill(0, $actualGroups, null);
        $this->groupFieldIds = array_fill(0, $actualGroups, null);
        $this->groupPoolSectionIds = array_fill(0, $actualGroups, null);

        if ($this->offGroupSchema > 0) {
            $sp = $this->offGroupSchema;
            $gsGroupCount = $this->safeReadU16($sp);
            $sp += 2;
            $maxGsGroups = min($gsGroupCount, $actualGroups);
            for ($gi = 0; $gi < $maxGsGroups; $gi++) {
                $sp += 2;
                $fldCount = $this->safeReadU16($sp);
                $sp += 2;
                $sp += 4;
                $stride = $this->safeReadU32($sp);
                $sp += 4;
                $sp += 4;

                if ($gi < $actualGroups) {
                    $this->groupStrides[$gi] = $stride;
                    $widths = array_fill(0, $fldCount, 0);
                    $offsets = array_fill(0, $fldCount, 0);
                    $natives = array_fill(0, $fldCount, false);
                    $natTypes = array_fill(0, $fldCount, 0);
                    $fieldIds = array_fill(0, $fldCount, 0);
                    $poolSectionIds = array_fill(0, $fldCount, 0);
                    for ($fi = 0; $fi < $fldCount; $fi++) {
                        $fieldIds[$fi] = $this->safeReadU16($sp);
                        $sp += 2;
                        $widths[$fi] = $this->readByte($sp);
                        $sp += 1;
                        $fieldFlags = $this->readByte($sp);
                        $sp += 1;
                        $natives[$fi] = ($fieldFlags & 0x01) !== 0;
                        $natTypes[$fi] = ($fieldFlags >> 1) & 0x03;
                        $offsets[$fi] = $this->safeReadU32($sp);
                        $sp += 4;
                        $poolSectionIds[$fi] = $this->safeReadU32($sp);
                        $sp += 4;
                    }
                    $this->groupFieldWidths[$gi] = $widths;
                    $this->groupFieldOffsets[$gi] = $offsets;
                    $this->groupFieldNative[$gi] = $natives;
                    $this->groupFieldNativeType[$gi] = $natTypes;
                    $this->groupFieldIds[$gi] = $fieldIds;
                    $this->groupPoolSectionIds[$gi] = $poolSectionIds;
                } else {
                    $sp += $fldCount * 12;
                }
            }
        }

        for ($g = 0; $g < $actualGroups; $g++) {
            if ($this->groupStrides[$g] === 0) {
                $this->groupStrides[$g] = $this->groupFieldCounts[$g] * $this->poolIdxSize;
            }
            if ($this->groupFieldWidths[$g] === null) {
                $this->groupFieldWidths[$g] = array_fill(0, $this->groupFieldCounts[$g], $this->poolIdxSize);
            }
            if ($this->groupFieldOffsets[$g] === null) {
                $tempOffsets = [];
                for ($i = 0; $i < $this->groupFieldCounts[$g]; $i++) {
                    $tempOffsets[] = $i * $this->poolIdxSize;
                }
                $this->groupFieldOffsets[$g] = $tempOffsets;
            }
            if ($this->groupFieldNative[$g] === null) {
                $this->groupFieldNative[$g] = array_fill(0, $this->groupFieldCounts[$g], false);
            }
            if ($this->groupFieldNativeType[$g] === null) {
                $this->groupFieldNativeType[$g] = array_fill(0, $this->groupFieldCounts[$g], 0);
            }
            if ($this->groupFieldIds[$g] === null) {
                $this->groupFieldIds[$g] = array_fill(0, $this->groupFieldCounts[$g], -1);
            }
            if ($this->groupPoolSectionIds[$g] === null) {
                $this->groupPoolSectionIds[$g] = array_fill(0, $this->groupFieldCounts[$g], 0);
            }
        }

        $this->resolveFieldNames();
        $this->repairDimMasks();
        $this->poolsLoaded = false;
        $this->groupPoolDescs = null;
    }

    private function resolveFieldNames(): void
    {
        $offMeta = $this->offMeta;
        $this->versionName = '';
        $this->description = '';
        $this->edition = '';
        if (($this->flags & 4) && $offMeta > 0 && $offMeta + 4 <= $this->fileSize) {
            $fieldNames = null;
            $pos = $offMeta;
            while ($pos + 4 <= $this->fileSize) {
                $t = $this->readByte($pos);
                $length = $this->safeReadU16($pos + 2);
                if ($t === 0 || $length === 0) {
                    break;
                }
                if ($pos + 4 + $length > $this->fileSize) {
                    break;
                }
                $val = $this->readBytes($pos + 4, $length);
                if ($t === 1) {
                    $this->versionName = $val;
                } elseif ($t === 2) {
                    $fieldNames = explode('|', $val);
                } elseif ($t === 3) {
                    $this->description = $val;
                } elseif ($t === 4) {
                    $this->edition = $val;
                }
                $pos += 4 + $length;
            }

            if ($fieldNames && count($fieldNames) === $this->groupFieldCounts[0]) {
                $this->fieldNames = $fieldNames;
                $this->floatFieldIndices = [];
                foreach ($fieldNames as $i => $n) {
                    if (isset(self::FLOAT_FIELDS[$n])) {
                        $this->floatFieldIndices[] = $n;
                    }
                }
                $this->normalizedFieldMap = GeoInfo::buildNormalizedMap($fieldNames);
                return;
            }
        }

        $this->fieldNames = [];
        for ($i = 0; $i < $this->groupFieldCounts[0]; $i++) {
            $this->fieldNames[] = "field_{$i}";
        }
        $this->floatFieldIndices = [];
        $this->normalizedFieldMap = GeoInfo::buildNormalizedMap($this->fieldNames);

        // 兜底 edition
        if ($this->edition === '' && $this->versionName !== '') {
            $this->edition = $this->versionName;
        }
    }

    private function repairDimMasks(): void
    {
        $n = count($this->groupDimMasks);
        for ($g = 0; $g < $n; $g++) {
            if ($this->groupDimMasks[$g] !== 0) {
                continue;
            }
            $hasAsn = false;
            if (isset($this->groupFieldIds[$g]) && is_array($this->groupFieldIds[$g])) {
                foreach ($this->groupFieldIds[$g] as $fid) {
                    if ($fid == 1) {
                        $hasAsn = true;
                        break;
                    }
                }
            }
            if (!$hasAsn && is_array($this->fieldNames)) {
                foreach ($this->fieldNames as $n2) {
                    if ($n2 === 'asn') {
                        $hasAsn = true;
                        break;
                    }
                }
            }
            $this->groupDimMasks[$g] = $hasAsn ? 0x02 : 0x01;
        }
        // 兜底 edition：字段数推断
        if ($this->edition === '') {
            $this->edition = $this->inferEdition(count($this->fieldNames));
        }
    }

    private function inferEdition(int $numFields): string
    {
        if ($numFields >= 20) return 'ult';
        if ($numFields >= 12) return 'max';
        if ($numFields >= 8) return 'pro';
        return 'std';
    }

    private function parseRowSchema(): void
    {
        $this->rowGeoWidth = 3;
        $this->rowAsnWidth = 3;
        $this->rowUsageWidth = 0;
        if ($this->offRowSchema <= 0) return;
        $sp = $this->offRowSchema;
        $fCount = $this->readByte($sp);
        $stride = $this->readByte($sp + 1);
        if ($fCount < 1 || $fCount > 8) return;
        if ($sp + 4 + $fCount * 4 > $this->fileSize) return;
        if ($stride != $this->ipRowSize) return;

        $geoW = 0; $asnW = 0; $usageW = 0; $total = 0;
        $wpos = $sp + 4;
        $ok = true;
        for ($i = 0; $i < $fCount; $i++) {
            $fid = $this->readByte($wpos);
            $w = $this->readByte($wpos + 1);
            if ($fid === 0) $geoW = $w;
            elseif ($fid === 1) $asnW = $w;
            elseif ($fid === 2) $usageW = $w;
            $wpos += 4;
            $total += $w;
            if ($w < 1 || $w > 4) $ok = false;
        }
        if ($ok && $total === $this->ipRowSize) {
            $this->rowGeoWidth = $geoW;
            $this->rowAsnWidth = $asnW;
            $this->rowUsageWidth = $usageW;
        }
    }

    // ------------------------------------------------------------------
    // 池（惰性解析）
    // ------------------------------------------------------------------

    private function ensurePoolsLoaded(): void
    {
        if ($this->poolsLoaded) return;
        $this->poolsLoaded = true;

        $groupCount = count($this->groupFieldCounts);
        $this->groupPoolDescs = array_fill(0, $groupCount, []);

        if ($this->offPools <= 0) return;

        $poolCursor = $this->offPools;
        $poolEnd = $this->offMeta > 0 ? $this->offMeta : $this->fileSize;

        for ($g = 0; $g < $groupCount; $g++) {
            $fieldCount = $this->groupFieldCounts[$g];
            $groupDescs = [];
            $natives = $this->groupFieldNative[$g];
            for ($f = 0; $f < $fieldCount; $f++) {
                if ($natives && $f < count($natives) && $natives[$f]) {
                    $groupDescs[] = null;
                    continue;
                }
                if ($poolCursor + 4 > $poolEnd) {
                    $groupDescs[] = null;
                    continue;
                }
                $count = $this->safeReadU32($poolCursor);
                $poolCursor += 4;
                if ($this->offRowSchema > 0) {
                    $poolCursor += 4;
                }
                if ($count === 0 || $count > self::MAX_POOL_COUNT) {
                    $groupDescs[] = null;
                    continue;
                }
                $offsetTableBase = $poolCursor;
                $poolCursor += ($count + 1) * 4;
                $dataBase = $poolCursor;
                $totalLen = $this->safeReadU32($offsetTableBase + $count * 4);
                $poolCursor = $dataBase + $totalLen;
                $groupDescs[] = ['ot' => $offsetTableBase, 'db' => $dataBase, 'count' => $count];
            }
            $this->groupPoolDescs[$g] = $groupDescs;
        }
    }

    private function poolString($g, $f, $idx): string
    {
        if ($g < 0 || $g >= count($this->groupPoolDescs)) return '';
        if ($f < 0 || $f >= count($this->groupPoolDescs[$g])) return '';
        $desc = $this->groupPoolDescs[$g][$f];
        if ($desc === null) return '';
        if ($idx < 0 || $idx >= $desc['count']) return '';
        $start = $this->safeReadU32($desc['ot'] + $idx * 4);
        $end = $this->safeReadU32($desc['ot'] + ($idx + 1) * 4);
        $length = $end - $start;
        if ($length <= 0) return '';
        return $this->readBytes($desc['db'] + $start, $length);
    }

    // ------------------------------------------------------------------
    // Trie 遍历
    // ------------------------------------------------------------------

    private function getV4Child($nodeIdx, $bit)
    {
        if ($nodeIdx >= $this->v4NodeCount) return 0;
        if ($this->v4Node24) {
            $nodeOffset = $this->offV4Nodes + $nodeIdx * 6;
            $offset = $bit === 0 ? $nodeOffset : $nodeOffset + 3;
            $b0 = $this->readByte($offset);
            $b1 = $this->readByte($offset + 1);
            $b2 = $this->readByte($offset + 2);
            $val = $b0 | ($b1 << 8) | ($b2 << 16);
            if ($val & 0x800000) {
                return ($val & 0x7FFFFF) | self::SENTINEL;
            }
            return $val;
        } else {
            $childOff = $this->offV4Nodes + $nodeIdx * 8 + $bit * 4;
            return $this->safeReadU32($childOff);
        }
    }

    private function getV6Child($nodeIdx, $bit)
    {
        if ($nodeIdx >= $this->v6NodeCount) return 0;
        if ($this->v6Node24) {
            $nodeOffset = $this->offV6Nodes + $nodeIdx * 6;
            $offset = $bit === 0 ? $nodeOffset : $nodeOffset + 3;
            $b0 = $this->readByte($offset);
            $b1 = $this->readByte($offset + 1);
            $b2 = $this->readByte($offset + 2);
            $val = $b0 | ($b1 << 8) | ($b2 << 16);
            if ($val & 0x800000) {
                return ($val & 0x7FFFFF) | self::SENTINEL;
            }
            return $val;
        } else {
            $childOff = $this->offV6Nodes + $nodeIdx * 8 + $bit * 4;
            return $this->safeReadU32($childOff);
        }
    }

    private function trieWalkV4($ipInt)
    {
        $hi16 = ($ipInt >> 16) & 0xFFFF;
        $ptr = $this->safeReadU32($this->offV4Jump + $hi16 * 4);

        if ($ptr === 0) return 0;
        if ($ptr & self::SENTINEL) {
            return $ptr & self::SENTINEL_MASK_31;
        }

        $idx = $ptr;
        $suffix = ($ipInt & 0xFFFF) << 16;
        $steps = 0;

        while (true) {
            $bit = ($suffix >> 31) & 1;
            $child = $this->getV4Child($idx, $bit);

            if ($child === 0) return 0;
            if ($child & self::SENTINEL) {
                return $child & self::SENTINEL_MASK_31;
            }

            $idx = $child;
            $suffix <<= 1;
            $steps++;
            if ($steps >= self::MAX_TRIE_WALK_STEPS) return 0;
        }
    }

    private function trieWalkV6(string $ipBin)
    {
        $v6_jump_bits = $this->v6JumpBits;

        $idx_jump = 0;
        $bits_collected = 0;
        for ($i = 0; $i < 16; $i++) {
            $byte = ord($ipBin[$i]);
            $bits_left = $v6_jump_bits - $bits_collected;
            if ($bits_left <= 0) break;
            if ($bits_left >= 8) {
                $idx_jump = ($idx_jump << 8) | $byte;
                $bits_collected += 8;
            } else {
                $idx_jump = ($idx_jump << $bits_left) | ($byte >> (8 - $bits_left));
                $bits_collected += $bits_left;
                break;
            }
        }

        $ptr = $this->safeReadU32($this->offV6Jump + $idx_jump * 4);
        if ($ptr === 0) return 0;
        if ($ptr & self::SENTINEL) {
            return $ptr & self::SENTINEL_MASK_31;
        }

        $idx = $ptr;
        $depth = $v6_jump_bits;
        $steps = 0;

        while ($depth < 128) {
            if (++$steps >= self::MAX_TRIE_WALK_STEPS) return 0;
            $byteIdx = (int)($depth / 8);
            $bitIdx = 7 - ($depth % 8);
            $bit = (ord($ipBin[$byteIdx]) >> $bitIdx) & 1;

            $child = $this->getV6Child($idx, $bit);
            if ($child === 0) return 0;
            if ($child & self::SENTINEL) {
                return $child & self::SENTINEL_MASK_31;
            }

            $idx = $child;
            $depth += 1;
        }

        return 0;
    }

    // ------------------------------------------------------------------
    // IP 行 / Geo 解析（SENTINEL 剥离在解析前完成）
    // ------------------------------------------------------------------

    private function readIPRow($rowId)
    {
        if ($rowId <= 0 || $rowId >= $this->rowCount) {
            return [0, 0, 0];
        }
        $off = $this->offIPRow + $rowId * $this->ipRowSize;
        $geoId = 0;
        $asnId = 0;
        $usageTypeId = 0;

        if ($this->offRowSchema > 0) {
            $p = $off;
            $geoId = $this->safeReadUintWidth($p, $this->rowGeoWidth);
            $p += $this->rowGeoWidth;
            if ($this->rowAsnWidth > 0) {
                $asnId = $this->safeReadUintWidth($p, $this->rowAsnWidth);
                $p += $this->rowAsnWidth;
            }
            if ($this->rowUsageWidth > 0) {
                $usageTypeId = $this->safeReadUintWidth($p, $this->rowUsageWidth);
            }
        } else {
            $geoId = $this->safeReadU24($off);
            $asnId = $this->safeReadU24($off + 3);
            if ($this->ipRowSize >= 9) {
                $usageTypeId = $this->safeReadU24($off + 6);
            }
        }

        return [$geoId, $asnId, $usageTypeId];
    }

    /** 由 rowId 解析 GeoInfo。防御性剥离高位哨兵位（契约 §8 规则 1）。 */
    private function resolveRowId($rowId, $groupIndex)
    {
        $rowId &= self::SENTINEL_MASK_31; // 防御性剥离（idempotent）
        list($geoId, $asnId, $usageTypeId) = $this->readIPRow($rowId);
        $mask = $groupIndex < count($this->groupDimMasks) ? $this->groupDimMasks[$groupIndex] : 0;

        if ($mask & 0x02) {
            $entryId = $asnId;
        } elseif ($mask & 0x04) {
            $entryId = $usageTypeId;
        } else {
            $entryId = $geoId;
        }

        if ($entryId === 0) {
            return null;
        }
        return $this->resolveGeo($entryId, $groupIndex);
    }

    /** 解析 entryId 为 GeoInfo，带 per-snapshot 有界无锁缓存。 */
    private function resolveGeo($entryId, $groupIndex)
    {
        if ($groupIndex < 0 || $groupIndex >= count($this->groupFieldCounts)) return null;
        if ($entryId < 0 || $entryId >= $this->groupEntryCounts[$groupIndex]) return null;

        // 有界缓存命中：直接复用（近零分配）
        $cacheKey = $groupIndex . ':' . $entryId;
        if (isset($this->geoCache[$cacheKey])) {
            return $this->geoCache[$cacheKey];
        }

        $this->ensurePoolsLoaded();

        $fieldCount = $this->groupFieldCounts[$groupIndex];
        if ($fieldCount <= 0) return null;

        $groupEntryStart = $this->offGeoEntries + $this->groupEntryOffsets[$groupIndex];
        $stride = $this->groupStrides[$groupIndex];
        $entryOffset = $groupEntryStart + $entryId * $stride;

        $widths = $this->groupFieldWidths[$groupIndex];
        $baseOffsets = $this->groupFieldOffsets[$groupIndex];
        $natives = $this->groupFieldNative[$groupIndex];
        $natTypes = $this->groupFieldNativeType[$groupIndex];

        $values = [];
        for ($i = 0; $i < $fieldCount; $i++) {
            $w = $widths[$i];
            $fo = $entryOffset + $baseOffsets[$i];
            $isNative = $natives && $i < count($natives) && $natives[$i];

            if ($isNative) {
                $t = $natTypes && $i < count($natTypes) ? $natTypes[$i] : 0;
                if ($t === 1) {
                    // float
                    if ($w === 4) {
                        $valNum = $this->safeReadF32($fo);
                    } else {
                        $valNum = $this->safeReadF64($fo);
                    }
                    $val = GeoInfo::formatFloatValue($valNum);
                } else {
                    $valNum = $this->safeReadUintWidth($fo, $w);
                    $val = (string)$valNum;
                }
            } else {
                $idx = $this->safeReadUintWidth($fo, $w);
                $val = $this->poolString($groupIndex, $i, $idx);
            }

            $values[] = $val;
        }

        $info = new GeoInfo($values, $this->fieldNames, $this->floatFieldIndices, $this->normalizedFieldMap);

        // 写入有界缓存；超界则清空重建（碰撞只重算，绝不返回错值）
        if ($this->geoCacheSize >= self::GEO_CACHE_LIMIT) {
            $this->geoCache = [];
            $this->geoCacheSize = 0;
        }
        $this->geoCache[$cacheKey] = $info;
        $this->geoCacheSize++;

        return $info;
    }

    // ------------------------------------------------------------------
    // CIDR 前缀长度重建
    // ------------------------------------------------------------------

    private function lookupV4PrefixLen($ipInt): int
    {
        $this->curV4 = $ipInt & 0xFFFFFFFF;
        if (!$this->hasV4 || $this->offV4Jump <= 0) return -1;
        $ptr = $this->safeReadU32($this->offV4Jump + (($ipInt >> 16) & 0xFFFF) * 4);
        if ($ptr === 0) return -1;
        if ($ptr & self::SENTINEL) {
            return $this->walkV4Depth(0, 0, 16);
        }
        return $this->walkV4Depth($ptr, 16, 32);
    }

    private function walkV4Depth($idx, $startDepth, $maxDepth): int
    {
        if ($startDepth >= $maxDepth) return -1;
        for ($depth = $startDepth; $depth < $maxDepth; $depth++) {
            if ($idx >= $this->v4NodeCount) return -1;
            $bit = ($this->curV4 >> (31 - $depth)) & 1;
            $child = $this->getV4Child($idx, $bit);
            if ($child === 0) return -1;
            if ($child & self::SENTINEL) return $depth + 1;
            $idx = $child;
        }
        return -1;
    }

    private function lookupV6PrefixLen(string $ipBin): int
    {
        if (!$this->hasV6 || $this->offV6Jump <= 0 || strlen($ipBin) !== 16) return -1;
        $jumpBits = $this->v6JumpBits;
        $pref = $this->readPrefixBits($ipBin, $jumpBits);
        $ptr = $this->safeReadU32($this->offV6Jump + $pref * 4);
        if ($ptr === 0) return -1;
        if ($ptr & self::SENTINEL) {
            return $this->walkV6Depth($ipBin, 0, 0, $jumpBits);
        }
        return $this->walkV6Depth($ipBin, $ptr, $jumpBits, 128);
    }

    private function walkV6Depth(string $ipBin, $idx, $startDepth, $maxDepth): int
    {
        if ($startDepth >= $maxDepth) return -1;
        for ($depth = $startDepth; $depth < $maxDepth; $depth++) {
            if ($idx >= $this->v6NodeCount) return -1;
            $bit = (ord($ipBin[$depth >> 3]) >> (7 - ($depth & 7))) & 1;
            $child = $this->getV6Child($idx, $bit);
            if ($child === 0) return -1;
            if ($child & self::SENTINEL) return $depth + 1;
            $idx = $child;
        }
        return -1;
    }

    private function readPrefixBits(string $bytes, int $bits): int
    {
        $val = 0;
        for ($i = 0; $i < $bits; $i++) {
            $bit = (ord($bytes[$i >> 3]) >> (7 - ($i & 7))) & 1;
            $val = ($val << 1) | $bit;
        }
        return $val;
    }

    private function formatV4Cidr($ipInt, int $n): string
    {
        $net = $n === 0 ? 0 : ($ipInt & (0xFFFFFFFF << (32 - $n)));
        return (($net >> 24) & 0xFF) . '.' . (($net >> 16) & 0xFF) . '.'
            . (($net >> 8) & 0xFF) . '.' . ($net & 0xFF) . '/' . $n;
    }

    private function formatV6Cidr(string $ipBin, int $n): string
    {
        $net = $ipBin;
        for ($bit = $n; $bit < 128; $bit++) {
            $byteIdx = intdiv($bit, 8);
            $net[$byteIdx] = chr(ord($net[$byteIdx]) & ~(1 << (7 - ($bit & 7))));
        }
        $g = [];
        for ($i = 0; $i < 8; $i++) {
            $g[$i] = ((ord($net[2 * $i]) & 0xFF) << 8) | (ord($net[2 * $i + 1]) & 0xFF);
        }
        // RFC 5952：最长全零组段（并列取最左），长度 ≥ 2 才压缩
        $bestStart = -1; $bestLen = 0; $curStart = -1; $curLen = 0;
        for ($i = 0; $i < 8; $i++) {
            if ($g[$i] === 0) {
                if ($curStart < 0) { $curStart = $i; $curLen = 1; } else { $curLen++; }
            } else {
                if ($curLen > $bestLen) { $bestStart = $curStart; $bestLen = $curLen; }
                $curStart = -1; $curLen = 0;
            }
        }
        if ($curLen > $bestLen) { $bestStart = $curStart; $bestLen = $curLen; }

        $sb = '';
        if ($bestLen >= 2) {
            for ($i = 0; $i < $bestStart; $i++) {
                if ($i > 0) $sb .= ':';
                $sb .= dechex($g[$i]);
            }
            $sb .= '::';
            $first = true;
            for ($i = $bestStart + $bestLen; $i < 8; $i++) {
                if (!$first) $sb .= ':';
                $sb .= dechex($g[$i]);
                $first = false;
            }
        } else {
            for ($i = 0; $i < 8; $i++) {
                if ($i > 0) $sb .= ':';
                $sb .= dechex($g[$i]);
            }
        }
        return $sb . '/' . $n;
    }

    private function isV4MappedBytes(string $bytes): bool
    {
        return strlen($bytes) === 16
            && ord($bytes[10]) === 0xFF && ord($bytes[11]) === 0xFF
            && $bytes[0] === "\0" && $bytes[1] === "\0" && $bytes[2] === "\0" && $bytes[3] === "\0"
            && $bytes[4] === "\0" && $bytes[5] === "\0" && $bytes[6] === "\0" && $bytes[7] === "\0"
            && $bytes[8] === "\0" && $bytes[9] === "\0";
    }

    private function v4FromMappedBytes(string $bytes): int
    {
        return ((ord($bytes[12]) & 0xFF) << 24) | ((ord($bytes[13]) & 0xFF) << 16)
            | ((ord($bytes[14]) & 0xFF) << 8) | (ord($bytes[15]) & 0xFF);
    }

    // ------------------------------------------------------------------
    // 统一字节读取（缓冲 / 流式共用）
    // ------------------------------------------------------------------

    /**
     * Resolve PHP memory_limit (e.g. "128M", "2G", "-1") to bytes.
     * Returns 0 when unlimited (-1) so the caller falls back to buffering.
     */
    private function parseMemoryLimitBytes(): int
    {
        $raw = trim((string)ini_get('memory_limit'));
        if ($raw === '' || $raw === '-1') {
            return 0;
        }
        $unit = strtolower($raw[strlen($raw) - 1]);
        $num = (int)$raw;
        switch ($unit) {
            case 'g': $num *= 1024 * 1024 * 1024; break;
            case 'm': $num *= 1024 * 1024; break;
            case 'k': $num *= 1024; break;
        }
        return $num;
    }

    private function readBytes($off, $len)
    {
        if ($len <= 0) return '';
        if ($this->stream !== null) {
            if ($off < 0) return '';
            if (@fseek($this->stream, $off, SEEK_SET) !== 0) return '';
            $b = @fread($this->stream, $len);
            return ($b === false) ? '' : $b;
        }
        if ($this->data === null || $off < 0) return '';
        $avail = strlen($this->data) - $off;
        if ($avail <= 0) return '';
        if ($len > $avail) {
            $len = $avail;
        }
        return substr($this->data, $off, $len);
    }

    private function readByte($off)
    {
        $b = $this->readBytes($off, 1);
        return $b === '' ? 0 : ord($b);
    }

    /** 流式安全的小端 U16 读取（缓冲模式零分配快路径）。 */
    private function safeReadU16($off)
    {
        if ($this->data !== null) {
            if ($off < 0 || $off + 2 > strlen($this->data)) {
                throw new QzdbException('Out of bounds reading U16 at offset ' . $off, self::ERROR_OUT_OF_BOUNDS);
            }
            return unpack('v', $this->data, $off)[1];
        }
        $b = $this->readBytes($off, 2);
        if (strlen($b) < 2) {
            throw new QzdbException('Out of bounds reading U16 at offset ' . $off, self::ERROR_OUT_OF_BOUNDS);
        }
        return unpack('v', $b)[1];
    }

    private function safeReadU32($off)
    {
        if ($this->data !== null) {
            if ($off < 0 || $off + 4 > strlen($this->data)) {
                throw new QzdbException('Out of bounds reading U32 at offset ' . $off, self::ERROR_OUT_OF_BOUNDS);
            }
            return unpack('V', $this->data, $off)[1];
        }
        $b = $this->readBytes($off, 4);
        if (strlen($b) < 4) {
            throw new QzdbException('Out of bounds reading U32 at offset ' . $off, self::ERROR_OUT_OF_BOUNDS);
        }
        return unpack('V', $b)[1];
    }

    private function safeReadU64($off)
    {
        if ($this->data !== null) {
            if ($off < 0 || $off + 8 > strlen($this->data)) {
                throw new QzdbException('Out of bounds reading U64 at offset ' . $off, self::ERROR_OUT_OF_BOUNDS);
            }
            return unpack('P', $this->data, $off)[1];
        }
        $b = $this->readBytes($off, 8);
        if (strlen($b) < 8) {
            throw new QzdbException('Out of bounds reading U64 at offset ' . $off, self::ERROR_OUT_OF_BOUNDS);
        }
        return unpack('P', $b)[1];
    }

    private function safeReadU24($off)
    {
        if ($this->data !== null) {
            if ($off < 0 || $off + 3 > strlen($this->data)) {
                throw new QzdbException('Out of bounds reading U24 at offset ' . $off, self::ERROR_OUT_OF_BOUNDS);
            }
            return ord($this->data[$off]) | (ord($this->data[$off + 1]) << 8) | (ord($this->data[$off + 2]) << 16);
        }
        $b = $this->readBytes($off, 3);
        if (strlen($b) < 3) {
            throw new QzdbException('Out of bounds reading U24 at offset ' . $off, self::ERROR_OUT_OF_BOUNDS);
        }
        return ord($b[0]) | (ord($b[1]) << 8) | (ord($b[2]) << 16);
    }

    private function safeReadU48($off)
    {
        if ($this->data !== null) {
            if ($off < 0 || $off + 6 > strlen($this->data)) {
                throw new QzdbException('Out of bounds reading U48 at offset ' . $off, self::ERROR_OUT_OF_BOUNDS);
            }
            $low = unpack('V', $this->data, $off)[1];
            $high = unpack('v', $this->data, $off + 4)[1];
            return $low + ($high * 4294967296);
        }
        $b = $this->readBytes($off, 6);
        if (strlen($b) < 6) {
            throw new QzdbException('Out of bounds reading U48 at offset ' . $off, self::ERROR_OUT_OF_BOUNDS);
        }
        $low = unpack('V', $b, 0)[1];
        $high = unpack('v', $b, 4)[1];
        return $low + ($high * 4294967296);
    }

    private function safeReadF32($off): float
    {
        if ($this->data !== null) {
            if ($off < 0 || $off + 4 > strlen($this->data)) {
                throw new QzdbException('Out of bounds reading float32 at offset ' . $off, self::ERROR_OUT_OF_BOUNDS);
            }
            return unpack('f', $this->data, $off)[1];
        }
        $b = $this->readBytes($off, 4);
        if (strlen($b) < 4) {
            throw new QzdbException('Out of bounds reading float32 at offset ' . $off, self::ERROR_OUT_OF_BOUNDS);
        }
        return unpack('f', $b)[1];
    }

    private function safeReadF64($off): float
    {
        if ($this->data !== null) {
            if ($off < 0 || $off + 8 > strlen($this->data)) {
                throw new QzdbException('Out of bounds reading float64 at offset ' . $off, self::ERROR_OUT_OF_BOUNDS);
            }
            return unpack('d', $this->data, $off)[1];
        }
        $b = $this->readBytes($off, 8);
        if (strlen($b) < 8) {
            throw new QzdbException('Out of bounds reading float64 at offset ' . $off, self::ERROR_OUT_OF_BOUNDS);
        }
        return unpack('d', $b)[1];
    }

    private function safeReadUintWidth($off, $width)
    {
        if ($width <= 1) {
            return $this->readByte($off);
        } elseif ($width == 2) {
            return $this->safeReadU16($off);
        } elseif ($width == 3) {
            return $this->safeReadU24($off);
        } else {
            return $this->safeReadU32($off);
        }
    }

    // ------------------------------------------------------------------
    // CRC32-B
    // ------------------------------------------------------------------

    private static function crc32bInitTable(): void
    {
        if (self::$crc32bTable !== null) return;
        $table = [];
        for ($i = 0; $i < 256; $i++) {
            $crc = $i;
            for ($j = 0; $j < 8; $j++) {
                $crc = ($crc & 1) ? (0xEDB88320 ^ ($crc >> 1)) : ($crc >> 1);
            }
            $table[$i] = $crc;
        }
        self::$crc32bTable = $table;
    }

    private static function crc32bComputeFile(string $data, $stream = null, int $size = 0): int
    {
        self::crc32bInitTable();
        $table = self::$crc32bTable;
        $crc = 0xFFFFFFFF;

        if ($stream !== null) {
            fseek($stream, 0, SEEK_SET);
            $head = fread($stream, 16);
            for ($i = 0; $i < 16 && $i < strlen($head); $i++) {
                $crc = $table[($crc ^ ord($head[$i])) & 0xFF] ^ ($crc >> 8);
            }
            for ($i = 0; $i < 4; $i++) {
                $crc = $table[$crc & 0xFF] ^ ($crc >> 8);
            }
            fseek($stream, 20, SEEK_SET);
            $remaining = $size - 20;
            while ($remaining > 0) {
                $chunk = fread($stream, min(65536, $remaining));
                if ($chunk === false || $chunk === '') break;
                $clen = strlen($chunk);
                for ($i = 0; $i < $clen; $i++) {
                    $crc = $table[($crc ^ ord($chunk[$i])) & 0xFF] ^ ($crc >> 8);
                }
                $remaining -= $clen;
            }
            return $crc ^ 0xFFFFFFFF;
        }

        $len = strlen($data);
        for ($i = 0; $i < 16; $i++) {
            $crc = $table[($crc ^ ord($data[$i])) & 0xFF] ^ ($crc >> 8);
        }
        $crc = $table[$crc & 0xFF] ^ ($crc >> 8);
        $crc = $table[$crc & 0xFF] ^ ($crc >> 8);
        $crc = $table[$crc & 0xFF] ^ ($crc >> 8);
        $crc = $table[$crc & 0xFF] ^ ($crc >> 8);
        for ($i = 20; $i < $len; $i++) {
            $crc = $table[($crc ^ ord($data[$i])) & 0xFF] ^ ($crc >> 8);
        }
        return $crc ^ 0xFFFFFFFF;
    }

    // ------------------------------------------------------------------
    // IP 解析（严格，拒绝前导零 / 缺段 / 越界 / 双 :: / zone-id）
    // ------------------------------------------------------------------

    private static function initHex(): void
    {
        if (self::$HEX !== null) return;
        self::$HEX = array_fill(0, 128, 0);
        for ($i = 0; $i < 10; $i++) self::$HEX[48 + $i] = $i;
        for ($i = 0; $i < 6; $i++) { self::$HEX[97 + $i] = 10 + $i; self::$HEX[65 + $i] = 10 + $i; }
    }

    private static function fastParseIpv4($s)
    {
        $n = strlen($s);
        if ($n === 0 || $s[$n - 1] === '.') return null;
        $result = 0; $val = 0; $dots = 0; $start = 0;
        for ($i = 0; $i <= $n; $i++) {
            $c = $i < $n ? ord($s[$i]) : 46;
            if ($c === 46) {
                $segLen = $i - $start;
                if ($segLen === 0 || $segLen > 3) return null;
                if ($segLen > 1 && $s[$start] === '0') return null;
                $val = 0;
                for ($j = $start; $j < $i; $j++) {
                    $d = ord($s[$j]);
                    if ($d < 48 || $d > 57) return null;
                    $val = $val * 10 + ($d - 48);
                }
                if ($val > 255) return null;
                $result = ($result << 8) | $val;
                $dots++; $start = $i + 1;
            }
        }
        return $dots === 4 ? $result : null;
    }

    private static function fastParseIp($ip)
    {
        if (!is_string($ip)) return null;
        for ($i = 0, $n = strlen($ip); $i < $n; $i++) {
            $c = $ip[$i];
            if ($c === ' ' || $c === "\t" || $c === "\n" || $c === "\r" || $c === "\v" || $c === "\f") {
                return null;
            }
        }
        if ($n === 0 || $n > 45) return null;
        $s = $ip;
        if (strpos($s, ':') === false) {
            $v4 = self::fastParseIpv4($s);
            return $v4 !== null ? array($v4, null) : null;
        }
        if (strpos($s, '%') !== false) return null;
        $dc = strpos($s, '::');
        if ($dc !== false && strpos($s, '::', $dc + 2) !== false) return null;
        $lft = $dc !== false ? substr($s, 0, $dc) : $s;
        $rgt = $dc !== false ? substr($s, $dc + 2) : '';
        $lg = $lft !== '' ? explode(':', $lft) : array();
        $rg = $rgt !== '' ? explode(':', $rgt) : array();
        if ($lg === array('')) $lg = array();
        if ($rg === array('')) $rg = array();
        foreach ($lg as $g) { if ($g === '') return null; }
        foreach ($rg as $g) { if ($g === '') return null; }
        $allg = array_merge($lg, $rg);
        $hasV4 = false; $v4Int = 0;
        $last = count($allg) - 1;
        if ($last >= 0 && strpos($allg[$last], '.') !== false) {
            $v4Int = self::fastParseIpv4($allg[$last]);
            if ($v4Int === null) return null;
            $hasV4 = true;
            array_pop($allg);
        }
        $ng = count($allg);
        $v4Slots = $hasV4 ? 2 : 0;
        if ($dc !== false) {
            if ($ng + $v4Slots > 7) return null;
        } else {
            if ($ng + $v4Slots !== 8) return null;
        }
        self::initHex();
        foreach ($allg as $g) {
            $gl = strlen($g);
            if ($gl === 0 || $gl > 4) return null;
            for ($j = 0; $j < $gl; $j++) {
                $cc = ord($g[$j]);
                if ($cc >= 128 || (self::$HEX[$cc] === 0 && $cc !== 48)) return null;
            }
        }
        $zeros = 8 - $ng - $v4Slots;
        $buf = str_repeat("\0", 16);
        $off = 0;
        foreach ($lg as $g) {
            $v = 0;
            for ($j = 0; $j < strlen($g); $j++) $v = ($v << 4) | self::$HEX[ord($g[$j])];
            $buf[$off] = chr(($v >> 8) & 0xFF);
            $buf[$off + 1] = chr($v & 0xFF);
            $off += 2;
        }
        $off += $zeros * 2;
        foreach ($rg as $g) {
            $v = 0;
            for ($j = 0; $j < strlen($g); $j++) $v = ($v << 4) | self::$HEX[ord($g[$j])];
            $buf[$off] = chr(($v >> 8) & 0xFF);
            $buf[$off + 1] = chr($v & 0xFF);
            $off += 2;
        }
        if ($hasV4) {
            $buf[12] = chr(($v4Int >> 24) & 0xFF);
            $buf[13] = chr(($v4Int >> 16) & 0xFF);
            $buf[14] = chr(($v4Int >> 8) & 0xFF);
            $buf[15] = chr($v4Int & 0xFF);
        }
        // IPv4-Mapped 自动降级（契约 §8 规则 4）
        if (ord($buf[10]) === 0xFF && ord($buf[11]) === 0xFF
            && $buf[0] === "\0" && $buf[1] === "\0" && $buf[2] === "\0" && $buf[3] === "\0"
            && $buf[4] === "\0" && $buf[5] === "\0" && $buf[6] === "\0" && $buf[7] === "\0"
            && $buf[8] === "\0" && $buf[9] === "\0") {
            return array(((ord($buf[12]) << 24) | (ord($buf[13]) << 16) | (ord($buf[14]) << 8) | ord($buf[15])) & 0xffffffff, null);
        }
        return array(null, $buf);
    }

    /** 仅用于 CIDR：返回 16 字节二进制（非法返回 null；不降级，调用方自行判断 mapped）。 */
    private static function parseIpv6Raw($s)
    {
        $r = self::fastParseIp($s);
        if ($r === null || $r[1] === null) return null;
        return $r[1];
    }

    // 当前正在 CIDR 深度遍历的 V4 整数（walkV4Depth 使用）
    private $curV4 = 0;
}

/* ===========================================================================
 * Builder 模式加载入口（契约 §2）
 * =========================================================================== */
class QzdbBuilder
{
    private $source = 'path';   // 'path' | 'bytes' | 'stream'
    private $path = '';
    private $bytes = '';
    private $stream = null;
    private $groupIndex = 0;
    private $verifyCrc = true;

    public static function path(string $path): self
    {
        $b = new self();
        $b->source = 'path';
        $b->path = $path;
        return $b;
    }

    public static function bytes(string $bytes): self
    {
        $b = new self();
        $b->source = 'bytes';
        $b->bytes = $bytes;
        return $b;
    }

    public static function stream($handle): self
    {
        $b = new self();
        $b->source = 'stream';
        $b->stream = $handle;
        return $b;
    }

    public function groupIndex(int $groupIndex): self
    {
        $this->groupIndex = $groupIndex;
        return $this;
    }

    public function verifyCrc(bool $verifyCrc): self
    {
        $this->verifyCrc = $verifyCrc;
        return $this;
    }

    public function build(): QzdbReader
    {
        $reader = new QzdbReader(null, $this->groupIndex, $this->verifyCrc);
        if ($this->source === 'path') {
            $reader->load($this->path, $this->verifyCrc);
        } elseif ($this->source === 'bytes') {
            $reader->loadBytes($this->bytes, $this->verifyCrc);
        } else {
            $reader->loadStream($this->stream, $this->verifyCrc);
        }
        return $reader;
    }
}

/* ===========================================================================
 * 命名注册表 QzdbRegistry（实例级 + 进程全局级）
 * =========================================================================== */
class QzdbRegistry
{
    private static $GLOBAL = null;

    private $map = [];

    private static function globalInstance(): self
    {
        if (self::$GLOBAL === null) {
            self::$GLOBAL = new self();
        }
        return self::$GLOBAL;
    }

    public function register(string $name, string $path): void
    {
        if ($name === '' || $path === '') {
            throw new QzdbException('Name and path must not be empty', QzdbReader::ERROR_INVALID_PARAM);
        }
        $reader = QzdbBuilder::path($path)->build();
        $old = $this->map[$name] ?? null;
        $this->map[$name] = $reader;
        if ($old !== null) {
            $old->close();
        }
    }

    public function registerBuffer(string $name, string $bytes): void
    {
        if ($name === '' || $bytes === '') {
            throw new QzdbException('Name and buffer must not be empty', QzdbReader::ERROR_INVALID_PARAM);
        }
        $reader = QzdbBuilder::bytes($bytes)->build();
        $old = $this->map[$name] ?? null;
        $this->map[$name] = $reader;
        if ($old !== null) {
            $old->close();
        }
    }

    public function get(string $name): ?QzdbReader
    {
        return $this->map[$name] ?? null;
    }

    public function unregister(string $name): void
    {
        if (isset($this->map[$name])) {
            $this->map[$name]->close();
            unset($this->map[$name]);
        }
    }

    public function clear(): void
    {
        foreach ($this->map as $r) {
            try { $r->close(); } catch (\Throwable $e) {}
        }
        $this->map = [];
    }

    // 进程全局静态 API
    public static function registerGlobal(string $name, string $path): void
    {
        self::globalInstance()->register($name, $path);
    }

    public static function registerGlobalBuffer(string $name, string $bytes): void
    {
        self::globalInstance()->registerBuffer($name, $bytes);
    }

    public static function getGlobal(string $name): ?QzdbReader
    {
        return self::globalInstance()->get($name);
    }

    public static function unregisterGlobal(string $name): void
    {
        self::globalInstance()->unregister($name);
    }

    public static function clearGlobal(): void
    {
        self::globalInstance()->clear();
    }
}

/* ===========================================================================
 * 链式多库查询 ChainedReader（Fallback / Merge / MergeOverride）
 * =========================================================================== */
class ChainedReader
{
    public const MODE_FALLBACK = 0;
    public const MODE_MERGE = 1;
    public const MODE_MERGE_OVERRIDE = 2;

    private $readers;
    private $mode;

    private function __construct(array $readers, int $mode)
    {
        if (empty($readers)) {
            throw new QzdbException('ChainedReader requires at least one QzdbReader', QzdbReader::ERROR_INVALID_PARAM);
        }
        $this->readers = $readers;
        $this->mode = $mode;
    }

    public static function chain(QzdbReader ...$readers): self
    {
        return new self($readers, self::MODE_FALLBACK);
    }

    public static function chainMerge(QzdbReader ...$readers): self
    {
        return new self($readers, self::MODE_MERGE);
    }

    public static function chainMergeOverride(QzdbReader ...$readers): self
    {
        return new self($readers, self::MODE_MERGE_OVERRIDE);
    }

    public function find($ipStr)
    {
        if ($this->mode === self::MODE_FALLBACK) {
            foreach ($this->readers as $reader) {
                try {
                    $res = $reader->find($ipStr);
                    if ($res !== null) return $res;
                } catch (QzdbException $e) {
                    // PHP 不抛 INVALID_IP（返回 null），其它异常透传
                    throw $e;
                }
            }
            return null;
        }
        // MERGE / MERGE_OVERRIDE
        $merged = [];
        foreach ($this->readers as $reader) {
            try {
                $res = $reader->find($ipStr);
            } catch (QzdbException $e) {
                throw $e;
            }
            if ($res === null) continue;
            $fields = $res->getFieldNames();
            $values = $res->toMap();
            foreach ($fields as $f) {
                $v = $values[$f] ?? '';
                if ($this->mode === self::MODE_MERGE) {
                    if (!isset($merged[$f]) || $merged[$f] === '') {
                        $merged[$f] = $v;
                    }
                } else {
                    if ($v !== '' || !isset($merged[$f])) {
                        $merged[$f] = $v;
                    }
                }
            }
        }
        if (empty($merged)) return null;
        return new GeoInfo(array_values($merged), array_keys($merged));
    }

    public function findUint(int $ipInt)
    {
        if ($this->mode === self::MODE_FALLBACK) {
            foreach ($this->readers as $reader) {
                $res = $reader->findUint($ipInt);
                if ($res !== null) return $res;
            }
            return null;
        }
        return $this->find(self::uintToIpv4($ipInt));
    }

    public function findBytes(string $bytes)
    {
        if ($this->mode === self::MODE_FALLBACK) {
            foreach ($this->readers as $reader) {
                $res = $reader->findBytes($bytes);
                if ($res !== null) return $res;
            }
            return null;
        }
        return $this->find(self::bytesToIpString($bytes));
    }

    public function findFields($ipStr, $fields)
    {
        $full = $this->find($ipStr);
        if ($full === null || $fields === null || (is_array($fields) && count($fields) === 0)) {
            return $full;
        }
        $projNames = [];
        $projValues = [];
        foreach ($fields as $f) {
            $projNames[] = $f;
            $projValues[] = $full->get($f);
        }
        return new GeoInfo($projValues, $projNames);
    }

    public function findBatch(array $ips): array
    {
        $out = [];
        foreach ($ips as $ip) {
            try {
                $info = $this->find($ip);
                $out[] = new BatchResult((string)$ip, $info, null);
            } catch (QzdbException $e) {
                $out[] = new BatchResult((string)$ip, null, $e);
            }
        }
        return $out;
    }

    public function findBatchFields(array $ips, $fields): array
    {
        $out = [];
        foreach ($ips as $ip) {
            try {
                $info = $this->findFields($ip, $fields);
                $out[] = new BatchResult((string)$ip, $info, null);
            } catch (QzdbException $e) {
                $out[] = new BatchResult((string)$ip, null, $e);
            }
        }
        return $out;
    }

    public function findStream(iterable $ips): \Generator
    {
        foreach ($ips as $ip) {
            try {
                $info = $this->find($ip);
                yield new BatchResult((string)$ip, $info, null);
            } catch (QzdbException $e) {
                yield new BatchResult((string)$ip, null, $e);
            }
        }
    }

    public function editions(): array
    {
        return array_map(fn($r) => $r->getEdition(), $this->readers);
    }

    public function scopes(): array
    {
        return array_map(fn($r) => $r->getScope(), $this->readers);
    }

    public function dataMonths(): array
    {
        return array_map(fn($r) => $r->getDataMonth(), $this->readers);
    }

    public function readers(): array
    {
        return $this->readers;
    }

    private static function uintToIpv4(int $ipInt): string
    {
        return (($ipInt >> 24) & 0xFF) . '.' . (($ipInt >> 16) & 0xFF) . '.'
            . (($ipInt >> 8) & 0xFF) . '.' . ($ipInt & 0xFF);
    }

    private static function bytesToIpString(string $bytes): string
    {
        $len = strlen($bytes);
        if ($len === 4) return self::uintToIpv4(
            ((ord($bytes[0]) & 0xFF) << 24) | ((ord($bytes[1]) & 0xFF) << 16)
            | ((ord($bytes[2]) & 0xFF) << 8) | (ord($bytes[3]) & 0xFF));
        if ($len === 16) {
            $parts = [];
            for ($i = 0; $i < 8; $i++) {
                $parts[] = dechex(((ord($bytes[2 * $i]) & 0xFF) << 8) | (ord($bytes[2 * $i + 1]) & 0xFF));
            }
            return implode(':', $parts);
        }
        return '';
    }
}
