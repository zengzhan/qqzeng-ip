



mod qqzeng_ip_search;
fn main() {

 
  // 创建IPSearch3Span实例
  let ip_search = qqzeng_ip_search::IP_SEARCH.lock().unwrap();
  // 查询ip地址
  let ip_str = "103.22.180.95";
  let location = ip_search.query(ip_str);

  // 输出结果

  println!("The location of {} is {}", ip_str, location);
  //亚洲|泰国|曼谷大都会|曼谷||||Thailand|TH|100.5167|13.75
  

}
