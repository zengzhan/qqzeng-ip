<?php
//phpinfo();

ini_set('memory_limit','1024M');

$redis = new Redis();
$redis->connect('127.0.0.1', 6379);

//批量导入 更快
$redis->multi(Redis::PIPELINE);
//清空ip库
$redis->delete('qqzengip');
//批量导入
$arr = file('全球旗舰版-201808-368729.txt');
$data = array();
if($arr) {
    $i=0;
    foreach($arr as $line) {   
        $line=str_replace(array("\n","\r"),"",$line);
        list($start,$end,$startnum,$endnum,$continent,$country,$province, $city, $district,$isp,$areacode,$en,$cc,$lng,$lat) = explode("|", $line);
        // $redis->zAdd('qqzengip',16781311, '亚洲|中国|广东|深圳|南山|电信|440100|China|CN|113.280637|23.125178'); 自由定义字段  
        $redis->zAdd('qqzengip',intval($endnum),$line); 
       
        $i++;
        if( $i%10000==0){
            echo  $i.'<br>';
        }
        
    }
}
$redis->exec();
//统计数量
echo '数量：'.$redis->zCount('qqzengip', "-inf", "+inf").'<br>';
//版本
$result=$redis->zRangeByScore('qqzengip', 4294967295, 4294967295, array('limit' => array(0, 1)));
echo '版本：'.$result[0];
$redis->close();
?>
