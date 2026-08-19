<?php
class PhoneSearch{
// 2021-12 by  bao si  内存优化  查询标准版 支持  单个查询 和 批量查询
    private $prefmap=[]; 
    private $phonemap=[]; 
    private $phoneArr=[]; 
    private $addrArr=[]; 
    private $ispArr=[];
    private $fp;
    private $figureLen = 16;

    public function __construct($path) {
        $this->fp = fopen($path, 'rb');
        if(!$this->fp) die('Can\'t open qqzeng-phone.dat');
        $figureData = fread($this->fp, $this->figureLen);
        $figure_arr = str_split($figureData,4);
        $prefSize =$this->bytes2Long($figure_arr[0][0], $figure_arr[0][1], $figure_arr[0][2], $figure_arr[0][3]);
        $recordSize =$this->bytes2Long($figure_arr[1][0], $figure_arr[1][1], $figure_arr[1][2], $figure_arr[1][3]);
        $addrLength =$this->bytes2Long($figure_arr[2][0], $figure_arr[2][1], $figure_arr[2][2], $figure_arr[2][3]);
        $ispLength =$this->bytes2Long($figure_arr[3][0], $figure_arr[3][1], $figure_arr[3][2], $figure_arr[3][3]);

        //前缀区
        $prefOffset = ftell($this->fp);
        fseek( $this->fp, $prefOffset);
        $prefData = fread( $this->fp, $prefSize * 9);
        $pref_arr = str_split($prefData, 9);
        foreach($pref_arr as $v) {
            $n = ord($v[0]);
            $this->prefmap[$n][0] = $this->bytes2Long($v[1], $v[2], $v[3], $v[4]);
            $this->prefmap[$n][1] =$this-> bytes2Long($v[5], $v[6], $v[7], $v[8]);
        }
        
        //号码索引区
        $recordOffset = ftell($this->fp);
        fseek( $this->fp, $recordOffset);
        $recordData = fread( $this->fp, $recordSize * 7);
        $record_arr = str_split($recordData, 7);
        $i = 0;
        foreach($record_arr as $v) {
            $roughly = $this->bytes2Long($v[0], $v[1], $v[2], $v[3]);
            $this->phoneArr[$roughly] = $i;
            $this->phoneMap[$i][0] =$this->bytes2Long($v[4],$v[5]);
            $this->phoneMap[$i][1] = ord($v[6]);
            $i++;
        }

        //地区数组
        $addrOffset = ftell($this->fp);
        fseek( $this->fp, $addrOffset);
        $this->addrArr = explode('&',fread( $this->fp, $addrLength));
    
        //运营商数组
        $ispOffset = ftell($this->fp);
        fseek( $this->fp, $ispOffset);
        $this->ispArr = explode('&',fread( $this->fp, $ispLength));

        register_shutdown_function([&$this, '__destruct']);
    }

    //单个查询
    public function query(int|string $phone) {
        $pref= (int)(substr($phone,0, 3));
        $val = (int)(substr($phone,0,7));
        
        $low = $this->prefmap[$pref][0] ?? 0;
        $high = $this->prefmap[$pref][1] ?? 0;
        
	   if ($high == 0) {
            return '';
	   }
	   $cur = $this->phoneArr[$val] ?? '-1';
	   if ($cur != -1) {
            return $this->addrArr[$this->phoneMap[$cur][0]].'|'.$this->ispArr[$this->phoneMap[$cur][1]];
	   } else {
            return '';
	   }
    }

    //批量查询
    public function batchQuery(array $phones = []) {
        if(is_array($phones) && count($phones)){
            return array_map('self::query',$phones);
        }else{
            return [];
        }
    }

    private function bytes2Long(...$args) {
        $long = $i = 0;
        foreach($args as $v){
            $long += (ord($v)) << ($i++*8);
        }
        return $long;
    }

    private function __destruct() {
        if ($this->fp) {
            fclose($this->fp);
        }
        $this->fp = null;
    }
}
