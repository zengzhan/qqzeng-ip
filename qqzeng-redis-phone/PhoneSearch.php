<?php
header('Content-Type: text/json;charset=utf-8"');
$redis = new Redis();
$redis->connect('127.0.0.1', 6379);

//$t1 = microtime(true);

$mobile=1816308;

 $result=$redis->hGet('qqzeng-phone', $mobile);
  if(!empty($result)){
	  list($phone,$province, $city, $isp,$postcode,$citycode,$areacode) = explode("|", $result);	  
	  $arr =array(
				'code'=>0,
					'data'=> array( 
									'province'=>$province,
									'city'=>$city, 								
									'isp'=>$isp,
									'postcode'=>$postcode,
									'citycode'=>$citycode,
									'areacode'=>$areacode
								
							)); 
		echo   json_encode($arr); 
	}
	else
	{
		echo	'None';
	} 


//$t2 = microtime(true);
//echo '查询耗时'.($t2-$t1).'秒';
    
?>
