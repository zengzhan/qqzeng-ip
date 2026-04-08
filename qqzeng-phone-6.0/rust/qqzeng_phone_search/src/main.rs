mod qqzeng_phone_search;

use qqzeng_phone_search::PhoneSearch6Db;


fn main() -> Result<(), Box<dyn std::error::Error>> {
    // 初始化数据库
    let db = PhoneSearch6Db::instance();

    // 测试查询有效号码
    if let Some(result) = db.query("1933795") {
        println!("Query Result for '1234567': {}", result);//河南|焦作|454000|0391|410800|中国电信
    } else {
        println!("No result found for '1234567'");
    }

    // 测试查询无效号码
    if let Some(result) = db.query("9999999") {
        println!("Query Result for '9999999': {}", result);
    } else {
        println!("No result found for '9999999'");
    }

    Ok(())
}
