'use strict';

const fs = require('fs');

function _initCrc32Table() {
  const t = new Uint32Array(256);
  for (let i = 0; i < 256; i++) {
    let c = i;
    for (let j = 0; j < 8; j++)
      if (c & 1) c = (c >>> 1) ^ 0xEDB88320;
      else c >>>= 1;
    t[i] = c >>> 0;
  }
  return t;
}
const CRC32_TABLE = _initCrc32Table();

function _crc32(buf) {
  let crc = 0xFFFFFFFF;
  for (let i = 0; i < buf.length; i++)
    crc = (CRC32_TABLE[(crc ^ buf[i]) & 0xFF] >>> 0) ^ (crc >>> 8);
  return (crc ^ 0xFFFFFFFF) >>> 0;
}

const SENTINEL = 0x80000000;
const FLOAT_FIELDS = new Set(['longitude', 'latitude']);

// Parallel-array storage (not a dict) keeps per-query allocation cheap; floatFlags[i]
// replaces a Set so toPipe() does an O(1) boolean check per field.
class GeoInfo {
  constructor(vals = [], fieldNames = [], floatFlags = null) {
    this._vals = vals;
    this._fieldNames = fieldNames;
    this._floatFlags = floatFlags;
    this._nameToIdx = null;
    for (let i = 0; i < fieldNames.length; i++) {
      this[fieldNames[i]] = vals[i] !== undefined ? vals[i] : '';
    }
  }

  get(name) {
    if (this._nameToIdx === null) {
      this._nameToIdx = {};
      for (let i = 0; i < this._fieldNames.length; i++) {
        this._nameToIdx[this._fieldNames[i]] = i;
      }
    }
    const idx = this._nameToIdx[name];
    if (idx === undefined) return '';
    const v = this._vals[idx];
    return v !== undefined ? v : '';
  }

  toPipe() {
    const names = this._fieldNames;
    const vals = this._vals;
    const flags = this._floatFlags;
    const out = new Array(names.length);
    for (let i = 0; i < names.length; i++) {
      let val = vals[i] !== undefined ? vals[i] : '';
      if (flags && flags[i] && val !== '') {
        const num = parseFloat(val);
        val = !isNaN(num) ? num.toFixed(6) : val;
      }
      out[i] = String(val);
    }
    return out.join('|');
  }

  toDict() {
    const names = this._fieldNames;
    const vals = this._vals;
    const d = {};
    for (let i = 0; i < names.length; i++) d[names[i]] = vals[i] !== undefined ? vals[i] : '';
    return d;
  }
}

class QzdbSearcher {
  constructor(dbPath = null, groupIndex = 0) {
    this._data = Buffer.alloc(0);
    this._groupIndex = groupIndex;
    this._fieldNames = [];
    this._floatFieldIndices = new Set();
    this._versionName = '';

    // Header fields
    this._flags = 0;
    this._hasV4 = false;
    this._hasV6 = false;
    this._v4Node24 = false;
    this._v6Node24 = false;
    this._v6JumpBits = 16;
    this._poolCount = 0;
    this._poolIdxSize = 2;
    this._geoCount = 0;
    this._rowCount = 0;
    this._v4RecCount = 0;
    this._v6RecCount = 0;
    this._v4NodeCount = 0;
    this._v6NodeCount = 0;
    this._ipRowSize = 6;
    this._geoEntryGroupCount = 0;

    // Offsets
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

    // Schema and layout cache
    this._groupFieldCounts = [];
    this._groupEntryCounts = [];
    this._groupDimMasks = [];

    this._groupStrides = [];
    this._groupFieldWidths = [];
    this._groupFieldOffsets = [];
    this._groupFieldNative = [];
    this._groupFieldNativeType = [];
    this._groupFieldIds = [];
    this._groupPoolSectionIds = [];

    this._groupPools = null;
    this._poolsLoaded = false;

    if (dbPath !== null) {
      this.load(dbPath);
    }
  }

  static getInstance(dbPath = null, groupIndex = 0) {
    if (!QzdbSearcher._instance) {
      QzdbSearcher._instance = new QzdbSearcher(dbPath, groupIndex);
    } else if (dbPath !== null) {
      QzdbSearcher._instance.load(dbPath);
      QzdbSearcher._instance._groupIndex = groupIndex;
    }
    return QzdbSearcher._instance;
  }

  load(dbPath) {
    this._data = fs.readFileSync(dbPath);
    this._parseHeader();
    return this;
  }

  _readU16(off) {
    return this._data.readUInt16LE(off);
  }

  _readU32(off) {
    return this._data.readUInt32LE(off);
  }

  _readU64(off) {
    return Number(this._data.readBigUInt64LE(off));
  }

  _readU24(off) {
    const d = this._data;
    return d[off] | (d[off + 1] << 8) | (d[off + 2] << 16);
  }

  _readU48(off) {
    const d = this._data;
    return d[off]
      + d[off + 1] * 0x100
      + d[off + 2] * 0x10000
      + d[off + 3] * 0x1000000
      + d[off + 4] * 0x100000000
      + d[off + 5] * 0x10000000000;
  }

  _readUintWidth(off, width) {
    if (width <= 1) {
      return this._data[off];
    } else if (width === 2) {
      return this._readU16(off);
    } else if (width === 3) {
      return this._readU24(off);
    } else {
      return this._readU32(off);
    }
  }

  _parseHeader() {
    const d = this._data;
    if (d.length < 192) {
      throw new Error('File too small for QZDB header');
    }

    const magic = d.toString('ascii', 0, 4);
    if (magic !== 'QZDB') {
      throw new Error('Invalid magic, expected QZDB');
    }

    const fmtVer = d[4];
    if (fmtVer < 1 || fmtVer > 6) {
      throw new Error(`Unsupported format version: ${fmtVer}`);
    }

    this._flags = this._readU16(8);
    this._hasV4 = !!(this._flags & 1);
    this._hasV6 = !!(this._flags & 2);
    this._v4Node24 = !!(this._flags & 0x10);
    this._v6Node24 = !!(this._flags & 0x20);

    this._v6JumpBits = d[11];
    if (this._v6JumpBits === 0) {
      this._v6JumpBits = 16;
    }

    this._poolCount = d[12];
    this._poolIdxSize = d[13];
    this._geoCount = this._readU16(14);
    this._rowCount = this._readU32(20);
    this._v4RecCount = this._readU32(24);
    this._v6RecCount = this._readU32(28);

    const hs = this._readU32(36);
    if (hs !== 192) {
      throw new Error(`Unexpected header size: ${hs}`);
    }

    // Offsets
    this._offRowSchema = this._readU64(40);
    this._offGroupSchema = this._readU64(48);
    this._offV4Jump = this._readU64(64);
    this._offV4Nodes = this._readU64(72);
    this._offV6Jump = this._readU64(80);
    this._offV6Nodes = this._readU64(88);
    this._offIPRow = this._readU64(96);
    this._offGeoEntries = this._readU64(104);
    this._offPools = this._readU64(136);
    this._offMeta = this._readU64(144);

    this._v4NodeCount = this._readU32(152);
    this._v6NodeCount = this._readU32(156);
    this._ipRowSize = this._readU32(160);
    this._geoEntryGroupCount = this._readU32(164);

    // GeoEntryOffsets[4]
    this._groupEntryOffsets = [];
    for (let i = 0; i < 4; i++) {
      this._groupEntryOffsets.push(this._readU48(168 + i * 6));
    }

    // Parse GroupMetadataTable (at offGeoEntries)
    let gmOff = this._offGeoEntries;
    const groupCount = d[gmOff];
    gmOff += 1;

    let actualGroups = Math.min(groupCount, Math.max(1, this._geoEntryGroupCount));
    if (actualGroups > 4) actualGroups = 4;
    this._groupFieldCounts = new Array(actualGroups).fill(0);
    this._groupEntryCounts = new Array(actualGroups).fill(0);
    this._groupDimMasks = new Array(actualGroups).fill(0);

    for (let gi = 0; gi < actualGroups; gi++) {
      this._groupFieldCounts[gi] = d[gmOff];
      gmOff += 1;
      if (fmtVer === 1 || fmtVer >= 4) {
        this._groupEntryCounts[gi] = this._readU32(gmOff);
        gmOff += 4;
      } else {
        this._groupEntryCounts[gi] = this._readU16(gmOff);
        gmOff += 2;
      }

      if (fmtVer === 1 || fmtVer >= 3) {
        this._groupDimMasks[gi] = this._readU16(gmOff);
        gmOff += 2;
      } else {
        this._groupDimMasks[gi] = (gi !== 2) ? 0x01 : 0x02;
      }
    }

    // Initialize schema and widths
    this._groupStrides = new Array(actualGroups).fill(0);
    this._groupFieldWidths = new Array(actualGroups).fill(null);
    this._groupFieldOffsets = new Array(actualGroups).fill(null);
    this._groupFieldNative = new Array(actualGroups).fill(null);
    this._groupFieldNativeType = new Array(actualGroups).fill(null);
    this._groupFieldIds = new Array(actualGroups).fill(null);
    this._groupPoolSectionIds = new Array(actualGroups).fill(null);

    // Parse GROUP_SCHEMA if present
    if (this._offGroupSchema > 0) {
      let sp = this._offGroupSchema;
      const gsGroupCount = this._readU16(sp);
      sp += 2;
      const maxGsGroups = Math.min(gsGroupCount, actualGroups);
      for (let gi = 0; gi < maxGsGroups; gi++) {
        sp += 2; // skip groupId
        const fldCount = this._readU16(sp);
        sp += 2;
        sp += 4; // skip entryCount
        const stride = this._readU32(sp);
        sp += 4;
        sp += 4; // skip flags

        if (gi < actualGroups) {
          this._groupStrides[gi] = stride;
          const widths = new Array(fldCount).fill(0);
          const offsets = new Array(fldCount).fill(0);
          const natives = new Array(fldCount).fill(false);
          const natTypes = new Array(fldCount).fill(0);
          const fieldIds = new Array(fldCount).fill(0);
          const poolSectionIds = new Array(fldCount).fill(0);
          for (let fi = 0; fi < fldCount; fi++) {
            fieldIds[fi] = this._readU16(sp);
            sp += 2;
            widths[fi] = d[sp];
            sp += 1;
            const fieldFlags = d[sp];
            sp += 1;
            natives[fi] = (fieldFlags & 0x01) !== 0;
            natTypes[fi] = (fieldFlags >> 1) & 0x03;
            offsets[fi] = this._readU32(sp);
            sp += 4;
            poolSectionIds[fi] = this._readU32(sp);
            sp += 4;
          }
          this._groupFieldWidths[gi] = widths;
          this._groupFieldOffsets[gi] = offsets;
          this._groupFieldNative[gi] = natives;
          this._groupFieldNativeType[gi] = natTypes;
          this._groupFieldIds[gi] = fieldIds;
          this._groupPoolSectionIds[gi] = poolSectionIds;
        } else {
          sp += fldCount * 12;
        }
      }
    }

    // Fallback for groups without schema info
    for (let g = 0; g < actualGroups; g++) {
      if (this._groupStrides[g] === 0) {
        this._groupStrides[g] = this._groupFieldCounts[g] * this._poolIdxSize;
      }
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

    this._resolveFieldNames();
    this._poolsLoaded = false;
    this._groupPools = null;
  }

  _resolveFieldNames() {
    const d = this._data;
    const offMeta = this._offMeta;
    if ((this._flags & 4) && offMeta > 0 && offMeta + 4 <= d.length) {
      let fieldNames = null;
      let pos = offMeta;
      while (pos + 4 <= d.length) {
        const t = d[pos];
        const length = this._readU16(pos + 2);
        if (t === 0 || length === 0) {
          break;
        }
        const val = d.toString('utf8', pos + 4, pos + 4 + length);
        if (t === 1) {
          this._versionName = val;
        } else if (t === 2) {
          fieldNames = val.split('|');
        }
        pos += 4 + length;
      }

      if (fieldNames && fieldNames.length === this._groupFieldCounts[0]) {
        this._fieldNames = fieldNames;
        this._floatFlags = fieldNames.map(n => FLOAT_FIELDS.has(n));
        return;
      }
    }

    // Fallback placeholder names
    this._fieldNames = Array.from({ length: this._groupFieldCounts[0] }, (_, i) => `field_${i}`);
    this._floatFlags = new Array(this._groupFieldCounts[0]).fill(false);
  }

  getFloatIndices() {
    const out = [];
    for (let i = 0; i < this._floatFlags.length; i++) {
      if (this._floatFlags[i]) out.push(i);
    }
    return out;
  }

  _ensurePoolsLoaded() {
    if (this._poolsLoaded) {
      return;
    }
    this._poolsLoaded = true;

    const groupCount = this._groupFieldCounts.length;
    this._groupPools = new Array(groupCount).fill(null);

    if (this._offPools <= 0) {
      return;
    }

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

        if (poolCursor + 4 > poolEnd) {
          groupPoolList.push([]);
          continue;
        }
        const count = this._readU32(poolCursor);
        poolCursor += 4;
        if (this._offRowSchema > 0) {
          poolCursor += 4;
        }
        if (count === 0) {
          groupPoolList.push([]);
          continue;
        }

        // Read string offsets
        const offsets = [];
        for (let o = 0; o <= count; o++) {
          offsets.push(this._readU32(poolCursor));
          poolCursor += 4;
        }

        // Read string data
        const strings = new Array(count);
        for (let s = 0; s < count; s++) {
          const start = offsets[s];
          const end = offsets[s + 1];
          const length = end - start;
          if (length > 0) {
            strings[s] = d.toString('utf8', poolCursor + start, poolCursor + end);
          } else {
            strings[s] = '';
          }
        }
        poolCursor += offsets[count];
        groupPoolList.push(strings);
      }
      this._groupPools[g] = groupPoolList;
    }
  }

  _getV4Child(nodeIdx, bit) {
    if (this._v4Node24) {
      const nodeOffset = this._offV4Nodes + nodeIdx * 6;
      const offset = bit === 0 ? nodeOffset : nodeOffset + 3;
      const val = this._data[offset] | (this._data[offset + 1] << 8) | (this._data[offset + 2] << 16);
      if (val & 0x800000) {
        return (val & 0x7FFFFF) | SENTINEL;
      }
      return val;
    } else {
      const childOff = this._offV4Nodes + nodeIdx * 8 + bit * 4;
      return this._readU32(childOff);
    }
  }

  _getV6Child(nodeIdx, bit) {
    if (this._v6Node24) {
      const nodeOffset = this._offV6Nodes + nodeIdx * 6;
      const offset = bit === 0 ? nodeOffset : nodeOffset + 3;
      const val = this._data[offset] | (this._data[offset + 1] << 8) | (this._data[offset + 2] << 16);
      if (val & 0x800000) {
        return (val & 0x7FFFFF) | SENTINEL;
      }
      return val;
    } else {
      const childOff = this._offV6Nodes + nodeIdx * 8 + bit * 4;
      return this._readU32(childOff);
    }
  }

  _trieWalkV4(ipInt) {
    const d = this._data;
    const hi16 = (ipInt >>> 16) & 0xFFFF;
    const ptr = this._readU32(this._offV4Jump + hi16 * 4);

    if (ptr === 0) {
      return 0;
    }
    if (ptr & SENTINEL) {
      return ptr & 0x7FFFFFFF;
    }

    let idx = ptr;
    let suffix = (ipInt & 0xFFFF) << 16;

    while (true) {
      const bit = (suffix >>> 31) & 1;
      const child = this._getV4Child(idx, bit);

      if (child === 0) {
        return 0;
      }
      if (child & SENTINEL) {
        return child & 0x7FFFFFFF;
      }

      idx = child;
      suffix <<= 1;
    }
  }

  _trieWalkV6(ipInt) {
    const shift = 128 - this._v6JumpBits;
    const idxJump = Number(ipInt >> BigInt(shift)) & ((1 << this._v6JumpBits) - 1);
    const ptr = this._readU32(this._offV6Jump + idxJump * 4);

    if (ptr === 0) {
      return 0;
    }
    if (ptr & SENTINEL) {
      return ptr & 0x7FFFFFFF;
    }

    let idx = ptr;
    let depth = this._v6JumpBits;

    while (depth < 128) {
      const bit = Number((ipInt >> BigInt(127 - depth)) & 1n);
      const child = this._getV6Child(idx, bit);

      if (child === 0) {
        return 0;
      }
      if (child & SENTINEL) {
        return child & 0x7FFFFFFF;
      }

      idx = child;
      depth += 1;
    }

    return 0;
  }

  _readIPRow(rowId) {
    if (rowId <= 0 || rowId >= this._rowCount) {
      return [0, 0, 0];
    }
    const off = this._offIPRow + rowId * this._ipRowSize;
    const geoId = this._readU24(off);
    const asnId = this._readU24(off + 3);

    let usageTypeId = 0;
    if (this._ipRowSize >= 9) {
      usageTypeId = this._readU24(off + 6);
    }

    return [geoId, asnId, usageTypeId];
  }

  _resolveRowId(rowId, groupIndex) {
    const [geoId, asnId, usageTypeId] = this._readIPRow(rowId);
    const mask = groupIndex < this._groupDimMasks.length ? this._groupDimMasks[groupIndex] : 0;

    let entryId = 0;
    if (mask & 0x02) {
      entryId = asnId;
    } else if (mask & 0x04) {
      entryId = usageTypeId;
    } else {
      entryId = geoId;
    }

    if (entryId === 0) {
      return null;
    }
    return this._resolveGeo(entryId, groupIndex);
  }

  _resolveGeo(entryId, groupIndex) {
    if (groupIndex < 0 || groupIndex >= this._groupFieldCounts.length) {
      return null;
    }
    if (entryId < 0 || entryId >= this._groupEntryCounts[groupIndex]) {
      return null;
    }

    this._ensurePoolsLoaded();

    const fieldCount = this._groupFieldCounts[groupIndex];
    if (fieldCount <= 0) {
      return null;
    }

    const groupEntryStart = this._offGeoEntries + this._groupEntryOffsets[groupIndex];
    const stride = this._groupStrides[groupIndex];
    const entryOffset = groupEntryStart + entryId * stride;
    const d = this._data;

    const widths = this._groupFieldWidths[groupIndex];
    const baseOffsets = this._groupFieldOffsets[groupIndex];
    const natives = this._groupFieldNative[groupIndex];
    const natTypes = this._groupFieldNativeType[groupIndex];
    const groupPool = this._groupPools[groupIndex];
    const names = this._fieldNames;
    const vals = new Array(fieldCount);

    for (let i = 0; i < fieldCount; i++) {
      const w = widths[i];
      const fo = entryOffset + baseOffsets[i];
      const isNative = natives && i < natives.length && natives[i];

      let val;
      if (isNative) {
        const t = natTypes && i < natTypes.length ? natTypes[i] : 0;
        if (t === 1) {
          val = Number(w === 4 ? d.readFloatLE(fo) : d.readDoubleLE(fo)).toFixed(6);
        } else if (w <= 1) {
          val = String(d[fo]);
        } else if (w === 2) {
          val = String((d[fo] | (d[fo + 1] << 8)) >>> 0);
        } else if (w === 3) {
          val = String((d[fo] | (d[fo + 1] << 8) | (d[fo + 2] << 16)) >>> 0);
        } else {
          val = String((d[fo] | (d[fo + 1] << 8) | (d[fo + 2] << 16) | (d[fo + 3] << 24)) >>> 0);
        }
      } else {
        let idx;
        if (w <= 1) idx = d[fo];
        else if (w === 2) idx = d[fo] | (d[fo + 1] << 8);
        else if (w === 3) idx = d[fo] | (d[fo + 1] << 8) | (d[fo + 2] << 16);
        else idx = d[fo] | (d[fo + 1] << 8) | (d[fo + 2] << 16) | (d[fo + 3] << 24);

        let fieldId = i;
        if (this._groupFieldIds[groupIndex] && i < this._groupFieldIds[groupIndex].length) {
          fieldId = this._groupFieldIds[groupIndex][i];
        }

        // Pools are indexed by field position, not poolSectionId
        if (groupPool && i < groupPool.length && idx >= 0 && idx < groupPool[i].length) {
          val = groupPool[i][idx];
        } else {
          val = '';
        }
      }
      vals[i] = val;
    }

    return new GeoInfo(vals, names, this._floatFlags);
  }

  find(ipStr) {
    if (!ipStr) {
      return null;
    }

    if (ipStr.includes(':')) {
      if (ipStr.includes('.')) {
        const idx = ipStr.indexOf('::ffff:');
        if (idx >= 0 && ipStr.substring(0, idx + 7) === '::ffff:') {
          const v4 = ipStr.substring(ipStr.lastIndexOf(':') + 1);
          const p4 = v4.split('.');
          if (p4.length === 4) {
            const ipInt = (+p4[0] << 24) | (+p4[1] << 16) | (+p4[2] << 8) | +p4[3];
            return this.findUint(ipInt >>> 0);
          }
        }
      }
      const p = parseIPv6(ipStr);
      if (p === null) {
        return null;
      }
      // Check for IPv4-mapped IPv6 (::ffff:x.x.x.x)
      if (p === 0n) {
        return null;
      }
      const isV4Mapped = (p >> 32n) === 0xffffn && (p >> 48n) === 0n;
      if (isV4Mapped) {
        return this.findUint(Number(p & 0xffffffffn));
      }
      return this.findV6Uint(p);
    }

    const ipInt = fastParseIp(ipStr);
    if (ipInt === null) {
      return null;
    }
    return this.findUint(ipInt);
  }

  findUint(ipInt) {
    if (!this._hasV4) {
      return null;
    }
    const rowId = this._trieWalkV4(ipInt);
    if (rowId === 0) {
      return null;
    }
    return this._resolveRowId(rowId, this._groupIndex);
  }

  findV6Uint(ipInt) {
    if (!this._hasV6) {
      return null;
    }
    const rowId = this._trieWalkV6(ipInt);
    if (rowId === 0) {
      return null;
    }
    return this._resolveRowId(rowId, this._groupIndex);
  }

  // High/low are BigInts (each 64-bit) forming the 128-bit address.
  findV6(high, low) {
    if (!this._hasV6) {
      return null;
    }
    const ipInt = (BigInt(high) << 64n) | (BigInt(low) & 0xFFFFFFFFFFFFFFFFn);
    const rowId = this._trieWalkV6(ipInt);
    if (rowId === 0) {
      return null;
    }
    return this._resolveRowId(rowId, this._groupIndex);
  }

  findStr(ipStr) {
    const info = this.find(ipStr);
    if (info === null) {
      return '';
    }
    return info.toPipe();
  }

  get version() {
    return this._versionName;
  }

  get field_names() {
    return this._fieldNames;
  }

  get version_code() {
    const pcMap = { 6: 1, 7: 2, 25: 3 };
    return pcMap[this._poolCount] !== undefined ? pcMap[this._poolCount] : 3;
  }

  get pool_count() {
    return this._poolCount;
  }

  verifyCrc() {
    if (this._data.length < 20) {
      return false;
    }
    const stored = this._data.readUInt32LE(16);
    const original = Buffer.alloc(4);
    this._data.copy(original, 0, 16, 20);
    const copy = Buffer.from(this._data);
    copy.writeUInt32LE(0, 16);
    const computed = _crc32(copy);
    original.copy(this._data, 16, 0, 4);
    return stored === computed;
  }
}

function fastParseIp(ip) {
  let result = 0, val = 0, dots = 0;
  for (let i = 0; i < ip.length; i++) {
    const c = ip.charCodeAt(i);
    if (c >= 48 && c <= 57) {
      val = val * 10 + (c - 48);
      if (val > 255) return null;
    } else if (c === 46) {
      if (i === 0 || ip.charCodeAt(i - 1) === 46) return null;
      result = (result << 8) | val;
      val = 0;
      dots++;
    } else {
      return null;
    }
  }
  if (dots !== 3) return null;
  if (ip.charCodeAt(ip.length - 1) === 46) return null;
  return ((result << 8) | val) >>> 0;
}

function parseIPv6(str) {
  const idx = str.indexOf('%');
  if (idx >= 0) str = str.substring(0, idx);
  const parts = str.split(':');
  // Count non-empty groups manually, avoiding .filter() intermediate array.
  let nonEmpty = 0;
  for (let i = 0; i < parts.length; i++) {
    if (parts[i] !== '') nonEmpty++;
  }
  const fill = 8 - nonEmpty;
  // Build the BigInt directly, avoiding the intermediate expanded[] array.
  let val = 0n;
  let filled = false;
  let count = 0;
  for (const p of parts) {
    if (p === '' && !filled) {
      for (let j = 0; j < fill; j++) { val = (val << 16n); count++; }
      filled = true;
    } else if (p !== '') {
      const parsed = parseInt(p, 16);
      if (isNaN(parsed)) return null;
      val = (val << 16n) | BigInt(parsed);
      count++;
    }
  }
  if (count !== 8) return null;
  return val;
}

module.exports = QzdbSearcher;
