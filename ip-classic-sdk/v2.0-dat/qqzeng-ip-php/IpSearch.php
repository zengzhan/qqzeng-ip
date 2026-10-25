<?php

/**
 * qqzeng-ip 2.0 (.dat) 高性能解析 - PHP 版 (PHP 7.4 ~ 8.5+)
 *
 * 文件结构 (UTF-8, Little-Endian):
 *   文件头  16 字节 (4-4-4-4): [索引区首条偏移][索引区末条偏移][前缀区首偏移][前缀区末偏移]
 *   内容区  唯一不重复的地区字符串,按 (offset,len) 被索引区引用
 *   索引区  12 字节/条 (4-4-3-1): [起始IP][结束IP][地区偏移24b][长度8b]
 *   前缀区   9 字节/条 (1-4-4): [前缀0-255][索引区start][索引区end]
 *
 * 用法:
 *   $reader = new IpSearch('qqzeng-ip.dat');      // 指定路径(可多实例)
 *   $reader = new IpSearch();                     // 自动探测默认路径
 *   $reader = IpSearch::fromBytes($bytes);        // 从内存字节流加载
 *   $str  = $reader->get('1.2.3.4');              // 兼容旧版方法名
 *   $str  = $reader->find('1.2.3.4');             // 多语言统一别名
 *   $str  = $reader->findUint(0x01020304);        // 整数查询
 * 未命中/非法 IP 一律返回 ''。
 *
 * 相比历史版本的修复与优化:
 *   1. 逐查询 fseek/fread 文件 IO 改为一次性解析进内存 packed 数组。
 *   2. ip2long(接受 "1.2.3" 等残缺 IP)改为严格 fastParseIp 校验。
 *   3. 前缀关联数组改为固定 256 长度数组,缺失前缀以 -1 标记
 *      (修复 0.0.0.0/8 前缀 low=high=0 被判未命中的 bug)。
 *   4. 字符串池去重常驻;分块 unpack 控制加载内存峰值。
 *   5. 加载时完整校验文件结构,非法文件抛 RuntimeException(fail-closed)。
 */
class IpSearch
{
    /** @var array<int,int> 每条记录起始 IP */
    private $starts = [];

    /** @var array<int,int> 每条记录结束 IP */
    private $ends = [];

    /** @var array<int,int> 记录 -> 字符串下标 */
    private $strIdx = [];

    /** @var array<int,string> 去重后的地区字符串 */
    private $strings = [];

    /** @var array<int,int> 前缀 -> start 索引,-1 表示前缀缺失 */
    private $prefLo;

    /** @var array<int,int> 前缀 -> end 索引,-1 表示前缀缺失 */
    private $prefHi;

    /** 兼容旧版: new IpSearch('qqzeng-ip.dat');缺省路径时自动探测。 */
    public function __construct($database = null)
    {
        if ($database === null || $database === '') {
            $database = self::findDbPath();
            if ($database === null) {
                throw new RuntimeException('IpSearch: no loadable .dat found');
            }
        }
        $data = file_get_contents($database);
        if ($data === false) {
            throw new RuntimeException("IpSearch: cannot read $database");
        }
        $this->load($data);
    }

    /** 新增: 从内存字节流加载。 */
    public static function fromBytes($bytes)
    {
        $reader = new \ReflectionClass(__CLASS__)->newInstanceWithoutConstructor();
        $reader->load($bytes);
        return $reader;
    }

    public static function findDbPath()
    {
        $names = ['qqzeng-ip-utf8.dat', 'qqzeng-ip.dat', 'qqzeng-ip-china-utf8.dat', 'qqzeng-ip-global-utf8.dat'];
        $dirs = [
            '.', 'dada/国内精华版-202610', 'dada/全球旗舰版-202610',
            '../dada/国内精华版-202610', '../dada/全球旗舰版-202610',
            '../../dada/国内精华版-202610', '../../dada/全球旗舰版-202610',
        ];
        foreach ($dirs as $d) {
            foreach ($names as $n) {
                $p = $d === '.' ? $n : "$d/$n";
                if (is_file($p)) {
                    return $p;
                }
            }
        }
        return null;
    }

    private function load($data)
    {
        $len = strlen($data);
        if ($len < 16) {
            throw new RuntimeException('database too small');
        }
        self::ensureMemory($len);
        $this->prefLo = array_fill(0, 256, -1);
        $this->prefHi = array_fill(0, 256, -1);

        $firstIndex = self::le32($data, 0);
        $lastIndex  = self::le32($data, 4);
        $prefStart  = self::le32($data, 8);
        $prefEnd    = self::le32($data, 12);

        if ($firstIndex < 16 || $lastIndex < $firstIndex || ($lastIndex - $firstIndex) % 12 !== 0
            || $prefEnd < $prefStart || $prefStart < 16 || ($prefEnd - $prefStart) % 9 !== 0) {
            throw new RuntimeException('bad header');
        }

        $recordCount = intdiv($lastIndex - $firstIndex, 12) + 1;
        $prefixCount = intdiv($prefEnd - $prefStart, 9) + 1;
        if ($firstIndex + 12 * $recordCount > $len || $prefStart + 9 * $prefixCount > $len) {
            throw new RuntimeException('header exceeds file size');
        }

        for ($k = 0; $k < $prefixCount; $k++) {
            $o = $prefStart + $k * 9;
            $n = ord($data[$o]);
            $lo = self::le32($data, $o + 1);
            $hi = self::le32($data, $o + 5);
            if ($lo > $hi || $hi >= $recordCount) {
                throw new RuntimeException('bad prefix range');
            }
            $this->prefLo[$n] = $lo;
            $this->prefHi[$n] = $hi;
        }

        $this->starts = [];
        $this->ends = [];
        $this->strIdx = [];

        /* 字符串池去重: key = offset*256 + len */
        $seen = [];
        $strings = [];
        $chunk = 65536; /* 每块 65536 条,unpack 峰值内存可控 */
        for ($base = 0; $base < $recordCount; $base += $chunk) {
            $n = min($chunk, $recordCount - $base);
            $region = substr($data, $firstIndex + $base * 12, $n * 12);
            $triples = unpack('V*', $region);
            /* 每条记录 3 个 u32: start / end / (offset24|len<<24) */
            for ($i = 1; $i <= $n * 3; $i += 3) {
                $this->starts[] = $triples[$i];
                $this->ends[]   = $triples[$i + 1];
                $packed = $triples[$i + 2];
                $off = $packed & 0xFFFFFF;
                $slen = $packed >> 24;
                if ($off + $slen > $len) {
                    throw new RuntimeException('string region out of range');
                }
                $key = $off * 256 + $slen;
                if (isset($seen[$key])) {
                    $this->strIdx[] = $seen[$key];
                } else {
                    $id = count($strings);
                    $strings[] = substr($data, $off, $slen);
                    $seen[$key] = $id;
                    $this->strIdx[] = $id;
                }
            }
            unset($triples, $region);
        }
        $this->strings = $strings;
    }

    private static function le32($b, $o)
    {
        return ord($b[$o]) | (ord($b[$o + 1]) << 8) | (ord($b[$o + 2]) << 16) | (ord($b[$o + 3]) << 24);
    }

    /**
     * packed 数组与字符串池的常驻内存约为文件大小的 4~5 倍,
     * 加载前按需提升 memory_limit(-1 表示不限,保持原样)。
     */
    private static function ensureMemory($dataLen)
    {
        $limit = ini_get('memory_limit');
        if ($limit === false || $limit === '-1' || $limit === '') {
            return;
        }
        $limit = trim($limit);
        $val = (int)$limit;
        $mult = 1;
        $last = strtolower(substr($limit, -1));
        if ($last === 'k') $mult = 1024;
        elseif ($last === 'm') $mult = 1048576;
        elseif ($last === 'g') $mult = 1073741824;
        $cur = $val * $mult;
        $need = $dataLen * 6 + 134217728; /* 6 倍文件 + 128MB 余量 */
        if ($cur < $need) {
            ini_set('memory_limit', (string)(int)ceil($need / 1048576) . 'M');
        }
    }

    /**
     * 严格 IPv4 解析: 每段 1~3 位数字且 <=255,恰好 4 段。
     * 非法返回 -1(>=0 为合法 uint32)。
     */
    public static function fastParseIp($ip)
    {
        if (!is_string($ip)) {
            return -1;
        }
        $val = 0;
        $result = 0;
        $shift = 24;
        $digits = 0;
        $n = strlen($ip);
        for ($i = 0; $i < $n; $i++) {
            $c = ord($ip[$i]);
            if ($c >= 48 && $c <= 57) {           /* '0'-'9' */
                $val = $val * 10 + ($c - 48);
                if ($val > 255) {
                    return -1;
                }
                $digits++;
                if ($digits > 3) {
                    return -1; /* 拒绝 "0008.8.8.8" 这类超长前导零 */
                }
            } elseif ($c === 46) {                /* '.' */
                if ($digits === 0 || $shift === 0) {
                    return -1; /* 空段或第 5 段 */
                }
                $result |= $val << $shift;
                $val = 0;
                $digits = 0;
                $shift -= 8;
            } else {
                return -1;
            }
        }
        if ($digits === 0 || $shift !== 0) {
            return -1;
        }
        return $result | $val;
    }

    /** 兼容旧版方法名: 未命中/非法返回 ''。 */
    public function get($ip_address)
    {
        return $this->find($ip_address);
    }

    /** 多语言统一别名。 */
    public function find($ip)
    {
        $v = self::fastParseIp($ip);
        if ($v < 0) {
            return '';
        }
        return $this->findUint($v);
    }

    /** 整数查询(跳过字符串解析,高性能场景)。 */
    public function findUint($val)
    {
        $p = ($val >> 24) & 0xFF;
        $lo = $this->prefLo[$p];
        if ($lo < 0) {
            return ''; /* 前缀不存在(不能以 high==0 判未命中) */
        }
        $hi = $this->prefHi[$p];
        if ($lo === $hi) {
            $idx = $lo;
        } else {
            /* 二分逼近: 最小的 endip >= val */
            $l = $lo;
            $h = $hi;
            $idx = 0;
            while ($l <= $h) {
                $mid = $l + (($h - $l) >> 1);
                if ($this->ends[$mid] >= $val) {
                    $idx = $mid;
                    if ($mid === 0) {
                        break;
                    }
                    $h = $mid - 1;
                } else {
                    $l = $mid + 1;
                }
            }
        }
        if ($this->starts[$idx] <= $val && $this->ends[$idx] >= $val) {
            return $this->strings[$this->strIdx[$idx]];
        }
        return '';
    }

    /** 记录数(调试辅助)。 */
    public function recordCount()
    {
        return count($this->starts);
    }

    /**
     * 迭代全部区间记录: yield [startIp(uint32), endIp(uint32), regionString]。
     * 供 Redis 导入等批量场景使用。
     */
    public function records()
    {
        $n = count($this->starts);
        for ($i = 0; $i < $n; $i++) {
            yield $this->starts[$i] => [$this->ends[$i], $this->strings[$this->strIdx[$i]]];
        }
    }
}
