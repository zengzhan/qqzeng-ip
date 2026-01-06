/**
 * phone 區域工具
 * nodejs by hch_work
*/

"use strict";
const log = require('koa-log4').getLogger('Cache');
const fs = require('fs');

var dataBuffer = null;
var prefmap = [];   // 000 - 199
var phonemap = [];
var phoneArr = [];
var addrArr = [];
var ispArr = [];
// var prefixDict = {};
// var firstStartIpOffset;




/**
 * 加載二進制文件
*/
var loadBinaryData = function (filepath) {

    dataBuffer = fs.readFileSync(filepath);
    var PrefSize = dataBuffer.readUInt32LE(0);// 索引区第一条流位置
    var RecordSize = dataBuffer.readUInt32LE(4);// 前缀区第一条的流位置
    var descLength = dataBuffer.readUInt32LE(8);// 前缀区最后一条的流位置
    var ispLength = dataBuffer.readUInt32LE(12);// isp長度 

    // console.log(PrefSize, RecordSize, descLength, ispLength)
    // 內容數組
    var descOffset = 16 + PrefSize * 9 + RecordSize * 7;
    var descString  = dataBuffer.toString("utf-8", descOffset, descOffset + descLength);
    addrArr = descString.split('&');

    // 運營商數組
    var ispOffset = 16 + PrefSize * 9 + RecordSize * 7 + descLength;
    var ispString = dataBuffer.toString("utf-8", ispOffset, ispOffset + ispLength);
    ispArr = ispString.split('&');

    // 前綴區
    var m = 0;
    for (var k = 0; k < PrefSize; k++) {
        var i = 16 + k * 9;
        var n = dataBuffer.readUInt8(i);//无符号整型
        
        //var prefix = dataBuffer.readUInt8(i);//无符号整型
        var start_index = dataBuffer.readUInt32LE(i + 1);
        var end_index = dataBuffer.readUInt32LE(i + 5);
        prefmap[n] = { start_index: start_index, end_index: end_index };
        if(m < n)
        {
            for (; m < n; m++){
                prefmap[m] = { start_index: 0, end_index: 0 };
            }
            m++;
        }
        else{
            m++;
        }
    }

    // 索引區
    phoneArr = [];
    phonemap = [];
    for ( var i = 0; i < RecordSize; i++){
        var p = 16 + PrefSize * 9 + (i * 7);
        phoneArr[i] = dataBuffer.readUInt32LE(p);
        var start_index = dataBuffer.readUInt16LE(p + 4);
        var end_index = dataBuffer.readInt8(p + 6);
        phonemap[i] = { start_index: start_index, end_index: end_index }
    }
}

/**
 * 號段查詢
 * @param {7位或11位} phone 
 */
var Query = function(phone) {
    if(!phone || ((phone+"").length != 7 && (phone+"").length != 11))
    {
        return null;
    }
    var pref = parseInt((phone+"").substr(0, 3));   // 前7位
    var val = parseInt((phone+"").substr(0, 7));
    var low = prefmap[pref].start_index;
    var high = prefmap[pref].end_index;
    if(high == 0){ 
        return "";
    }

    var cur = low == high ? low : BinarySearch(low, high, val);
    if(cur != -1){
        return addrArr[phonemap[cur].start_index] + "|" + ispArr[phonemap[cur].end_index];
    }
    else{
        return "";
    }
}

/**
 * 二分算法
 */
var BinarySearch = function(low, high, key){
    var M = 0;
    while( low <= high){
        var mid = (low + high) >> 1;
        var phoneNum = phoneArr[mid];
        if (phoneNum >= key){
            M = mid;
            if(mid == 0){
                break;
            }
            high = mid - 1;
        }else{
            low = mid + 1;
        }
    }
    return M;
    // if(low > high){
    //     return -1;
    // }
    // else{
    //     var mid = (low + high) / 2;
    //     var phoneNum = phoneArr[mid];
    //     if (phoneNum == key)
    //         return mid;
    //     else if (phoneNum > key)
    //         return BinarySearch(low, mid - 1, key);
    //     else
    //         return BinarySearch(mid + 1, high, key);
    // }
}

exports.load = function (file) {
    if (dataBuffer === null) {
        loadBinaryData(file);      
    }   
}

exports.query = Query;
