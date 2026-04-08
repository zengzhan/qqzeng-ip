
// 使用模块的文件
const PhoneSearch6Db = require('./PhoneSearch6Db');

const instance = new PhoneSearch6Db();

const result = instance.query("1800000");
console.log(result);

//1886878-> 浙江|杭州|310000|0571|330100|中国移动


//测试命令  node app.js
