<?php
/**
 * 使用PHP代码从[qqzeng-ip.dat]数据库的二进制文件中获取IP地址所在地理位置信息
 * @qqzeng-ip   2015-08-08
 */
class IpSearch {
    private $firstStartIpOffset; //索引区第一条流位置
    private $lastStartIpOffset; //索引区最后一条流位置
    private $prefixStartOffset; //前缀区第一条的流位置
    private $prefixEndOffset; //前缀区最后一条的流位置
    private $ipCount; //ip段数量
    private $prefixCount; //前缀数量
    private $fp;
    private $prefix_array = array();
    function __construct($database) {
        $this->fp = @fopen($database, 'rb');
        $buf = $this->read($this->fp, 0, 16);
        $this->firstStartIpOffset = $this->BytesToLong($buf[0], $buf[1], $buf[2], $buf[3]);
        $this->lastStartIpOffset = $this->BytesToLong($buf[4], $buf[5], $buf[6], $buf[7]);
        $this->prefixStartOffset = $this->BytesToLong($buf[8], $buf[9], $buf[10], $buf[11]);
        $this->prefixEndOffset = $this->BytesToLong($buf[12], $buf[13], $buf[14], $buf[15]);
        $this->ipCount = floor(($this->lastStartIpOffset - $this->firstStartIpOffset) / 12) + 1;
        $this->prefixCount = floor(($this->prefixEndOffset - $this->prefixStartOffset) / 9) + 1;        
        $pref_buf = $this->read($this->fp, $this->prefixStartOffset, $this->prefixCount * 9);
        for ($k = 0; $k < $this->prefixCount; $k++) {
            $i = $k * 9;
            $start_index = $this->BytesToLong($pref_buf[1 + $i], $pref_buf[2 + $i], $pref_buf[3 + $i], $pref_buf[4 + $i]);
            $end_index = $this->BytesToLong($pref_buf[5 + $i], $pref_buf[6 + $i], $pref_buf[7 + $i], $pref_buf[8 + $i]);
            $this->prefix_array[ord($pref_buf[$i]) ] = array(
                'start_index' => $start_index,
                'end_index' => $end_index
            );
        }
    }
    function __destruct() {
        if ($this->fp !== NULL) {
            fclose($this->fp);
        }
    }
    function get($ip_address) {
        if ($ip_address == '') return;
        $high = 0;
        $low = 0;
        $startIp = 0;
        $endIp = 0;
        $local_offset = 0;
        $local_length = 0;
        $prefix = explode('.', $ip_address) [0];
        $ipNum = $this->ip2uint($ip_address);
        if (array_key_exists($prefix, $this->prefix_array))     
        {
            $index = $this->prefix_array[$prefix];
            $low = $index['start_index'];
            $high = $index['end_index'];
        } else {
            return "";
        }
        $left = $low == $high ? $low : $this->BinarySearch($low, $high, $ipNum);
        $this->GetIndex($left, $startIp, $endIp, $local_offset, $local_length);       
        if (($startIp <= $ipNum) && ($endIp >= $ipNum)) {
            return $this->GetLocal($local_offset, $local_length);
        } else {
            return "";
        }
    }
    function BinarySearch($low, $high, $k) {
        $M = 0;
        while ($low <= $high) {
            $mid = floor(($low + $high) / 2);
            $endipNum = $this->GetEndIp($mid);           
            if ($endipNum >= $k) {
                $M = $mid;
                if ($mid == 0) {
                    break;
                }
                $high = $mid - 1;
            } else $low = $mid + 1;
        }
        return $M;
    }
    function GetIndex($left, &$startip, &$endip, &$local_offset, &$local_length) {
        $left_offset = $this->firstStartIpOffset + ($left * 12);
        $buf = $this->read($this->fp, $left_offset, 12);
        $startip = $this->BytesToLong($buf[0], $buf[1], $buf[2], $buf[3]);
        $endip = $this->BytesToLong($buf[4], $buf[5], $buf[6], $buf[7]);
        $r3 = (ord($buf[8]) << 0 | ord($buf[9]) << 8 | ord($buf[10]) << 16);
        if ($r3 < 0) $r3+= 4294967296;//负数时
        $local_offset = $r3;
        $local_length = ord($buf[11]);
    }
    function getEndIp($left) {
        $left_offset = $this->firstStartIpOffset + ($left * 12) + 4;
        $buf = $this->read($this->fp, $left_offset, 4);
        return $this->BytesToLong($buf[0], $buf[1], $buf[2], $buf[3]);
    }
    function GetLocal($local_offset, $local_length) {
        return $this->read($this->fp, $local_offset, $local_length);
    }
    function read($stream, $offset, $numberOfBytes) {
        if (fseek($stream, $offset) == 0) {
            $value = fread($stream, $numberOfBytes);
            return $value;
        }
    }
    function ip2uint($strIP) {
        $lngIP = ip2long($strIP);
        if ($lngIP < 0) {
            $lngIP+= 4294967296;//负数时
        }
        return $lngIP;
    }
    function BytesToLong($a, $b, $c, $d) {
        $iplong = (ord($a) << 0) | (ord($b) << 8) | (ord($c) << 16) | (ord($d) << 24);
        if ($iplong < 0) {
            $iplong+= 4294967296;//负数时
        };
        return $iplong;
    }
}

/* 
	调用：
	$reader = new IpSearch('qqzeng-ip.dat');
	$r = $reader->get($ip);
	->亚洲|中国|香港|九龙|||810200|Hong Kong|HK|114.17495|22.327115
 */
 
?>
