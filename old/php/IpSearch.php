<?php
/**
 * 使用PHP代码从[qqzeng-ip]数据库的二进制文件中获取IP地址所在地理位置信息
 * @qqzeng-ip
 */
 define('filepath' ,"ip.dat");
class IpLocation {
	 var $StartIP = 0;
    var $EndIP = 0;
    var $Country = '';
    var $Local = '';
 
    var $CountryFlag = 0; // 标识 Country位置
        // 0x01,随后3字节为Country偏移,没有Local
        // 0x02,随后3字节为Country偏移,接着是Local
        // 其他,Country,Local,Local有类似的压缩。可能多重引用。
 
    var $fp;
 
    var $FirstStartIp = 0;
    var $LastStartIp = 0;
    var $EndIpOff = 0;
 
    function getStartIp($RecNo)
    {
        $offset = $this->FirstStartIp + $RecNo * 7;
        @fseek($this->fp, $offset, SEEK_SET);
        $buf = fread($this->fp, 7);
        $this->EndIpOff = ord($buf[4]) + (ord($buf[5]) * 256) + (ord($buf[6]) * 256 * 256);
        $this->StartIp = ord($buf[0]) + (ord($buf[1]) * 256) + (ord($buf[2]) * 256 * 256) + (ord($buf[3]) * 256 * 256 * 256);
        return $this->StartIp;
    }
 
    function getEndIp()
    {
        @fseek($this->fp, $this->EndIpOff, SEEK_SET);
        $buf = fread($this->fp, 5);
        $this->EndIp = ord($buf[0]) + (ord($buf[1]) * 256) + (ord($buf[2]) * 256 * 256) + (ord($buf[3]) * 256 * 256 * 256);
        $this->CountryFlag = ord($buf[4]);
        return $this->EndIp;
    }
 
    function getCountry()
    {
        switch($this->CountryFlag)
        {
            case 1: 
            case 2: 
               
            default: 
                $this->Country = $this->getFlagStr($this->EndIpOff + 4); 
				 $this->Local =(1 == $this->CountryFlag) ?  $this->getFlagStr(ftell($this->fp)) :$this->getFlagStr($this->EndIpOff + 8); 
               
        }
    }
 
    function getFlagStr($offset)
    {
        $flag = 0;
         
        while(1)
        {
            @fseek($this->fp, $offset, SEEK_SET);
            $flag = ord(fgetc($this->fp));
             
            if($flag == 1 || $flag == 2)
            {
                $buf = fread($this->fp, 3);
                 
                if($flag == 2)
                {
                    $this->CountryFlag = 2;
                    $this->EndIpOff = $offset - 4;
                }
                 
                $offset = ord($buf[0]) + (ord($buf[1]) * 256) + (ord($buf[2]) * 256 * 256);
            }
            else
                break;
        }
         
        if($offset < 12) return '';
         
        @fseek($this->fp, $offset, SEEK_SET);
 
        return $this->getStr();
    }
 
    function getStr()
    {
        $str = '';
         
        while(1)
        {
            $c = fgetc($this->fp);
 
            if(ord($c[0]) == 0) break;
             
            $str .= $c;
        }
         
        return $str;
    }
 
    function seek($ip_address = '')
    {
        if(!$ip_address) return;
         
       
 
        $nRet;
        $ip = $this->IpToInt($ip_address);
        $this->fp = fopen(filepath, "rb");
         
        if($this->fp == NULL)
        {
            $szLocal = "OpenFileError";
            return 1;
        }
         
        @fseek($this->fp, 0, SEEK_SET);
        $buf = fread($this->fp, 8);
        $this->FirstStartIp = ord($buf[0]) + (ord($buf[1]) * 256) + (ord($buf[2]) * 256 * 256) + (ord($buf[3]) * 256 * 256 * 256);
        $this->LastStartIp = ord($buf[4]) + (ord($buf[5]) * 256) + (ord($buf[6]) * 256 * 256) + (ord($buf[7]) * 256 * 256 * 256);
 
        $RecordCount = floor(($this->LastStartIp - $this->FirstStartIp) / 7);
         
        if($RecordCount <= 1)
        {
            $this->Country = "";
            fclose($this->fp) ;
            return 2 ;
        }
 
        $RangB = 0;
        $RangE = $RecordCount;
         
        // Match ...
        while($RangB < $RangE - 1)
        {
            $RecNo = floor(($RangB + $RangE) / 2);
            $this->getStartIp($RecNo) ;
 
            if($ip == $this->StartIp)
            {
                $RangB = $RecNo;
                break;
            }
             
            if($ip > $this->StartIp) $RangB = $RecNo;
            else $RangE = $RecNo;
        }
         
        $this->getStartIp($RangB);
        $this->getEndIp();
 
        if(($this->StartIp <= $ip) && ($this->EndIp >= $ip))
        {
            $this->getCountry();
        }
        else
        {
            $this->Country = '无';
            $this->Local = '';
        }
         
        fclose($this->fp);
    }
 
    function IpToInt($Ip)
    {
        $array = explode('.', $Ip);
        $Int = ($array[0] * 256 * 256 * 256) + ($array[1] * 256 * 256) + ($array[2] * 256) + $array[3];
 
        return $Int;
    }
}
