using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace Qqzeng
{
    public enum ErrorCode
    {
        NotFound,
        Corrupted,
        OutOfBounds,
        InvalidParam,
        BadHeader,
        BadMagic,
        Unsupported
    }

    public class QzdbException : Exception
    {
        public ErrorCode Code { get; }

        public QzdbException(ErrorCode code, string message) : base(message)
        {
            Code = code;
        }

        public QzdbException(ErrorCode code, string message, Exception inner) : base(message, inner)
        {
            Code = code;
        }
    }

    public class GeoInfo
    {
        public Dictionary<string, string> Fields;
        public string[] FieldNames;
        public HashSet<string> FloatIndices;
        public string[] Values;

        public bool IsEmpty => Values == null || Values.Length == 0;

        public string Get(string name)
        {
            if (Fields != null && Fields.TryGetValue(name, out var val))
                return val;
            for (int i = 0; i < FieldNames.Length; i++)
            {
                if (FieldNames[i] == name && i < Values.Length)
                    return Values[i] ?? "";
            }
            return "";
        }

        public string ToPipe()
        {
            if (IsEmpty) return "";
            var parts = new string[FieldNames.Length];
            for (int i = 0; i < FieldNames.Length; i++)
            {
                string val = i < Values.Length ? Values[i] ?? "" : "";
                if (FloatIndices.Contains(FieldNames[i]) && val.Length > 0)
                {
                    if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double f))
                        val = f.ToString("F6", CultureInfo.InvariantCulture);
                }
                parts[i] = val;
            }
            return string.Join("|", parts);
        }
    }

    public sealed class QzdbSearcher
    {
        private static readonly Lazy<QzdbSearcher> _lazy = new(() => new QzdbSearcher());
        public static QzdbSearcher Instance => _lazy.Value;

        private const uint SENTINEL = 0x80000000;
        private const uint SENTINEL_MASK_24 = 0x7FFFFF;
        private const uint SENTINEL_MASK_31 = 0x7FFFFFFF;
        private const int MaxTrieWalkSteps = 1000;
        private static readonly HashSet<string> FloatFields = new() { "longitude", "latitude" };

        private byte[] _data;
        private int _groupIndex;
        private string[] _fieldNames;
        private Dictionary<string, int> _fieldNameToIdx;
        private HashSet<string> _floatFieldIndices = new();
        private string _versionName = "";

        // Header fields
        private ushort _flags;
        private bool _hasV4;
        private bool _hasV6;
        private bool _v4Node24;
        private bool _v6Node24;
        private int _v6JumpBits = 16;
        private int _poolCount;
        private int _poolIdxSize;
        private int _geoCount;
        private int _rowCount;
        private uint _v4RecCount;
        private uint _v6RecCount;
        private uint _v4NodeCount;
        private uint _v6NodeCount;
        private int _ipRowSize = 6;
        private int _geoEntryGroupCount;

        // Offsets
        private long _offV4Jump;
        private long _offV4Nodes;
        private long _offV6Jump;
        private long _offV6Nodes;
        private long _offIPRow;
        private long _offGeoEntries;
        private long _offPools;
        private long _offMeta;
        private long _offRowSchema;
        private long _offGroupSchema;

        // Schema/layout cache
        private int[] _groupFieldCounts;
        private uint[] _groupEntryCounts;
        private ushort[] _groupDimMasks;
        private long[] _groupEntryOffsets;

        private int[] _groupStrides;
        private int[][] _groupFieldWidths;
        private int[][] _groupFieldOffsets;
        private bool[][] _groupFieldNative;
        private int[][] _groupFieldNativeType;
        private ushort[][] _groupFieldIds;
        private uint[][] _groupPoolSectionIds;

        private string[][][] _groupPools;
        private volatile bool _poolsLoaded;

        private readonly object _loadLock = new();
        private readonly object _poolsLock = new();

        public QzdbSearcher() { }

        public static QzdbSearcher GetInstance(string dbPath = null, int groupIndex = 0)
        {
            if (dbPath != null)
                Instance.Load(dbPath, groupIndex);
            return Instance;
        }

        public void Load(string dbPath, int groupIndex = 0)
        {
            lock (_loadLock)
            {
                _groupIndex = groupIndex;
                byte[] raw;
                try
                {
                    raw = File.ReadAllBytes(dbPath);
                }
                catch (FileNotFoundException ex)
                {
                    throw new QzdbException(ErrorCode.NotFound, $"Database file not found: {dbPath}", ex);
                }
                catch (IOException ex)
                {
                    throw new QzdbException(ErrorCode.Corrupted, $"Failed to read database file: {dbPath}", ex);
                }
                _data = raw;
                ParseHeader(raw);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ushort SafeReadU16(byte[] d, int off)
        {
            return BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(off, 2));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint SafeReadU32(byte[] d, int off)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(off, 4));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ulong SafeReadU64(byte[] d, int off)
        {
            return BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(off, 8));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint SafeReadU24(byte[] d, int off)
        {
            return (uint)(d[off] | (d[off + 1] << 8) | (d[off + 2] << 16));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private long SafeReadU48(byte[] d, int off)
        {
            return (long)(d[off] | ((long)d[off + 1] << 8) | ((long)d[off + 2] << 16) |
                          ((long)d[off + 3] << 24) | ((long)d[off + 4] << 32) | ((long)d[off + 5] << 40));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint SafeReadUintWidth(byte[] d, int off, int width)
        {
            if (width <= 1) return d[off];
            if (width == 2) return SafeReadU16(d, off);
            if (width == 3) return SafeReadU24(d, off);
            return SafeReadU32(d, off);
        }

        private void ParseHeader(byte[] d)
        {
            if (d.Length < 192)
                throw new QzdbException(ErrorCode.Corrupted, "File too small for QZDB header");

            string magic = Encoding.ASCII.GetString(d, 0, 4);
            if (magic != "QZDB")
                throw new QzdbException(ErrorCode.BadMagic, "Invalid magic, expected QZDB");

            int fmtVer = d[4];
            if (fmtVer < 1 || fmtVer > 6)
                throw new QzdbException(ErrorCode.Unsupported, $"Unsupported format version: {fmtVer}");

            _flags = SafeReadU16(d, 8);
            _hasV4 = (_flags & 1) != 0;
            _hasV6 = (_flags & 2) != 0;
            _v4Node24 = (_flags & 0x10) != 0;
            _v6Node24 = (_flags & 0x20) != 0;

            _v6JumpBits = d[11];
            if (_v6JumpBits == 0) _v6JumpBits = 16;
            if (_v6JumpBits < 16 || _v6JumpBits > 20)
                throw new QzdbException(ErrorCode.InvalidParam, $"v6JumpBits out of range [16,20]: {_v6JumpBits}");

            _poolCount = d[12];
            _poolIdxSize = d[13];
            if (_poolIdxSize != 2 && _poolIdxSize != 3)
                throw new QzdbException(ErrorCode.InvalidParam, $"poolIdxSize must be 2 or 3, got {_poolIdxSize}");
            _geoCount = SafeReadU16(d, 14);
            _rowCount = (int)SafeReadU32(d, 20);
            _v4RecCount = SafeReadU32(d, 24);
            _v6RecCount = SafeReadU32(d, 28);

            int hs = (int)SafeReadU32(d, 36);
            if (hs != 192)
                throw new QzdbException(ErrorCode.BadHeader, $"Unexpected header size: {hs}");

            _offRowSchema = (long)SafeReadU64(d, 40);
            _offGroupSchema = (long)SafeReadU64(d, 48);
            _offV4Jump = (long)SafeReadU64(d, 64);
            _offV4Nodes = (long)SafeReadU64(d, 72);
            _offV6Jump = (long)SafeReadU64(d, 80);
            _offV6Nodes = (long)SafeReadU64(d, 88);
            _offIPRow = (long)SafeReadU64(d, 96);
            _offGeoEntries = (long)SafeReadU64(d, 104);
            _offPools = (long)SafeReadU64(d, 136);
            _offMeta = (long)SafeReadU64(d, 144);

            _v4NodeCount = SafeReadU32(d, 152);
            _v6NodeCount = SafeReadU32(d, 156);
            _ipRowSize = (int)SafeReadU32(d, 160);
            if (_ipRowSize < 1 || _ipRowSize > 64)
                throw new QzdbException(ErrorCode.InvalidParam, $"ipRowSize out of range [1,64]: {_ipRowSize}");
            _geoEntryGroupCount = (int)SafeReadU32(d, 164);
            if (_geoEntryGroupCount < 1 || _geoEntryGroupCount > 255)
                throw new QzdbException(ErrorCode.InvalidParam, $"geoEntryGroupCount out of range [1,255]: {_geoEntryGroupCount}");

            // Bounds validation for section offsets
            long dlen = d.Length;
            long v4NodeSize = _v4Node24 ? 6 : 8;
            long v6NodeSize = _v6Node24 ? 6 : 8;
            long v6JumpSize = (1L << _v6JumpBits) * 4;

            if (_offV4Jump > 0 && _offV4Jump + 65536 * 4 > dlen)
                throw new QzdbException(ErrorCode.OutOfBounds, "V4 jump table offset out of bounds");
            if (_offV4Nodes > 0 && _offV4Nodes + (long)_v4NodeCount * v4NodeSize > dlen)
                throw new QzdbException(ErrorCode.OutOfBounds, "V4 nodes table offset out of bounds");
            if (_offV6Jump > 0 && _offV6Jump + v6JumpSize > dlen)
                throw new QzdbException(ErrorCode.OutOfBounds, "V6 jump table offset out of bounds");
            if (_offV6Nodes > 0 && _offV6Nodes + (long)_v6NodeCount * v6NodeSize > dlen)
                throw new QzdbException(ErrorCode.OutOfBounds, "V6 nodes table offset out of bounds");
            if (_offIPRow > 0 && _offIPRow + (long)_rowCount * _ipRowSize > dlen)
                throw new QzdbException(ErrorCode.OutOfBounds, "IP row table offset out of bounds");

            _groupEntryOffsets = new long[4];
            for (int i = 0; i < 4; i++)
                _groupEntryOffsets[i] = SafeReadU48(d, 168 + i * 6);

            int gmOff = (int)_offGeoEntries;
            int groupCount = d[gmOff];
            gmOff++;

            int actualGroups = groupCount < 1 ? 1 : groupCount;
            if (_geoEntryGroupCount > 0 && _geoEntryGroupCount < actualGroups)
                actualGroups = _geoEntryGroupCount;
            if (actualGroups > 4)
                actualGroups = 4;

            _groupFieldCounts = new int[actualGroups];
            _groupEntryCounts = new uint[actualGroups];
            _groupDimMasks = new ushort[actualGroups];

            for (int gi = 0; gi < actualGroups; gi++)
            {
                _groupFieldCounts[gi] = d[gmOff];
                gmOff++;
                if (fmtVer == 1 || fmtVer >= 4)
                {
                    _groupEntryCounts[gi] = SafeReadU32(d, gmOff);
                    gmOff += 4;
                }
                else
                {
                    _groupEntryCounts[gi] = SafeReadU16(d, gmOff);
                    gmOff += 2;
                }

                if (fmtVer == 1 || fmtVer >= 3)
                {
                    _groupDimMasks[gi] = SafeReadU16(d, gmOff);
                    gmOff += 2;
                }
                else
                {
                    _groupDimMasks[gi] = (gi != 2) ? (ushort)0x01 : (ushort)0x02;
                }
            }

            _groupStrides = new int[actualGroups];
            _groupFieldWidths = new int[actualGroups][];
            _groupFieldOffsets = new int[actualGroups][];
            _groupFieldNative = new bool[actualGroups][];
            _groupFieldNativeType = new int[actualGroups][];
            _groupFieldIds = new ushort[actualGroups][];
            _groupPoolSectionIds = new uint[actualGroups][];

            if (_offGroupSchema > 0)
            {
                int sp = (int)_offGroupSchema;
                int gsGroupCount = SafeReadU16(d, sp);
                sp += 2;
                int maxGsGroups = Math.Min(gsGroupCount, actualGroups);
                for (int gi = 0; gi < maxGsGroups; gi++)
                {
                    sp += 2; // skip groupId
                    int fldCount = SafeReadU16(d, sp);
                    sp += 2;
                    sp += 4; // skip entryCount
                    int stride = (int)SafeReadU32(d, sp);
                    sp += 4;
                    sp += 4; // skip flags

                    if (gi < actualGroups)
                    {
                        _groupStrides[gi] = stride;
                        var widths = new int[fldCount];
                        var offsets = new int[fldCount];
                        var natives = new bool[fldCount];
                        var natTypes = new int[fldCount];
                        var fieldIds = new ushort[fldCount];
                        var poolSectionIds = new uint[fldCount];
                        for (int fi = 0; fi < fldCount; fi++)
                        {
                            fieldIds[fi] = SafeReadU16(d, sp);
                            sp += 2;
                            widths[fi] = d[sp];
                            sp++;
                            int fieldFlags = d[sp];
                            sp++;
                            natives[fi] = (fieldFlags & 0x01) != 0;
                            natTypes[fi] = (fieldFlags >> 1) & 0x03;
                            offsets[fi] = (int)SafeReadU32(d, sp);
                            sp += 4;
                            poolSectionIds[fi] = SafeReadU32(d, sp);
                            sp += 4;
                        }
                        _groupFieldWidths[gi] = widths;
                        _groupFieldOffsets[gi] = offsets;
                        _groupFieldNative[gi] = natives;
                        _groupFieldNativeType[gi] = natTypes;
                        _groupFieldIds[gi] = fieldIds;
                        _groupPoolSectionIds[gi] = poolSectionIds;
                    }
                    else
                    {
                        sp += fldCount * 12;
                    }
                }
            }

            for (int g = 0; g < actualGroups; g++)
            {
                if (_groupStrides[g] == 0)
                    _groupStrides[g] = _groupFieldCounts[g] * _poolIdxSize;
                if (_groupFieldWidths[g] == null)
                {
                    _groupFieldWidths[g] = new int[_groupFieldCounts[g]];
                    Array.Fill(_groupFieldWidths[g], _poolIdxSize);
                }
                if (_groupFieldOffsets[g] == null)
                {
                    _groupFieldOffsets[g] = new int[_groupFieldCounts[g]];
                    for (int i = 0; i < _groupFieldCounts[g]; i++)
                        _groupFieldOffsets[g][i] = i * _poolIdxSize;
                }
                if (_groupFieldNative[g] == null)
                    _groupFieldNative[g] = new bool[_groupFieldCounts[g]];
                if (_groupFieldNativeType[g] == null)
                    _groupFieldNativeType[g] = new int[_groupFieldCounts[g]];
            }

            ResolveFieldNames(d);
            _poolsLoaded = false;
            _groupPools = null;
        }

        private void ResolveFieldNames(byte[] d)
        {
            if ((_flags & 4) != 0 && _offMeta > 0 && _offMeta + 4 <= d.Length)
            {
                string[] fieldNames = null;
                int pos = (int)_offMeta;
                while (pos + 4 <= d.Length)
                {
                    byte t = d[pos];
                    ushort length = SafeReadU16(d, pos + 2);
                    if (t == 0 || length == 0) break;
                    if (pos + 4 + length > d.Length) break;
                    string val = Encoding.UTF8.GetString(d, pos + 4, length);
                    if (t == 1)
                        _versionName = val;
                    else if (t == 2)
                        fieldNames = val.Split('|');
                    pos += 4 + length;
                }

                if (fieldNames != null && fieldNames.Length == _groupFieldCounts[0])
                {
                    _fieldNames = fieldNames;
                    _fieldNameToIdx = new Dictionary<string, int>(_fieldNames.Length);
                    for (int i = 0; i < _fieldNames.Length; i++)
                        _fieldNameToIdx[_fieldNames[i]] = i;
                    _floatFieldIndices.Clear();
                    foreach (var n in fieldNames)
                    {
                        if (FloatFields.Contains(n))
                            _floatFieldIndices.Add(n);
                    }
                    return;
                }
            }

            _fieldNames = new string[_groupFieldCounts[0]];
            for (int i = 0; i < _fieldNames.Length; i++)
                _fieldNames[i] = $"field_{i}";
            _fieldNameToIdx = new Dictionary<string, int>(_fieldNames.Length);
            for (int i = 0; i < _fieldNames.Length; i++)
                _fieldNameToIdx[_fieldNames[i]] = i;
        }

        private void EnsurePoolsLoaded()
        {
            if (_poolsLoaded) return;
            lock (_poolsLock)
            {
                if (_poolsLoaded) return;

                int groupCount = _groupFieldCounts.Length;
                _groupPools = new string[groupCount][][];

                if (_offPools <= 0) return;

                int poolCursor = (int)_offPools;
                int poolEnd = _offMeta > 0 ? (int)_offMeta : _data.Length;
                byte[] d = _data;

                for (int g = 0; g < groupCount; g++)
                {
                    int fieldCount = _groupFieldCounts[g];
                    var groupPoolList = new string[fieldCount][];
                    bool[] natives = _groupFieldNative[g];
                    for (int f = 0; f < fieldCount; f++)
                    {
                        if (natives != null && f < natives.Length && natives[f])
                        {
                            groupPoolList[f] = Array.Empty<string>();
                            continue;
                        }

                        if (poolCursor + 4 > poolEnd)
                        {
                            groupPoolList[f] = Array.Empty<string>();
                            continue;
                        }
                        int count = (int)SafeReadU32(d, poolCursor);
                        poolCursor += 4;
                        if (_offRowSchema > 0)
                            poolCursor += 4;
                        if (count == 0)
                        {
                            groupPoolList[f] = Array.Empty<string>();
                            continue;
                        }

                        var offsets = new uint[count + 1];
                        for (int o = 0; o <= count; o++)
                        {
                            offsets[o] = SafeReadU32(d, poolCursor);
                            poolCursor += 4;
                        }

                        var strings = new string[count];
                        for (int s = 0; s < count; s++)
                        {
                            int start = (int)offsets[s];
                            int end = (int)offsets[s + 1];
                            int length = end - start;
                            if (length > 0)
                                strings[s] = Encoding.UTF8.GetString(d, poolCursor + start, length);
                            else
                                strings[s] = "";
                        }
                        poolCursor += (int)offsets[count];
                        groupPoolList[f] = strings;
                    }
                    _groupPools[g] = groupPoolList;
                }
                _poolsLoaded = true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint GetV4Child(uint nodeIdx, uint bit)
        {
            if (_v4Node24)
            {
                long nodeOffset = _offV4Nodes + nodeIdx * 6;
                long offset = bit == 0 ? nodeOffset : nodeOffset + 3;
                uint val = (uint)(_data[offset] | (_data[offset + 1] << 8) | (_data[offset + 2] << 16));
                if ((val & 0x800000) != 0)
                    return (val & SENTINEL_MASK_24) | SENTINEL;
                return val;
            }
            else
            {
                long childOff = _offV4Nodes + nodeIdx * 8 + bit * 4;
                return SafeReadU32(_data, (int)childOff);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint GetV6Child(uint nodeIdx, uint bit)
        {
            if (_v6Node24)
            {
                long nodeOffset = _offV6Nodes + nodeIdx * 6;
                long offset = bit == 0 ? nodeOffset : nodeOffset + 3;
                uint val = (uint)(_data[offset] | (_data[offset + 1] << 8) | (_data[offset + 2] << 16));
                if ((val & 0x800000) != 0)
                    return (val & SENTINEL_MASK_24) | SENTINEL;
                return val;
            }
            else
            {
                long childOff = _offV6Nodes + nodeIdx * 8 + bit * 4;
                return SafeReadU32(_data, (int)childOff);
            }
        }

        private uint TrieWalkV4(uint ipInt)
        {
            uint hi16 = (ipInt >> 16) & 0xFFFF;
            uint ptr = SafeReadU32(_data, (int)(_offV4Jump + hi16 * 4));

            if (ptr == 0) return 0;
            if ((ptr & SENTINEL) != 0) return ptr & SENTINEL_MASK_31;

            uint idx = ptr;
            uint suffix = (ipInt & 0xFFFF) << 16;
            uint steps = 0;

            while (true)
            {
                if (++steps > 32) return 0;
                uint bit = (suffix >> 31) & 1;
                uint child = GetV4Child(idx, bit);

                if (child == 0) return 0;
                if ((child & SENTINEL) != 0) return child & SENTINEL_MASK_31;

                idx = child;
                suffix <<= 1;
            }
        }

        private uint TrieWalkV6(ulong ipHigh, ulong ipLow)
        {
            uint idxJump = (uint)(ipHigh >>> (64 - _v6JumpBits)) & (uint)((1 << _v6JumpBits) - 1);
            uint ptr = SafeReadU32(_data, (int)(_offV6Jump + idxJump * 4));

            if (ptr == 0) return 0;
            if ((ptr & SENTINEL) != 0) return ptr & SENTINEL_MASK_31;

            uint idx = ptr;
            int depth = _v6JumpBits;

            while (depth < 128)
            {
                uint bit = (depth <= 63)
                    ? (uint)((ipHigh >>> (63 - depth)) & 1)
                    : (uint)((ipLow >>> (127 - depth)) & 1);
                uint child = GetV6Child(idx, bit);

                if (child == 0) return 0;
                if ((child & SENTINEL) != 0) return child & SENTINEL_MASK_31;

                idx = child;
                depth += 1;
            }
            return 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ReadIPRow(uint rowId, out uint geoId, out uint asnId, out uint usageTypeId)
        {
            geoId = 0; asnId = 0; usageTypeId = 0;
            if (rowId <= 0 || rowId >= _rowCount) return;
            long off = _offIPRow + rowId * _ipRowSize;
            geoId = SafeReadU24(_data, (int)off);
            asnId = SafeReadU24(_data, (int)off + 3);
            if (_ipRowSize >= 9)
                usageTypeId = SafeReadU24(_data, (int)off + 6);
        }

        private GeoInfo ResolveRowId(uint rowId, int groupIndex)
        {
            ReadIPRow(rowId, out uint geoId, out uint asnId, out uint usageTypeId);
            ushort mask = groupIndex < _groupDimMasks.Length ? _groupDimMasks[groupIndex] : (ushort)0;

            uint entryId = geoId;
            if ((mask & 0x02) != 0)
                entryId = asnId;
            else if ((mask & 0x04) != 0)
                entryId = usageTypeId;

            if (entryId == 0) return null;
            return ResolveGeo(entryId, groupIndex);
        }

        private GeoInfo ResolveGeo(uint entryId, int groupIndex)
        {
            if (groupIndex < 0 || groupIndex >= _groupFieldCounts.Length) return null;
            if (entryId < 0) return null;
            if (entryId >= _groupEntryCounts[groupIndex]) return null;

            EnsurePoolsLoaded();

            int fieldCount = _groupFieldCounts[groupIndex];
            if (fieldCount <= 0) return null;

            long groupEntryStart = _offGeoEntries + _groupEntryOffsets[groupIndex];
            int stride = _groupStrides[groupIndex];
            long entryOffset = groupEntryStart + (long)entryId * stride;
            byte[] d = _data;

            int[] widths = _groupFieldWidths[groupIndex];
            int[] baseOffsets = _groupFieldOffsets[groupIndex];
            bool[] natives = _groupFieldNative[groupIndex];
            int[] natTypes = _groupFieldNativeType[groupIndex];

            var values = new string[fieldCount];
            for (int i = 0; i < fieldCount; i++)
            {
                int w = widths[i];
                int fo = (int)(entryOffset + baseOffsets[i]);
                bool isNative = natives != null && i < natives.Length && natives[i];

                if (isNative)
                {
                    int t = (natTypes != null && i < natTypes.Length) ? natTypes[i] : 0;
                    if (t == 1)
                    {
                        values[i] = w == 4
                            ? BitConverter.ToSingle(d, fo).ToString(CultureInfo.InvariantCulture)
                            : BitConverter.ToDouble(d, fo).ToString(CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        values[i] = SafeReadUintWidth(d, fo, w).ToString();
                    }
                }
                else
                {
                    uint idx = SafeReadUintWidth(d, fo, w);
                    string[][] gp = _groupPools[groupIndex];
                    if (gp != null && i < gp.Length && idx < (uint)gp[i].Length)
                    {
                        values[i] = gp[i][(int)idx];
                    }
                    else
                    {
                        values[i] = "";
                    }
                }
            }

            return new GeoInfo { FieldNames = _fieldNames, FloatIndices = _floatFieldIndices, Values = values };
        }

        public GeoInfo FindFields(string ipStr, string[] fieldNames)
        {
            if (fieldNames == null || fieldNames.Length == 0)
                return Find(ipStr);
            var rowId = LookupRowId(ipStr);
            if (rowId == 0) return null;
            return ResolveGeoFields(rowId, _groupIndex, fieldNames);
        }

        private GeoInfo ResolveGeoFields(uint rowId, int groupIndex, string[] fieldNames)
        {
            ReadIPRow(rowId, out uint geoId, out uint asnId, out uint usageId);
            var mask = groupIndex < _groupDimMasks.Length ? _groupDimMasks[groupIndex] : (ushort)0;
            uint entryId = (mask & 0x02) != 0 ? asnId : (mask & 0x04) != 0 ? usageId : geoId;
            if (entryId == 0 || groupIndex < 0 || groupIndex >= _groupFieldCounts.Length) return null;
            if (entryId >= _groupEntryCounts[groupIndex]) return null;

            EnsurePoolsLoaded();
            int fieldCount = _groupFieldCounts[groupIndex];
            if (fieldCount <= 0) return null;

            var indices = new List<int>(fieldNames.Length);
            foreach (var name in fieldNames)
                if (_fieldNameToIdx.TryGetValue(name, out var idx)) indices.Add(idx);

            if (indices.Count == 0) return null;

            long groupEntryStart = _offGeoEntries + _groupEntryOffsets[groupIndex];
            int stride = _groupStrides[groupIndex];
            long entryOffset = groupEntryStart + (long)entryId * stride;
            byte[] d = _data;
            int[] widths = _groupFieldWidths[groupIndex];
            int[] baseOffsets = _groupFieldOffsets[groupIndex];
            bool[] natives = _groupFieldNative[groupIndex];
            int[] natTypes = _groupFieldNativeType[groupIndex];

            var values = new string[fieldCount];
            foreach (int i in indices)
            {
                if (i < 0 || i >= fieldCount) continue;
                int w = widths[i];
                int fo = (int)(entryOffset + baseOffsets[i]);
                bool isNative = natives != null && i < natives.Length && natives[i];
                if (isNative)
                {
                    int t = (natTypes != null && i < natTypes.Length) ? natTypes[i] : 0;
                    values[i] = t == 1
                        ? (w == 4 ? BitConverter.ToSingle(d, fo).ToString(CultureInfo.InvariantCulture) : BitConverter.ToDouble(d, fo).ToString(CultureInfo.InvariantCulture))
                        : SafeReadUintWidth(d, fo, w).ToString();
                }
                else
                {
                    uint idx = SafeReadUintWidth(d, fo, w);
                    var gp = _groupPools[groupIndex];
                    values[i] = (gp != null && i < gp.Length && idx < (uint)gp[i].Length) ? gp[i][(int)idx] : "";
                }
            }
            return new GeoInfo { FieldNames = _fieldNames, FloatIndices = _floatFieldIndices, Values = values };
        }

        public void Reload(string path)
        {
            Load(path);
        }

        public GeoInfo Find(string ipStr)
        {
            if (string.IsNullOrEmpty(ipStr)) return null;
            if (!FastParseIp(ipStr, out var result)) return null;
            if (result.IsV4) return FindUint(result.V4);
            return FindV6Uint(result.V6High, result.V6Low);
        }

        public GeoInfo FindUint(uint ipInt)
        {
            if (!_hasV4) return null;
            uint rowId = TrieWalkV4(ipInt);
            if (rowId == 0) return null;
            return ResolveRowId(rowId, _groupIndex);
        }

        public GeoInfo FindV6Uint(ulong ipHigh, ulong ipLow)
        {
            if (!_hasV6) return null;
            uint rowId = TrieWalkV6(ipHigh, ipLow);
            if (rowId == 0) return null;
            return ResolveRowId(rowId, _groupIndex);
        }

        /// <summary>Lookup row_id only (trie walk, no data materialization). Returns 0 if not found.</summary>
        public uint LookupRowId(string ipStr)
        {
            if (string.IsNullOrEmpty(ipStr)) return 0;
            if (!FastParseIp(ipStr, out var result)) return 0;
            if (result.IsV4) return LookupRowIdUint(result.V4);
            return LookupRowIdV6(result.V6High, result.V6Low);
        }

        /// <summary>Lookup row_id for a pre-parsed IPv4 integer.</summary>
        public uint LookupRowIdUint(uint ipInt)
        {
            if (!_hasV4) return 0;
            return TrieWalkV4(ipInt);
        }

        public uint LookupRowIdV6(ulong ipHigh, ulong ipLow)
        {
            if (!_hasV6) return 0;
            return TrieWalkV6(ipHigh, ipLow);
        }

        /// <summary>Lookup raw entry IDs from a row_id. Returns (geoId, asnId, usageId) tuple, or null if invalid.</summary>
        public (uint geoId, uint asnId, uint usageTypeId)? LookupIds(uint rowId)
        {
            if (rowId <= 0 || rowId >= _rowCount) return null;
            ReadIPRow(rowId, out uint geoId, out uint asnId, out uint usageTypeId);
            return (geoId, asnId, usageTypeId);
        }

        public string FindStr(string ipStr)
        {
            var info = Find(ipStr);
            return info == null || info.IsEmpty ? "" : info.ToPipe();
        }

        public string[] FieldNames => (string[])(_fieldNames?.Clone() ?? Array.Empty<string>());

        public string Version => _versionName;

        public bool VerifyCrc()
        {
            if (_data == null || _data.Length < 20) return false;
            uint stored = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(16, 4));
            byte[] copy = (byte[])_data.Clone();
            Array.Clear(copy, 16, 4);
            uint computed = Crc32Table.Compute(copy);
            return stored == computed;
        }

        private static readonly byte[] HexLUT = new byte[128];
        static QzdbSearcher()
        {
            for (int i = 0; i < 10; i++) HexLUT[48 + i] = (byte)i;
            for (int i = 0; i < 6; i++) { HexLUT[97 + i] = (byte)(10 + i); HexLUT[65 + i] = (byte)(10 + i); }
        }

        private readonly struct ParseResult
        {
            public readonly uint V4;
            public readonly ulong V6High, V6Low;
            public readonly bool IsV4;
            public ParseResult(uint v4) { V4 = v4; V6High = 0; V6Low = 0; IsV4 = true; }
            public ParseResult(ulong v6High, ulong v6Low) { V4 = 0; V6High = v6High; V6Low = v6Low; IsV4 = false; }
        }

        private static bool FastParseIpv4(string s, out uint v4)
        {
            v4 = 0;
            int n = s.Length;
            if (n == 0 || s[n - 1] == '.') return false;
            uint result = 0; int val = 0, dots = 0, start = 0;
            for (int i = 0; i <= n; i++)
            {
                char c = i < n ? s[i] : '.';
                if (c == '.')
                {
                    int segLen = i - start;
                    if (segLen == 0 || segLen > 3) return false;
                    if (segLen > 1 && s[start] == '0') return false;
                    val = 0;
                    for (int j = start; j < i; j++)
                    {
                        char d = s[j];
                        if (d < '0' || d > '9') return false;
                        val = val * 10 + (d - '0');
                    }
                    if (val > 255) return false;
                    result = (result << 8) | (uint)val;
                    dots++; start = i + 1;
                }
            }
            if (dots != 4) return false;
            v4 = result;
            return true;
        }

        private static bool FastParseIp(string s, out ParseResult result)
        {
            result = default;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            int n = s.Length;
            if (n == 0 || n > 45) return false;
            if (!s.Contains(':'))
            {
                if (!FastParseIpv4(s, out uint v4)) return false;
                result = new ParseResult(v4);
                return true;
            }
            if (s.Contains('%')) return false;
            int dc = s.IndexOf("::");
            if (dc >= 0 && s.IndexOf("::", dc + 2) >= 0) return false;
            string lft = dc >= 0 ? s.Substring(0, dc) : s;
            string rgt = dc >= 0 ? s.Substring(dc + 2) : "";
            string[] lg = lft.Length > 0 ? lft.Split(':') : Array.Empty<string>();
            string[] rg = rgt.Length > 0 ? rgt.Split(':') : Array.Empty<string>();
            if (lg.Length == 1 && lg[0] == "") lg = Array.Empty<string>();
            if (rg.Length == 1 && rg[0] == "") rg = Array.Empty<string>();
            foreach (var g in lg) if (g == "") return false;
            foreach (var g in rg) if (g == "") return false;
            var allg = new List<string>(lg.Length + rg.Length);
            allg.AddRange(lg); allg.AddRange(rg);
            bool hasV4 = false; uint v4Int = 0;
            int last = allg.Count - 1;
            if (last >= 0 && allg[last].Contains('.'))
            {
                if (!FastParseIpv4(allg[last], out v4Int)) return false;
                hasV4 = true;
                allg.RemoveAt(last);
            }
            int ng = allg.Count;
            int v4Slots = hasV4 ? 2 : 0;
            if (dc >= 0) { if (ng + v4Slots > 7) return false; }
            else { if (ng + v4Slots != 8) return false; }
            foreach (var g in allg)
            {
                int gl = g.Length;
                if (gl == 0 || gl > 4) return false;
                for (int j = 0; j < gl; j++)
                {
                    char cc = g[j];
                    if (cc >= 128 || (HexLUT[cc] == 0 && cc != '0')) return false;
                }
            }
            int zeros = 8 - ng - v4Slots;
            byte[] buf = new byte[16];
            int off = 0;
            foreach (var g in lg)
            {
                int v = 0;
                for (int j = 0; j < g.Length; j++) v = (v << 4) | HexLUT[g[j]];
                buf[off] = (byte)(v >> 8); buf[off + 1] = (byte)v;
                off += 2;
            }
            off += zeros * 2;
            foreach (var g in rg)
            {
                int v = 0;
                for (int j = 0; j < g.Length; j++) v = (v << 4) | HexLUT[g[j]];
                buf[off] = (byte)(v >> 8); buf[off + 1] = (byte)v;
                off += 2;
            }
            if (hasV4) { buf[12] = (byte)(v4Int >> 24); buf[13] = (byte)(v4Int >> 16); buf[14] = (byte)(v4Int >> 8); buf[15] = (byte)v4Int; }
            if (buf[10] == 0xff && buf[11] == 0xff &&
                buf[0] == 0 && buf[1] == 0 && buf[2] == 0 && buf[3] == 0 &&
                buf[4] == 0 && buf[5] == 0 && buf[6] == 0 && buf[7] == 0 &&
                buf[8] == 0 && buf[9] == 0)
            {
                result = new ParseResult((uint)((buf[12] << 24) | (buf[13] << 16) | (buf[14] << 8) | buf[15]));
                return true;
            }
            ulong v6High = 0, v6Low = 0;
            for (int i = 0; i < 8; i++) v6High = (v6High << 8) | buf[i];
            for (int i = 8; i < 16; i++) v6Low = (v6Low << 8) | buf[i];
            result = new ParseResult(v6High, v6Low);
            return true;
        }

        private static class Crc32Table
        {
            private static readonly uint[] Table;
            static Crc32Table()
            {
                Table = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    uint entry = i;
                    for (int j = 0; j < 8; j++)
                        if ((entry & 1) == 1) entry = (entry >> 1) ^ 0xEDB88320;
                        else entry >>= 1;
                    Table[i] = entry;
                }
            }
            public static uint Compute(byte[] buffer)
            {
                uint crc = 0xffffffff;
                for (int i = 0; i < buffer.Length; i++)
                    crc = Table[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
                return crc ^ 0xffffffff;
            }
        }
    }
}
