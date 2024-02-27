



mod qqzeng_phone_search;
fn main() {

  // 创建单例实例
  let mut phone_search_instance = qqzeng_phone_search::PhoneSearch4Binary::new();

  // 加载数据
  let _ = phone_search_instance.load_dat();

  // 使用单例实例进行查询
  let result = phone_search_instance.query("1927105");
  println!("{}", result);
  //1927105->北京|北京|100000|010|110100|中国广电

}
