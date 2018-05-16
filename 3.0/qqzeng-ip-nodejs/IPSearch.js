// 高性能IP数据库格式详解 qqzeng-ip-3.0-ultimate.dat
// 编码：UTF8  字节序：Little - Endian
// 返回多个字段信息（如：亚洲 | 中国 | 香港 | 九龙 | 油尖旺 | 新世界电讯 | 810200 | Hong Kong| HK | 114.17495 | 22.327115）
// https://github.com/zengzhan/qqzeng-ip

"use strict";
var fs = require('fs');

var data = null;
var prefStart = [256];
var prefEnd = [256];
var endArr = [];
var addrArr = [];

var loadBinaryData = function (filepath) {

    data = fs.readFileSync(filepath);
    var RecordSize = data.readUInt32LE(0);
    for (var k = 0; k < 256; k++) {
        var i = k * 8 + 4;
        prefStart[k] = data.readUInt32LE(i);
        prefEnd[k] = data.readUInt32LE(i + 4);
    }

    endArr = [RecordSize];
    addrArr = [RecordSize];
    for (var i = 0; i < RecordSize; i++) {
        var p = 2052 + (i * 8);
        endArr[i] = data.readUInt32LE(p);
        var offset = data.readUIntLE(4 + p, 3);//3 bit 无符号整型
        var length = data.readUInt8(7 + p);//1 bit 无符号整型      
        addrArr[i] = data.slice(offset, offset + length).toString('utf-8');
    }


};


var Query = function (ip) {
    var ipArray = ip.split('.'), ipInt = ip2long(ip), pref = parseInt(ipArray[0]);
    var low = prefStart[pref], high = prefEnd[pref];
    var cur = low == high ? low : BinarySearch(low, high, ipInt);
    return addrArr[cur];
}


var BinarySearch = function (low, high, k) {
    var M = 0;
    while (low <= high) {
        var mid = Math.floor((low + high) / 2);
        var endipNum = endArr[mid];
        if (endipNum >= k) {
            M = mid;
            if (mid === 0) {
                break;   //防止溢出
            }
            high = mid - 1;
        }
        else
            low = mid + 1;
    }
    return M;
}

var ip2long = function (ip) { return new Buffer(ip.split('.')).readUInt32BE(0) }

exports.load = function (file) {
    if (data === null) {
        loadBinaryData(file);
    }
}


exports.query = Query;
