'use strict';

/*
 * QZDB Node.js SDK —— 纯离线、零依赖、高性能 IP 地理定位数据库读取器。
 *
 * 严格遵循 multi-lang/API_CONTRACT.md（v2.4，唯一事实来源）：
 *   - SENTINEL 高位哨兵位在解析前剥离（§8.1）
 *   - 原生浮点 6 位小数格式（§8.2），toPipe 直接拼接已格式化字符串（§8.3）
 *   - IPv4-Mapped IPv6 自动降级（§8.4）
 *   - Fail-Closed：非法 Magic/Header/CRC/截断构造即拒绝（§8.5）
 *   - CIDR 由 Trie 叶子深度重建（§8.6）
 *   - 未命中/非法 IP 返回 null（Node 约定，§4）
 */

const fs = require('fs');

// ===========================================================================
// 常量
// ===========================================================================
const SENTINEL = 0x80000000;        // 32 位节点哨兵位
const SENTINEL_24 = 0x800000;       // 24 位节点哨兵位
const SENTINEL_MASK_31 = 0x7FFFFFFF;
const SENTINEL_MASK_24 = 0x7FFFFF;
// 派生步数上限（取代魔法常量 1000）：正确 Patricia 树深度不可能超过
// IP 地址位数 + 8（root 余量）；超过即视为敌对文件，拒绝（返回 miss）
// 是正确的 fail-closed 行为。IPv4 = max(32+8, 40) = 40，IPv6 = max(128+8, 40) = 136。
// 本文件 V4 游走使用此上限；V6 游走由 `while (depth < 128)` 构造性有界，无需步数上限。
const V4_ADDR_BITS = 32;
const MAX_TRIE_WALK_STEPS = V4_ADDR_BITS + 8; // = max(32+8, 40) = 40，IPv4 路径使用
const MAX_POOL_COUNT = 1 << 26;
const GEO_CACHE_CAP = 1 << 16;      // per-snapshot 有界 GeoInfo 缓存容量

// ---------------------------------------------------------------------------
// 版本档次与字段名的自描述契约（FORMAT §10.3）
//
// 文件自己就说明了它是什么：Header.VersionMask（偏移 6）与每个
// GROUP_SCHEMA 组头的 groupId 都是同一套 one-hot 版本位掩码，这才是权威信号，
// 字段个数只是最后兜底。EDITION_BY_BIT 按 bit0..bit4 顺序给出档次名。
// ---------------------------------------------------------------------------
const EDITION_BY_BIT = ['std', 'asn', 'pro', 'max', 'ult']; // bit0..bit4

// 各档次的规范字段顺序（FORMAT 附录 1）。仅在文件没有 Metadata field_names
// 时使用；永远不覆盖文件自带的字段名。
const EDITION_FIELD_NAMES = {
  std: ['continent', 'country_code', 'country', 'province', 'city', 'isp'],
  asn: ['continent', 'country_code', 'country', 'isp', 'asn', 'as_name',
    'as_domain', 'usage_type'],
  pro: ['continent', 'country_code', 'country', 'province', 'city', 'district',
    'geo_id', 'longitude', 'latitude', 'timezone', 'isp'],
  max: ['continent', 'country_code', 'country', 'province', 'city', 'district',
    'geo_id', 'longitude', 'latitude', 'timezone', 'isp', 'asn', 'as_name',
    'as_domain', 'usage_type'],
  ult: ['continent', 'continent_en', 'country_code', 'country_alpha3', 'country',
    'country_en', 'province', 'province_en', 'city', 'city_en', 'district',
    'district_en', 'geo_id', 'longitude', 'latitude', 'timezone', 'languages',
    'currency_code', 'phone_prefix', 'emoji_flag', 'isp', 'asn', 'as_name',
    'as_domain', 'usage_type'],
};

// 来源标记。8 种语言使用完全相同的字符串值，跨语言对拍可直接比较。
const EDITION_SOURCE_VERSION_MASK = 'version_mask'; // 来自 VersionMask/groupId（权威）
const EDITION_SOURCE_METADATA = 'metadata';         // 来自 Metadata primary_version/version_list
const EDITION_SOURCE_INFERRED = 'inferred';         // 兜底：字段数唯一匹配
const EDITION_SOURCE_UNKNOWN = 'unknown';           // 确实判定不出，不臆造

const FIELD_NAMES_SOURCE_METADATA = 'metadata';     // 文件自带 Metadata field_names
const FIELD_NAMES_SOURCE_EDITION = 'edition';       // 已知档次的规范表
const FIELD_NAMES_SOURCE_SYNTHETIC = 'synthetic';   // field_0..field_N-1 占位符

// 反向索引：字段数 -> 档次，仅在该字段数唯一时有效。
const EDITION_BY_FIELD_COUNT = Object.create(null);
for (const [_ed, _names] of Object.entries(EDITION_FIELD_NAMES)) {
  EDITION_BY_FIELD_COUNT[_names.length] = (_names.length in EDITION_BY_FIELD_COUNT) ? null : _ed;
}

/** one-hot 版本位掩码 -> 档次名；非 one-hot 返回 ''。 */
function editionFromMask(mask) {
  if (!(mask > 0) || (mask & (mask - 1)) !== 0) return ''; // 0 或多于一位 -> 不是单一档次
  const bit = 31 - Math.clz32(mask);
  return bit < EDITION_BY_BIT.length ? EDITION_BY_BIT[bit] : '';
}

// ===========================================================================
// 错误类型
// ===========================================================================
class QzdbError extends Error {
  constructor(message, code) {
    super(message);
    this.name = 'QzdbError';
    this.code = code;
  }
}
QzdbError.NOT_FOUND = 'NOT_FOUND';
QzdbError.CORRUPTED = 'CORRUPTED';
QzdbError.OUT_OF_BOUNDS = 'OUT_OF_BOUNDS';
QzdbError.INVALID_PARAM = 'INVALID_PARAM';
QzdbError.BAD_HEADER = 'BAD_HEADER';
QzdbError.BAD_MAGIC = 'BAD_MAGIC';
QzdbError.UNSUPPORTED = 'UNSUPPORTED';

// ===========================================================================
// CRC32（canonical：偏移 16~19 填 0 计算；与 Java/C# 交叉对齐）
// ===========================================================================
function _initCrc32Table() {
  const t = new Uint32Array(256);
  for (let i = 0; i < 256; i++) {
    let c = i;
    for (let j = 0; j < 8; j++) {
      if (c & 1) c = (c >>> 1) ^ 0xEDB88320;
      else c >>>= 1;
    }
    t[i] = c >>> 0;
  }
  return t;
}
const CRC32_TABLE = _initCrc32Table();

// Node >= 20.15 / 22.2 提供 zlib.crc32（C++ 实现，支持链式初值），
// 与下方表驱动完全同构；大库加载校验快一个数量级。低版本回退表驱动。
const _zlibCrc32 = require('zlib').crc32;
const _CRC_ZERO4 = Buffer.alloc(4);

function _crc32File(buf) {
  if (typeof _zlibCrc32 === 'function') {
    // CRC 字段（偏移 16-19）视为 0：前 16 字节 → 4 个零字节 → 其余。
    let crc = _zlibCrc32(buf.subarray(0, 16), 0) >>> 0;
    crc = _zlibCrc32(_CRC_ZERO4, crc) >>> 0;
    if (buf.length > 20) crc = _zlibCrc32(buf.subarray(20), crc) >>> 0;
    return crc;
  }
  let crc = 0xFFFFFFFF;
  // 前 16 字节
  for (let i = 0; i < 16; i++) {
    crc = (CRC32_TABLE[(crc ^ buf[i]) & 0xFF] >>> 0) ^ (crc >>> 8);
  }
  // CRC 字段（4 字节）视为 0
  for (let i = 0; i < 4; i++) {
    crc = (CRC32_TABLE[(crc ^ 0) & 0xFF] >>> 0) ^ (crc >>> 8);
  }
  // 偏移 20 之后
  for (let i = 20; i < buf.length; i++) {
    crc = (CRC32_TABLE[(crc ^ buf[i]) & 0xFF] >>> 0) ^ (crc >>> 8);
  }
  return (crc ^ 0xFFFFFFFF) >>> 0;
}

function _crc32Hex(buf) {
  return _crc32File(buf).toString(16).padStart(8, '0');
}

// ===========================================================================
// 原生浮点格式化（FORMAT §10.5 统一契约：整数值→精确十进制展开、非整数→固定
// 6 位小数、NaN/Inf → ''）
// ===========================================================================
function _formatNativeFloat(fw, d, off) {
  const num = fw === 4 ? d.readFloatLE(off) : d.readDoubleLE(off);
  if (num !== num) return '';           // NaN
  if (!Number.isFinite(num)) return ''; // Inf
  if (num === Math.floor(num)) {
    // 整数值须输出「精确十进制展开」。|v| < 2^53 时 String(trunc) 恰为精确值；
    // 更大的整数 Number→String 改走最短 round-trip（如 9223372036854774784 →
    // "9223372036854775000"），且 ≥1e21 变科学计数法（1e300 → "1e+300"），
    // 均违反契约——须经 IEEE754 位解码转 BigInt 定点输出。
    if (Math.abs(num) < 9007199254740992) return String(Math.trunc(num));
    return _doubleToExactBigInt(num).toString();
  }
  return num.toFixed(6);
}

// IEEE754 位解码：把整数数值的 finite double 精确转换为 BigInt（§10.5 大整值分支专用）
function _doubleToExactBigInt(v) {
  const b = Buffer.alloc(8);
  b.writeDoubleLE(v, 0);
  const bits = b.readBigUInt64LE(0);
  const neg = ((bits >> 63n) & 1n) === 1n;
  let exp = Number((bits >> 52n) & 0x7FFn);
  const frac = bits & ((1n << 52n) - 1n);
  let mant;
  if (exp === 0) { mant = frac; exp = -1022; } else { mant = frac | (1n << 52n); exp -= 1075; }
  const mag = exp >= 0 ? mant << BigInt(exp) : mant >> BigInt(-exp);
  return neg ? -mag : mag;
}

// ===========================================================================
// GeoInfo 响应实体（§6）
// ===========================================================================
// 保留键，绝不覆盖（原型污染 / 方法遮蔽防护）
const GEOINFO_RESERVED = new Set([
  '_vals', '_fieldNames', '_floatFlags', '_normMap',
  '__proto__', 'constructor', 'prototype',
  'get', 'toPipe', 'toPipeString', 'toMap', 'toDict', 'toJson',
  'toString', 'valueOf', 'hasOwnProperty', 'fieldNames', 'values',
  'getCidr', 'getCountry', 'getCountryEn', 'getCountryAlpha2', 'getCountryAlpha3',
  'getProvince', 'getProvinceEn', 'getCity', 'getCityEn', 'getDistrict',
  'getGeoId', 'getLongitude', 'getLatitude', 'getTimezone', 'getIsp', 'getIspEn',
  'getAsn', 'getAsName', 'getAsDomain', 'getUsageType',   'getCurrencyCode', 'getContinent', 'getContinentEn', 'getCountryCode',
  'getCurrencyName', 'getPhonePrefix', 'getEmojiFlag', 'getLanguages',
]);

class GeoInfo {
  constructor(vals, fieldNames, floatFlags, normalizedMap) {
    this._fieldNames = fieldNames || [];
    this._vals = vals || [];
    this._floatFlags = floatFlags && floatFlags.length === this._fieldNames.length
      ? floatFlags
      : this._fieldNames.map((n) => GeoInfo.isNumericFieldName(n));
    if (normalizedMap) {
      this._normMap = normalizedMap;
    } else {
      this._normMap = Object.create(null);
      for (let i = 0; i < this._fieldNames.length; i++) {
        const nk = GeoInfo.normalizeKey(this._fieldNames[i]);
        if (!(nk in this._normMap)) this._normMap[nk] = i;
      }
    }
    // DX：将字段暴露为自身属性（info.country），但不遮蔽内部/方法
    for (let i = 0; i < this._fieldNames.length; i++) {
      const name = this._fieldNames[i];
      if (!GEOINFO_RESERVED.has(name) && !Object.prototype.hasOwnProperty.call(this, name)) {
        this[name] = this._vals[i] !== undefined ? this._vals[i] : '';
      }
    }
    // 不可变：冻结自身与底层数组，防止调用方误写污染共享缓存（P0-2）
    Object.freeze(this._vals);
    Object.freeze(this._fieldNames);
    Object.freeze(this);
  }

  static normalizeKey(key) {
    if (!key) return '';
    let s = '';
    for (let i = 0; i < key.length; i++) {
      const c = key[i];
      if (c !== '_' && c !== '-') s += c.toLowerCase();
    }
    return s;
  }

  static isNumericFieldName(name) {
    if (!name) return false;
    const norm = GeoInfo.normalizeKey(name);
    return norm === 'longitude' || norm === 'latitude' || norm === 'asn' || norm === 'geoid';
  }

  /** 动态取字段（大小写/下划线/连字符不敏感）；缺失返回 ''，绝不抛错。 */
  get(name) {
    if (!name) return '';
    const idx = this._normMap[GeoInfo.normalizeKey(name)];
    if (idx === undefined) return '';
    const v = this._vals[idx];
    return v !== undefined ? v : '';
  }

  /** 管道符拼接：直接拼接已解码的字符串值，禁止任何重新格式化（§8.3）。 */
  toPipe() {
    const n = this._fieldNames.length;
    if (n === 0) return '';
    const out = new Array(n);
    for (let i = 0; i < n; i++) {
      const v = this._vals[i];
      out[i] = v !== undefined ? v : '';
    }
    return out.join('|');
  }

  toPipeString() { return this.toPipe(); }

  toMap() {
    const d = {};
    for (let i = 0; i < this._fieldNames.length; i++) {
      d[this._fieldNames[i]] = this._vals[i] !== undefined ? this._vals[i] : '';
    }
    return d;
  }

  toDict() { return this.toMap(); }

  fieldNames() { return this._fieldNames.slice(); }
  values() { return this._vals.slice(); }

  /** 手写 JSON 序列化：经纬度/asn/geo_id 为 JSON 数字，其余为字符串（§6）。 */
  toJson() {
    const names = this._fieldNames;
    const vals = this._vals;
    let s = '{';
    let first = true;
    for (let i = 0; i < names.length; i++) {
      const name = names[i];
      if (name == null) continue;
      const val = i < vals.length ? vals[i] : null;
      if (!first) s += ',';
      first = false;
      s += '"' + _escapeJson(name) + '":';
      const numeric = i < this._floatFlags.length ? this._floatFlags[i] : GeoInfo.isNumericFieldName(name);
      if (val == null || val === '') {
        s += numeric ? 'null' : '""';
      } else if (numeric) {
        s += _isJsonNumber(val) ? val : 'null';
      } else {
        s += '"' + _escapeJson(val) + '"';
      }
    }
    s += '}';
    return s;
  }

  toString() { return this.toPipe(); }

  // =========================================================================
  // 语义 Getter 全集（§6）
  // =========================================================================
  getCidr() { return this.get('cidr'); } // CIDR 不是数据库字段（§6：恒返回 ''）；统一走 get() 路径
  getCountry() { return this.get('country'); }
  getCountryEn() { return this.get('country_en'); }
  getCountryAlpha2() { return this.get('country_code'); } // 数据集以 country_code 存 alpha-2；历史返回 "" 为字段名笔误 bug
  getCountryAlpha3() { return this.get('country_alpha3'); }
  getContinent() { return this.get('continent'); }
  getContinentEn() { return this.get('continent_en'); }
  getCountryCode() { return this.get('country_code'); }
  getProvince() { return this.get('province'); }
  getProvinceEn() { return this.get('province_en'); }
  getCity() { return this.get('city'); }
  getCityEn() { return this.get('city_en'); }
  getDistrict() { return this.get('district'); }
  getTimezone() { return this.get('timezone'); }
  getIsp() { return this.get('isp'); }
  getIspEn() { return this.get('isp_en'); }
  getAsName() { return this.get('as_name'); }
  getAsDomain() { return this.get('as_domain'); }
  getCurrencyCode() { return this.get('currency_code'); }
  getCurrencyName() { return this.get('currency_name'); }
  getPhonePrefix() { return this.get('phone_prefix'); }
  getEmojiFlag() { return this.get('emoji_flag'); }
  getLanguages() { return this.get('languages'); }

  getGeoId() {
    const v = this.get('geo_id');
    if (v === '') return null;
    const n = Number(v);
    return Number.isFinite(n) ? Math.trunc(n) : null;
  }
  getLongitude() {
    const v = this.get('longitude');
    if (v === '') return null;
    const n = Number(v);
    return Number.isFinite(n) ? n : null;
  }
  getLatitude() {
    const v = this.get('latitude');
    if (v === '') return null;
    const n = Number(v);
    return Number.isFinite(n) ? n : null;
  }
  getAsn() {
    const v = this.get('asn');
    if (v === '') return null;
    const n = Number(v);
    return Number.isFinite(n) ? Math.trunc(n) : null;
  }
  getUsageType() { return UsageType.fromString(this.get('usage_type')); }
}

const _JSON_SAFE_RE = /^[^"\\\u0000-\u001f]*$/;
function _escapeJson(str) {
  // 快通道：无需转义字符时原样返回（绝大多数池字符串），toJson 密集场景 2-5×
  if (_JSON_SAFE_RE.test(str)) return str;
  let out = '';
  for (let i = 0; i < str.length; i++) {
    const c = str.charCodeAt(i);
    switch (str[i]) {
      case '"': out += '\\"'; break;
      case '\\': out += '\\\\'; break;
      case '\b': out += '\\b'; break;
      case '\f': out += '\\f'; break;
      case '\n': out += '\\n'; break;
      case '\r': out += '\\r'; break;
      case '\t': out += '\\t'; break;
      default:
        if (c < 0x20) {
          out += '\\u' + c.toString(16).padStart(4, '0');
        } else {
          out += str[i];
        }
    }
  }
  return out;
}

function _isJsonNumber(val) {
  // 与 C# GeoInfo.IsJsonNumber 逐字对齐的 JSON 数字文法校验：
  // 拒绝 "1." / ".5" / "01" / "+1" 等非法形态（修复前这类值会以裸 token
  // 进入 JSON 输出，产出非法 JSON）。
  const n = val.length;
  if (n === 0) return false;
  let i = 0;
  if (val[0] === '-') {
    if (n === 1) return false;
    i = 1;
  }
  if (i >= n) return false;
  if (val[i] === '0') {
    i++;
    if (i < n && val[i] >= '0' && val[i] <= '9') return false;
  } else {
    if (val[i] < '1' || val[i] > '9') return false;
    while (++i < n && val[i] >= '0' && val[i] <= '9') { /* consume digits */ }
  }
  if (i < n && val[i] === '.') {
    i++;
    const fracStart = i;
    while (i < n && val[i] >= '0' && val[i] <= '9') i++;
    if (i === fracStart) return false;
  }
  if (i < n && (val[i] === 'e' || val[i] === 'E')) {
    i++;
    if (i < n && (val[i] === '+' || val[i] === '-')) i++;
    const expStart = i;
    while (i < n && val[i] >= '0' && val[i] <= '9') i++;
    if (i === expStart) return false;
  }
  return i === n;
}

// ===========================================================================
// UsageType（§6 / Tier1：21 个预定义 + 未知兜底）
// ===========================================================================
const KNOWN_USAGE = [
  ['AICrawler', 'AI 爬虫', 'AICrawler', 'AI 训练 / AI 搜索爬虫（GPTBot、ClaudeBot 等）'],
  ['Backbone', '骨干网', 'Backbone', '运营商骨干传输网 / 国际出口'],
  ['Broadband', '宽带', 'Broadband', '家庭/企业宽带接入（xDSL、光纤、Cable、拨号等）'],
  ['Business', '企业', 'Business', '企业专线 / 企业组网'],
  ['CDN', 'CDN', 'CDN', '内容分发网络'],
  ['Cloud', '云服务', 'Cloud', '公有云 / 托管云（AWS、阿里云、Azure 等）'],
  ['DNS', 'DNS', 'DNS', 'DNS 基础设施 / Anycast DNS'],
  ['DataCenter', '数据中心', 'DataCenter', 'IDC / 机房托管'],
  ['Education', '教育网', 'Education', '高校 / 科研网（CERNET 等）'],
  ['Finance', '金融', 'Finance', '银行 / 证券 / 保险等金融机构'],
  ['Government', '政府', 'Government', '政务 / 公共机构网络'],
  ['ISP', '互联网提供商', 'ISP', '未细分类型的通用 ISP 接入'],
  ['IXP', '交换中心', 'IXP', '互联网交换中心'],
  ['IoT', '物联网', 'IoT', '物联网设备接入网络'],
  ['Mobile', '移动网络', 'Mobile', '蜂窝移动网络（2G/3G/4G/5G）'],
  ['Reserved', '保留地址', 'Reserved', '保留 / 未分配地址'],
  ['Satellite', '卫星互联网', 'Satellite', '卫星 / 低轨星座接入（Starlink 等）'],
  ['Spider', '爬虫', 'Spider', '通用搜索引擎 / 通用网络爬虫'],
  ['Streaming', '流媒体', 'Streaming', '音视频 / 直播流媒体平台'],
  ['Unknown', '未知', 'Unknown', '无法判定用途'],
  ['VPN', 'VPN/代理', 'VPN', 'VPN / 代理 / 隐私网络出口'],
];

class UsageType {
  constructor(raw, displayZh, displayEn, description, known) {
    this._raw = raw;
    this._zh = displayZh;
    this._en = displayEn;
    this._desc = description;
    this._known = known;
  }
  rawValue() { return this._raw; }
  getDisplayZh() { return this._zh; }
  getDisplayEn() { return this._en; }
  getDescription() { return this._desc; }
  isKnown() { return this._known; }
  toString() { return this._raw; }

  static fromString(raw) {
    if (raw == null || raw === '') {
      return USAGE_UNKNOWN;
    }
    const hit = USAGE_MAP[raw.toLowerCase()];
    if (hit) return hit;
    return new UsageType(raw, raw, raw, raw, false);
  }
}
const USAGE_MAP = Object.create(null);
const USAGE_UNKNOWN = new UsageType('Unknown', '未知', 'Unknown', '无法判定用途', true);
for (const [raw, zh, en, desc] of KNOWN_USAGE) {
  const u = new UsageType(raw, zh, en, desc, true);
  USAGE_MAP[raw.toLowerCase()] = u;
  if (raw === 'Unknown') { /* keep reference */ }
}

// ===========================================================================
// RowIds（§5）
// ===========================================================================
class RowIds {
  constructor(geoId, asnId, usageId) {
    this.geoId = geoId;
    this.asnId = asnId;
    this.usageId = usageId;
  }
}

// ===========================================================================
// BatchResult（§5）
// ===========================================================================
class BatchResult {
  constructor(input, result, error) {
    this.input = input;
    this.result = result; // GeoInfo | null
    // 规范 §5 批量三态：命中 → result 非空且 error 为 null；
    // 未命中 → result/error 皆为 null；非法输入 → error 填 QzdbError。
    // 非法 IP 属于第三态（hasError），不要与"未命中"混为一谈。
    this.error = error;   // QzdbError | null
  }
  get info() { return this.result; }
  isSuccess() { return this.error === null && this.result !== null; }
  isNotFound() { return this.error === null && this.result === null; }
  hasError() { return this.error !== null; }
}

// ===========================================================================
// QzdbReader（核心读取器）
// ===========================================================================
class QzdbReader {
  constructor(dbPath = null, groupIndex = 0, verifyCrc = true) {
    this._data = Buffer.alloc(0);
    this._groupIndex = groupIndex;
    this._verifyCrc = verifyCrc;
    this._closed = false;

    // 元数据
    this._flags = 0;
    this._hasV4 = false;
    this._hasV6 = false;
    this._v4Node24 = false;
    this._v6Node24 = false;
    this._v6JumpBits = 16;
    this._poolCount = 0;
    this._poolIdxSize = 2;
    this._buildDate = 0;

    this._geoCount = 0;
    this._rowCount = 0;
    this._v4RecCount = 0;
    this._v6RecCount = 0;
    this._v4NodeCount = 0;
    this._v6NodeCount = 0;
    this._ipRowSize = 6;
    this._geoEntryGroupCount = 0;

    // 偏移
    this._offV4Jump = 0;
    this._offV4Nodes = 0;
    this._offV6Jump = 0;
    this._offV6Nodes = 0;
    this._offIPRow = 0;
    this._offGeoEntries = 0;
    this._offPools = 0;
    this._offMeta = 0;
    this._offRowSchema = 0;
    this._offGroupSchema = 0;

    // 布局
    this._groupFieldCounts = [];
    this._groupEntryCounts = [];
    this._groupDimMasks = [];
    this._groupStrides = [];
    this._groupFieldWidths = [];
    this._groupFieldOffsets = [];
    this._groupFieldNative = [];
    this._groupFieldNativeType = [];
    this._groupFieldIds = [];
    this._groupEntryOffsets = [];
    this._groupFieldNames = [];
    this._groupIds = [];
    this._groupPools = null;
    this._poolsLoaded = false;

    // 字段名与索引
    this._fieldNames = [];
    this._fieldNameToIdx = Object.create(null);
    this._normFieldMap = Object.create(null);
    this._floatFlags = [];

    // 元数据串
    this._versionName = '';
    this._description = '';
    this._primaryVersion = '';
    this._edition = '';
    this._versionMask = 0;
    this._editionSource = EDITION_SOURCE_UNKNOWN;
    this._fieldNamesSource = FIELD_NAMES_SOURCE_SYNTHETIC;
    this._dataMonth = '';
    this._buildTimeStr = '';
    this._metaDataMonth = ''; // Metadata TLV type=5（v2.4 权威）
    this._scope = '';         // Metadata TLV type=6（v2.4 权威；无条目 ""）

    // 行 schema
    this._rowGeoWidth = 3;
    this._rowAsnWidth = 3;
    this._rowUsageWidth = 0;

    // 有界缓存
    this._geoCache = null;
    this._geoMetaCache = [];
    this._geoCacheMask = GEO_CACHE_CAP - 1;

    if (dbPath !== null) {
      this.load(dbPath);
    }
  }

  static open(path, options = {}) {
    return new QzdbReader(path, options.groupIndex || 0, options.verifyCrc !== false);
  }

  static openBuffer(buffer, options = {}) {
    const r = new QzdbReader(null, options.groupIndex || 0, options.verifyCrc !== false);
    r.loadBuffer(buffer);
    return r;
  }

  // -------------------------------------------------------------------------
  // 加载
  // -------------------------------------------------------------------------
  load(dbPath, verifyCrc = null) {
    if (verifyCrc !== null) this._verifyCrc = verifyCrc;
    const data = fs.readFileSync(dbPath);
    this._data = data;
    this._parseHeader();
    if (this._verifyCrc && !this.verifyCrc()) {
      throw new QzdbError(
        'CRC32 checksum mismatch — the .qzdb file is corrupted or truncated',
        QzdbError.CORRUPTED,
      );
    }
    this._initCache();
    this._closed = false;
    return this;
  }

  loadBuffer(bytes, verifyCrc = null) {
    if (verifyCrc !== null) this._verifyCrc = verifyCrc;
    const data = Buffer.isBuffer(bytes) ? Buffer.from(bytes) : Buffer.from(bytes);
    this._data = data;
    this._parseHeader();
    if (this._verifyCrc && !this.verifyCrc()) {
      throw new QzdbError(
        'CRC32 checksum mismatch — the buffer is corrupted or truncated',
        QzdbError.CORRUPTED,
      );
    }
    this._initCache();
    this._closed = false;
    return this;
  }

  _initCache() {
    this._geoCache = {
      keys: new Array(GEO_CACHE_CAP).fill(-1),
      groups: new Array(GEO_CACHE_CAP).fill(-1),
      vals: new Array(GEO_CACHE_CAP).fill(null),
    };
  }

  // -------------------------------------------------------------------------
  // 头部解析
  // -------------------------------------------------------------------------
  // Fail-Closed 原语：Buffer.readXxxLE 越界会抛 RangeError/TypeError，
  // 那是宿主异常而不是 QZDB 的契约异常。统一翻译成 QzdbError，保证任何
  // 畸形/恶意 .qzdb 都只会得到结构化错误，绝不把宿主异常泄漏给调用方。
  // V8 的 try/catch 在无异常路径上不影响 TurboFan 优化，热路径零开销。
  _oob(off, need) {
    throw new QzdbError(
      `Read out of bounds: offset=${off}, need=${need}, size=${this._data.length}`,
      QzdbError.OUT_OF_BOUNDS,
    );
  }
  safeReadU16(off) {
    try { return this._data.readUInt16LE(off); } catch (e) { return this._oob(off, 2); }
  }
  safeReadU32(off) {
    try { return this._data.readUInt32LE(off); } catch (e) { return this._oob(off, 4); }
  }
  safeReadU64(off) {
    try { return Number(this._data.readBigUInt64LE(off)); } catch (e) { return this._oob(off, 8); }
  }
  safeReadU8(off) {
    const v = this._data[off];
    if (v === undefined) return this._oob(off, 1);
    return v;
  }
  safeReadU24(off) {
    const d = this._data;
    if (off < 0 || off + 3 > d.length) return this._oob(off, 3);
    return d[off] | (d[off + 1] << 8) | (d[off + 2] << 16);
  }
  safeReadU48(off) {
    const d = this._data;
    if (off < 0 || off + 6 > d.length) return this._oob(off, 6);
    return d[off]
      + d[off + 1] * 0x100
      + d[off + 2] * 0x10000
      + d[off + 3] * 0x1000000
      + d[off + 4] * 0x100000000
      + d[off + 5] * 0x10000000000;
  }
  safeReadUintWidth(off, width) {
    if (width <= 1) return this.safeReadU8(off);
    if (width === 2) return this.safeReadU16(off);
    if (width === 3) return this.safeReadU24(off);
    return this.safeReadU32(off);
  }

  _parseHeader() {
    const d = this._data;
    if (d.length < 192) {
      throw new QzdbError('File too small for QZDB header', QzdbError.BAD_HEADER);
    }
    const magic = d.toString('ascii', 0, 4);
    if (magic !== 'QZDB') {
      throw new QzdbError('Invalid magic, expected QZDB', QzdbError.BAD_MAGIC);
    }
    const fmtVer = d[4];
    if (fmtVer !== 1) {
      throw new QzdbError(`Unsupported HeaderVersion: ${fmtVer} (QZDB requires version 1)`, QzdbError.UNSUPPORTED);
    }

    // VersionMask（偏移 6）：one-hot 版本位掩码，判定档次的权威信号。
    this._versionMask = this.safeReadU16(6);

    this._flags = this.safeReadU16(8);
    this._hasV4 = !!(this._flags & 1);
    this._hasV6 = !!(this._flags & 2);
    this._v4Node24 = !!(this._flags & 0x10);
    this._v6Node24 = !!(this._flags & 0x20);

    this._v6JumpBits = d[11] || 16;
    if (this._v6JumpBits < 8 || this._v6JumpBits > 20) {
      throw new QzdbError(`v6JumpBits out of range [8,20]: ${this._v6JumpBits}`, QzdbError.INVALID_PARAM);
    }
    this._poolCount = d[12];
    this._poolIdxSize = d[13];
    if (this._poolIdxSize !== 2 && this._poolIdxSize !== 3) {
      throw new QzdbError(`poolIdxSize must be 2 or 3, got ${this._poolIdxSize}`, QzdbError.INVALID_PARAM);
    }
    this._geoCount = this.safeReadU16(14);
    this._rowCount = this.safeReadU32(20);
    this._buildDate = this.safeReadU32(32);
    this._v4RecCount = this.safeReadU32(24);
    this._v6RecCount = this.safeReadU32(28);

    const hs = this.safeReadU32(36);
    if (hs !== 192) {
      throw new QzdbError(`Unexpected header size: ${hs}`, QzdbError.BAD_HEADER);
    }

    this._offRowSchema = this.safeReadU64(40);
    this._offGroupSchema = this.safeReadU64(48);
    this._offV4Jump = this.safeReadU64(64);
    this._offV4Nodes = this.safeReadU64(72);
    this._offV6Jump = this.safeReadU64(80);
    this._offV6Nodes = this.safeReadU64(88);
    this._offIPRow = this.safeReadU64(96);
    this._offGeoEntries = this.safeReadU64(104);
    this._offPools = this.safeReadU64(136);
    this._offMeta = this.safeReadU64(144);

    this._v4NodeCount = this.safeReadU32(152);
    this._v6NodeCount = this.safeReadU32(156);
    this._ipRowSize = this.safeReadU32(160);
    if (this._ipRowSize < 1 || this._ipRowSize > 64) {
      throw new QzdbError(`ipRowSize out of range [1,64]: ${this._ipRowSize}`, QzdbError.INVALID_PARAM);
    }
    this._geoEntryGroupCount = this.safeReadU32(164);
    if (this._geoEntryGroupCount < 1 || this._geoEntryGroupCount > 255) {
      throw new QzdbError(`geoEntryGroupCount out of range [1,255]: ${this._geoEntryGroupCount}`, QzdbError.INVALID_PARAM);
    }

    // 边界校验
    //
    // 192 字节头里的每个 section 偏移都由文件（可能是攻击者）控制，必须在
    // 解析期一次性对齐真实 buffer 长度校验完毕，绝不留到查询热路径。
    // 判据写成 `required > dlen - offset`，加法永不溢出（对齐 Go checkOffset /
    // Rust check_offset）。
    const dlen = d.length;
    const v4NodeSize = this._v4Node24 ? 6 : 8;
    const v6NodeSize = this._v6Node24 ? 6 : 8;
    const v6JumpSize = (1 << this._v6JumpBits) * 4;
    const chk = (offset, required, field) => {
      if (offset === 0) return;
      if (!Number.isFinite(offset) || offset < 0 || offset > dlen || required > dlen - offset) {
        throw new QzdbError(
          `Section ${field} out of bounds (offset=${offset}, need=${required}, size=${dlen})`,
          QzdbError.OUT_OF_BOUNDS,
        );
      }
    };
    chk(this._offV4Jump, 65536 * 4, 'v4_jump');
    chk(this._offV4Nodes, this._v4NodeCount * v4NodeSize, 'v4_nodes');
    chk(this._offV6Jump, v6JumpSize, 'v6_jump');
    chk(this._offV6Nodes, this._v6NodeCount * v6NodeSize, 'v6_nodes');
    chk(this._offIPRow, this._rowCount * this._ipRowSize, 'ip_row');
    // 变长 section 只校验固定头部；后续遍历每一步都会再次自检。
    chk(this._offGeoEntries, 1, 'geo_entries');
    chk(this._offPools, 4, 'pools');
    chk(this._offMeta, 4, 'meta');
    chk(this._offRowSchema, 4, 'row_schema');
    chk(this._offGroupSchema, 2, 'group_schema');

    // ROW_SCHEMA 必须在 section 边界校验之后再解析，否则 offRowSchema
    // 可以指到文件外并让宽度推断读到越界数据。
    this._parseRowSchema();

    // GeoEntryOffsets[4]
    this._groupEntryOffsets = [];
    for (let i = 0; i < 4; i++) {
      this._groupEntryOffsets.push(this.safeReadU48(168 + i * 6));
    }

    // GroupMetadataTable
    let gmOff = this._offGeoEntries;
    const tableGroups = this.safeReadU8(gmOff);
    gmOff += 1;
    let actualGroups = Math.min(tableGroups, Math.max(1, this._geoEntryGroupCount));
    if (actualGroups > 4) actualGroups = 4;
    // 组数为 0 的文件没有任何可查询维度，直接拒绝，避免构造出一个
    // 空 reader 后在取字段名时抛宿主 TypeError（与 Rust/Go/Python 契约一致）。
    if (!(actualGroups >= 1)) {
      throw new QzdbError('GroupMetadataTable groupCount is 0', QzdbError.CORRUPTED);
    }
    // 每组 = fieldCount(1) + entryCount(4) + dimensionMask(2)。
    if (actualGroups * 7 > dlen - gmOff) {
      throw new QzdbError('GroupMetadataTable truncated', QzdbError.CORRUPTED);
    }
    this._actualGroups = actualGroups;
    this._groupFieldCounts = new Array(actualGroups).fill(0);
    this._groupEntryCounts = new Array(actualGroups).fill(0);
    this._groupDimMasks = new Array(actualGroups).fill(0);
    for (let gi = 0; gi < actualGroups; gi++) {
      this._groupFieldCounts[gi] = this.safeReadU8(gmOff);
      gmOff += 1;
      this._groupEntryCounts[gi] = this.safeReadU32(gmOff);
      gmOff += 4;
      this._groupDimMasks[gi] = this.safeReadU16(gmOff);
      gmOff += 2;
    }

    this._groupStrides = new Array(actualGroups).fill(0);
    this._groupFieldWidths = new Array(actualGroups).fill(null);
    this._groupFieldOffsets = new Array(actualGroups).fill(null);
    this._groupFieldNative = new Array(actualGroups).fill(null);
    this._groupFieldNativeType = new Array(actualGroups).fill(null);
    this._groupFieldIds = new Array(actualGroups).fill(null);
    this._groupPools = null;
    this._poolsLoaded = false;

    // GROUP_SCHEMA 里各组自报的 fieldCount 是第二个"真相来源"，与
    // GroupMetadataTable 的 fieldCount 可能被伪造成不一致；下方不变式块统一收口。
    const schemaFldCounts = new Array(actualGroups).fill(-1);
    this._groupIds = new Array(actualGroups).fill(0);
    if (this._offGroupSchema > 0) {
      let sp = this._offGroupSchema;
      const gsGroupCount = this.safeReadU16(sp);
      sp += 2;
      const maxGsGroups = Math.min(gsGroupCount, actualGroups);
      for (let gi = 0; gi < maxGsGroups; gi++) {
        // 每组头 16 字节：groupId(2) fldCount(2) entryCount(4) stride(4) flags(4)
        // groupId 就是该组的 one-hot 版本位掩码，与 Header.VersionMask 同一套编码。
        if (16 > dlen - sp) break;
        this._groupIds[gi] = this.safeReadU16(sp);
        sp += 2;
        const fldCount = this.safeReadU16(sp);
        sp += 2;
        sp += 4;
        const stride = this.safeReadU32(sp);
        sp += 4;
        sp += 4;
        if (fldCount * 12 > dlen - sp) break;
        schemaFldCounts[gi] = fldCount;
        if (gi < actualGroups) {
          this._groupStrides[gi] = stride;
          const widths = new Array(fldCount).fill(0);
          const offsets = new Array(fldCount).fill(0);
          const natives = new Array(fldCount).fill(false);
          const natTypes = new Array(fldCount).fill(0);
          const fieldIds = new Array(fldCount).fill(0);
          for (let fi = 0; fi < fldCount; fi++) {
            fieldIds[fi] = this.safeReadU16(sp);
            sp += 2;
            widths[fi] = d[sp];
            sp += 1;
            const fieldFlags = d[sp];
            sp += 1;
            natives[fi] = (fieldFlags & 0x01) !== 0;
            natTypes[fi] = (fieldFlags >> 1) & 0x03;
            offsets[fi] = this.safeReadU32(sp);
            sp += 4;
            sp += 4;
          }
          this._groupFieldWidths[gi] = widths;
          this._groupFieldOffsets[gi] = offsets;
          this._groupFieldNative[gi] = natives;
          this._groupFieldNativeType[gi] = natTypes;
          this._groupFieldIds[gi] = fieldIds;
        } else {
          sp += fldCount * 12;
        }
      }
    }

    // 不变式：widths/offsets/native/nativeType[g] 的长度必须等于
    // groupFieldCounts[g]。GroupMetadataTable 与 GROUP_SCHEMA 各报一个字段数，
    // 真实文件必然一致；伪造文件可以把 GROUP_SCHEMA 的字段数改小，而所有下游
    // 消费者（池加载、字段抽取）都按 groupFieldCounts[g] 迭代，于是越界。
    // 一旦发现不一致就整组丢弃 schema，交给下面的默认布局重建
    // （与 Rust/Go/C/Python 同一套收口逻辑）。
    for (let g = 0; g < actualGroups; g++) {
      if (schemaFldCounts[g] >= 0 && schemaFldCounts[g] !== this._groupFieldCounts[g]) {
        this._groupStrides[g] = 0;
        this._groupFieldWidths[g] = null;
        this._groupFieldOffsets[g] = null;
        this._groupFieldNative[g] = null;
        this._groupFieldNativeType[g] = null;
        this._groupFieldIds[g] = null;
      }
    }

    for (let g = 0; g < actualGroups; g++) {
      if (this._groupStrides[g] === 0) this._groupStrides[g] = this._groupFieldCounts[g] * this._poolIdxSize;
      if (this._groupFieldWidths[g] === null) {
        this._groupFieldWidths[g] = new Array(this._groupFieldCounts[g]).fill(this._poolIdxSize);
      }
      if (this._groupFieldOffsets[g] === null) {
        this._groupFieldOffsets[g] = Array.from({ length: this._groupFieldCounts[g] }, (_, i) => i * this._poolIdxSize);
      }
      if (this._groupFieldNative[g] === null) {
        this._groupFieldNative[g] = new Array(this._groupFieldCounts[g]).fill(false);
      }
      if (this._groupFieldNativeType[g] === null) {
        this._groupFieldNativeType[g] = new Array(this._groupFieldCounts[g]).fill(0);
      }
    }

    this._parseMeta();
    this._repairDimMasks();
  }

  // dimensionMask 缺失时的重建：只看该组解析出来的字段名里有没有 asn。
  // fieldId 只是槽位序号（0..N-1），不带任何跨档语义，绝不可用来判定维度。
  _repairDimMasks() {
    const asnKey = GeoInfo.normalizeKey('asn');
    for (let g = 0; g < this._groupDimMasks.length; g++) {
      if (this._groupDimMasks[g] !== 0) continue;
      const names = this._groupFieldNames[g] || [];
      let hasAsn = false;
      for (let i = 0; i < names.length; i++) {
        if (GeoInfo.normalizeKey(names[i]) === asnKey) { hasAsn = true; break; }
      }
      this._groupDimMasks[g] = hasAsn ? 0x02 : 0x01;
    }
  }

  /**
   * 从文件的自描述信息解析出版本档次与字段名。
   *
   * 两个答案都只来自文件自己声明的内容，优先级在 8 种 SDK 中完全一致
   * （FORMAT §10.3）：
   *
   *   edition      1. GROUP_SCHEMA.groupId / Header.VersionMask 的 one-hot 位
   *                2. Metadata primary_version，或只有一项的 version_list
   *                3. 字段数唯一匹配（兜底）
   *                4. ''（unknown）—— 判定不出就不臆造
   *   field_names  1. Metadata field_names（基数与该组一致时）
   *                2. 已知档次且基数一致时用规范表
   *                3. field_0..field_N-1 占位符
   *
   * getEditionSource() / getFieldNamesSource() 报告命中了哪条规则，
   * 调用方可据此区分"文件里读出来的"与"SDK 补出来的"。
   */
  _parseMeta() {
    const d = this._data;
    const offMeta = this._offMeta;

    // load()/loadBuffer() 复用实例重载时先复位（§8.2：无条目即回落/""）
    this._metaDataMonth = '';
    this._scope = '';

    // --- Metadata TLV 遍历 ---------------------------------------------------
    let metaNames = null;
    if ((this._flags & 4) && offMeta > 0 && offMeta + 4 <= d.length) {
      let pos = offMeta;
      while (pos + 4 <= d.length) {
        const t = d[pos];
        const length = this.safeReadU16(pos + 2);
        if (t === 0 || length === 0) break;
        // 伪造的 TLV 可以声明一个越过 EOF 的长度；直接停止遍历，
        // 不去解码被截断的尾巴。
        if (length > d.length - (pos + 4)) break;
        const val = d.toString('utf8', pos + 4, pos + 4 + length);
        if (t === 1) this._versionName = val;
        else if (t === 2) metaNames = val.split('|');
        else if (t === 3) this._description = val;
        else if (t === 4) this._primaryVersion = val;
        else if (t === 5) this._metaDataMonth = val; // v2.4：数据期号（权威）
        else if (t === 6) this._scope = val;         // v2.4：数据覆盖范围（权威）
        // 未知 type 按设计跳过（FORMAT §8.1）
        pos += 4 + length;
      }
    }

    // --- 逐组解析 edition + 字段名 -------------------------------------------
    this._groupFieldNames = new Array(this._actualGroups);
    const groupEditions = new Array(this._actualGroups);
    const groupEditionSources = new Array(this._actualGroups);
    const groupNameSources = new Array(this._actualGroups);

    for (let g = 0; g < this._actualGroups; g++) {
      const numFields = this._groupFieldCounts[g];

      // edition：先用本组自己的掩码，再回落到文件级掩码
      const mask = (this._groupIds[g] || this._versionMask) >>> 0;
      let edition = editionFromMask(mask);
      let source = EDITION_SOURCE_VERSION_MASK;
      if (!edition) {
        edition = (this._primaryVersion || '').trim();
        if (!edition) {
          const tokens = (this._versionName || '').split(',')
            .map((s) => s.trim()).filter((s) => s.length > 0);
          if (tokens.length === 1) edition = tokens[0];
        }
        if (edition) source = EDITION_SOURCE_METADATA;
      }
      if (!edition) {
        edition = EDITION_BY_FIELD_COUNT[numFields] || '';
        source = edition ? EDITION_SOURCE_INFERRED : EDITION_SOURCE_UNKNOWN;
      }

      // 字段名
      let names;
      let namesSource;
      if (metaNames && metaNames.length === numFields) {
        names = metaNames;
        namesSource = FIELD_NAMES_SOURCE_METADATA;
      } else {
        const canonical = EDITION_FIELD_NAMES[edition];
        if (canonical && canonical.length === numFields) {
          names = canonical.slice();
          namesSource = FIELD_NAMES_SOURCE_EDITION;
        } else {
          names = Array.from({ length: numFields }, (_, i) => `field_${i}`);
          namesSource = FIELD_NAMES_SOURCE_SYNTHETIC;
        }
      }

      this._groupFieldNames[g] = names;
      groupEditions[g] = edition;
      groupEditionSources[g] = source;
      groupNameSources[g] = namesSource;
    }

    const gi = this._groupIndex < this._actualGroups ? this._groupIndex : 0;
    this._fieldNames = this._groupFieldNames[gi] || [];
    this._edition = groupEditions[gi] || '';
    this._editionSource = groupEditionSources[gi] || EDITION_SOURCE_UNKNOWN;
    this._fieldNamesSource = groupNameSources[gi] || FIELD_NAMES_SOURCE_SYNTHETIC;

    this._fieldNameToIdx = Object.create(null);
    this._normFieldMap = Object.create(null);
    for (let i = 0; i < this._fieldNames.length; i++) {
      this._fieldNameToIdx[this._fieldNames[i]] = i;
      const nk = GeoInfo.normalizeKey(this._fieldNames[i]);
      if (!(nk in this._normFieldMap)) this._normFieldMap[nk] = i;
    }
    this._floatFlags = this._fieldNames.map((n) => GeoInfo.isNumericFieldName(n));

    // 数据期号 / 覆盖范围（FORMAT §8.2）：Metadata TLV type=5(data_month)、
    // type=6(scope) 为权威来源；Header BuildDate 仅作回落，buildTimeStr 始终取自它。
    if (this._buildDate > 0) {
      const y = Math.floor(this._buildDate / 10000);
      const m = Math.floor(this._buildDate / 100) % 100;
      const dd = this._buildDate % 100;
      const mm = String(m).padStart(2, '0');
      const dds = String(dd).padStart(2, '0');
      this._buildTimeStr = `${y}-${mm}-${dds}`;
    } else {
      this._buildTimeStr = '';
    }
    if (!this._metaDataMonth) {
      this._dataMonth = this._buildDate > 0
        ? `${Math.floor(this._buildDate / 10000)}-${String(Math.floor(this._buildDate / 100) % 100).padStart(2, '0')}`
        : '';
    } else {
      this._dataMonth = this._metaDataMonth;
    }
  }

  _parseRowSchema() {
    this._rowGeoWidth = 3;
    this._rowAsnWidth = 3;
    this._rowUsageWidth = 0;
    if (this._offRowSchema <= 0) return;
    const d = this._data;
    const sp = this._offRowSchema;
    const fieldCount = d[sp];
    const stride = d[sp + 1];
    if (fieldCount < 1 || fieldCount > 8) return;
    if (sp + 4 + fieldCount * 4 > d.length) return;
    if (stride !== this._ipRowSize) return;

    let geoW = 0, asnW = 0, usageW = 0, total = 0;
    let wpos = sp + 4;
    let ok = true;
    for (let i = 0; i < fieldCount; i++) {
      const fid = d[wpos];
      const w = d[wpos + 1];
      if (fid === 0) geoW = w;
      else if (fid === 1) asnW = w;
      else if (fid === 2) usageW = w;
      wpos += 4;
      total += w;
      if (w < 1 || w > 4) ok = false;
    }
    if (ok && total === this._ipRowSize) {
      this._rowGeoWidth = geoW;
      this._rowAsnWidth = asnW;
      this._rowUsageWidth = usageW;
    }
  }

  _ensurePoolsLoaded() {
    if (this._poolsLoaded) return;
    this._poolsLoaded = true;

    const groupCount = this._groupFieldCounts.length;
    this._groupPools = new Array(groupCount).fill(null);
    if (this._offPools <= 0) return;

    let poolCursor = this._offPools;
    const poolEnd = this._offMeta > 0 ? this._offMeta : this._data.length;
    const d = this._data;

    for (let g = 0; g < groupCount; g++) {
      const fieldCount = this._groupFieldCounts[g];
      const groupPoolList = [];
      const natives = this._groupFieldNative[g];
      for (let f = 0; f < fieldCount; f++) {
        if (natives && f < natives.length && natives[f]) {
          groupPoolList.push([]);
          continue;
        }
        if (poolCursor + 4 > poolEnd) { groupPoolList.push([]); continue; }
        const count = this.safeReadU32(poolCursor);
        poolCursor += 4;
        if (this._offRowSchema > 0) poolCursor += 4;
        if (count === 0 || count > MAX_POOL_COUNT) { groupPoolList.push([]); continue; }

        // (count + 1) 个偏移必须整体落在 POOLS section 内。count 直接来自磁盘，
        // 这里是阻止伪造池头把游标推出文件末尾的关键边界。
        if (poolCursor > poolEnd || (count + 1) * 4 > poolEnd - poolCursor) {
          groupPoolList.push([]);
          continue;
        }

        const offsets = [];
        for (let o = 0; o <= count; o++) {
          offsets.push(this.safeReadU32(poolCursor));
          poolCursor += 4;
        }
        const strBase = poolCursor;
        const avail = poolEnd - strBase;
        // 偏移表是累积结构：offsets[i+1] >= offsets[i]，末项即字符串区总字节数。
        // 单调性必须强制校验而非默认成立 —— 否则伪造表可让每一项都横跨整个 section，
        // count 段 × section 长度会放大成 GB 级 Buffer#toString 分配，
        // 直接把 Node 进程 OOM 打死（FATAL heap limit），属可远程触发的 DoS。
        // 有 start >= prevEnd && end <= tail 后各段互不重叠且落在 [0, tail]，总量必 <= tail。
        const tail = offsets[count];
        if (tail > avail) { groupPoolList.push([]); continue; }
        const strings = new Array(count).fill('');
        let prevEnd = 0;
        for (let s = 0; s < count; s++) {
          const start = offsets[s];
          const end = offsets[s + 1];
          if (start < prevEnd || end < start || end > tail) continue;
          prevEnd = end;
          if (end > start) strings[s] = d.toString('utf8', strBase + start, strBase + end);
        }
        poolCursor += tail;
        groupPoolList.push(strings);
      }
      this._groupPools[g] = groupPoolList;
    }
  }

  // -------------------------------------------------------------------------
  // Trie 子节点
  // -------------------------------------------------------------------------
  _getV4Child(nodeIdx, bit) {
    // 与 PHP/_walkV4Depth 对齐：伪造 child 指针越过节点段时 fail-closed 返回 0，
    // 而不是把文件内其它 section 当节点读（最坏返回错误 rowId 而非 miss）。
    if (nodeIdx >= this._v4NodeCount) return 0;
    if (this._v4Node24) {
      const nodeOffset = this._offV4Nodes + nodeIdx * 6;
      const offset = bit === 0 ? nodeOffset : nodeOffset + 3;
      const val = this._data[offset] | (this._data[offset + 1] << 8) | (this._data[offset + 2] << 16);
      if (val & SENTINEL_24) return (val & SENTINEL_MASK_24) | SENTINEL;
      return val;
    }
    return this.safeReadU32(this._offV4Nodes + nodeIdx * 8 + bit * 4);
  }

  _getV6Child(nodeIdx, bit) {
    if (nodeIdx >= this._v6NodeCount) return 0;
    if (this._v6Node24) {
      const nodeOffset = this._offV6Nodes + nodeIdx * 6;
      const offset = bit === 0 ? nodeOffset : nodeOffset + 3;
      const val = this._data[offset] | (this._data[offset + 1] << 8) | (this._data[offset + 2] << 16);
      if (val & SENTINEL_24) return (val & SENTINEL_MASK_24) | SENTINEL;
      return val;
    }
    return this.safeReadU32(this._offV6Nodes + nodeIdx * 8 + bit * 4);
  }

  _trieWalkV4(ipInt) {
    const hi16 = (ipInt >>> 16) & 0xFFFF;
    const ptr = this.safeReadU32(this._offV4Jump + hi16 * 4);
    if (ptr === 0) return 0;
    if (ptr & SENTINEL) return ptr & SENTINEL_MASK_31;

    let idx = ptr;
    let suffix = (ipInt & 0xFFFF) << 16;
    // 有界 for：jump 表命中内节点后最多再走 16 步（与 Java/Go 同构），免每步步数分支
    for (let step = 0; step < 16; step++) {
      const bit = (suffix >>> 31) & 1;
      const child = this._getV4Child(idx, bit);
      if (child === 0) return 0;
      if (child & SENTINEL) return child & SENTINEL_MASK_31;
      idx = child;
      suffix <<= 1;
    }
    return 0;
  }

  _trieWalkV6Buf(ipBuf) {
    const jumpBits = this._v6JumpBits;
    let idxJump = 0;
    if (jumpBits <= 32 && jumpBits > 0) {
      idxJump = ipBuf.readUInt32BE(0) >>> (32 - jumpBits);
    } else {
      const b0 = ipBuf[0], b1 = ipBuf[1], b2 = ipBuf[2], b3 = ipBuf[3];
      const b4 = ipBuf[4], b5 = ipBuf[5], b6 = ipBuf[6];
      const hi = (b0 << 24) | (b1 << 16) | (b2 << 8) | b3;
      const lo = (b4 << 16) | (b5 << 8) | b6;
      idxJump = ((hi << (jumpBits - 32)) | (lo >> (64 - jumpBits))) & ((1 << jumpBits) - 1);
    }
    const ptr = this.safeReadU32(this._offV6Jump + idxJump * 4);
    if (ptr === 0) return 0;
    if (ptr & SENTINEL) return ptr & SENTINEL_MASK_31;

    let idx = ptr;
    let depth = jumpBits;
    while (depth < 128) {
      const byteIdx = depth >> 3;
      const bit = (ipBuf[byteIdx] >> (7 - (depth & 7))) & 1;
      const child = this._getV6Child(idx, bit);
      if (child === 0) return 0;
      if (child & SENTINEL) return child & SENTINEL_MASK_31;
      idx = child;
      depth += 1;
    }
    return 0;
  }

  // -------------------------------------------------------------------------
  // IPRow 解析
  // -------------------------------------------------------------------------
  _readIPRow(rowId) {
    if (rowId <= 0 || rowId >= this._rowCount) return [0, 0, 0];
    const off = this._offIPRow + rowId * this._ipRowSize;
    let geoId = 0;
    let asnId = 0;
    let usageTypeId = 0;
    if (this._offRowSchema > 0) {
      let p = off;
      geoId = this.safeReadUintWidth(p, this._rowGeoWidth);
      p += this._rowGeoWidth;
      if (this._rowAsnWidth > 0) { asnId = this.safeReadUintWidth(p, this._rowAsnWidth); p += this._rowAsnWidth; }
      if (this._rowUsageWidth > 0) usageTypeId = this.safeReadUintWidth(p, this._rowUsageWidth);
    } else {
      geoId = this.safeReadU24(off);
      asnId = this.safeReadU24(off + 3);
      if (this._ipRowSize >= 9) usageTypeId = this.safeReadU24(off + 6);
    }
    return [geoId, asnId, usageTypeId];
  }

  _resolveRowId(rowId, groupIndex) {
    rowId &= SENTINEL_MASK_31; // §8.1 防御性剥离
    if (rowId <= 0) return null;
    const [geoId, asnId, usageTypeId] = this._readIPRow(rowId);
    const mask = groupIndex < this._groupDimMasks.length ? this._groupDimMasks[groupIndex] : 0;
    let entryId = 0;
    if (mask & 0x02) entryId = asnId;
    else if (mask & 0x04) entryId = usageTypeId;
    else entryId = geoId;
    if (entryId === 0) return null;
    return this._resolveGeo(entryId, groupIndex);
  }

  // GeoEntry 解包（带 per-snapshot 有界缓存，碰撞只重算）
  _resolveGeo(entryId, groupIndex) {
    if (groupIndex < 0 || groupIndex >= this._groupFieldCounts.length) return null;
    if (entryId < 0 || entryId >= this._groupEntryCounts[groupIndex]) return null;

    const cache = this._geoCache;
    const slot = cache ? (Math.imul(entryId, 2654435761) + groupIndex * 97) & this._geoCacheMask : -1;
    if (cache) {
      if (cache.keys[slot] === entryId && cache.groups[slot] === groupIndex && cache.vals[slot]) {
        return cache.vals[slot];
      }
    }
    const geo = this._resolveGeoUncached(entryId, groupIndex);
    if (cache && geo) {
      cache.keys[slot] = entryId;
      cache.groups[slot] = groupIndex;
      cache.vals[slot] = geo;
    }
    return geo;
  }

  _resolveGeoUncached(entryId, groupIndex) {
    this._ensurePoolsLoaded();
    const gc = this._groupFieldCounts[groupIndex];
    const groupEntryStart = this._offGeoEntries + this._groupEntryOffsets[groupIndex];
    const stride = this._groupStrides[groupIndex];
    const entryOffset = groupEntryStart + entryId * stride;
    const d = this._data;
    const widths = this._groupFieldWidths[groupIndex];
    const baseOffsets = this._groupFieldOffsets[groupIndex];
    const natives = this._groupFieldNative[groupIndex];
    const natTypes = this._groupFieldNativeType[groupIndex];
    const gp = this._groupPools[groupIndex];
    // 每组的 {names, floatFlags, normMap} 只在首个 miss 构建一次并复用：
    // 此前每次 miss 都做 ~2×fields 次 normalizeKey 字符串构建（ult 档 50 次/miss）。
    let meta = this._geoMetaCache[groupIndex];
    if (meta === undefined) {
      const names = this._groupFieldNames[groupIndex] || this._fieldNames;
      const floatFlags = names.map((n) => GeoInfo.isNumericFieldName(n));
      const normMap = Object.create(null);
      for (let i = 0; i < names.length; i++) {
        const nk = GeoInfo.normalizeKey(names[i]);
        if (!(nk in normMap)) normMap[nk] = i;
      }
      meta = { names, floatFlags, normMap };
      this._geoMetaCache[groupIndex] = meta;
    }
    const { names, floatFlags, normMap } = meta;
    const vals = new Array(gc);
    for (let i = 0; i < gc; i++) {
      const w = widths[i];
      const fo = entryOffset + baseOffsets[i];
      let val = '';
      if (natives && i < natives.length && natives[i]) {
        const t = (natTypes && i < natTypes.length) ? natTypes[i] : 0;
        if (t === 1) val = _formatNativeFloat(w, d, fo);
        else val = String(this.safeReadUintWidth(fo, w) >>> 0);
      } else {
        const idx = this.safeReadUintWidth(fo, w);
        if (gp && i < gp.length && idx >= 0 && idx < gp[i].length) val = gp[i][idx];
      }
      vals[i] = val;
    }
    return new GeoInfo(vals, names, floatFlags, normMap);
  }

  // -------------------------------------------------------------------------
  // 单条查询 API（§3）
  // -------------------------------------------------------------------------
  find(ipStr) {
    if (!ipStr) return null;
    if (this._closed) return null;
    const result = fastParseIp(ipStr);
    if (!result) return null;
    if (result.v4 !== null) return this.findUint(result.v4);
    if (!this._hasV6) return null;
    const rowId = this._trieWalkV6Buf(result.v6);
    if (rowId === 0) return null;
    return this._resolveRowId(rowId, this._groupIndex);
  }

  findUint(ipInt) {
    if (this._closed || !this._hasV4) return null;
    const rowId = this._trieWalkV4(ipInt >>> 0);
    if (rowId === 0) return null;
    return this._resolveRowId(rowId, this._groupIndex);
  }

  findV6Uint(ipInt) {
    if (this._closed || !this._hasV6) return null;
    const rowId = this._trieWalkV6Buf(_bigint128ToBuf(ipInt));
    if (rowId === 0) return null;
    return this._resolveRowId(rowId, this._groupIndex);
  }

  findV6(high, low) {
    if (this._closed || !this._hasV6) return null;
    const rowId = this._trieWalkV6Buf(_highLowToBuf(high, low));
    if (rowId === 0) return null;
    return this._resolveRowId(rowId, this._groupIndex);
  }

  findBytes(ipBytes) {
    if (this._closed || !ipBytes) return null;
    const len = ipBytes.length;
    if (len === 4) {
      const ipInt = (((ipBytes[0] & 0xFF) << 24) | ((ipBytes[1] & 0xFF) << 16)
        | ((ipBytes[2] & 0xFF) << 8) | (ipBytes[3] & 0xFF)) >>> 0;
      return this.findUint(ipInt);
    }
    if (len === 16) {
      const b = Buffer.isBuffer(ipBytes) ? ipBytes : Buffer.from(ipBytes);
      if (isV4MappedBuf(b)) {
        const ipInt = (((b[12] & 0xFF) << 24) | ((b[13] & 0xFF) << 16)
          | ((b[14] & 0xFF) << 8) | (b[15] & 0xFF)) >>> 0;
        return this.findUint(ipInt);
      }
      if (!this._hasV6) return null;
      const rowId = this._trieWalkV6Buf(b);
      if (rowId === 0) return null;
      return this._resolveRowId(rowId, this._groupIndex);
    }
    return null;
  }

  // 字段投影：从缓存的全字段结果按请求顺序切片（§3，对齐 Java golden 语义）。
  // - 未知字段在该位置补 ''（不跳过）
  // - 保留重复字段与调用方顺序
  // - 全部未知时仍返回 GeoInfo（字段值全为 ''）
  // - fields=null/空 等价于 find
  findFields(ipStr, fieldNames = null) {
    if (fieldNames === null || fieldNames.length === 0) return this.find(ipStr);
    const full = this.find(ipStr); // 未命中/非法 → null（Node 语言约定）
    if (full === null) return null;
    const norm = this._normFieldMap;
    const floatAll = this._floatFlags;
    const n = fieldNames.length;
    const outNames = new Array(n);
    const outVals = new Array(n);
    const floatFlags = new Array(n);
    for (let i = 0; i < n; i++) {
      const name = fieldNames[i];
      const idx = norm[GeoInfo.normalizeKey(name)];
      outNames[i] = name;
      const oob = idx === undefined || idx >= full._vals.length;
      outVals[i] = oob ? '' : full._vals[idx];
      floatFlags[i] = oob ? false : !!floatAll[idx];
    }
    return new GeoInfo(outVals, outNames, floatFlags, null);
  }

  findStr(ipStr) {
    const info = this.find(ipStr);
    return info === null ? '' : info.toPipe();
  }

  // -------------------------------------------------------------------------
  // 批量 / 流式（§5）
  // -------------------------------------------------------------------------
  findBatch(ips) {
    if (ips == null) return [];
    const out = [];
    for (const ip of ips) {
      try {
        if (fastParseIp(ip) == null) {
          throw new QzdbError('Invalid IP: ' + ip, QzdbError.INVALID_PARAM);
        }
        out.push(new BatchResult(ip, this.find(ip), null));
      } catch (e) {
        out.push(new BatchResult(ip, null, e instanceof QzdbError ? e : new QzdbError(String(e), QzdbError.CORRUPTED)));
      }
    }
    return out;
  }

  findBatchFields(ips, fields) {
    if (ips == null) return [];
    const out = [];
    for (const ip of ips) {
      try {
        if (fastParseIp(ip) == null) {
          throw new QzdbError('Invalid IP: ' + ip, QzdbError.INVALID_PARAM);
        }
        out.push(new BatchResult(ip, this.findFields(ip, fields), null));
      } catch (e) {
        out.push(new BatchResult(ip, null, e instanceof QzdbError ? e : new QzdbError(String(e), QzdbError.CORRUPTED)));
      }
    }
    return out;
  }

  *findIter(ips) {
    if (ips == null) return;
    for (const ip of ips) {
      try {
        if (fastParseIp(ip) == null) {
          throw new QzdbError('Invalid IP: ' + ip, QzdbError.INVALID_PARAM);
        }
        yield new BatchResult(ip, this.find(ip), null);
      } catch (e) {
        yield new BatchResult(ip, null, e instanceof QzdbError ? e : new QzdbError(String(e), QzdbError.CORRUPTED));
      }
    }
  }

  *findStream(ips) { yield* this.findIter(ips); }

  // -------------------------------------------------------------------------
  // 低级行号（§5）
  // -------------------------------------------------------------------------
  lookupRowId(ipStr) {
    if (!ipStr) return 0;
    if (this._closed) return 0;
    const result = fastParseIp(ipStr);
    if (!result) return 0;
    if (result.v4 !== null) return this.lookupRowIdUint(result.v4);
    if (!this._hasV6) return 0;
    return this._trieWalkV6Buf(result.v6);
  }

  lookupRowIdUint(ipInt) {
    if (this._closed || !this._hasV4) return 0;
    return this._trieWalkV4(ipInt >>> 0);
  }

  lookupRowIdV6(ipInt) {
    if (this._closed || !this._hasV6) return 0;
    return this._trieWalkV6Buf(_bigint128ToBuf(ipInt));
  }

  lookupRowIdBytes(ipBytes) {
    if (this._closed || !ipBytes) return 0;
    const len = ipBytes.length;
    if (len === 4) {
      return this.lookupRowIdUint((((ipBytes[0] & 0xFF) << 24) | ((ipBytes[1] & 0xFF) << 16)
        | ((ipBytes[2] & 0xFF) << 8) | (ipBytes[3] & 0xFF)) >>> 0);
    }
    if (len === 16) {
      const b = Buffer.isBuffer(ipBytes) ? ipBytes : Buffer.from(ipBytes);
      if (isV4MappedBuf(b)) {
        return this.lookupRowIdUint((((b[12] & 0xFF) << 24) | ((b[13] & 0xFF) << 16)
          | ((b[14] & 0xFF) << 8) | (b[15] & 0xFF)) >>> 0);
      }
      if (!this._hasV6) return 0;
      return this._trieWalkV6Buf(b);
    }
    return 0;
  }

  lookupIds(rowId) {
    if (this._closed) return null;
    rowId &= SENTINEL_MASK_31;
    if (rowId <= 0 || rowId >= this._rowCount) return null;
    const [geoId, asnId, usageId] = this._readIPRow(rowId);
    return new RowIds(geoId, asnId, usageId);
  }

  // -------------------------------------------------------------------------
  // CIDR 反查（§5 / §8.6）
  // -------------------------------------------------------------------------
  lookupCidr(ipStr) {
    if (!ipStr) return null;
    if (this._closed) return null;
    const result = fastParseIp(ipStr);
    if (!result) return null;
    if (result.v4 !== null) {
      const n = this._v4PrefixLen(result.v4);
      return n < 0 ? null : this._formatV4Cidr(result.v4, n);
    }
    if (!this._hasV6) return null;
    const n = this._v6PrefixLen(result.v6);
    return n < 0 ? null : this._formatV6Cidr(result.v6, n);
  }

  lookupCidrUint(ipInt) {
    if (this._closed || !this._hasV4) return null;
    const n = this._v4PrefixLen(ipInt >>> 0);
    return n < 0 ? null : this._formatV4Cidr(ipInt >>> 0, n);
  }

  lookupCidrBytes(ipBytes) {
    if (this._closed || !ipBytes) return null;
    const len = ipBytes.length;
    if (len === 4) {
      const ipInt = (((ipBytes[0] & 0xFF) << 24) | ((ipBytes[1] & 0xFF) << 16)
        | ((ipBytes[2] & 0xFF) << 8) | (ipBytes[3] & 0xFF)) >>> 0;
      const n = this._v4PrefixLen(ipInt);
      return n < 0 ? null : this._formatV4Cidr(ipInt, n);
    }
    if (len === 16) {
      const b = Buffer.isBuffer(ipBytes) ? ipBytes : Buffer.from(ipBytes);
      if (isV4MappedBuf(b)) {
        const ipInt = (((b[12] & 0xFF) << 24) | ((b[13] & 0xFF) << 16)
          | ((b[14] & 0xFF) << 8) | (b[15] & 0xFF)) >>> 0;
        const n = this._v4PrefixLen(ipInt);
        return n < 0 ? null : this._formatV4Cidr(ipInt, n);
      }
      if (!this._hasV6) return null;
      const n = this._v6PrefixLen(b);
      return n < 0 ? null : this._formatV6Cidr(b, n);
    }
    return null;
  }

  _v4PrefixLen(ipInt) {
    if (!this._hasV4 || this._offV4Jump <= 0) return -1;
    const ptr = this.safeReadU32(this._offV4Jump + ((ipInt >>> 16) & 0xFFFF) * 4);
    if (ptr === 0) return -1;
    if (ptr & SENTINEL) return this._walkV4Depth(ipInt, 0, 0, 16);
    return this._walkV4Depth(ipInt, ptr, 16, 32);
  }

  _walkV4Depth(ipInt, startIdx, startDepth, maxDepth) {
    let idx = startIdx;
    const d = this._data;
    const nOff = this._offV4Nodes;
    const nodeCount = this._v4NodeCount;
    const node24 = this._v4Node24;
    for (let depth = startDepth; depth < maxDepth; depth++) {
      if (idx >= nodeCount) return -1;
      const bit = (ipInt >>> (31 - depth)) & 1;
      let child;
      if (node24) {
        const off = nOff + idx * 6 + (bit === 0 ? 0 : 3);
        child = d[off] | (d[off + 1] << 8) | (d[off + 2] << 16);
        if (child & SENTINEL_24) return depth + 1;
      } else {
        const off = nOff + idx * 8 + (bit === 0 ? 0 : 4);
        child = this.safeReadU32(off);
        if (child & SENTINEL) return depth + 1;
      }
      if (child === 0) return -1;
      idx = child;
    }
    return -1;
  }

  _v6PrefixLen(buf) {
    if (!this._hasV6 || this._offV6Jump <= 0) return -1;
    const pref = this._v6PrefixBits(buf, this._v6JumpBits);
    const ptr = this.safeReadU32(this._offV6Jump + pref * 4);
    if (ptr === 0) return -1;
    if (ptr & SENTINEL) return this._walkV6Depth(buf, 0, 0, this._v6JumpBits);
    return this._walkV6Depth(buf, ptr, this._v6JumpBits, 128);
  }

  _v6PrefixBits(bytes, bits) {
    let val = 0;
    for (let i = 0; i < bits; i++) {
      const bit = (bytes[i >> 3] >>> (7 - (i & 7))) & 1;
      val = (val << 1) | bit;
    }
    return val;
  }

  _walkV6Depth(buf, startIdx, startDepth, maxDepth) {
    let idx = startIdx;
    const d = this._data;
    const nOff = this._offV6Nodes;
    const nodeCount = this._v6NodeCount;
    const node24 = this._v6Node24;
    for (let depth = startDepth; depth < maxDepth; depth++) {
      if (idx >= nodeCount) return -1;
      const bit = (buf[depth >> 3] >>> (7 - (depth & 7))) & 1;
      let child;
      if (node24) {
        const off = nOff + idx * 6 + (bit === 0 ? 0 : 3);
        child = d[off] | (d[off + 1] << 8) | (d[off + 2] << 16);
        if (child & SENTINEL_24) return depth + 1;
      } else {
        const off = nOff + idx * 8 + (bit === 0 ? 0 : 4);
        child = this.safeReadU32(off);
        if (child & SENTINEL) return depth + 1;
      }
      if (child === 0) return -1;
      idx = child;
    }
    return -1;
  }

  _formatV4Cidr(ipInt, n) {
    const net = n === 0 ? 0 : (ipInt & ((~0 << (32 - n)) >>> 0));
    return `${(net >>> 24) & 0xFF}.${(net >>> 16) & 0xFF}.${(net >>> 8) & 0xFF}.${net & 0xFF}/${n}`;
  }

  _formatV6Cidr(buf, n) {
    const net = Buffer.from(buf);
    for (let bit = n; bit < 128; bit++) {
      net[bit >> 3] &= ~(1 << (7 - (bit & 7)));
    }
    const g = new Array(8);
    for (let i = 0; i < 8; i++) g[i] = (net[2 * i] << 8) | net[2 * i + 1];
    // RFC 5952：最长全零段（并列取最左），长度 ≥2 才压缩
    let bestStart = -1, bestLen = 0, curStart = -1, curLen = 0;
    for (let i = 0; i < 8; i++) {
      if (g[i] === 0) {
        if (curStart < 0) { curStart = i; curLen = 1; } else curLen++;
      } else {
        if (curLen > bestLen) { bestStart = curStart; bestLen = curLen; }
        curStart = -1; curLen = 0;
      }
    }
    if (curLen > bestLen) { bestStart = curStart; bestLen = curLen; }

    let s = '';
    if (bestLen >= 2) {
      for (let i = 0; i < bestStart; i++) { if (i > 0) s += ':'; s += g[i].toString(16); }
      s += '::';
      let first = true;
      for (let i = bestStart + bestLen; i < 8; i++) { if (!first) s += ':'; s += g[i].toString(16); first = false; }
    } else {
      for (let i = 0; i < 8; i++) { if (i > 0) s += ':'; s += g[i].toString(16); }
    }
    return s + '/' + n;
  }

  // -------------------------------------------------------------------------
  // 热更新与生命周期（§2）
  // -------------------------------------------------------------------------
  reload(dbPath) {
    const tmp = new QzdbReader(null, this._groupIndex, true);
    tmp.load(dbPath, true); // 失败抛错 → 旧快照（this）继续服务
    Object.assign(this, tmp); // 原子替换全部状态字段
    return this;
  }

  reloadBuffer(bytes) {
    const tmp = new QzdbReader(null, this._groupIndex, true);
    tmp.loadBuffer(bytes, true); // reload 强制 CRC
    Object.assign(this, tmp);
    return this;
  }

  close() {
    this._data = Buffer.alloc(0);
    this._hasV4 = false;
    this._hasV6 = false;
    this._poolsLoaded = false;
    this._groupPools = null;
    this._fieldNames = [];
    this._normFieldMap = Object.create(null);
    this._floatFlags = [];
    this._geoCache = null;
    this._geoMetaCache = [];
    this._closed = true;
  }

  // -------------------------------------------------------------------------
  // 元信息自省（§5）
  // -------------------------------------------------------------------------
  getVersion() { return this._versionName; }
  getDataMonth() { return this._dataMonth; }
  getEdition() { return this._edition; }
  /** Header.VersionMask（偏移 6）原值：one-hot 版本位掩码。 */
  getVersionMask() { return this._versionMask; }
  /** 档次判定命中的规则：version_mask / metadata / inferred / unknown。 */
  getEditionSource() { return this._editionSource; }
  /** 字段名来源：metadata / edition / synthetic。 */
  getFieldNamesSource() { return this._fieldNamesSource; }
  /** 数据覆盖范围：Metadata TLV type=6（v2.4 权威；无条目 ""，FORMAT §8.2）。 */
  getScope() { return this._scope; }
  getBuildTime() { return this._buildTimeStr; }
  getDescription() { return this._description; }
  getFileHash() {
    // 空/已 close 的 reader 返回 '00000000'（与 PHP 一致），
    // 而不是对空 Buffer 算出假 CRC（undefined & 0xFF === 0 的静默垃圾值）。
    if (!this._data || this._data.length < 20) return '00000000';
    return _crc32Hex(this._data);
  }
  getFieldNames() { return this._fieldNames.slice(); }
  hasField(name) { return GeoInfo.normalizeKey(name) in this._normFieldMap; }
  verifyCrc() {
    if (!this._data || this._data.length < 20) return false;
    return _crc32File(this._data) === this._data.readUInt32LE(16);
  }
  getGroupCount() { return this._actualGroups; }
  getPoolCount() { return this._poolCount; }

  // 兼容既有脚本的访问器
  get version() { return this._versionName; }
  get field_names() { return this._fieldNames; }
  get version_code() {
    const pcMap = { 6: 1, 7: 2, 25: 3 };
    return pcMap[this._poolCount] !== undefined ? pcMap[this._poolCount] : 3;
  }
  get pool_count() { return this._poolCount; }
}

// Builder（§2）：Builder(path|bytes).groupIndex(n).verifyCrc(b).build()
QzdbReader.Builder = class {
  constructor(arg) {
    this._file = null;
    this._buffer = null;
    this._groupIndex = 0;
    this._verifyCrc = true;
    if (typeof arg === 'string') this._file = arg;
    else if (arg instanceof QzdbReader) { /* 忽略，仅接受源 */ }
    else if (Buffer.isBuffer(arg) || arg instanceof Uint8Array) this._buffer = Buffer.from(arg);
  }
  groupIndex(i) { this._groupIndex = i; return this; }
  verifyCrc(b) { this._verifyCrc = b; return this; }
  build() {
    const r = new QzdbReader(null, this._groupIndex, this._verifyCrc);
    if (this._file) r.load(this._file, this._verifyCrc);
    else if (this._buffer) r.loadBuffer(this._buffer, this._verifyCrc);
    else throw new QzdbError('Builder requires a file path or buffer', QzdbError.INVALID_PARAM);
    return r;
  }
};

// ===========================================================================
// 大整数辅助
// ===========================================================================
function _bigint128ToBuf(ipInt) {
  const buf = Buffer.allocUnsafe(16);
  buf.writeBigUInt64BE(BigInt(ipInt) >> 64n, 0);
  buf.writeBigUInt64BE(BigInt(ipInt) & 0xFFFFFFFFFFFFFFFFn, 8);
  return buf;
}
function _highLowToBuf(high, low) {
  const buf = Buffer.allocUnsafe(16);
  buf.writeBigUInt64BE(BigInt(high), 0);
  buf.writeBigUInt64BE(BigInt(low) & 0xFFFFFFFFFFFFFFFFn, 8);
  return buf;
}
function isV4MappedBuf(b) {
  for (let i = 0; i < 10; i++) if (b[i] !== 0) return false;
  return (b[10] & 0xFF) === 0xFF && (b[11] & 0xFF) === 0xFF;
}

// ===========================================================================
// 严格 IP 解析（§4 / Tier1：拒绝前导零、越界、缺段、超长、zone-id、CIDR 形式等）
// ===========================================================================
const _HEX = new Uint8Array(128);
(function initHex() {
  for (let i = 0; i < 10; i++) _HEX[48 + i] = i;
  for (let i = 0; i < 6; i++) { _HEX[97 + i] = 10 + i; _HEX[65 + i] = 10 + i; }
})();

function _fastParseIPv4(s) {
  const n = s.length;
  if (n === 0 || s.charCodeAt(n - 1) === 46) return null;
  let result = 0, val = 0, dots = 0, start = 0;
  for (let i = 0; i <= n; i++) {
    const c = i < n ? s.charCodeAt(i) : 46;
    if (c === 46) {
      const segLen = i - start;
      if (segLen === 0 || segLen > 3) return null;
      if (segLen > 1 && s.charCodeAt(start) === 48) return null;
      val = 0;
      for (let j = start; j < i; j++) {
        const d = s.charCodeAt(j);
        if (d < 48 || d > 57) return null;
        val = val * 10 + (d - 48);
      }
      if (val > 255) return null;
      result = (result << 8) | val;
      dots++;
      start = i + 1;
    }
  }
  return dots === 4 ? (result >>> 0) : null;
}

function fastParseIp(ip) {
  if (typeof ip !== 'string') return null;
  const s = ip;
  const n = s.length;
  if (n === 0 || n > 45) return null;
  // Fail-Closed：拒绝任何空白符（不静默 trim，防 SSRF）
  for (let i = 0; i < n; i++) {
    const c = s.charCodeAt(i);
    if (c === 32 || c === 9 || c === 10 || c === 13 || c === 11 || c === 12) return null;
  }
  if (s.indexOf(':') < 0) {
    const v4 = _fastParseIPv4(s);
    return v4 === null ? null : { v4, v6: null };
  }
  if (s.indexOf('%') >= 0) return null;        // zone-id
  if (s.indexOf('/') >= 0) return null;         // CIDR 形式拒绝
  const dc = s.indexOf('::');
  if (dc >= 0 && s.indexOf('::', dc + 2) >= 0) return null; // 多个 ::
  const lft = dc >= 0 ? s.substring(0, dc) : s;
  const rgt = dc >= 0 ? s.substring(dc + 2) : '';
  const lg = lft ? lft.split(':') : [];
  const rg = rgt ? rgt.split(':') : [];
  if (lg.length === 1 && lg[0] === '') lg.length = 0;
  if (rg.length === 1 && rg[0] === '') rg.length = 0;
  for (let i = 0; i < lg.length; i++) if (lg[i] === '') return null;
  for (let i = 0; i < rg.length; i++) if (rg[i] === '') return null;
  const allg = lg.concat(rg);
  let hasV4 = false, v4Int = 0;
  const last = allg.length - 1;
  if (last >= 0 && allg[last].indexOf('.') >= 0) {
    v4Int = _fastParseIPv4(allg[last]);
    if (v4Int === null) return null;
    hasV4 = true;
    allg.length = last;
  }
  // 内嵌 IPv4 必须位于地址末尾（最后 32 位）。若带 "::" 压缩且 v4 落在 "::" 之前
  // （rgt 为空，即 "a.b.c.d::" 形态），属于非法地址，netip 同样拒绝，这里显式拒绝。
  if (hasV4 && dc >= 0 && rg.length === 0) return null;
  const ng = allg.length;
  const v4Slots = hasV4 ? 2 : 0;
  if (dc >= 0) {
    if (ng + v4Slots > 7) return null;
  } else {
    if (ng + v4Slots !== 8) return null;
  }
  for (let i = 0; i < ng; i++) {
    const g = allg[i];
    const gl = g.length;
    if (gl === 0 || gl > 4) return null;
    for (let j = 0; j < gl; j++) {
      const cc = g.charCodeAt(j);
      if (cc >= 128 || (_HEX[cc] === 0 && cc !== 48)) return null;
    }
  }
  const zeros = 8 - ng - v4Slots;
  const buf = Buffer.alloc(16);
  let off = 0;
  for (let i = 0; i < lg.length; i++) {
    const g = lg[i];
    let v = 0;
    for (let j = 0; j < g.length; j++) v = (v << 4) | _HEX[g.charCodeAt(j)];
    buf[off] = v >> 8; buf[off + 1] = v & 0xff;
    off += 2;
  }
  off += zeros * 2;
  for (let i = 0; i < rg.length; i++) {
    const g = rg[i];
    let v = 0;
    for (let j = 0; j < g.length; j++) v = (v << 4) | _HEX[g.charCodeAt(j)];
    buf[off] = v >> 8; buf[off + 1] = v & 0xff;
    off += 2;
  }
  if (hasV4) {
    buf[12] = (v4Int >>> 24); buf[13] = (v4Int >>> 16) & 0xff;
    buf[14] = (v4Int >>> 8) & 0xff; buf[15] = v4Int & 0xff;
  }
  if (buf[10] === 0xff && buf[11] === 0xff
    && buf[0] === 0 && buf[1] === 0 && buf[2] === 0 && buf[3] === 0
    && buf[4] === 0 && buf[5] === 0 && buf[6] === 0 && buf[7] === 0
    && buf[8] === 0 && buf[9] === 0) {
    return { v4: ((buf[12] << 24) | (buf[13] << 16) | (buf[14] << 8) | buf[15]) >>> 0, v6: null };
  }
  return { v4: null, v6: buf };
}

// ===========================================================================
// QzdbRegistry（多库命名注册表，§1 / README §9）
// ===========================================================================
class QzdbRegistry {
  constructor() {
    this._map = Object.create(null);
    // 退休队列：register()/unregister() 用它延迟关闭被替换/移除的 reader，
    // 而不是立即同步 close()。
    //
    // 原因：get(name) 把 reader 对象本体交给调用方；Node 是单线程事件循环，
    // 但异步代码依然可能交错执行——调用方完全可能写出
    //   const r = registry.get('geo');
    //   await someAsyncOp();
    //   const info = r.find(ip);
    // 而 await 让出期间恰好有另一次 register('geo', newPath) 在事件循环里跑完。
    // QzdbReader.close() 本身是内存安全的（只是把内部 Buffer 换成空的，
    // find() 系列方法都有 _closed 检查后 fail-safe 返回 null，不会抛异常
    // 更不会读取已释放内存），但这意味着上面那段代码会静默拿到 null——
    // 比异常更难排查的一类 bug。register/unregister 是运维触发的低频动作，
    // 而一次 find() 调用之间的 await 间隙通常是毫秒级，把刚替换下来的 reader
    // 在有限容量队列里多留几轮，几乎总能让在途调用安全完成，同时把最坏情况
    // （reader 一直不被关闭）限制在 QUARANTINE_CAPACITY 个以内。
    this._quarantine = [];
  }
  _retire(old) {
    if (!old) return;
    this._quarantine.push(old);
    while (this._quarantine.length > QzdbRegistry.QUARANTINE_CAPACITY) {
      const evicted = this._quarantine.shift();
      try { evicted.close(); } catch (e) { /* ignore */ }
    }
  }
  register(name, path) {
    if (!name || !path) throw new QzdbError('Name and path must not be empty', QzdbError.INVALID_PARAM);
    const reader = new QzdbReader.Builder(path).build();
    const old = this._map[name];
    this._map[name] = reader;
    this._retire(old);
    return reader;
  }
  registerBuffer(name, buffer) {
    if (!name || !buffer) throw new QzdbError('Name and buffer must not be empty', QzdbError.INVALID_PARAM);
    const reader = new QzdbReader.Builder(buffer).build();
    const old = this._map[name];
    this._map[name] = reader;
    this._retire(old);
    return reader;
  }
  get(name) { return name == null ? null : (this._map[name] || null); }
  unregister(name) {
    const removed = name ? this._map[name] : null;
    if (removed) { delete this._map[name]; this._retire(removed); }
  }
  clear() {
    // clear() 视为终止关闭动作（而非热更新），同步立即 close，
    // 与直接调用某个 reader 的 close() 语义一致；调用方在调用 clear() 时
    // 应已停止发起查询。退休队列中尚未关闭的旧 reader 一并冲刷关闭。
    for (const k of Object.keys(this._map)) { try { this._map[k].close(); } catch (e) { /* ignore */ } }
    this._map = Object.create(null);
    let q;
    while ((q = this._quarantine.shift())) { try { q.close(); } catch (e) { /* ignore */ } }
  }
}
QzdbRegistry.QUARANTINE_CAPACITY = 8;
// 进程全局快捷方式
const _GLOBAL_REG = new QzdbRegistry();
QzdbRegistry.registerGlobal = (name, path) => _GLOBAL_REG.register(name, path);
QzdbRegistry.registerGlobalBuffer = (name, buffer) => _GLOBAL_REG.registerBuffer(name, buffer);
QzdbRegistry.getGlobal = (name) => _GLOBAL_REG.get(name);
QzdbRegistry.unregisterGlobal = (name) => _GLOBAL_REG.unregister(name);
QzdbRegistry.clearGlobal = () => _GLOBAL_REG.clear();

// ===========================================================================
// ChainedReader（多库链式组合，§1 / README §8）
// ===========================================================================
class ChainedReader {
  constructor(readers, mode) {
    if (!readers || readers.length === 0) {
      throw new QzdbError('ChainedReader requires at least one QzdbReader', QzdbError.INVALID_PARAM);
    }
    this._readers = readers.slice();
    this._mode = mode;
  }
  static chain(...readers) { return new ChainedReader(readers, 'FALLBACK'); }
  static chainMerge(...readers) { return new ChainedReader(readers, 'MERGE'); }
  static chainMergeOverride(...readers) { return new ChainedReader(readers, 'MERGE_OVERRIDE'); }

  find(ipStr) {
    if (this._mode === 'FALLBACK') {
      for (const r of this._readers) {
        const res = r.find(ipStr);
        if (res !== null) return res;
      }
      return null;
    }
    const merged = Object.create(null);
    for (const r of this._readers) {
      const res = r.find(ipStr);
      if (res === null) continue;
      const fields = res.fieldNames();
      const vals = res.values();
      for (let i = 0; i < fields.length; i++) {
        const f = fields[i];
        const v = i < vals.length ? vals[i] : '';
        if (this._mode === 'MERGE') {
          if (!(f in merged) || merged[f] === '') merged[f] = v;
        } else {
          if (v !== '' || !(f in merged)) merged[f] = v;
        }
      }
    }
    if (Object.keys(merged).length === 0) return null;
    const fieldNames = Object.keys(merged);
    const values = fieldNames.map((f) => merged[f]);
    return new GeoInfo(values, fieldNames, fieldNames.map((n) => GeoInfo.isNumericFieldName(n)), null);
  }

  findUint(ipInt) { return this.find(uintToStr(ipInt)); }
  findBytes(ip16) {
    if (this._mode === 'FALLBACK') {
      for (const r of this._readers) {
        const res = r.findBytes(ip16);
        if (res !== null) return res;
      }
      return null;
    }
    return this.find(bytesToStr(ip16));
  }

  findFields(ipStr, fields) {
    const full = this.find(ipStr);
    if (full === null || fields == null || fields.length === 0) return full;
    const values = fields.map((f) => full.get(f));
    return new GeoInfo(values, fields.slice(), fields.map((f) => GeoInfo.isNumericFieldName(f)), null);
  }

  findBatch(ips) {
    if (ips == null) return [];
    const out = [];
    for (const ip of ips) out.push(new BatchResult(ip, this.find(ip), null));
    return out;
  }
  findBatchFields(ips, fields) {
    if (ips == null) return [];
    const out = [];
    for (const ip of ips) out.push(new BatchResult(ip, this.findFields(ip, fields), null));
    return out;
  }
  *findStream(ips) {
    if (ips == null) return;
    for (const ip of ips) yield new BatchResult(ip, this.find(ip), null);
  }

  editions() { return this._readers.map((r) => r.getEdition()); }
  scopes() { return this._readers.map((r) => r.getScope()); }
  dataMonths() { return this._readers.map((r) => r.getDataMonth()); }
  readers() { return this._readers.slice(); }
}

function uintToStr(ipInt) {
  return `${(ipInt >>> 24) & 0xFF}.${(ipInt >>> 16) & 0xFF}.${(ipInt >>> 8) & 0xFF}.${ipInt & 0xFF}`;
}
function bytesToStr(ip16) {
  if (ip16.length === 4) return uintToStr((((ip16[0] & 0xFF) << 24) | ((ip16[1] & 0xFF) << 16) | ((ip16[2] & 0xFF) << 8) | (ip16[3] & 0xFF)) >>> 0);
  const b = Buffer.isBuffer(ip16) ? ip16 : Buffer.from(ip16);
  const g = [];
  for (let i = 0; i < 8; i++) g.push(((b[2 * i] << 8) | b[2 * i + 1]).toString(16));
  return g.join(':');
}

// ===========================================================================
// 导出
// ===========================================================================
QzdbReader.GeoInfo = GeoInfo;
QzdbReader.UsageType = UsageType;
QzdbReader.RowIds = RowIds;
QzdbReader.BatchResult = BatchResult;
QzdbReader.QzdbRegistry = QzdbRegistry;
QzdbReader.ChainedReader = ChainedReader;
QzdbReader.QzdbError = QzdbError;
QzdbReader.Error = QzdbError;
QzdbReader.parseIp = fastParseIp;

module.exports = QzdbReader;
// 自描述契约常量（跨语言取值一致，见 FORMAT §10.3）
module.exports.EDITION_BY_BIT = EDITION_BY_BIT;
module.exports.EDITION_FIELD_NAMES = EDITION_FIELD_NAMES;
module.exports.editionFromMask = editionFromMask;
module.exports.EDITION_SOURCE_VERSION_MASK = EDITION_SOURCE_VERSION_MASK;
module.exports.EDITION_SOURCE_METADATA = EDITION_SOURCE_METADATA;
module.exports.EDITION_SOURCE_INFERRED = EDITION_SOURCE_INFERRED;
module.exports.EDITION_SOURCE_UNKNOWN = EDITION_SOURCE_UNKNOWN;
module.exports.FIELD_NAMES_SOURCE_METADATA = FIELD_NAMES_SOURCE_METADATA;
module.exports.FIELD_NAMES_SOURCE_EDITION = FIELD_NAMES_SOURCE_EDITION;
module.exports.FIELD_NAMES_SOURCE_SYNTHETIC = FIELD_NAMES_SOURCE_SYNTHETIC;
// 内部格式化器（FORMAT §10.5 统一契约）——导出仅供边界回归测试使用
module.exports._formatNativeFloat = _formatNativeFloat;
