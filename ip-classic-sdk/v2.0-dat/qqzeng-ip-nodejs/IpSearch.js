// 高性能IP数据库格式详解 qqzeng-ip.dat
// 编码：UTF8  字节序：Little - Endian
// 返回多个字段信息（如：亚洲 | 中国 | 香港 | 九龙 | 油尖旺 | 新世界电讯 | 810200 | Hong Kong| HK | 114.17495 | 22.327115）
// https://github.com/zengzhan/qqzeng-ip

"use strict";
var fs = require('fs');

var dataBuffer = null;
var prefixDict = {};
var firstStartIpOffset;

var loadBinaryData = function (filepath) {
   
    dataBuffer = fs.readFileSync(filepath);
    firstStartIpOffset = dataBuffer.readUInt32LE(0);//索引区第一条流位置
    var prefixStartOffset = dataBuffer.readUInt32LE(8);//前缀区第一条的流位置
    var prefixEndOffset = dataBuffer.readUInt32LE(12);//前缀区最后一条的流位置
    var prefixCount = (prefixEndOffset - prefixStartOffset) / 9 + 1; //前缀区块每组 9字节; 

    for (var k = 0; k < prefixCount; k++) {
        var i = prefixStartOffset + k * 9;
        var prefix = dataBuffer.readUInt8(i);//无符号整型
        var start_index = dataBuffer.readUInt32LE(i + 1);
        var end_index = dataBuffer.readUInt32LE(i + 5);
        prefixDict[prefix] = { start_index: start_index, end_index: end_index };
    }
   
};


var Query = function (ip) {
    var ipArray = ip.split('.'), ipInt = ip2long(ip), ip_prefix_value = parseInt(ipArray[0]);
    var high = 0, low = 0;
    var prefix_data = prefixDict[ip_prefix_value];

    if (prefix_data) {
        low = prefix_data.start_index;
        high = prefix_data.end_index;
    }
    else {
        return [];
    }

    var my_index = low === high ? low : BinarySearch(low, high, ipInt);
    var index_data = GetIndex(my_index);
    if ((index_data.start_num <= ipInt) && (index_data.end_num >= ipInt)) {
        return GetLocal(index_data.local_offset, index_data.local_length);
    }
    else {
        return [];
    }

}

var GetLocal = function (local_offset, local_length) {
    return dataBuffer.slice(local_offset, local_offset + local_length).toString('utf-8');
    //如果返回数组 结果加上 split('|')
}

var GetIndex = function (left) {
    var left_offset = firstStartIpOffset + (left * 12);
    var start_num = dataBuffer.readUInt32LE(left_offset);
    var end_num = dataBuffer.readUInt32LE(4 + left_offset);
    var local_offset = dataBuffer.readUIntLE(8 + left_offset,3);//3 bit 无符号整型
    var local_length = dataBuffer.readUInt8(11 + left_offset);//1 bit 无符号整型
    return { start_num: start_num, end_num: end_num, local_offset: local_offset, local_length: local_length}
}

var GetEndIp = function (left) {   
    var left_offset = firstStartIpOffset + (left * 12);
    return dataBuffer.readUInt32LE(4 + left_offset);
}


var BinarySearch = function (low, high, k) {
    var M = 0;
    while (low <= high) {
        var mid = Math.floor((low + high) / 2);
        var endipNum = GetEndIp(mid);
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
    if (dataBuffer === null) {
        loadBinaryData(file);      
    }   
}

exports.query = Query;
