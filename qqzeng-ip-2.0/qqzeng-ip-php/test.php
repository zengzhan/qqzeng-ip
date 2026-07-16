<?php
header('Content-Type: text/json;charset=utf-8"');
include_once("IpSearch.php");


$reader = new IpSearch('qqzeng-ip-utf8.dat');


$client_ip='218.5.76.154';

$area_info = $reader->get($client_ip);

list($continent,$country,$province, $city, $district,$isp,$areacode,$en,$cc,$lng,$lat) = explode("|", $area_info);

$arr =array(
	'php'=>PHP_VERSION,
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
	{"php":"5.3.28","code":0,"data":{"ip":"218.5.76.154","continent":"\u4e9a\u6d32","country":"\u4e2d\u56fd","province":"\u798f\u5efa","city":"\u53a6\u95e8","district":"\u601d\u660e","isp":"\u7535\u4fe1","areacode":"350203","en":"China","cc":"CN","lng":"118.08233","lat":"24.44543"}}
 */
?>
