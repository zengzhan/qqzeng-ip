
// 使用模块的文件
const PhoneSearch4Binary = require('./phoneSearch4Binary');

const instance = new PhoneSearch4Binary();
const result = instance.query("1886878");
console.log(result);

//1886878-> 浙江|杭州|310000|0571|330100|中国移动


//测试命令  node app.js
