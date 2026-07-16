<?php
header('Content-Type: text/json;charset=utf-8"');
$redis = new Redis();
$redis->connect('127.0.0.1', 6379);

//$t1 = microtime(true);

$ip='78.138.53.9';
$ipnum = ip2long($ip);

$result=$redis->zRangeByScore('qqzengip', $ipnum, "inf", array( "limit" => array(0, 1) ));

$k = array_keys($result);
$k = $k[0];
$area_info = $result[$k];
list($start,$end,$startnum,$endnum,$continent,$country,$province, $city, $district,$isp,$areacode,$en,$cc,$lng,$lat) = explode("|", $area_info);

// 国内精华版 或者 国外拓展版 开启这段

// if($ipnum<$startnum){
// return 'none';
// }

$arr =array(
			'code'=>0,
				'data'=> array( 
								'ip'=>$ip,
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
    
//$t2 = microtime(true);
//echo '查询耗时'.($t2-$t1).'秒';
    
?>
