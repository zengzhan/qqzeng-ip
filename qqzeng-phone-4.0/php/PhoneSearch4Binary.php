<?php
namespace mobile;
//手机号段归属地查询 4.0版   php-8.3.3
class PhoneSearch4Binary
{

	private $prefDict = array();
    private $data;
    private $addrArr;
    private $ispArr;
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
		$datPath = __DIR__ . '/qqzeng-phone-4.0.dat';
        $this->data = file_get_contents($datPath);

        $prefSize = unpack("V", substr($this->data, 0, 4))[1];
        $descLength = unpack("V", substr($this->data, 8, 4))[1];
        $ispLength = unpack("V", substr($this->data, 12, 4))[1];
        $phoneSize = unpack("V", substr($this->data, 4, 4))[1];
        $verNum = unpack("V", substr($this->data, 16, 4))[1];

        $headLength = 20;
        $startIndex = $headLength + $descLength + $ispLength;

	  // 内容数组
        $descString = substr($this->data, $headLength, $descLength);
        $this->addrArr = explode('&', $descString);

        // 运营商数组
        $ispString = substr($this->data, $headLength + $descLength, $ispLength);
        $this->ispArr = explode('&', $ispString);


		for ($m = 0; $m < $prefSize; $m++) {
            $i = $m * 5 + $startIndex;
            $pref = ord($this->data[$i]);
            $index = unpack("V", substr($this->data, $i + 1, 4))[1];
            $this->prefDict[$pref] = $index;
        }
	}



      public function query($phone) {
        $prefix = substr($phone, 0, 3);
        $suffix = (int)substr($phone, 3, 4);
        $addrispIndex = 0;

        if (array_key_exists($prefix, $this->prefDict)) {
            $start = $this->prefDict[$prefix];
            $p = $start + $suffix * 2;
            $addrispIndex = unpack("v", substr($this->data, $p, 2))[1];
        }

        if ($addrispIndex == 0) {
            return "|||||";
        }

        $address = $this->addrArr[$addrispIndex >> 5];
        $isp = $this->ispArr[$addrispIndex & 0x001F];
        return $address . "|" . $isp;
    }

       

}
