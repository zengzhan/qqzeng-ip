<?php

/**
 * qqzeng-ip 6.0 (.db) 高性能解析 - PHP 版 (PHP 7.2+, 零依赖, strict_types)
 *
 * 文件结构 (UTF-8):
 *   文件头    4 字节: 节点数量 nodeCount (uint32 小端)
 *   一级索引  65536 条 x 3 字节 (大端 int24): IPv4 前 16 位 -> 节点号或叶子
 *   节点区    nodeCount x 6 字节 (每节点 2 个子指针, 各 3 字节大端 int24)
 *   字符串区  其余部分, '\t' 分隔的 UTF-8 结果串
 *   int24 最高位 (0x800000) 置位 = 叶子, 低 22 位为字符串下标;
 *   否则为节点区跳转下标。查询消耗 IP 后 16 位 (MSB first)。
 *
 * 兼容旧版 API (IpDbSearch 与 IpDbSearchOptimized 已合并):
 *   IpDbSearch::getInstance()->find('1.2.3.4')
 * 新增:
 *   IpDbSearch::fromPath($path) / IpDbSearch::fromBytes($bytes)
 *   findUint(int $ipInt) 整数查询
 *
 * 相比历史版本的修复与优化:
 *   1. fastParseIp 严格化: 旧版 ip2long 接受 "1.2.3"/"1.2" 等残缺 IP
 *      (C 层实现, 残缺形式静默补零); 现按统一严格规则校验。
 *   2. 加载期完整校验 (fail-closed): 尺寸/头字段/全部一级索引与节点跳转
 *      合法性/叶子下标范围, 非法文件抛 RuntimeException;
 *      查询路径带 16 跳上限, 杜绝非法文件导致的死循环。
 *   3. 合并 Optimized 版设计: 索引/节点预解析进 SplFixedArray,
 *      字符串区用 "大字符串 + 偏移量" 索引 (不 explode 成数组),
 *      内存比逐串拆分降低约 70%, 查询零拼装。
 *   4. 分块 unpack 控制加载内存峰值; 路径探测补充仓库 dada/ 目录。
 */

declare(strict_types=1);

namespace Qqzeng\Ip;

use RuntimeException;

class IpDbSearch
{
    private static $instance = null;

    /** @var \SplFixedArray<int> 一级索引 (预解析 int24 值) */
    private $first;

    /** @var \SplFixedArray<int> nodeCount*2 个子指针 (预解析) */
    private $nodes;

    /** @var string 字符串区原始字节 */
    private $stringData;

    /** @var \SplFixedArray<int> 字符串起始偏移 (stringCount+1 项, 末项为哨兵) */
    private $stringOffsets;

    /** @var int */
    private $stringCount = 0;

    /** @var int */
    private $nodeCount = 0;

    const INDEX_START_INDEX = 0x30004;
    const END_MASK = 0x800000;
    const COMPL_MASK = 0x7FFFFF;
    const DB_FILE_NAME = 'qqzeng-ip-6.0-global.db';

    private function __construct(?string $dbPath = null)
    {
        if ($dbPath === null) {
            $dbPath = self::findDbPath();
            if ($dbPath === null) {
                throw new RuntimeException('Fatal: Cannot find ' . self::DB_FILE_NAME);
            }
        }
        $data = @file_get_contents($dbPath);
        if ($data === false) {
            throw new RuntimeException('Failed to read database file: ' . $dbPath);
        }
        $this->load($data);
    }

    /** 单例 (自动探测默认路径)。 */
    public static function getInstance(): IpDbSearch
    {
        if (self::$instance === null) {
            self::$instance = new self();
        }
        return self::$instance;
    }

    /** 指定路径构造 (多实例)。 */
    public static function fromPath(string $dbPath): IpDbSearch
    {
        return new self($dbPath);
    }

    /** 从内存字节流构造。 */
    public static function fromBytes(string $bytes): IpDbSearch
    {
        $obj = (new \ReflectionClass(__CLASS__))->newInstanceWithoutConstructor();
        $obj->load($bytes);
        return $obj;
    }

    /** 探测默认数据库路径 (脚本目录/当前目录/data/dada)。 */
    public static function findDbPath(): ?string
    {
        $name = self::DB_FILE_NAME;
        $dirs = [
            __DIR__,
            getcwd(),
            __DIR__ . '/data',
            __DIR__ . '/../data',
            __DIR__ . '/../../data',
            getcwd() . '/data',
            getcwd() . '/../data',
            getcwd() . '/dada/全球旗舰版-202610',
            getcwd() . '/../dada/全球旗舰版-202610',
            getcwd() . '/../../dada/全球旗舰版-202610',
            getcwd() . '/../../../dada/全球旗舰版-202610',
        ];
        foreach ($dirs as $d) {
            $p = $d . '/' . $name;
            if (is_file($p)) {
                return $p;
            }
        }
        return null;
    }

    private function load(string $data)
    {
        $dataLen = strlen($data);
        if ($dataLen < self::INDEX_START_INDEX) {
            throw new RuntimeException('Invalid database file size');
        }
        self::ensureMemory($dataLen);

        $nodeCount = ord($data[0]) | (ord($data[1]) << 8)
            | (ord($data[2]) << 16) | (ord($data[3]) << 24);
        if ($nodeCount <= 0) {
            throw new RuntimeException('Invalid node count');
        }
        $sa = self::INDEX_START_INDEX + $nodeCount * 6;
        if ($sa > $dataLen) {
            throw new RuntimeException('Node area exceeds file size');
        }

        // 字符串区: 保留原始字节 + 偏移索引 (内存友好)
        // offsets 按 [start0, end0, start1, end1, ...] 成对存放, end 不含分隔符
        $this->stringData = substr($data, $sa);
        $tabCount = substr_count($this->stringData, "\t");
        $offsets = new \SplFixedArray(($tabCount + 1) * 2);
        $offsets[0] = 0;
        $idx = 1;
        $pos = 0;
        while (($pos = strpos($this->stringData, "\t", $pos)) !== false) {
            $offsets[$idx++] = $pos;        // 前一串结束(不含)
            $offsets[$idx++] = $pos + 1;    // 下一串起点
            $pos++;
        }
        $offsets[$idx] = strlen($this->stringData); // 末串结束
        $this->stringOffsets = $offsets;
        $this->stringCount = $tabCount + 1;
        $stringCount = $this->stringCount;

        $entryOK = function (int $v) use ($nodeCount, $stringCount): bool {
            if (($v & self::END_MASK) !== 0) {
                return ($v & self::COMPL_MASK) < $stringCount;
            }
            return $v >= 0 && $v < $nodeCount;
        };

        // 一级索引预解析 + 校验
        $first = new \SplFixedArray(65536);
        for ($i = 0; $i < 65536; $i++) {
            $o = 4 + $i * 3;
            $v = (ord($data[$o]) << 16) | (ord($data[$o + 1]) << 8) | ord($data[$o + 2]);
            if (!$entryOK($v)) {
                throw new RuntimeException('Bad first-level index entry');
            }
            $first[$i] = $v;
        }
        $this->first = $first;

        // 节点区预解析 + 跳转合法性校验 (fail-closed), 分块 unpack 控制峰值
        $nodes = new \SplFixedArray($nodeCount * 2);
        $childCount = $nodeCount * 2;
        $chunkChildren = 65536;
        for ($base = 0; $base < $childCount; $base += $chunkChildren) {
            $n = min($chunkChildren, $childCount - $base);
            $u = unpack('C*', substr($data, self::INDEX_START_INDEX + $base * 3, $n * 3));
            for ($j = 1, $k = $base; $j <= $n * 3; $j += 3, $k++) {
                $v = ($u[$j] << 16) | ($u[$j + 1] << 8) | $u[$j + 2];
                if (!$entryOK($v)) {
                    throw new RuntimeException('Bad node jump');
                }
                $nodes[$k] = $v;
            }
            unset($u);
        }
        $this->nodes = $nodes;
        $this->nodeCount = $nodeCount;
    }

    /**
     * 常驻内存约为文件大小的 3~4 倍, 加载前按需提升 memory_limit。
     */
    private static function ensureMemory(int $dataLen)
    {
        $limit = ini_get('memory_limit');
        if ($limit === false || $limit === '-1' || $limit === '') {
            return;
        }
        $val = (int) $limit;
        $mult = 1;
        $last = strtolower(substr($limit, -1));
        if ($last === 'g') {
            $mult = 1073741824;
        } elseif ($last === 'm') {
            $mult = 1048576;
        } elseif ($last === 'k') {
            $mult = 1024;
        }
        $need = $dataLen * 4 + 134217728; /* 4 倍文件 + 128MB 余量 */
        if ($val * $mult < $need) {
            ini_set('memory_limit', (string) (int) ceil($need / 1048576) . 'M');
        }
    }

    /** 查询 IP 字符串, 未命中/非法返回 ''。 */
    public function find($ip): string
    {
        $v = self::fastParseIp($ip);
        if ($v < 0) {
            return '';
        }
        return $this->findUint($v);
    }

    /** 整数查询 (uint32 语义)。 */
    public function findUint(int $ipInt): string
    {
        $ipInt &= 0xFFFFFFFF;
        $record = $this->first[$ipInt >> 16];
        $suffix = $ipInt & 0xFFFF;
        $nodes = $this->nodes;
        $hops = 0;
        // 后缀 16 位 -> 最多 16 跳; 超限说明文件非法 (fail-closed)
        while (($record & self::END_MASK) === 0) {
            if ($hops >= 16) {
                return '';
            }
            $hops++;
            $record = $nodes[$record * 2 + (($suffix >> 15) & 1)];
            $suffix = ($suffix << 1) & 0xFFFF;
        }
        $index = $record & self::COMPL_MASK;
        if ($index < $this->stringCount) {
            $k = $index << 1;
            return substr($this->stringData, $this->stringOffsets[$k],
                $this->stringOffsets[$k + 1] - $this->stringOffsets[$k]);
        }
        return '';
    }

    public function nodeCount(): int
    {
        return $this->nodeCount;
    }

    public function stringCount(): int
    {
        return $this->stringCount;
    }

    /**
     * 严格 IPv4 解析: 每段 1~3 位纯数字且 <=255, 恰好 4 段。非法返回 -1。
     *
     * 快路径 filter_var (C 层) 只放行无前导零的规范点分十进制, 该形式下
     * ip2long 语义唯一 (十进制, 无八进制歧义); 其余写法 (含 '01.2.3.4'
     * 这类统一规范接受的带前导零形式) 走 explode + ctype_digit 严格路径,
     * 与其他语言逐字符实现语义完全一致 (每段 <= 3 位)。
     */
    public static function fastParseIp($ip): int
    {
        if (!is_string($ip)) {
            return -1;
        }
        if (filter_var($ip, FILTER_VALIDATE_IP, FILTER_FLAG_IPV4) !== false) {
            return (int) ip2long($ip);
        }
        $parts = explode('.', $ip);
        if (count($parts) !== 4) {
            return -1;
        }
        $val = 0;
        foreach ($parts as $p) {
            if ($p === '' || !ctype_digit($p) || strlen($p) > 3) {
                return -1;
            }
            $n = (int) $p;
            if ($n > 255) {
                return -1;
            }
            $val = ($val << 8) | $n;
        }
        return $val;
    }
}
