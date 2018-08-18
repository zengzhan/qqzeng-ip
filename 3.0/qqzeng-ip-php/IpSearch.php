<?php
/* 
PHP Version 7.2.2
qqzeng-ip 3.0 2018-08-18

在Java C# 中单例会一直存在于整个应用程序的生命周期里，变量是跨页面级的，真正可以做到这个实例在应用程序生命周期中的唯一性。
然而在PHP中，所有的变量无论是全局变量还是类的静态成员，都是页面级的，每次页面被执行时，都会重新建立新的对象，都会在页面执行完毕后被清空


*/

class IpSearch  {
  
    private $prefStart=array(); 
    private $prefEnd=array(); 
    private $endArr=array(); 
    private $addrArr=array(); 
    private $fp;
 
    static private   $instance= null;

    private    function __construct() { 
        $this->loadDat(); 
    }

    private  function loadDat(){
        $path='qqzeng-ip-3.0-ultimate.dat';
        $this->fp = fopen($path, 'rb');
        $fsize = filesize($path);
     
        $data = fread( $this->fp, $fsize); 

        for ($k = 0; $k < 256; $k++)
        {
            $i = $k * 8 + 4;           
            $this->prefStart[$k]=unpack("V",$data,$i)[1];
            $this->prefEnd[$k]=unpack("V",$data,$i+4)[1];               
        }

        $RecordSize =unpack("V",$data,0)[1];  

        for ($i = 0; $i <$RecordSize; $i++)
        {
            $p = 2052 + ($i * 8);         
            $this->endArr[$i] =unpack("V",$data,$p)[1];
      
            $offset =  unpack("V", $data[4 + $p]. $data[5 + $p]. $data[6 + $p]  . "\x0")[1];
            $length =unpack("C", $data[7 + $p])[1];
          
            fseek($this->fp, $offset);
            $this->addrArr[$i] =fread( $this->fp,  $length);
        }


    }

    function __destruct(){
        if ($this->fp !== NULL) {
            fclose($this->fp);
        }
    }

    private  function __clone() {}
    private  function __wakeup() {}
        
    public static function getInstance() {
        if (self::$instance instanceof IpSearch ) {           
            return self::$instance;
        }
        else{
          
            return self::$instance = new IpSearch();
          
        }
       
    }
    
    public function get($ip) {
        $val =sprintf("%u",ip2long($ip));
        $ip_arr = explode('.', $ip); 
        $pref = $ip_arr[0];
        $low = $this->prefStart[$pref];
        $high =  $this->prefEnd[$pref];
        $cur = $low == $high ? $low : $this->BinarySearch($low, $high, $val);
        return $this->addrArr[$cur];
    }

    private function BinarySearch($low, $high, $k) {
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
   
    
}
 
?>
