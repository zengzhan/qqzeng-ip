/**
 * qqzeng-ip 2.0 (.dat) 高性能解析 - Node.js 版
 *
 * 文件结构 (UTF-8, Little-Endian):
 *   文件头  16 字节 (4-4-4-4): [索引区首条偏移][索引区末条偏移][前缀区首偏移][前缀区末偏移]
 *   内容区  唯一不重复的地区字符串,按 (offset,len) 被索引区引用
 *   索引区  12 字节/条 (4-4-3-1): [起始IP][结束IP][地区偏移24b][长度8b]
 *   前缀区   9 字节/条 (1-4-4): [前缀0-255][索引区start][索引区end]
 *
 * 兼容旧版 API:
 *   require('./IpSearch').load(file)   -> 加载默认单例
 *   require('./IpSearch').query(ip)    -> 查询(未命中返回 '')
 * 新增多实例 API:
 *   const { IpSearch } = require('./IpSearch');
 *   const china = new IpSearch('qqzeng-ip-china-utf8.dat');
 *   const global = new IpSearch('qqzeng-ip-global-utf8.dat');
 *   IpSearch.fromBytes(buffer)         -> 从内存字节流加载
 *
 * 相比历史版本的修复与优化:
 *   1. 移除已废弃的 new Buffer(...)(旧版 ip2long 实际是错误用法),
 *      改为手写 fastParseIp 位运算严格校验(>>> 0 保持 32 位无符号)。
 *   2. slice() 改为 subarray();前缀对象字典改为固定 256 长度 TypedArray,
 *      缺失前缀以 -1 标记(修复 0.0.0.0/8 被误判未命中的 bug)。
 *   3. 未命中统一返回 ""(旧版返回 [],易引发调用方 undefined 拼接)。
 *   4. 索引预解析为 Uint32Array,字符串池去重常驻,查询零解码零分配。
 */

'use strict';

const fs = require('fs');
const path = require('path');

const DEFAULT_NAMES = [
  'qqzeng-ip-utf8.dat',
  'qqzeng-ip.dat',
  'qqzeng-ip-china-utf8.dat',
  'qqzeng-ip-global-utf8.dat',
];
const DEFAULT_DIRS = [
  '.',
  'dada/国内精华版-202610',
  'dada/全球旗舰版-202610',
  '../dada/国内精华版-202610',
  '../dada/全球旗舰版-202610',
  '../../dada/国内精华版-202610',
  '../../dada/全球旗舰版-202610',
];

class IpSearch {
  /**
   * @param {string} [dbPath] .dat 文件路径;缺省时自动探测常见位置。
   */
  constructor(dbPath) {
    if (dbPath === undefined || dbPath === null) {
      dbPath = IpSearch.findDbPath();
      if (!dbPath) throw new Error('IpSearch: no loadable .dat found');
    }
    this._load(fs.readFileSync(dbPath));
  }

  /** 从内存字节流(Buffer/Uint8Array)加载。 */
  static fromBytes(bytes) {
    const s = new IpSearch.__bare();
    s._load(bytes);
    return s;
  }

  static __bare() {
    return Object.create(IpSearch.prototype);
  }

  static findDbPath(startDir) {
    const dirs = startDir ? [startDir] : DEFAULT_DIRS;
    for (const d of dirs) {
      for (const n of DEFAULT_NAMES) {
        const p = path.join(d, n);
        if (fs.existsSync(p)) return p;
      }
    }
    return null;
  }

  /** @private */
  _load(data) {
    if (!data || data.length < 16) throw new Error('database too small');

    const firstIndex = data.readUInt32LE(0);
    const lastIndex = data.readUInt32LE(4);
    const prefStart = data.readUInt32LE(8);
    const prefEnd = data.readUInt32LE(12);

    if (firstIndex < 16 || lastIndex < firstIndex || (lastIndex - firstIndex) % 12 !== 0
        || prefEnd < prefStart || prefStart < 16 || (prefEnd - prefStart) % 9 !== 0) {
      throw new Error('bad header');
    }

    const recordCount = (lastIndex - firstIndex) / 12 + 1;
    const prefixCount = (prefEnd - prefStart) / 9 + 1;
    if (firstIndex + 12 * recordCount > data.length
        || prefStart + 9 * prefixCount > data.length) {
      throw new Error('header exceeds file size');
    }

    this._prefLo = new Int32Array(256).fill(-1);
    this._prefHi = new Int32Array(256).fill(-1);
    for (let k = 0; k < prefixCount; k++) {
      const o = prefStart + k * 9;
      const n = data.readUInt8(o);
      const lo = data.readUInt32LE(o + 1);
      const hi = data.readUInt32LE(o + 5);
      if (lo > hi || hi >= recordCount) throw new Error('bad prefix range');
      this._prefLo[n] = lo;
      this._prefHi[n] = hi;
    }

    this._starts = new Uint32Array(recordCount);
    this._ends = new Uint32Array(recordCount);
    this._strIdx = new Uint32Array(recordCount);

    /* 字符串池去重: key = offset*256 + len */
    const seen = new Map();
    const strings = [];
    for (let i = 0; i < recordCount; i++) {
      const o = firstIndex + i * 12;
      const off = data.readUIntLE(o + 8, 3);
      const len = data.readUInt8(o + 11);
      if (off + len > data.length) throw new Error('string region out of range');
      this._starts[i] = data.readUInt32LE(o);
      this._ends[i] = data.readUInt32LE(o + 4);
      const key = off * 256 + len;
      let id = seen.get(key);
      if (id === undefined) {
        id = strings.length;
        strings.push(data.toString('utf8', off, off + len));
        seen.set(key, id);
      }
      this._strIdx[i] = id;
    }
    this._strings = strings;
  }

  /**
   * 严格 IPv4 解析: 每段 1~3 位数字且 <=255,恰好 4 段。
   * 非法返回 -1(>=0 为合法 uint32)。
   */
  static fastParseIp(ip) {
    if (typeof ip !== 'string') return -1;
    let val = 0;
    let result = 0;
    let shift = 24;
    let digits = 0;
    for (let i = 0; i < ip.length; i++) {
      const c = ip.charCodeAt(i);
      if (c >= 48 && c <= 57) {          /* '0'-'9' */
        val = val * 10 + (c - 48);
        if (val > 255) return -1;
        digits++;
        if (digits > 3) return -1; /* 拒绝 "0008.8.8.8" 这类超长前导零 */
      } else if (c === 46) {             /* '.' */
        if (digits === 0 || shift === 0) return -1; /* 空段或第 5 段 */
        result = (result | (val << shift)) >>> 0;
        val = 0;
        digits = 0;
        shift -= 8;
      } else {
        return -1;
      }
    }
    if (digits === 0 || shift !== 0) return -1;
    return (result | val) >>> 0;
  }

  /** 字符串 IP 查询: 未命中/非法返回 ""。 */
  find(ip) {
    const v = IpSearch.fastParseIp(ip);
    if (v < 0) return '';
    return this.findUint(v);
  }

  /** 兼容旧版命名。 */
  query(ip) { return this.find(ip); }

  /** 整数查询(跳过字符串解析,高性能场景)。 */
  findUint(val) {
    const p = (val >>> 24) & 0xff;
    const lo = this._prefLo[p];
    if (lo < 0) return ''; /* 前缀不存在(不能以 high==0 判未命中) */
    const hi = this._prefHi[p];
    let idx;
    if (lo === hi) {
      idx = lo;
    } else {
      /* 二分逼近: 最小的 endip >= val */
      let l = lo;
      let h = hi;
      idx = 0;
      while (l <= h) {
        const mid = l + ((h - l) >>> 1);
        if (this._ends[mid] >= val) {
          idx = mid;
          if (mid === 0) break;
          h = mid - 1;
        } else {
          l = mid + 1;
        }
      }
    }
    if (this._starts[idx] <= val && this._ends[idx] >= val) {
      return this._strings[this._strIdx[idx]];
    }
    return '';
  }
}

/* ---- 兼容旧版模块级单例 API: exports.load(file) / exports.query(ip) ---- */
let instance = null;

exports.IpSearch = IpSearch;

exports.load = function (file) {
  if (instance === null) {
    instance = file ? new IpSearch(file) : new IpSearch();
  }
};

exports.query = function (ip) {
  if (instance === null) {
    const p = IpSearch.findDbPath();
    if (!p) return '';
    instance = new IpSearch(p);
  }
  return instance.find(ip);
};

module.exports = exports;
