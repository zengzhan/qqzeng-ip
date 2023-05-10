



mod qqzeng_phone_search;
fn main() {

  let phone_search = qqzeng_phone_search::PHONE_SEARCH.lock().unwrap();
  // 查询号段归属地
  let phone = "13382401245";
  let location = phone_search.query(phone);

  // 输出结果

  println!("The location of {} is {}", phone, location);
  //江苏|南京|210000|025|320100|电信
  

}
