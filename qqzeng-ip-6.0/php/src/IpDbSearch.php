<?php

namespace Qqzeng\Ip;

use Exception;

class IpDbSearch
{
    private static $instance = null;
    
    private $data;
    private $geoispArr;
    
    const INDEX_START_INDEX = 0x30004;
    const END_MASK = 0x800000;
    const COMPL_MASK = ~self::END_MASK;
    const DB_FILE_NAME = 'qqzeng-ip-6.0-global.db';

    private function __construct()
    {
        $this->loadDb();
    }

    public static function getInstance()
    {
        if (self::$instance === null) {
            self::$instance = new self();
        }
        return self::$instance;
    }

    private function loadDb()
    {
        $dbPath = $this->findDbPath();
        if (!$dbPath) {
            throw new Exception("Fatal: Cannot find " . self::DB_FILE_NAME);
        }

        $this->data = file_get_contents($dbPath);
        if ($this->data === false) {
            throw new Exception("Failed to read database file");
        }

        if (strlen($this->data) < self::INDEX_START_INDEX) {
            throw new Exception("Invalid database file size");
        }

        // 节点数量 (小端序)
        // unpack 'V' 是 unsigned long (32bit, little endian)
        $nodeCount = unpack('V', substr($this->data, 0, 4))[1];
        
        $stringAreaOffset = self::INDEX_START_INDEX + $nodeCount * 6;
        
        if ($stringAreaOffset > strlen($this->data)) {
            throw new Exception("Invalid metadata");
        }

        // 解析字符串区
        $content = substr($this->data, $stringAreaOffset);
        $this->geoispArr = explode("\t", $content);
    }

    public function find($ip)
    {
        if (empty($ip)) {
            return "";
        }

        $ipLong = $this->fastParseIp($ip);
        if ($ipLong === false) {
            return "";
        }

        $prefix = ($ipLong >> 16) & 0xFFFF;
        $suffix = $ipLong & 0xFFFF;

        // 一级索引
        $record = $this->readInt24(4 + $prefix * 3);

        while (($record & self::END_MASK) !== self::END_MASK) {
            $bit = ($suffix >> 15) & 1;
            $offset = self::INDEX_START_INDEX + $record * 6 + $bit * 3;
            $record = $this->readInt24($offset);
            $suffix = ($suffix << 1) & 0xFFFF;
        }

        $index = $record & self::COMPL_MASK;
        if (isset($this->geoispArr[$index])) {
            return $this->geoispArr[$index];
        }
        return "";
    }

    private function readInt24($offset)
    {
        // PHP 字符串可以直接当数组访问字节
        // ord() 获取字节值
        // 大端序逻辑
        return (ord($this->data[$offset]) << 16) | 
               (ord($this->data[$offset + 1]) << 8) | 
               ord($this->data[$offset + 2]);
    }

    private function fastParseIp($ip)
    {
        // 极致性能使用 ip2long (C函数)
        // ip2long 返回的是有符号整型 (在32位系统上)，在64位系统上是int64
        // 我们需要由符号转无符号处理逻辑
        $long = ip2long($ip);
        if ($long === false) {
            return false;
        }
        // 确保是32位无符号
        return ($long < 0) ? ($long + 4294967296) : $long;
    }

    private function findDbPath()
    {
        $attempts = [
            __DIR__ . '/' . self::DB_FILE_NAME,
            getcwd() . '/' . self::DB_FILE_NAME,
            dirname(__DIR__, 2) . '/data/' . self::DB_FILE_NAME, // 假设在 src/Qqzeng/Ip
            dirname(__DIR__, 3) . '/data/' . self::DB_FILE_NAME,
            '../data/' . self::DB_FILE_NAME,
        ];

        foreach ($attempts as $path) {
            if (file_exists($path)) {
                return $path;
            }
        }
        return null;
    }
}
