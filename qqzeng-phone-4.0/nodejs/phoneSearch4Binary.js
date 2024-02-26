/**
 * 手机归属地查询解析 4.0 内存版
 * nodejs by qqzeng-ip
*/
// phoneSearch4Binary.js

const fs = require('fs');
const path = require('path');

class PhoneSearch4Binary {
    constructor() {
        this.loadDat();
    }

    loadDat() {
        const datPath = path.join(__dirname, 'qqzeng-phone-4.0.dat');
        this.data = fs.readFileSync(datPath);

        const PrefSize = this.readUInt32(0);
        const descLength = this.readUInt32(8);
        const ispLength = this.readUInt32(12);
        const headLength = 20;
        const startIndex = headLength + descLength + ispLength;

        //内容数组        
        const descString = this.readString(headLength, descLength);
        this.addrArr = descString.split('&');

        //运营商数组        
        const ispString = this.readString(headLength + descLength, ispLength);
        this.ispArr = ispString.split('&');

        this.prefDict = {};

        for (let m = 0; m < PrefSize; m++) {
            const i = m * 5 + startIndex;
            const pref = this.data[i];
            const index = this.readUInt32(i + 1);
            this.prefDict[pref.toString()] = index;
        }
    }

    readUInt32(offset) {
        return this.data.readUInt32LE(offset);
    }

    readString(offset, length) {
        return this.data.toString('utf8', offset, offset + length);
    }

    query(phone) {
        const prefix = phone.substring(0, 3);
        const suffix = parseInt(phone.substring(3, 7), 10);
        let addrispIndex = 0;

        const start = this.prefDict[prefix];
        if (start !== undefined) {
            const p = start + suffix * 2;
            addrispIndex = this.data.readUInt16LE(p);
        }

        if (addrispIndex === 0) {
            return "不存在";
        }

        return this.addrArr[addrispIndex >> 5] + "|" + this.ispArr[addrispIndex & 0x001F];
    }
}

module.exports = PhoneSearch4Binary;
