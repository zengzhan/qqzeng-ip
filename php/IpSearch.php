<?php
/**
 * 使用PHP代码从[qqzeng-ip]数据库的二进制文件中获取IP地址所在地理位置信息
 */
class IpLocation {
	private $ip_filepath;
	private $handle;
	private $first_index_pos;
	private $last_index_pos;
	private $index_count;
	
	
	public function __construct($ip_filepath){
		$this->ip_filepath = $ip_filepath;
		
		$this->handle = fopen($this->ip_filepath, "r");
		$this->first_index_pos = self::fgetint($this->handle, false);
		$this->last_index_pos = self::fgetint($this->handle, false);
		$this->index_count = ($this->last_index_pos - $this->first_index_pos)/7 + 1;
	}
	
	/**
	 * 查找一个IP地址所对应的地理位置信息
	 * @param mixed $ip_address 由字符串或者整数表示的一个IP地址
	 * @return array IP地址所对应的country和area信息
	 */
	public function seek($ip_address){
		if(is_int($ip_address)) $ip_int = $ip_address;
		else $ip_int = self::ip2int($ip_address);
		
		$ip_record_index = $this->half_find($ip_int, 0, $this->index_count-1);
		fseek($this->handle, $this->first_index_pos + $ip_record_index*7 + 4, SEEK_SET);
		$ip_record_pos = self::fgetint_with_three_bytes($this->handle);
		return $this->get_country_and_area($ip_record_pos);
	}
	
	
	/**
	 * 使用二分法查找一个ip地址在ip数据库文件当中所对应的数据位置
	 * @param integer $ip_int
	 * @param integer $index_low
	 * @param integer $index_high
	 */
	private function half_find($ip_int, $index_low, $index_high){
		if ($index_high - $index_low == 1) return $index_low;
		$index_middle = intval( ($index_low + $index_high) / 2 );
		$file_offset = $this->first_index_pos + $index_middle * 7;
		fseek($this->handle, $file_offset, SEEK_SET);
		$ip_middle_int = self::fgetint($this->handle, false);
		if ($ip_middle_int == $ip_int) return $index_low;
		elseif (($ip_int > $ip_middle_int && $ip_middle_int > 0) || ( $ip_int < 0 && ($ip_int > $ip_middle_int || $ip_middle_int > 0 ))){
			$index_low = $index_middle;
			return $this->half_find($ip_int, $index_low, $index_high, $this->first_index_pos);
		}else{
			$index_high = $index_middle;
			return $this->half_find($ip_int, $index_low, $index_high, $this->first_index_pos);
		}
	}
	
	
	/**
	 * 获取某一条IP地址所对应的国家和地区信息，返回一个数组
	 * @param int $ip_record_pos 该ip地址信息所在的文件位置
	 * @return array
	 */
	private function get_country_and_area($ip_record_pos){
		$country = $area = "";
		
		fseek($this->handle, $ip_record_pos+4, SEEK_SET);
		$flag = ord(fgetc($this->handle));
		if($flag == 1){ #the next three bytes are another pointer
			$ip_record_level_two_pos = self::fgetint_with_three_bytes($this->handle);
			fseek($this->handle, $ip_record_level_two_pos, SEEK_SET);
			$level_two_flag = ord(fgetc($this->handle));
			if($level_two_flag == 2){
				$ip_record_level_three_pos = self::fgetint_with_three_bytes($this->handle);
				$level_three_flag = ord(fgetc($this->handle));
				fseek($this->handle, $ip_record_level_three_pos, SEEK_SET);
				$country = self::fgets_zero_end($this->handle);
				
				if($level_three_flag==1 || $level_three_flag==2){
					fseek($this->handle, $ip_record_level_two_pos+5, SEEK_SET);
					$ip_record_area_string_pos = self::fgetint_with_three_bytes($this->handle);
					fseek($this->handle, $ip_record_area_string_pos, SEEK_SET);
					$area = self::fgets_zero_end($this->handle);
				}else{
					fseek($this->handle, $ip_record_level_two_pos+4, SEEK_SET);
					$area = self::fgets_zero_end($this->handle);
				}
			}else{
				fseek($this->handle, $ip_record_level_two_pos, SEEK_SET);
				$country = self::fgets_zero_end($this->handle);
				$area = self::fgets_zero_end($this->handle);
			}
		}elseif($flag == 2){
			$ip_record_level_two_pos = self::fgetint_with_three_bytes($this->handle);
			fseek($this->handle, $ip_record_level_two_pos, SEEK_SET);
			$country = self::fgets_zero_end($this->handle);
			fseek($this->handle, $ip_record_pos+8, SEEK_SET);
			$area = self::fgets_zero_end($this->handle);
		}else{
			fseek($this->handle, $ip_record_pos+4, SEEK_SET);
			$country = self::fgets_zero_end($this->handle);
			$area = self::fgets_zero_end($this->handle);
		}
		
		$country = iconv("gb18030", "utf-8//IGNORE", $country);
		if($area){
			if(ord($area{0}) == 2) $area = ""; // 不规则字符
			else $area = iconv("gb18030", "utf-8//IGNORE", $area);
		}
		return array($country, $area);
	}
	/**
	 * 从文件的当前位置读取一个以\0结尾的字符串
	 * @param resource $handle 文件指针
	 */
	private static function fgets_zero_end($handle){
		$result = "";
		while(true){
			$char = fgetc($handle);
			if(ord($char) == 0) break;
			$result .= $char;
		}
		return $result;
	}
	
	/**
	 * 读取文件的一个字节
	 * @param resource $handle 文件指针
	 * @return int
	 */
	private static function fget($handle){
		$char = fgetc($handle);
		return ord($char);
	}
	
	/**
	 * 读取一个四个字节的整数
	 * @param resource $handle 文件指针
	 * @param boolean $big 是否采用大端法读取，如果要使用小端法，传入false
	 */
	private static function fgetint($handle, $big=true){
		static $int_max = 2147483647;
		if($big)
			$int = self::fget($handle) << 24
					 | self::fget($handle) << 16
					 | self::fget($handle) <<  8
					 | self::fget($handle);
		else
			$int = self::fget($handle)
					 | self::fget($handle) <<  8
					 | self::fget($handle) << 16
					 | self::fget($handle) << 24;
		if($int <= $int_max){
			return $int;
		}else{
			return $int - $int_max - $int_max - 2;
		}
	}
	/**
	 * 采用小端法读取一个只用三个字节表示的整数
	 * @param resource $handle 文件指针
	 */
	private static function fgetint_with_three_bytes($handle){
		return self::fget($handle)
				 | self::fget($handle) <<  8
				 | self::fget($handle) << 16;
	}
	/**
	 * php 自带的 ip2long 函数在 32 位环境下会输出负数，而在 64 位的情况下不会输出负数
	 * 该函数将字符串形式的ip地址转化成一个整数值，并使在 64 环境下运行时按照 32 位条件一样统一输出负数
	 * @param string $ip_address
	 * @return int
	 */
	public static function ip2int($ip_address){
		static $int_max = 2147483647;
		$iplong = ip2long($ip_address);
		if($iplong === false) return 0;
		elseif($iplong <= $int_max){
			return $iplong;
		}else{
			$iplong = $iplong - $int_max - $int_max - 2;
			return $iplong;
		}
	}
}
