/*
 * qqzeng-ip 2.0 (.dat) Node.js 版冒烟测试
 *
 * 兼容旧版用法 (模块级单例): load(path) + query(ip)
 * 新写法参见 IpSearch.js 文件头注释: new IpSearch(path) / fromBytes()。
 */
var fs = require('fs');
var ipFinder = require('./IpSearch.js');

var dbFile = __dirname + '/qqzeng-ip.dat';
try {
    ipFinder.load(fs.existsSync(dbFile) ? dbFile : undefined);
} catch (e) {
    console.log('数据库未找到, 跳过 (把 .dat 放到本目录或 dada/ 下): ' + e.message);
    process.exit(0);
}

console.time('ip run time');

console.log(ipFinder.query('255.255.255.255'));
console.log(ipFinder.query('221.226.99.130'));
console.log(ipFinder.query('43.245.217.130'));
console.log(ipFinder.query('114.114.114.114'));
console.log(ipFinder.query('180.76.76.76'));
console.log(ipFinder.query('218.205.107.205'));
/* 非法输入必须返回空串 */
console.log(JSON.stringify(ipFinder.query('1..2.3')));
console.log(JSON.stringify(ipFinder.query('0008.8.8.8')));

console.timeEnd('ip run time');
console.log('ok');
