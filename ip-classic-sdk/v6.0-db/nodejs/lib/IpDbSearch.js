'use strict';

/**
 * qqzeng-ip 6.0 (.db) 高性能解析 - Node.js 版 (零依赖)
 *
 * 文件结构 (UTF-8):
 *   文件头    4 字节: 节点数量 nodeCount (uint32 小端)
 *   一级索引  65536 条 x 3 字节 (大端 int24): IPv4 前 16 位 -> 节点号或叶子
 *   节点区    nodeCount x 6 字节 (每节点 2 个子指针, 各 3 字节大端 int24)
 *   字符串区  其余部分, '\t' 分隔的 UTF-8 结果串
 *   int24 最高位 (0x800000) 置位 = 叶子, 低 22 位为字符串下标;
 *   否则为节点区跳转下标。查询消耗 IP 后 16 位 (MSB first)。
 *
 * 兼容旧版 API:
 *   const IpDbSearch = require('./lib/IpDbSearch');
 *   IpDbSearch.getInstance().find('1.2.3.4') / findUint(ipInt)
 * 新增:
 *   new IpDbSearch(dbPath) 多实例 / IpDbSearch.fromBytes(buffer)
 *
 * 相比历史版本的修复与优化:
 *   1. fastParseIp 严格化: 旧版接受 "1..2.3"/"1.2.3." 等残缺 IP;
 *      现按统一严格规则校验 (每段 1~3 位且 <=255, 恰好 4 段)。
 *   2. 加载期完整校验 (fail-closed): 尺寸/头字段/全部一级索引与节点跳转
 *      合法性/叶子下标范围, 非法文件抛 Error。
 *   3. 逐查询 int24 拼装改为 Uint32Array 预解析 (一级索引 + 节点区),
 *      查询路径零拼装; 原始 Buffer 加载后可被 GC 回收。
 *   4. 路径探测补充仓库 dada/ 目录。
 */

const fs = require('fs');
const path = require('path');

const INDEX_START = 0x30004;
const FIRST_COUNT = 65536;
const END_MASK = 0x800000;
const COMPL_MASK = 0x7fffff;
const DB_FILE_NAME = 'qqzeng-ip-6.0-global.db';

class IpDbSearch {
  /**
   * @param {string} [dbPath] 缺省时自动探测默认路径
   */
  constructor(dbPath) {
    let buf;
    if (dbPath === undefined || dbPath === null || dbPath === '') {
      const p = IpDbSearch.findDbPath();
      if (!p) throw new Error(`Fatal: Cannot find ${DB_FILE_NAME}`);
      buf = fs.readFileSync(p);
    } else {
      buf = fs.readFileSync(dbPath);
    }
    this._parse(buf);
  }

  static getInstance() {
    if (!IpDbSearch.instance) {
      IpDbSearch.instance = new IpDbSearch();
    }
    return IpDbSearch.instance;
  }

  /** 从内存字节流加载 (Buffer / Uint8Array)。 */
  static fromBytes(buf) {
    const s = Object.create(IpDbSearch.prototype);
    s._parse(buf);
    return s;
  }

  /** 严格 IPv4 解析: 每段 1~3 位数字且 <=255, 恰好 4 段。非法返回 -1。 */
  static fastParseIp(ip) {
    if (typeof ip !== 'string') return -1;
    let val = 0;
    let result = 0;
    let shift = 24;
    let digits = 0;
    for (let i = 0; i < ip.length; i++) {
      const c = ip.charCodeAt(i);
      if (c >= 48 && c <= 57) { // '0'-'9'
        val = val * 10 + (c - 48);
        if (val > 255) return -1;
        digits++;
        if (digits > 3) return -1; // 拒绝 "0008.8.8.8" 这类超长前导零
      } else if (c === 46) { // '.'
        if (digits === 0 || shift === 0) return -1; // 空段或第 5 段
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

  /** 探测默认数据库路径 (当前目录/模块目录/data/dada)。 */
  static findDbPath() {
    const dirs = [
      '.',
      'data',
      '../data',
      '../../data',
      '../../../data',
      'dada/全球旗舰版-202610',
      '../dada/全球旗舰版-202610',
      '../../dada/全球旗舰版-202610',
      '../../../dada/全球旗舰版-202610',
    ];
    const bases = [process.cwd(), __dirname];
    for (const base of bases) {
      for (const d of dirs) {
        const p = d === '.' ? path.join(base, DB_FILE_NAME) : path.join(base, d, DB_FILE_NAME);
        try {
          if (fs.statSync(p).isFile()) return p;
        } catch (_) { /* continue */ }
      }
    }
    return null;
  }

  _parse(buf) {
    if (!buf || buf.length < INDEX_START) {
      throw new Error('Invalid database file size');
    }
    const nodeCount = buf.readUInt32LE(0);
    if (nodeCount === 0) throw new Error('Invalid node count');
    const nodeAreaEnd = INDEX_START + nodeCount * 6;
    if (nodeAreaEnd > buf.length) throw new Error('Node area exceeds file size');

    // 字符串表 (一次性解码切分, 后续查询零拷贝引用)
    const content = buf.toString('utf8', nodeAreaEnd);
    const geoisp = content.split('\t');
    const stringCount = geoisp.length;

    const entryOK = (v) =>
      (v & END_MASK) !== 0 ? (v & COMPL_MASK) < stringCount : v < nodeCount;

    // 一级索引 + 节点区预解析为 Uint32Array, 边解析边校验 (fail-closed)
    const first = new Uint32Array(FIRST_COUNT);
    for (let i = 0; i < FIRST_COUNT; i++) {
      const o = 4 + i * 3;
      const v = (buf[o] << 16) | (buf[o + 1] << 8) | buf[o + 2];
      if (!entryOK(v)) throw new Error('Bad first-level index entry');
      first[i] = v;
    }
    const nodes = new Uint32Array(nodeCount * 2);
    for (let n = 0, o = INDEX_START; n < nodeCount; n++, o += 6) {
      const a = (buf[o] << 16) | (buf[o + 1] << 8) | buf[o + 2];
      const b = (buf[o + 3] << 16) | (buf[o + 4] << 8) | buf[o + 5];
      if (!entryOK(a) || !entryOK(b)) throw new Error('Bad node jump');
      nodes[n * 2] = a;
      nodes[n * 2 + 1] = b;
    }

    this._first = first;
    this._nodes = nodes;
    this._geoisp = geoisp;
    this._nodeCount = nodeCount;
  }

  /** 查询 IP 字符串, 未命中/非法返回 ''。 */
  find(ip) {
    const v = IpDbSearch.fastParseIp(ip);
    if (v < 0) return '';
    return this.findUint(v);
  }

  /** 整数查询 (uint32)。 */
  findUint(ipInt) {
    const first = this._first;
    const nodes = this._nodes;
    let record = first[ipInt >>> 16];
    let suffix = ipInt & 0xffff;
    let hops = 0;
    // 后缀 16 位 -> 最多 16 跳; 超限说明文件非法 (fail-closed)
    while ((record & END_MASK) === 0) {
      if (hops >= 16) return '';
      hops++;
      record = nodes[record * 2 + ((suffix >> 15) & 1)];
      suffix = (suffix << 1) & 0xffff;
    }
    const index = record & COMPL_MASK;
    const arr = this._geoisp;
    return index < arr.length ? arr[index] : '';
  }

  get nodeCount() {
    return this._nodeCount;
  }

  get stringCount() {
    return this._geoisp.length;
  }
}

module.exports = IpDbSearch;
module.exports.IpDbSearch = IpDbSearch;
module.exports.default = IpDbSearch;
