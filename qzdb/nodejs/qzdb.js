'use strict';

const fs = require('fs');

const SENTINEL = 0x80000000;
const SENTINEL_MASK_24 = 0x7FFFFF;
const SENTINEL_MASK_31 = 0x7FFFFFFF;
const MAX_TRIE_WALK_STEPS = 1000;

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

const FLOAT_FIELDS = new Set(['longitude', 'latitude']);

// Reserved keys that must never be overwritten by DB field names (prototype pollution / method clobber).
const GEOINFO_RESERVED = new Set([
  '_vals', '_fieldNames', '_floatFlags', '_nameToIdx',
  'get', 'toPipe', 'constructor', '__proto__', 'toString', 'valueOf', 'hasOwnProperty',
]);

// Parallel-array storage (not a dict) keeps per-query allocation cheap; floatFlags[i]
// replaces a Set so toPipe() does an O(1) boolean check per field.
class GeoInfo {
  constructor(vals = [], fieldNames = [], floatFlags = null) {
    this._vals = vals;
    this._fieldNames = fieldNames;
    this._floatFlags = floatFlags;
    this._nameToIdx = Object.create(null);
    for (let i = 0; i < fieldNames.length; i++) {
      const name = fieldNames[i];
      this._nameToIdx[name] = i;
      // Expose fields as own props for DX (info.country), but never clobber internals/methods.
      if (!GEOINFO_RESERVED.has(name)) {
        this[name] = vals[i] !== undefined ? vals[i] : '';
      }
    }
  }

  get(name) {
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
    this._fieldNameToIdx = {};
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
      try {
        QzdbSearcher._instance = new QzdbSearcher(dbPath, groupIndex);
      } catch (error) {
        if (error instanceof QzdbError) {
          throw error;
        }
        throw new QzdbError(error.message, QzdbError.CORRUPTED);
      }
    } else if (dbPath !== null) {
      try {
        QzdbSearcher._instance.load(dbPath);
        QzdbSearcher._instance._groupIndex = groupIndex;
      } catch (error) {
        if (error instanceof QzdbError) {
          throw error;
        }
        throw new QzdbError(error.message, QzdbError.CORRUPTED);
      }
    }
    return QzdbSearcher._instance;
  }

  load(dbPath) {
    this._data = fs.readFileSync(dbPath);
    this._parseHeader();
    return this;
  }

  safeReadU16(off) {
    return this._data.readUInt16LE(off);
  }

  safeReadU32(off) {
    return this._data.readUInt32LE(off);
  }

  safeReadU64(off) {
    return Number(this._data.readBigUInt64LE(off));
  }

  safeReadU24(off) {
    const d = this._data;
    return d[off] | (d[off + 1] << 8) | (d[off + 2] << 16);
  }

  safeReadU48(off) {
    const d = this._data;
    return d[off]
      + d[off + 1] * 0x100
      + d[off + 2] * 0x10000
      + d[off + 3] * 0x1000000
      + d[off + 4] * 0x100000000
      + d[off + 5] * 0x10000000000;
  }

  safeReadUintWidth(off, width) {
    if (width <= 1) {
      return this._data[off];
    } else if (width === 2) {
      return this.safeReadU16(off);
    } else if (width === 3) {
      return this.safeReadU24(off);
    } else {
      return this.safeReadU32(off);
    }
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
    if (fmtVer < 1 || fmtVer > 6) {
      throw new QzdbError(`Unsupported format version: ${fmtVer}`, QzdbError.UNSUPPORTED);
    }

    this._flags = this.safeReadU16(8);
    this._hasV4 = !!(this._flags & 1);
    this._hasV6 = !!(this._flags & 2);
    this._v4Node24 = !!(this._flags & 0x10);
    this._v6Node24 = !!(this._flags & 0x20);

    this._v6JumpBits = d[11];
    if (this._v6JumpBits === 0) {
      this._v6JumpBits = 16;
    }
    if (this._v6JumpBits < 16 || this._v6JumpBits > 20) {
      throw new QzdbError(`v6JumpBits out of range [16,20]: ${this._v6JumpBits}`, QzdbError.INVALID_PARAM);
    }

    this._poolCount = d[12];
    this._poolIdxSize = d[13];
    if (this._poolIdxSize !== 2 && this._poolIdxSize !== 3) {
      throw new QzdbError(`poolIdxSize must be 2 or 3, got ${this._poolIdxSize}`, QzdbError.INVALID_PARAM);
    }
    this._geoCount = this.safeReadU16(14);
    this._rowCount = this.safeReadU32(20);
    this._v4RecCount = this.safeReadU32(24);
    this._v6RecCount = this.safeReadU32(28);

    const hs = this.safeReadU32(36);
    if (hs !== 192) {
      throw new QzdbError(`Unexpected header size: ${hs}`, QzdbError.BAD_HEADER);
    }

    // Offsets
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

    // Bounds validation for section offsets
    const dlen = d.length;
    const v4NodeSize = this._v4Node24 ? 6 : 8;
    const v6NodeSize = this._v6Node24 ? 6 : 8;
    const v6JumpSize = (1 << this._v6JumpBits) * 4;

    if (this._offV4Jump > 0 && this._offV4Jump + 65536 * 4 > dlen) {
      throw new QzdbError('V4 jump table offset out of bounds', QzdbError.OUT_OF_BOUNDS);
    }
    if (this._offV4Nodes > 0 && this._offV4Nodes + this._v4NodeCount * v4NodeSize > dlen) {
      throw new QzdbError('V4 nodes table offset out of bounds', QzdbError.OUT_OF_BOUNDS);
    }
    if (this._offV6Jump > 0 && this._offV6Jump + v6JumpSize > dlen) {
      throw new QzdbError('V6 jump table offset out of bounds', QzdbError.OUT_OF_BOUNDS);
    }
    if (this._offV6Nodes > 0 && this._offV6Nodes + this._v6NodeCount * v6NodeSize > dlen) {
      throw new QzdbError('V6 nodes table offset out of bounds', QzdbError.OUT_OF_BOUNDS);
    }
    if (this._offIPRow > 0 && this._offIPRow + this._rowCount * this._ipRowSize > dlen) {
      throw new QzdbError('IP row table offset out of bounds', QzdbError.OUT_OF_BOUNDS);
    }

    // GeoEntryOffsets[4]
    this._groupEntryOffsets = [];
    for (let i = 0; i < 4; i++) {
      this._groupEntryOffsets.push(this.safeReadU48(168 + i * 6));
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
        this._groupEntryCounts[gi] = this.safeReadU32(gmOff);
        gmOff += 4;
      } else {
        this._groupEntryCounts[gi] = this.safeReadU16(gmOff);
        gmOff += 2;
      }

      if (fmtVer === 1 || fmtVer >= 3) {
        this._groupDimMasks[gi] = this.safeReadU16(gmOff);
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
      const gsGroupCount = this.safeReadU16(sp);
      sp += 2;
      const maxGsGroups = Math.min(gsGroupCount, actualGroups);
      for (let gi = 0; gi < maxGsGroups; gi++) {
        sp += 2; // skip groupId
        const fldCount = this.safeReadU16(sp);
        sp += 2;
        sp += 4; // skip entryCount
        const stride = this.safeReadU32(sp);
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
            poolSectionIds[fi] = this.safeReadU32(sp);
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
        const length = this.safeReadU16(pos + 2);
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
        this._fieldNameToIdx = Object.create(null);
        for (let i = 0; i < fieldNames.length; i++) this._fieldNameToIdx[fieldNames[i]] = i;
        this._floatFlags = fieldNames.map(n => FLOAT_FIELDS.has(n));
        return;
      }
    }

    // Fallback placeholder names
    this._fieldNames = Array.from({ length: this._groupFieldCounts[0] }, (_, i) => `field_${i}`);
    this._fieldNameToIdx = Object.create(null);
    for (let i = 0; i < this._fieldNames.length; i++) this._fieldNameToIdx[this._fieldNames[i]] = i;
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
        const count = this.safeReadU32(poolCursor);
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
          offsets.push(this.safeReadU32(poolCursor));
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
        return (val & SENTINEL_MASK_24) | SENTINEL;
      }
      return val;
    } else {
      const childOff = this._offV4Nodes + nodeIdx * 8 + bit * 4;
      return this.safeReadU32(childOff);
    }
  }

  _getV6Child(nodeIdx, bit) {
    if (this._v6Node24) {
      const nodeOffset = this._offV6Nodes + nodeIdx * 6;
      const offset = bit === 0 ? nodeOffset : nodeOffset + 3;
      const val = this._data[offset] | (this._data[offset + 1] << 8) | (this._data[offset + 2] << 16);
      if (val & 0x800000) {
        return (val & SENTINEL_MASK_24) | SENTINEL;
      }
      return val;
    } else {
      const childOff = this._offV6Nodes + nodeIdx * 8 + bit * 4;
      return this.safeReadU32(childOff);
    }
  }

  _trieWalkV4(ipInt) {
    const d = this._data;
    const hi16 = (ipInt >>> 16) & 0xFFFF;
    const ptr = this.safeReadU32(this._offV4Jump + hi16 * 4);

    if (ptr === 0) {
      return 0;
    }
    if (ptr & SENTINEL) {
      return ptr & SENTINEL_MASK_31;
    }

    let idx = ptr;
    let suffix = (ipInt & 0xFFFF) << 16;
    let steps = 0;

    while (true) {
      if (++steps >= MAX_TRIE_WALK_STEPS) return 0;
      const bit = (suffix >>> 31) & 1;
      const child = this._getV4Child(idx, bit);

      if (child === 0) {
        return 0;
      }
      if (child & SENTINEL) {
        return child & SENTINEL_MASK_31;
      }

      idx = child;
      suffix <<= 1;
    }
  }

  // PERF-03: Buffer-based V6 trie walk (no BigInt, operates on raw bytes)
  _trieWalkV6Buf(ipBuf) {
    // Extract jump index using 4×uint32 approach (no BigInt)
    const jumpBits = this._v6JumpBits;
    let idxJump = 0;
    if (jumpBits <= 32) {
      // All jump bits fit in first 4 bytes
      const b0 = ipBuf[0], b1 = ipBuf[1], b2 = ipBuf[2], b3 = ipBuf[3];
      if (jumpBits <= 8) idxJump = b0 >> (8 - jumpBits);
      else if (jumpBits <= 16) idxJump = ((b0 << 8) | b1) >> (16 - jumpBits);
      else if (jumpBits <= 24) idxJump = ((b0 << 16) | (b1 << 8) | b2) >> (24 - jumpBits);
      else idxJump = ((b0 << 24) | (b1 << 16) | (b2 << 8) | b3) >> (32 - jumpBits);
    } else {
      // Jump bits span into second dword
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

  _readIPRow(rowId) {
    if (rowId <= 0 || rowId >= this._rowCount) {
      return [0, 0, 0];
    }
    const off = this._offIPRow + rowId * this._ipRowSize;
    const geoId = this.safeReadU24(off);
    const asnId = this.safeReadU24(off + 3);

    let usageTypeId = 0;
    if (this._ipRowSize >= 9) {
      usageTypeId = this.safeReadU24(off + 6);
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
    if (!ipStr) return null;
    const result = fastParseIp(ipStr);
    if (!result) return null;
    if (result.v4 !== null) return this.findUint(result.v4);
    if (!this._hasV6) return null;
    const rowId = this._trieWalkV6Buf(result.v6);
    if (rowId === 0) return null;
    return this._resolveRowId(rowId, this._groupIndex);
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
    // One-shot BigInt→bytes, then zero-BigInt walk (same as string path).
    const rowId = this._trieWalkV6Buf(_bigint128ToBuf(ipInt));
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
    const buf = _highLowToBuf(high, low);
    const rowId = this._trieWalkV6Buf(buf);
    if (rowId === 0) {
      return null;
    }
    return this._resolveRowId(rowId, this._groupIndex);
  }

  findFields(ipStr, fieldNames = null) {
    if (fieldNames === null || fieldNames.length === 0) {
      return this.find(ipStr);
    }
    const rowId = this.lookupRowId(ipStr);
    if (rowId === 0) return null;
    const ids = this.lookupIds(rowId);
    if (ids === null) return null;
    const [geoId, asnId, usageTypeId] = ids;
    const mask = this._groupDimMasks[this._groupIndex] || 0;
    const entryId = (mask & 0x02) ? asnId : ((mask & 0x04) ? usageTypeId : geoId);
    if (entryId === 0) return null;
    const indices = [];
    for (const name of fieldNames) {
      const idx = this._fieldNameToIdx[name];
      if (idx !== undefined) indices.push(idx);
    }
    if (indices.length === 0) return null;
    const fields = this._resolveGeoFields(entryId, this._groupIndex, indices);
    return new GeoInfo(fields, this._fieldNames, this._floatFlags);
  }

  _resolveGeoFields(entryId, groupIndex, indices) {
    this._ensurePoolsLoaded();
    const gc = this._groupFieldCounts[groupIndex];
    if (gc <= 0) return {};
    const groupEntryStart = this._offGeoEntries + this._groupEntryOffsets[groupIndex];
    const stride = this._groupStrides[groupIndex];
    const entryOffset = groupEntryStart + entryId * stride;
    const d = this._data;
    const widths = this._groupFieldWidths[groupIndex];
    const baseOffsets = this._groupFieldOffsets[groupIndex];
    const natives = this._groupFieldNative[groupIndex];
    const natTypes = this._groupFieldNativeType[groupIndex];
    const fields = {};
    for (const i of indices) {
      if (i < 0 || i >= gc) continue;
      const w = widths[i];
      const fo = entryOffset + baseOffsets[i];
      let val = '';
      if (natives && natives[i]) {
        const t = (natTypes && natTypes[i]) || 0;
        if (t === 1) {
          val = w === 4 ? String(d.readFloatLE(fo)) : String(d.readDoubleLE(fo));
        } else {
          val = String(this.safeReadUintWidth(fo, w));
        }
      } else {
        const poolIdx = this.safeReadUintWidth(fo, w);
        const gp = this._groupPools[groupIndex];
        if (gp && i < gp.length && poolIdx < gp[i].length) {
          val = gp[i][poolIdx];
        }
      }
      fields[this._fieldNames[i]] = val;
    }
    return fields;
  }

  // Atomically replace database state with a fresh load
  reload(dbPath) {
    this.load(dbPath);
  }

  lookupRowId(ipStr) {
    if (!ipStr) return 0;
    const result = fastParseIp(ipStr);
    if (!result) return 0;
    if (result.v4 !== null) return this.lookupRowIdUint(result.v4);
    if (!this._hasV6) return 0;
    return this._trieWalkV6Buf(result.v6);
  }

  lookupRowIdUint(ipInt) {
    if (!this._hasV4) return 0;
    return this._trieWalkV4(ipInt);
  }

  lookupRowIdV6(ipInt) {
    if (!this._hasV6) return 0;
    return this._trieWalkV6Buf(_bigint128ToBuf(ipInt));
  }

  lookupIds(rowId) {
    if (rowId <= 0 || rowId >= this._rowCount) return null;
    return this._readIPRow(rowId);
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
    if (!this._data || this._data.length < 20) {
      return false;
    }
    const stored = this._data.readUInt32LE(16);
    // Segmented CRC: CRC field counted as zero — no full-buffer copy/mutation.
    const computed = _crc32File(this._data);
    return stored === computed;
  }

  close() {
    this._data = Buffer.alloc(0);
    this._poolsLoaded = false;
    this._groupPools = null;
    this._fieldNames = [];
    this._fieldNameToIdx = {};
    this._floatFieldIndices = new Set();
    this._floatFlags = null;
    this._versionName = '';
  }
}

/** Convert a 128-bit BigInt or high/low pair to a 16-byte big-endian Buffer. */
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

function _crc32Update(crc, buf, start, end) {
  for (let i = start; i < end; i++) {
    crc = (CRC32_TABLE[(crc ^ buf[i]) & 0xFF] >>> 0) ^ (crc >>> 8);
  }
  return crc >>> 0;
}

function _crc32File(buf) {
  let crc = 0xFFFFFFFF;
  crc = _crc32Update(crc, buf, 0, 16);
  // 4 zero bytes for the CRC field
  for (let i = 0; i < 4; i++) {
    crc = (CRC32_TABLE[(crc ^ 0) & 0xFF] >>> 0) ^ (crc >>> 8);
  }
  crc = _crc32Update(crc, buf, 20, buf.length);
  return (crc ^ 0xFFFFFFFF) >>> 0;
}

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
  // Fail-closed: reject any whitespace (no silent trim — SSRF-safe, cross-lang consistent)
  for (let i = 0; i < n; i++) {
    const c = s.charCodeAt(i);
    if (c === 32 || c === 9 || c === 10 || c === 13 || c === 11 || c === 12) return null;
  }
  if (s.indexOf(':') < 0) {
    const v4 = _fastParseIPv4(s);
    if (v4 === null) return null;
    return { v4, v6: null };
  }
  if (s.indexOf('%') >= 0) return null;
  const dc = s.indexOf('::');
  if (dc >= 0 && s.indexOf('::', dc + 2) >= 0) return null;
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
      if (cc >= 128 || _HEX[cc] === 0 && cc !== 48) return null;
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
  if (hasV4) { buf[12] = (v4Int >>> 24); buf[13] = (v4Int >>> 16) & 0xff; buf[14] = (v4Int >>> 8) & 0xff; buf[15] = v4Int & 0xff; }
  if (buf[10] === 0xff && buf[11] === 0xff && buf[0] === 0 && buf[1] === 0 && buf[2] === 0 && buf[3] === 0 && buf[4] === 0 && buf[5] === 0 && buf[6] === 0 && buf[7] === 0 && buf[8] === 0 && buf[9] === 0) {
    return { v4: ((buf[12] << 24) | (buf[13] << 16) | (buf[14] << 8) | buf[15]) >>> 0, v6: null };
  }
  return { v4: null, v6: buf };
}

module.exports = QzdbSearcher;
