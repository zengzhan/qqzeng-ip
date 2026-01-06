<?php
//phpinfo();

ini_set('memory_limit','1024M');

$redis = new Redis();
$redis->connect('127.0.0.1', 6379);

//批量导入 更快
$redis->multi(Redis::PIPELINE);
//清空号段库
$redis->delete('qqzeng-phone');
//批量导入
$arr = file('phone-qqzeng-201808-411856.txt');
$data = array();
if($arr) {
    $i=0;
    foreach($arr as $line) {   
        $line=str_replace(array("\n","\r"),"",$line);
        list($pref,$phone,$province, $city, $isp,$postcode,$citycode,$areacode) = explode("\t", $line);     
      
      //  $redis->zAdd('qqzeng-phone',intval($phone),$phone.'|'.$province.'|'.$city.'|'.$isp.'|'.$postcode.'|'.$citycode.'|'.$areacode); 
  
      //  $redis->hSet('qqzeng-phone',1364426, '136|1364426|辽宁|大连|移动|116000|0411|210200'); 自由定义字段  

      // 在号段归属地查询中 Hashes 存储 比 Sorted 更快   ！  Sorted 适合在IP数据库 查询 ！！！

        $redis->hSet('qqzeng-phone',intval($phone),$phone.'|'.$province.'|'.$city.'|'.$isp.'|'.$postcode.'|'.$citycode.'|'.$areacode); 
       
        $i++;
        if( $i%10000==0){
            echo  $i.'<br>';
        }
        
    }
}
$redis->exec();

//统计数量
//echo 'Sorted数量：'.$redis->zCount('qqzeng-phone', "-inf", "+inf").'<br>';

echo 'Hashes数量：'.$redis->hLen('qqzeng-phone').'<br>';

$redis->close();
?>
