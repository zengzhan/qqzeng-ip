<?php
/* 
PHP Version 5.3+
qqzeng-ip 3.0 2018-08-04
*/
class IpSearch {

    private $prefStart=array(); 
    private $prefEnd=array(); 
    private $endArr=array(); 
    private $addrArr=array(); 
    private $fp;

    function __construct($path) {
        $this->fp = fopen($path, 'rb');
        $fsize = filesize($path);
     
        $data = fread( $this->fp, $fsize); 

        for ($k = 0; $k < 256; $k++)
        {
            $i = $k * 8 + 4;           
            $this->prefStart[$k] =$this->BytesToLong($data[$i], $data[$i+1], $data[$i+2], $data[$i+3]);
            $this->prefEnd[$k] =$this->BytesToLong($data[$i+4], $data[$i+5], $data[$i+6], $data[$i+7]);         
        }

        $RecordSize =$this->BytesToLong($data[0], $data[1], $data[2], $data[3]);

        for ($i = 0; $i <$RecordSize; $i++)
        {
            $p = 2052 + ($i * 8);         
            $this->endArr[$i] =$this->BytesToLong($data[$p], $data[$p+1], $data[$p+2], $data[$p+3]);
      
            $offset =  $this->BytesToLong($data[4 + $p], $data[5 + $p], $data[6 + $p] ,"\x0");
            $length =  ord($data[7 + $p]);
          
            fseek( $this->fp, $offset);
            $this->addrArr[$i] =fread( $this->fp,  $length);
        }
    }

    function __destruct() {
        if ($this->fp !== NULL) {
            fclose($this->fp);
        }
    }

    function get($ip) {
        $val =sprintf("%u",ip2long($ip));
        $ip_arr = explode('.', $ip); 
        $pref = $ip_arr[0];
        $low = $this->prefStart[$pref];
        $high =  $this->prefEnd[$pref];
        $cur = $low == $high ? $low : $this->BinarySearch($low, $high, $val);
        return $this->addrArr[$cur];
    }

    function BinarySearch($low, $high, $k) {
        $M = 0;
        while ($low <= $high) {
            $mid = floor(($low + $high) / 2);
            $endipNum = $this->endArr[$mid];           
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
   
    function BytesToLong($a, $b, $c, $d) {
        $iplong = (ord($a) << 0) | (ord($b) << 8) | (ord($c) << 16) | (ord($d) << 24);
        if ($iplong < 0) {
            $iplong+= 4294967296;//负数时
        };
        return $iplong;
    }
    
}
 
?>
