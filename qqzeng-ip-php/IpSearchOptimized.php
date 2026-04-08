<?php

/**
 * IpSearchOptimized - 最终修正提速版
 */
class IpSearchOptimized {
    private $data; 
    private $prefix_array = [];
    private $startIps;
    private $endIps;
    private $offsets;
    private $lengths;

    private static $cache = [];
    private static $limit = 1024;

    public function __construct($database) {
        if (!file_exists($database)) return;
        $this->data = file_get_contents($database);
        if (!$this->data) return;

        $header = unpack('V4', substr($this->data, 0, 16));
        $firstOffset = $header[1];
        $lastOffset  = $header[2];
        $prefStart   = $header[3];
        $prefEnd     = $header[4];
        
        $count = intval(($lastOffset - $firstOffset) / 12) + 1;
        $prefCount = intval(($prefEnd - $prefStart) / 9) + 1;
        for ($k = 0; $k < $prefCount; $k++) {
            $off = $prefStart + ($k * 9);
            $this->prefix_array[ord($this->data[$off])] = [
                's' => unpack('V', substr($this->data, $off + 1, 4))[1],
                'e' => unpack('V', substr($this->data, $off + 5, 4))[1]
            ];
        }

        $this->startIps = new SplFixedArray($count);
        $this->endIps   = new SplFixedArray($count);
        $this->offsets  = new SplFixedArray($count);
        $this->lengths  = new SplFixedArray($count);

        $indexChunk = substr($this->data, $firstOffset, $count * 12);
        for ($i = 0; $i < $count; $i++) {
            $base = $i * 12;
            $row = unpack('V2s/V1i', substr($indexChunk, $base, 12));
            $this->startIps[$i] = $row['s1'];
            $this->endIps[$i]   = $row['s2'];
            $this->offsets[$i]  = $row['i'] & 0xFFFFFF;
            $this->lengths[$i]  = $row['i'] >> 24;
        }
    }

    public function get($ip) {
        if (empty($ip)) return "";
        if (isset(self::$cache[$ip])) return self::$cache[$ip];

        $parts = explode('.', $ip);
        if (count($parts) !== 4) return "";
        
        // 确保无符号整型转换
        $ipNum = (int)$parts[0] * 16777216 + ($parts[1] << 16) + ($parts[2] << 8) + (int)$parts[3];

        $prefix = (int)$parts[0];
        if (!isset($this->prefix_array[$prefix])) return "";

        $idx = $this->prefix_array[$prefix];
        $low = $idx['s'];
        $high = $idx['e'];

        $left = -1;
        while ($low <= $high) {
            $mid = ($low + $high) >> 1;
            if ($this->endIps[$mid] >= $ipNum) {
                $left = $mid;
                if ($mid == 0) break;
                $high = $mid - 1;
            } else {
                $low = $mid + 1;
            }
        }

        if ($left !== -1 && $ipNum >= $this->startIps[$left]) {
            $res = substr($this->data, $this->offsets[$left], $this->lengths[$left]);
            if (count(self::$cache) > self::$limit) self::$cache = [];
            return self::$cache[$ip] = $res;
        }
        
        return self::$cache[$ip] = "";
    }
}

/* 
|--------------------------------------------------------------------------
| 调用示例 (Usage Example)
|--------------------------------------------------------------------------
| 
| $dbPath = 'qqzeng-ip-china-utf8.dat';
| $reader = new IpSearchOptimized($dbPath);
| 
| $ip = '103.35.27.255';
| $result = $reader->get($ip);
| 
| echo $result;
| // 输出样例：亚洲|中国|重庆|重庆||新网|500100|China|CN|106.5050|29.5332
|
*/
