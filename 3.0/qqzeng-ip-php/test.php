<?php
header('Content-Type: text/json;charset=utf-8"');
include_once("IpSearch.php");

//旧版 
//$reader = new IpSearch('qqzeng-ip-3.0-ultimate.dat');
$reader = IpSearch::getInstance();

$client_ip='8.8.8.8';

for	($i=0;$i<1000;$i++){
	$area_info = $reader->get($client_ip);
}

$area_info = $reader->get($client_ip);

list($continent,$country,$province, $city, $district,$isp,$areacode,$en,$cc,$lng,$lat) = explode("|", $area_info);

$arr =array(
			'code'=>0,
				'data'=> array( 
								'ip'=>$client_ip,
								'continent'=>$continent,
								'country'=>$country,
								'province'=>$province,
								'city'=>$city, 
								'district'=>$district, 
								'isp'=>$isp,
								'areacode'=>$areacode,
								'en'=>$en,
								'cc'=>$cc,
								'lng'=>$lng,
								'lat'=>$lat
								
						)); 
	echo   json_encode($arr); 
	
	//返回json数据 
/* 
	{"code":0,"data":{"ip":"8.8.8.8","continent":"\u5317\u7f8e\u6d32","country":"\u7f8e\u56fd","province":"","city":"","district":"","isp":"GoogleDNS","areacode":"","en":"United States","cc":"US","lng":"-95.712891","lat":"37.09024"}}
 */
?>
