<?php
/* 本class采用动态读取文件  edited by SharmakaCA


1、使用了单例模式，确保每次只加载一个文件实例，避免重复加载。
2、使用二分查找优化索引定位，效率高。
3、使用了 fopen、fseek 和 fread 来直接读取二进制文件，节约内存。
4、每个方法都有详细的注释，便于维护。
*/


$object = QQzeng3::getInstance();

$info = $object->get($ip);

class QQzeng3
{
    private $fp; // 文件指针
    private $prefStart; // 索引区起始偏移
    private $prefEnd; // 索引区结束偏移

    private static $instance = null; // 单例模式实例

    /**
     * 构造函数
     * @param string $filePath IP 数据文件路径
     * @throws \Exception 如果文件无法打开
     */
    private function __construct($filePath)
    {
        $this->fp = fopen($filePath, 'rb');
        if (!$this->fp) {
            throw new \Exception('Unable to open IP data file.');
        }

        $this->loadIndexRange();
    }

    /**
     * 获取单例实例
     * @param string $filePath IP 数据文件路径
     * @return self 单例实例
     */
    public static function getInstance()
    {
        $filePath = __DIR__ . '/qqzeng-ip-3.0-ultimate.dat';

        if (self::$instance === null) {
            self::$instance = new self($filePath);
        }
        return self::$instance;
    }

    /**
     * 加载索引区的范围
     */
    private function loadIndexRange()
    {
        fseek($this->fp, 4); // 索引区起始偏移在文件第4字节开始
        $this->prefStart = [];
        $this->prefEnd   = [];

        // 加载256个索引段范围
        for ($k = 0; $k < 256; $k++) {
            $this->prefStart[$k] = unpack('V', fread($this->fp, 4))[1];
            $this->prefEnd[$k]   = unpack('V', fread($this->fp, 4))[1];
        }
    }

    /**
     * 查询 IP 对应的地址信息
     * @param string $ip 查询的 IP 地址
     * @return string 地址信息
     */
    public function get($ip)
    {
        $val   = sprintf('%u', ip2long($ip)); // 将 IP 转为无符号整数
        $ipArr = explode('.', $ip);
        $pref  = (int) $ipArr[0]; // 获取 IP 的前缀部分

        $low  = $this->prefStart[$pref];
        $high = $this->prefEnd[$pref];

        // 通过二分查找定位索引
        $cur = ($low === $high) ? $low : $this->binarySearch($low, $high, $val);

        return $this->getAddressByOffset($cur);
    }

    /**
     * 二分查找索引
     * @param int $low 索引区的起始位置
     * @param int $high 索引区的结束位置
     * @param int $target 查询的目标 IP 地址
     * @return int 目标记录的索引位置
     */
    private function binarySearch($low, $high, $target)
    {
        while ($low <= $high) {
            $mid = ($low + $high) >> 1; // 计算中间位置

            fseek($this->fp, 2052 + ($mid * 8)); // 跳转到中间索引位置
            $endIp = unpack('V', fread($this->fp, 4))[1]; // 获取该段的结束 IP

            if ($endIp >= $target) {
                if ($mid === 0 || $this->getEndIp($mid - 1) < $target) {
                    return $mid; // 找到目标索引
                }
                $high = $mid - 1; // 缩小高位范围
            } else {
                $low = $mid + 1; // 缩小低位范围
            }
        }
        return $low; // 默认返回最近的索引
    }

    /**
     * 获取指定索引的结束 IP
     * @param int $index 索引位置
     * @return int 结束 IP 地址
     */
    private function getEndIp($index)
    {
        fseek($this->fp, 2052 + ($index * 8)); // 跳转到指定索引
        return unpack('V', fread($this->fp, 4))[1];
    }

    /**
     * 获取指定偏移的地址信息
     * @param int $index 索引位置
     * @return string 地址信息
     */
    private function getAddressByOffset($index)
    {
        fseek($this->fp, 2052 + ($index * 8) + 4); // 跳转到地址偏移部分

        $offset = unpack('V', fread($this->fp, 3) . "\x0")[1]; // 获取偏移位置
        $length = unpack('C', fread($this->fp, 1))[1]; // 获取地址长度

        fseek($this->fp, $offset); // 跳转到实际地址位置
        return fread($this->fp, $length); // 读取地址内容
    }

    /**
     * 析构函数，关闭文件指针
     */
    public function __destruct()
    {
        if ($this->fp !== null) {
            fclose($this->fp);
        }
    }
}
