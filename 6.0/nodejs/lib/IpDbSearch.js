const fs = require('fs');
const path = require('path');

// 常量定义
const IndexStartIndex = 0x30004;
const EndMask = 0x800000;
const ComplMask = ~EndMask & 0xFFFFFF; // JS位运算是32位有符号，需确保掩码正确
const DbFileName = 'qqzeng-ip-6.0-global.db';

class IpDbSearch {
    constructor() {
        this.data = null;
        this.geoispArr = null;
        this.isLoaded = false;
        this._loadDb();
    }

    static getInstance() {
        if (!IpDbSearch.instance) {
            IpDbSearch.instance = new IpDbSearch();
        }
        return IpDbSearch.instance;
    }

    _loadDb() {
        try {
            const dbPath = this._findDbPath();
            if (!dbPath) {
                throw new Error(`Fatal: Cannot find ${DbFileName}`);
            }

            // 同步读取文件到 Buffer
            this.data = fs.readFileSync(dbPath);

            if (this.data.length < IndexStartIndex) {
                throw new Error('Invalid database file size');
            }

            // 读取节点数量 (小端序)
            const nodeCount = this.data.readUInt32LE(0);

            const stringAreaOffset = IndexStartIndex + nodeCount * 6;

            if (stringAreaOffset > this.data.length) {
                throw new Error('Invalid metadata');
            }

            // 解析字符串区
            const content = this.data.toString('utf8', stringAreaOffset);
            this.geoispArr = content.split('\t');

            this.isLoaded = true;
        } catch (err) {
            throw new Error(`Failed to initialize IpDbSearch: ${err.message}`);
        }
    }

    find(ip) {
        if (!ip) return "";
        let { prefix, suffix } = this._fastParseIp(ip);
        if (prefix === -1) return "";
        return this.findUint((prefix << 16) | suffix);
    }

    findUint(ipInt) {
        const prefix = (ipInt >>> 16) & 0xFFFF;
        let suffix = ipInt & 0xFFFF;

        let record = this._readInt24(4 + prefix * 3);

        while ((record & EndMask) !== EndMask) {
            const bit = (suffix >> 15) & 1;
            const offset = IndexStartIndex + record * 6 + bit * 3;
            record = this._readInt24(offset);
            suffix = (suffix << 1) & 0xFFFF;
        }

        const index = record & ComplMask;
        if (index < this.geoispArr.length) {
            return this.geoispArr[index];
        }
        return "";
    }

    _readInt24(offset) {
        return (this.data[offset] << 16) | (this.data[offset + 1] << 8) | this.data[offset + 2];
    }

    _fastParseIp(ip) {
        let val = 0;
        let result = 0;
        let shift = 24;
        let len = ip.length;

        for (let i = 0; i < len; i++) {
            const code = ip.charCodeAt(i);
            if (code >= 48 && code <= 57) { // '0'-'9'
                val = val * 10 + (code - 48);
            } else if (code === 46) { // '.'
                if (val > 255) return { prefix: -1 };
                result = (result | (val << shift)) >>> 0;
                val = 0;
                shift -= 8;
            } else {
                return { prefix: -1 };
            }
        }

        if (val > 255 || shift !== 0) return { prefix: -1 };
        result = (result | val) >>> 0;

        const prefix = (result >>> 16) & 0xFFFF;
        const suffix = result & 0xFFFF;

        return { prefix, suffix };
    }

    _findDbPath() {
        const attempts = [
            path.join(__dirname, DbFileName),
            path.join(process.cwd(), DbFileName),
            path.join(__dirname, '../data', DbFileName),
            path.join(__dirname, '../../data', DbFileName),
            path.join(__dirname, '../../../data', DbFileName),
            path.join(process.cwd(), '../data', DbFileName)
        ];

        for (const p of attempts) {
            if (fs.existsSync(p)) return p;
        }
        return null;
    }
}

module.exports = IpDbSearch;
