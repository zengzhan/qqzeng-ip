const fs = require('fs');

const SENTINEL = 0x80000000;
const FLOAT_FIELDS = new Set(['longitude', 'latitude']);

class GeoInfoV20 {
    constructor(values, fieldNames) {
        this.values = values;
        this.fieldNames = fieldNames;
    }

    get(name) {
        const idx = this.fieldNames.indexOf(name);
        return idx >= 0 && idx < this.values.length ? this.values[idx] : '';
    }

    toPipe(floatIndices) {
        return this.values.map((v, i) => {
            if (floatIndices.has(i) && v) {
                const n = parseFloat(v);
                if (!isNaN(n)) return n.toFixed(6);
            }
            return v;
        }).join('|');
    }
}

class QzdbSearcherV20 {
    constructor(dbPath, groupIndex = 0) {
        this.groupIndex = groupIndex;
        this._floatIndices = new Set();

        const buf = fs.readFileSync(dbPath);
        this._data = buf;

        if (buf.length < 192 || buf.toString('utf8', 0, 4) !== 'QZ20') {
            throw new Error('Invalid V20 file: bad magic or too small');
        }
        this._parseHeader();
    }

    static getInstance(dbPath) {
        if (!QzdbSearcherV20._instance) {
            QzdbSearcherV20._instance = new QzdbSearcherV20(dbPath);
        }
        return QzdbSearcherV20._instance;
    }

    _readU16(off) { return this._data.readUInt16LE(off); }
    _readU32(off) { return this._data.readUInt32LE(off); }
    _readU64(off) { return Number(this._data.readBigUInt64LE(off)); }
    _readU24(off) {
        const d = this._data;
        return d[off] | (d[off + 1] << 8) | (d[off + 2] << 16);
    }
    _readU48(off) {
        const d = this._data;
        return d[off] | (d[off + 1] << 8) | (d[off + 2] << 16)
            | (d[off + 3] << 24) | (d[off + 4] << 32) | (d[off + 5] << 40);
    }

    _parseHeader() {
        const d = this._data;
        const fmtVer = d[4];
        if (fmtVer !== 2 && fmtVer !== 3 && fmtVer !== 4) {
            throw new Error(`Unsupported V20 format version: ${fmtVer}`);
        }
        this._fmtVer = fmtVer;

        this._flags = this._readU16(8);
        this._hasV4 = !!(this._flags & 1);
        this._hasV6 = !!(this._flags & 2);

        this._v6JumpBits = d[11] || 16;
        this._poolCount = d[12];
        this._poolIdxSize = d[13];
        this._geoCount = this._readU16(14);
        this._rowCount = this._readU32(20);
        this._v4RecCount = this._readU32(24);
        this._v6RecCount = this._readU32(28);

        if (this._readU32(36) !== 192) {
            throw new Error('Unexpected V20 header size');
        }

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

        // GroupMetadataTable
        let gmOff = this._offGeoEntries;
        const groupCount = d[gmOff++];
        const actualGroups = Math.max(1, groupCount);
        const ag = this._geoEntryGroupCount > 0 && this._geoEntryGroupCount < actualGroups
            ? this._geoEntryGroupCount : actualGroups;

        this._groupFieldCounts = [];
        this._groupEntryCounts = [];
        this._groupDimMasks = [];

        for (let gi = 0; gi < ag; gi++) {
            this._groupFieldCounts.push(d[gmOff++]);
            if (fmtVer >= 4) {
                this._groupEntryCounts.push(this._readU32(gmOff));
                gmOff += 4;
            } else {
                this._groupEntryCounts.push(this._readU16(gmOff));
                gmOff += 2;
            }
            if (fmtVer >= 3) {
                this._groupDimMasks.push(this._readU16(gmOff));
                gmOff += 2;
            } else {
                this._groupDimMasks.push(gi !== 2 ? 0x01 : 0x02);
            }
        }

        if (groupCount === 0 && ag === 1) {
            this._groupFieldCounts[0] = this._poolCount;
            this._groupEntryCounts[0] = this._geoCount;
            this._groupEntryOffsets[0] = 0;
            this._groupDimMasks[0] = 0x01;
        }

        // Read metadata
        this._fieldNames = [];
        this._versionName = '';
        this._resolveFieldNames();
    }

    _resolveFieldNames() {
        const d = this._data;
        const offMeta = this._offMeta;
        if (!(this._flags & 4) || !offMeta || offMeta + 4 > d.length) return;

        let pos = offMeta;
        let fieldNames = null;

        while (pos + 4 <= d.length) {
            const typ = d[pos];
            const length = this._readU16(pos + 2);
            if (!typ || !length) break;
            if (pos + 4 + length > d.length) break;
            const val = d.toString('utf8', pos + 4, pos + 4 + length);
            if (typ === 1) this._versionName = val;
            else if (typ === 2) fieldNames = val.split('|');
            pos += 4 + length;
        }

        if (fieldNames && fieldNames.length === this._groupFieldCounts[0]) {
            this._fieldNames = fieldNames;
            this._floatIndices = new Set(
                fieldNames.map((n, i) => FLOAT_FIELDS.has(n) ? i : -1).filter(i => i >= 0)
            );
        }
    }

    _ensurePoolsLoaded() {
        if (this._poolsLoaded) return;
        this._poolsLoaded = true;
        this._groupPools = [];

        if (!this._offPools) return;

        const d = this._data;
        const poolEnd = this._offMeta || d.length;
        let cursor = this._offPools;

        for (let g = 0; g < this._groupFieldCounts.length; g++) {
            const fieldCount = this._groupFieldCounts[g];
            const groupPools = [];

            for (let f = 0; f < fieldCount; f++) {
                if (cursor + 4 > poolEnd) { groupPools.push([]); continue; }
                const count = this._readU32(cursor);
                cursor += 4;
                if (!count) { groupPools.push([]); continue; }

                const offsets = [];
                for (let j = 0; j <= count; j++) {
                    offsets.push(this._readU32(cursor));
                    cursor += 4;
                }

                const strings = [];
                for (let si = 0; si < count; si++) {
                    const start = offsets[si];
                    const end = offsets[si + 1];
                    strings.push(end > start ? d.toString('utf8', cursor + start, cursor + end) : '');
                }
                cursor += offsets[count];
                groupPools.push(strings);
            }
            this._groupPools.push(groupPools);
        }
    }

    _trieWalkV4(ipInt) {
        const d = this._data;
        const hi16 = (ipInt >>> 16) & 0xFFFF;
        let ptr = this._readU32(this._offV4Jump + hi16 * 4);

        if (ptr === 0) return 0;
        if (ptr & SENTINEL) return ptr & 0x7FFFFFFF;

        let idx = ptr;
        let suffix = (ipInt & 0xFFFF) << 16;
        const nodesOff = this._offV4Nodes;

        while (true) {
            const bit = (suffix >>> 31) & 1;
            const child = this._readU32(nodesOff + idx * 8 + bit * 4);
            if (child === 0) return 0;
            if (child & SENTINEL) return child & 0x7FFFFFFF;
            idx = child;
            suffix <<= 1;
        }
    }

    _trieWalkV6(ipHi, ipLo) {
        const d = this._data;
        const shift = 128 - this._v6JumpBits;

        let idxJump;
        if (shift >= 64) {
            idxJump = ipHi >> BigInt(shift - 64);
        } else {
            idxJump = (ipHi << BigInt(64 - shift)) | (ipLo >> BigInt(shift));
        }
        const mask = (1n << BigInt(this._v6JumpBits)) - 1n;
        idxJump = Number(idxJump & mask);

        let ptr = this._readU32(this._offV6Jump + idxJump * 4);
        if (ptr === 0) return 0;
        if (ptr & SENTINEL) return ptr & 0x7FFFFFFF;

        let idx = ptr;
        let depth = this._v6JumpBits;
        const nodesOff = this._offV6Nodes;

        while (depth < 128) {
            const bitPos = 127 - depth;
            const bit = bitPos >= 64
                ? Number((ipHi >> BigInt(bitPos - 64)) & 1n)
                : Number((ipLo >> BigInt(bitPos)) & 1n);

            const child = this._readU32(nodesOff + idx * 8 + bit * 4);
            if (child === 0) return 0;
            if (child & SENTINEL) return child & 0x7FFFFFFF;
            idx = child;
            depth++;
        }
        return 0;
    }

    _readIPRow(rowId) {
        if (rowId <= 0 || rowId >= this._rowCount) return [0, 0, 0];
        const off = this._offIPRow + rowId * this._ipRowSize;
        const d = this._data;
        const geo = this._readU24(off);
        const asn = this._readU24(off + 3);
        const usage = this._ipRowSize >= 9 ? this._readU24(off + 6) : 0;
        return [geo, asn, usage];
    }

    _resolveRowId(rowId, groupIndex) {
        const [geoId, asnId, usageId] = this._readIPRow(rowId);
        const mask = groupIndex < this._groupDimMasks.length ? this._groupDimMasks[groupIndex] : 0;
        const entryId = (mask & 0x02) ? asnId : (mask & 0x04) ? usageId : geoId;
        if (!entryId) return null;
        return this._resolveGeo(entryId, groupIndex);
    }

    _resolveGeo(entryId, groupIndex) {
        if (groupIndex >= this._groupFieldCounts.length) return null;
        if (entryId >= this._groupEntryCounts[groupIndex]) return null;

        this._ensurePoolsLoaded();
        if (groupIndex >= this._groupPools.length) return null;

        const fieldCount = this._groupFieldCounts[groupIndex];
        if (!fieldCount) return null;

        const groupEntryStart = this._offGeoEntries + this._groupEntryOffsets[groupIndex];
        let entryOff = groupEntryStart + entryId * fieldCount * this._poolIdxSize;
        const d = this._data;

        const poolIdxs = [];
        for (let i = 0; i < fieldCount; i++) {
            let idx;
            if (this._poolIdxSize === 2) idx = this._readU16(entryOff);
            else if (this._poolIdxSize === 3) idx = this._readU24(entryOff);
            else idx = this._readU32(entryOff);
            poolIdxs.push(idx);
            entryOff += this._poolIdxSize;
        }

        const groupPool = this._groupPools[groupIndex];
        const fnames = this._fieldNames.length >= fieldCount ? this._fieldNames
            : Array.from({ length: fieldCount }, (_, i) => `field_${i}`);

        const values = [];
        for (let i = 0; i < fieldCount; i++) {
            const idx = poolIdxs[i];
            values.push(i < groupPool.length && idx < groupPool[i].length ? groupPool[i][idx] : '');
        }

        return new GeoInfoV20(values, fnames);
    }

    get version() { return this._versionName; }
    get field_names() { return this._fieldNames; }

    find(ipStr) {
        if (!ipStr) return null;

        let groupIdx = this.groupIndex;
        let ipClean = ipStr;
        const bangIdx = ipStr.indexOf('!');
        if (bangIdx >= 0) {
            const gi = parseInt(ipStr.substring(bangIdx + 1));
            if (!isNaN(gi)) groupIdx = gi;
            ipClean = ipStr.substring(0, bangIdx);
        }

        if (ipClean.includes(':')) {
            // IPv6
            const parts = ipClean.split(':');
            // Check for IPv4-mapped
            if (ipClean.includes('::ffff:') || ipClean.startsWith('::ffff:')) {
                const v4part = parts[parts.length - 1];
                const v4parts = v4part.split('.');
                if (v4parts.length === 4) {
                    const ipInt = ((+v4parts[0] * 256 + +v4parts[1]) * 256 + +v4parts[2]) * 256 + +v4parts[3];
                    return this._lookupV4(ipInt >>> 0, groupIdx);
                }
            }
            // Full IPv6 - use BigInt
            const addr = BigInt('0x1' + ipClean.replace(/:/g, '').padStart(32, '0'));
            const hi = Number(addr >> 64n);
            const lo = Number(addr & 0xFFFFFFFFFFFFFFFFn);
            return this._lookupV6(hi, lo, groupIdx);
        }

        const parts = ipClean.split('.');
        if (parts.length !== 4) return null;
        const ipInt = ((+parts[0] * 256 + +parts[1]) * 256 + +parts[2]) * 256 + +parts[3];
        return this._lookupV4(ipInt >>> 0, groupIdx);
    }

    _lookupV4(ipInt, groupIndex) {
        if (!this._hasV4) return null;
        const rowId = this._trieWalkV4(ipInt);
        if (!rowId) return null;
        return this._resolveRowId(rowId, groupIndex);
    }

    _lookupV6(ipHi, ipLo, groupIndex) {
        if (!this._hasV6) return null;
        const rowId = this._trieWalkV6(BigInt(ipHi), BigInt(ipLo));
        if (!rowId) return null;
        return this._resolveRowId(rowId, groupIndex);
    }

    findStr(ipStr) {
        const info = this.find(ipStr);
        return info ? info.toPipe(this._floatIndices) : '';
    }

    verifyCRC() {
        const d = this._data;
        if (d.length < 20) return false;
        const stored = this._readU32(16);
        const buf = Buffer.from(d);
        buf[16] = 0; buf[17] = 0; buf[18] = 0; buf[19] = 0;
        const computed = crc32(buf);
        return stored === computed;
    }
}

// CRC32 implementation
const crc32Table = (() => {
    const table = new Uint32Array(256);
    for (let i = 0; i < 256; i++) {
        let c = i;
        for (let j = 0; j < 8; j++) {
            c = (c & 1) ? (c >>> 1) ^ 0xEDB88320 : c >>> 1;
        }
        table[i] = c;
    }
    return table;
})();

function crc32(data) {
    let crc = 0xFFFFFFFF;
    for (let i = 0; i < data.length; i++) {
        crc = crc32Table[(crc ^ data[i]) & 0xFF] ^ (crc >>> 8);
    }
    return (crc ^ 0xFFFFFFFF) >>> 0;
}

// Reset instance for testing
QzdbSearcherV20._instance = null;

module.exports = { QzdbSearcherV20, GeoInfoV20 };
