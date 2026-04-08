<?php
 namespace mobile;
header('Content-Type: text/json;charset=utf-8"');
require  ("PhoneSearch6Db.php");


$phone = isset($_GET["phone"]) ? $_GET["phone"] : '1881234';

try {
    $search = PhoneSearch6Db::getInstance();
    $area_info = $search->query($phone);
    if (!empty($area_info)) {

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

    }else{
         // 这里可以处理不存在的情况
         echo "No data found for this phone number.";
    }
   
} catch (Exception $e) {  
    echo json_encode(['code' => 1, 'message' => $e->getMessage()]);
}

?>
