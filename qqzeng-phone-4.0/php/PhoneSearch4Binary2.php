<?php
namespace mobile;
//手机号段归属地查询 4.0版   php-8.3.3
class PhoneSearch4Binary
{

	private $prefDict = array();
    private $data;
    private $addrArr;
    private $ispArr;

    private static $instance;
    public static function getInstance(): self
    {
        return self::$instance ??= new self();
    }
	
	private function __clone() {}      
    private function __construct()
    {
        $this->data = file_get_contents(__DIR__ . '/qqzeng-phone-4.0.dat');
        $this->loadData();
    } 
    private function loadData(): void
    { 
        $headLength = 20;
        [$prefSize, $phoneSize, $descLength, $ispLength, $verNum] = array_values(unpack("V5", substr($this->data, 0,  $headLength)));
        
        $startIndex = $headLength;
        $descData = substr($this->data, $startIndex, $descLength);
        $this->addrArr = explode('&', $descData);
        
        $startIndex += $descLength;
        $ispData = substr($this->data, $startIndex, $ispLength);
        $this->ispArr = explode('&', $ispData);
        
        $startIndex += $ispLength;
        for ($m = 0; $m < $prefSize; $m++) {
            $i = $m * 5 + $startIndex;
            $this->prefDict[ord($this->data[$i])] = unpack("V", substr($this->data, $i + 1, 4))[1];
        }
    }
	

    public function query($phone): string
    {
        [$prefix, $suffix] = [substr($phone, 0, 3), (int)substr($phone, 3, 4)];
        $addrispIndex = $this->prefDict[$prefix] ?? 0;

        if ($addrispIndex) {
            $addrispIndex = unpack("v", substr($this->data, $addrispIndex + $suffix * 2, 2))[1];
        }

        return $addrispIndex ? $this->addrArr[$addrispIndex >> 5] . "|" . $this->ispArr[$addrispIndex & 0x001F] : "|||||";
    }

       

}


//test.php

<?php
 namespace mobile;
header('Content-Type: text/json;charset=utf-8"');
require_once ("PhoneSearch4Binary.php");


$phone = isset($_GET["phone"]) ? $_GET["phone"] : '1881234';

try {
    $lookup = PhoneSearch4Binary::getInstance();
    $area_info = $lookup->query($phone);

    $data = explode("|", $area_info);

    $response = [
        'code' => 0,
        'data' => [
            'mobile' => $phone,
            'province' => $data[0],
            'city' => $data[1],
            'isp' => $data[5],
            'postcode' => $data[2],
            'citycode' => $data[3],
            'areacode' => $data[4],
           
        ],
    ];

    echo json_encode($response);
} catch (Exception $e) {  
    echo json_encode(['code' => 1, 'message' => $e->getMessage()]);
}

?>
