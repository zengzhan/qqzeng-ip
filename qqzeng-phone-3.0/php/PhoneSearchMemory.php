<?php
namespace mobile;
//手机号段归属地查询 内存版 3.0版
class PhoneSearchMemory
{
	//三个变量 要放到全局内存中缓存 免得每次调用又得重新初始化
	private $fp, $phone2D=[], $addrArr=[], $ispArr=[];
	//单例模式 
	private static $singleInstance;
	public static function getInstance() {
       	if (!self::$singleInstance) { 
       	      self::$singleInstance = new self(); 
       	} 
       	return self::$singleInstance;
	}
	
	private function __clone() {}      
     
	private  function __construct() 
	{
	$filename = 'qqzeng-phone-3.0.dat';
	$path = dirname(__FILE__).'/'.$filename;    
	$this->fp = fopen($path, 'rb') ;

	$prefLen =$this->getLong();
	$phoneLen =$this->getLong();
	$addrLen = $this->getLong();
	$ispLen =$this->getLong();
	$verNum = $this->getLong();

	$headLen = 20;
	$startIndex =$headLen + $addrLen + $ispLen;
	//区域数组    
	$this->addrArr = explode('&',stream_get_contents ($this->fp, $addrLen,-1));
	//运营商数组    
	$this->ispArr = explode('&',stream_get_contents($this->fp, $ispLen,-1));

	for ($m = 0; $m < $prefLen; $m++)
	{
	    $i = $m * 7 + $startIndex;
	    fseek($this->fp, $i);
	    $pref = $this->getLong(1);
	    $index =$this->getLong();
	    $length = $this->getLong(2);
	    for ($n = 0; $n < $length; $n++)
	    {
	        $p = $startIndex + $prefLen * 7 + ($n + $index) * 4;
	        fseek($this->fp, $p);
	        $suff = $this->getLong(2);
	        $addrispIndex = $this->getLong(2);
	        $this->phone2D[$pref][$suff] = $addrispIndex;
	    }
	}
	  fclose($this->fp);
	}



      public function query($phone) {
        $prefix = (int)(substr($phone, 0, 3));
        $suffix = (int)(substr($phone, 3, 4));
        $addrispIndex = $this->phone2D[$prefix][$suffix];
        if ($addrispIndex>0) {      
             return $this->addrArr[(int)$addrispIndex / 100].'|'.$this->ispArr[$addrispIndex% 100];

        } else {
            return '';
        }
    }

     private function getLong($len=4) {
        return $this->bytes2Long(...str_split(fread($this->fp, $len)));
      }
    
    
    private function bytes2Long(...$args) {
        $long = $i = 0;
        foreach($args as $v){
            $long += (ord($v)) << ($i++*8);
        }
        return $long;
    }
    

}
