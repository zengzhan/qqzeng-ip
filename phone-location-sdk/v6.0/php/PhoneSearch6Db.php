<?php
 namespace mobile;
class PhoneSearch6Db {
    private const HeaderSize = 32;                // 8个uint的头部
    private const PrefixCount = 200;              // 电话号码前缀总数（0-199）
    private const BitmapPopCountOffset = 0x4E2;   // 位图统计信息偏移量

    private static $instance = null;
    private $data = '';
    private $regionIsps = [];
    private $index = [];

    private function __construct() {
        $this->loadDatabase();
    }

    public static function getInstance() {
        if (self::$instance === null) {
            self::$instance = new self();
        }
        return self::$instance;
    }

    private function loadDatabase() {
        $filePath = __DIR__ . '/qqzeng-phone-6.0.db';
        if (!file_exists($filePath)) {
            throw new Exception("Database file not found");
        }

        $this->data = file_get_contents($filePath);
        $offset = 0;

        // 解析头部（小端序）
        $header = [];
        for ($i = 0; $i < 8; $i++) {
            $header[] = unpack('V', substr($this->data, $offset, 4))[1];
            $offset += 4;
        }

        // 解析地区与运营商表
        $regionsStart = self::HeaderSize;
        $ispsStart = $regionsStart + $header[1];
        $indexStart = $ispsStart + $header[2];

        $regions = explode('&', substr($this->data, $regionsStart, $header[1]));
        $isps = explode('&', substr($this->data, $ispsStart, $header[2]));

        // 构建地区-运营商组合
        $entryOffset = $header[3];
        $this->regionIsps = [];
        for ($i = 0; $i < $header[4]; $i++) {
            $entry = unpack('v', substr($this->data, $entryOffset + $i * 2, 2))[1];
            $regionIndex = $entry >> 5;
            $ispIndex = $entry & 0x1F;
            $this->regionIsps[] = $regions[$regionIndex] . '|' . $isps[$ispIndex];
        }

        // 构建前缀索引表
        $offset = $indexStart;
        $this->index = array_fill(0, self::PrefixCount, ['bitmapOffset' => 0, 'dataOffset' => 0]);
        for ($i = 0; $i < self::PrefixCount; $i++) {
            $prefix = unpack('V', substr($this->data, $offset, 4))[1];
            if ($prefix === $i) {
                $bitmapOffset = unpack('V', substr($this->data, $offset + 4, 4))[1];
                $dataOffset = unpack('V', substr($this->data, $offset + 8, 4))[1];
                $this->index[$i] = [
                    'bitmapOffset' => $bitmapOffset,
                    'dataOffset' => $dataOffset
                ];
                $offset += 12;
            } else {
                $this->index[$i] = ['bitmapOffset' => 0, 'dataOffset' => 0];
            }
        }
    }

    public function query($phone) {
        if (strlen($phone) > 11 || !ctype_digit($phone)) {
            throw new InvalidArgumentException("Invalid phone number format");
        }

        $prefix = intval(substr($phone, 0, 3));
        $subNum = intval(substr($phone, 3, 4));

        if ($prefix < 0 || $prefix >= self::PrefixCount) {
            return null;
        }

        $indexEntry = $this->index[$prefix];
        if ($indexEntry['bitmapOffset'] === 0 || $indexEntry['dataOffset'] === 0) {
            return null;
        }

        $byteIndex = $subNum >> 3;
        $bitIndex = $subNum & 0b0111;

        if ($indexEntry['bitmapOffset'] + $byteIndex >= strlen($this->data)) {
            return null;
        }

        $bitmap = ord($this->data[$indexEntry['bitmapOffset'] + $byteIndex]);
        if (($bitmap & (1 << $bitIndex)) === 0) {
            return null;
        }

        $popCountOffset = $indexEntry['bitmapOffset'] + self::BitmapPopCountOffset + ($byteIndex << 1);
        $preCount = unpack('v', substr($this->data, $popCountOffset, 2))[1];

        $localCount = $this->countBits($bitmap & ((1 << $bitIndex) - 1));

        $dataPos = $indexEntry['dataOffset'] + (($preCount + $localCount) << 1);
        $entry = unpack('v', substr($this->data, $dataPos, 2))[1];

        return $this->regionIsps[$entry] ?? null;
    }

    private function countBits($num) {
        $count = 0;
        while ($num > 0) {
            $count += $num & 1;
            $num >>= 1;
        }
        return $count;
    }
}
?>