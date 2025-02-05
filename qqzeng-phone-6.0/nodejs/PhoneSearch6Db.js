const fs = require('fs');
const path = require('path');

const HeaderSize = 32;
const PrefixCount = 200;
const BitmapPopCountOffset = 0x4E2;

class PhoneSearch6Db {
  constructor() {
    this.data = Buffer.alloc(0); // 用于存储数据库文件的二进制数据
    this.regionIsps = []; // 地区-运营商组合
    this.index = new Array(PrefixCount).fill({ BitmapOffset: 0, DataOffset: 0 });
    this.loadDatabase();
  }

  // 加载并解析数据库文件
  loadDatabase() {
    const filePath = path.join(__dirname, 'qqzeng-phone-6.0.db');
    if (!fs.existsSync(filePath)) throw new Error(`Database file not found: ${filePath}`);

    try {
      // 读取数据库文件为 Buffer
      this.data = fs.readFileSync(filePath);
      const span = this.data;

      // 解析头部（小端序）
      const header = [];
      for (let i = 0; i < 8; i++) {
        header[i] = span.readUInt32LE(i * 4);
      }

      // 解析地区与运营商表
      const regionsStart = HeaderSize;
      const ispsStart = regionsStart + header[1];
      const indexStart = ispsStart + header[2];

      const regions = span.toString('utf8', regionsStart, regionsStart + header[1]).split('&');
      const isps = span.toString('utf8', ispsStart, ispsStart + header[2]).split('&');

      // 构建地区-运营商组合
      this.regionIsps = new Array(header[4]);
      const entryOffset = header[3];
      for (let i = 0; i < this.regionIsps.length; i++) {
        const entry = span.readUInt16LE(entryOffset + i * 2);
        this.regionIsps[i] = `${regions[entry >> 5]}|${isps[entry & 0x1F]}`;
      }

      // 构建前缀索引表
      let pos = indexStart;
      for (let i = 0; i < PrefixCount; i++) {
        const prefix = span.readUInt32LE(pos);
        if (prefix === i) {
          this.index[i] = {
            BitmapOffset: span.readUInt32LE(pos + 4),
            DataOffset: span.readUInt32LE(pos + 8),
          };
          pos += 12;
        }
      }
    } catch (err) {
      throw new Error(`Invalid database format: ${err.message}`);
    }
  }

  // 查询电话号码归属地信息
  query(phone) {
    if (phone.length !== 7) {
      throw new Error('Invalid phone number format');
    }

    // 解析前缀和后四位
    const prefix = this.parsePhoneSegment(phone.slice(0, 3));
    const subNum = this.parsePhoneSegment(phone.slice(3, 7));

    // 前缀有效性检查
    if (prefix < 0 || prefix >= PrefixCount) return null;

    // 获取索引条目
    const { BitmapOffset, DataOffset } = this.index[prefix];
    if (BitmapOffset === 0 || DataOffset === 0) return null;

    // 位图检查
    const byteIndex = subNum >> 3;
    const bitIndex = subNum & 0b0111;

    if (BitmapOffset + byteIndex >= this.data.length) return null;

    const bitmap = this.data.readUInt8(BitmapOffset + byteIndex);
    if ((bitmap & (1 << bitIndex)) === 0) return null;

    // 计算有效数据位置
    const popCountOffset = BitmapOffset + BitmapPopCountOffset + (byteIndex << 1);
    const preCount = this.data.readUInt16LE(popCountOffset);
    const localCount = this.countSetBits(bitmap & ((1 << bitIndex) - 1));

    // 定位最终数据
    const dataPos = DataOffset + ((preCount + localCount) << 1);
    const entry = this.data.readUInt16LE(dataPos);

    return entry < this.regionIsps.length ? this.regionIsps[entry] : null;
  }

  // 将电话号码段转换为数字
  parsePhoneSegment(segment) {
    let result = 0;
    for (const char of segment) {
      result = result * 10 + (char.charCodeAt(0) - '0'.charCodeAt(0));
    }
    return result;
  }

  // 计算有效位的数量（popcount）
  countSetBits(bitmap) {
    let count = 0;
    while (bitmap > 0) {
      count++;
      bitmap &= bitmap - 1;
    }
    return count;
  }
}

module.exports = PhoneSearch6Db;
