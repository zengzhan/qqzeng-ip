<?php
 namespace mobile;
header('Content-Type: text/json;charset=utf-8"');
require_once ("PhoneSearchMemory.php");

$phone=$_GET["phone"]??'1881234';
$reader =  PhoneSearchMemory::getInstance(); 
$area_info = $reader->query($phone);

list($province, $city, $postcode,$citycode,$areacode,$isp) = explode("|", $area_info);

 $arr =array(
			'code'=>0,
				'data'=> array( 
								'mobile'=>$phone,
								'province'=>$province,
								'city'=>$city,
								'isp'=>$isp,
								'postcode'=>$postcode,
								'citycode'=>$citycode,
								'areacode'=>$areacode
						)); 
	echo   json_encode($arr); 
	
	//返回json数据 	
	/* 	
		{"code":0,"data":{"mobile":"1881234","province":"\u4e91\u5357","city":"\u66f2\u9756","isp":"655000","postcode":"0874","citycode":"530300","areacode":"\u79fb\u52a8"}}
	 */
?>
