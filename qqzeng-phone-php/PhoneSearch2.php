<?php
namespace phone;
// 2021-12 by  bao si  内存优化
class PhoneSearch {
    private $prefmap=[];
    private $phoneArr=[];
    private $addrArr=[];
    private $ispArr=[]; 
    private $fp;
    private $data = '';
    private $prefSize = 0;
    
    public function __construct($filename = "phone.dat") {
        $path = dirname(__FILE__).'/'.$filename;
        $this->fp = null;
        if (($this->fp = @fopen($path, 'rb')) !== false) {
	        $fsize = filesize($path);
	        $this->data = fread( $this->fp, $fsize); 
	        $this->prefSize =$this->BytesToLong($this->data[0], $this->data[1], $this->data[2], $this->data[3]);
	        $RecordSize =$this->BytesToLong($this->data[4], $this->data[5], $this->data[6], $this->data[7]);
	        $descLength =$this->BytesToLong($this->data[8], $this->data[9], $this->data[10], $this->data[11]);
	        $ispLength =$this->BytesToLong($this->data[12], $this->data[13], $this->data[14], $this->data[15]);
	
	        //内容数组
	        $descOffset = 16 + $this->prefSize * 9 + $RecordSize * 7;
	        fseek( $this->fp, $descOffset);
	       
	        $this->addrArr =explode('&',fread( $this->fp,  $descLength));
	    
	        //运营商数组
	        $ispOffset = 16 + $this->prefSize * 9 + $RecordSize * 7 + $descLength;
	        fseek( $this->fp, $ispOffset );
	        $this->ispArr = explode('&',fread( $this->fp,  $ispLength));
	
	        //前缀区
	        for ($k = 0; $k < $this->prefSize;$k++) {
	            $i = $k * 9 + 16;
	            $n = ord($this->data[$i]) & 0xFF;
	            $this->prefmap[$n][0] = $this->BytesToLong($this->data[$i + 1], $this->data[$i + 2], $this->data[$i + 3], $this->data[$i + 4]);
	            $this->prefmap[$n][1] =$this-> BytesToLong($this->data[$i + 5], $this->data[$i + 6], $this->data[$i + 7], $this->data[$i + 8]);
	        }
            register_shutdown_function([&$this, '__destruct']);
        }
    }

    private function getPhoneArr($low,$high){
        for ($i = $low; $i < $high; $i++)
        {
            $p = 16 + $this->prefSize * 9 + ($i * 7);
            $this->phoneArr[$i] = $this->BytesToLong($this->data[$p], $this->data[1 + $p], $this->data[2 + $p], $this->data[3 + $p]);
        }
    }

    private function readInfo($mid){
        $p = 16 + $this->prefSize * 9 + ($mid * 7);
        $add_mid = $this->BytesToLong2($this->data[4 + $p],$this->data[5 + $p]);
        $isp_mid = ord($this->data[6 + $p])<<0;
        return compact('add_mid','isp_mid');
    }

    private function BinarySearch($low, $high, $k) {
	
	   if ($low > $high) {
			return -1;
		} else {
			
			$mid = ($low + $high)>>1;
			$phoneNum = $this->phoneArr[$mid];
			if ($phoneNum == $k) return (int)$mid;
			else if ($phoneNum > $k) return $this->BinarySearch($low, $mid - 1, $k);
			else return $this->BinarySearch($mid + 1, $high, $k);
		}
    }
   
    private function BytesToLong($a, $b, $c, $d) {
        $iplong = (ord($a) << 0) | (ord($b) << 8) | (ord($c) << 16) | (ord($d) << 24);
        if ($iplong < 0) {
            $iplong+= 4294967296;//负数时
        };
        return $iplong;
    }
    
    private function BytesToLong2($a, $b) {
        $iplong = (ord($a) << 0) | (ord($b) << 8) ;
        if ($iplong < 0) {
            $iplong+= 4294967296;//负数时
        };
        return $iplong;
    }

    final public function get($phone) {
        if(!$this->fp) return [];
        $pref= (int)(substr($phone,0, 3));
        $val = (int)(substr($phone,0,7));
        $low = $this->prefmap[$pref][0];
        $high = $this->prefmap[$pref][1];
        $this->getPhoneArr($low,$high);
		if ($high == 0 || !$this->phoneArr) {
			return [];
		}
		$cur = $low == $high ? $low : $this->BinarySearch($low, $high, $val);
		if ($cur != -1) {
			$info = $this->readInfo($cur);
			$results = $this->addrArr[$info['add_mid']].'|'.$this->ispArr[$info['isp_mid']];
			list($province, $city, $isp,$postcode,$citycode,$areacode) = explode("|", $results);
			return compact('province', 'city', 'isp', 'citycode' ,'postcode', 'areacode');
		} else {
			return [];
        }
        
    }
    
    function __destruct() {
        if ($this->fp) {
            fclose($this->fp);
        }
        $this->fp = NULL;
    }
}
