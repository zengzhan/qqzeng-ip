<?php
namespace phone;

class PhoneQueryForQQZeng {
    private $prefMap=[], $addrArr=[], $ispArr=[], $fp, $phoneOffset;
    
    public function __construct($path = '') {
        if(!$path) $path = dirname(__FILE__).'/qqzeng-phone.dat';
        $this->fp = null;
        if (($this->fp = @fopen($path, 'rb')) !== false) {
	        $preSize = $this->getStore();
	        $phoneSize = $this->getStore();
	        $addrLen =  $this->getStore();
	        $ispLen = $this->getStore();
	        $preData = fread($this->fp, $preSize * 9);
	        foreach(str_split($preData, 9) as $v){
	            $this->prefmap[ord($v[0])] = substr($v, 1);
	        }
	        fseek( $this->fp, ftell($this->fp)+$phoneSize*7);
	        $this->addrArr = explode('&', fread($this->fp, $addrLen));
	        $this->ispArr = explode('&', fread($this->fp, $ispLen));
	        $this->phoneOffset = 16 + $preSize * 9;
        }
    }

    private function getStore($len=4) {
        return $this->bytes2Long(...str_split(fread($this->fp, $len)));
    }

    private function getPrePos($pre) {
        $m = $this->prefmap[$pre] ?? null;
        if(!$m) return false;
        $low = $this->bytes2Long($m[0],$m[1],$m[2],$m[3]);
        $size = $this->bytes2Long($m[4],$m[5],$m[6],$m[7]) - $low;
        if($size<0) return false;
        return compact('low','size');
    }

    private function getPhone($roughly,$pos) {
        $roughly = pack('N',$roughly);
        $offset = $this->phoneOffset + $pos['low'] * 7;
        $s = 0;
        $e = $pos['size'];
        return $this->binarySearch($roughly, $offset, $s, $e);
    }
    
    private function binarySearch($roughly, $offset, $s, $e) {
	   if ($s > $e) {
            return false;
	   }else{
            $i = ($s + $e) >> 1;
            fseek($this->fp, $offset + ($i * 7));
            $x = strrev(fread($this->fp, 4));
            if($x==$roughly) return $this->readRelation(fread($this->fp, 3));
            elseif($roughly>$x) return $this->binarySearch($roughly, $offset, $i + 1, $e);
            else return $this->binarySearch($roughly, $offset, $s, $i - 1);
	   }
    }

    private function readRelation($str){
        return [
        	$this->bytes2Long($str[0],$str[1]),
        	ord($str[2])
        ];
    }

    private function bytes2Long(...$args) {
        $long = $i = 0;
        foreach($args as $v){
            $long += (ord($v)) << ($i++*8);
        }
        return $long;
    }

    final public function query($phone) {
        if($this->fp===false) return '';
        $pref= (int)(substr($phone, 0, 3));
        $pos = $this->getPrePos($pref);
        if($pos===false) return '';
        $roughly = (int)(substr($phone, 0, 7));
        $rel = $this->getPhone($roughly,$pos);
        if($rel) {
            return $this->addrArr[$rel[0]].'|'.$this->ispArr[$rel[1]];
        } else {
            return '';
        }
    }

    function __destruct() {
        if ($this->fp) {
            fclose($this->fp);
        }
        $this->fp = NULL;
    }
}