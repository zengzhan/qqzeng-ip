/**
 * 手机归属地查询解析 3.0 内存版
 * nodejs by qqzeng-ip
*/

"use strict";
import { readFileSync } from 'fs';
import path from 'path';

var dataBuffer = null;
var phone2D = [];
var addrArr = [];
var ispArr = [];

/**
 * 初始化dat数据
*/
var loadBinaryData = function (filepath) {

    dataBuffer = readFileSync(filepath);
    var prefNum = dataBuffer.readUInt32LE(0);// 前缀数量
    var phoneNum = dataBuffer.readUInt32LE(4);// 号段数量
    var addrLen = dataBuffer.readUInt32LE(8);// 地区信息字节长度
    var ispLen = dataBuffer.readUInt32LE(12);// 运营商字节长度 
    var version = dataBuffer.readUInt32LE(16);// 发行版本

    var headLen = 20;   //文件头长度
    var startIndex = headLen + addrLen + ispLen;

    // 地区数组   
    var addrStr = dataBuffer.toString("utf-8", headLen, headLen + addrLen);
    addrArr = addrStr.split('&');

    // 运营商数组  
    var ispString = dataBuffer.toString("utf-8", headLen + addrLen, startIndex);
    ispArr = ispString.split('&');

    //二维数组  

    for (var m = 0; m < prefNum; m++) {
        var i = m * 7 + startIndex;
        var pref = dataBuffer.readUInt8(i);
        var index = dataBuffer.readUInt32LE(i + 1);
        var length = dataBuffer.readUInt16LE(i + 5);
        phone2D[pref] = [];
        for (var n = 0; n < length; n++) {
            var p = startIndex + prefNum * 7 + (n + index) * 4;
            var suff = dataBuffer.readUInt16LE(p);
            var addrispIndex = dataBuffer.readUInt16LE(p + 2);

            phone2D[pref][suff] = addrispIndex;
        }

    }


}

/**
 * 手机归属地查询
 * @param {7位或11位} phone 
 */
export const query = function (phone) {
    var pref = parseInt((phone + "").substring(0, 3)); //前三位
    var suffix = parseInt((phone + "").substring(3, 7)); //后四位
    var addrispIndex = phone2D[pref] != null ? phone2D[pref][suffix] ?? 0 : 0;

    if (addrispIndex == 0) {
        return "";
    }
    return addrArr[parseInt(addrispIndex / 100)] + "|" + ispArr[addrispIndex % 100];
}


export const load = function () {
    if (dataBuffer === null) {
        var file = path.resolve() + '/qqzeng-phone-3.0.dat';
        loadBinaryData(file);
    }
}


