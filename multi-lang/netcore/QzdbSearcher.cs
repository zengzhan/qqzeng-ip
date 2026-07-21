using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Qqzeng
{
    public class GeoInfo
    {
        public Dictionary<string, string> Fields;
        public string[] FieldNames;
        public HashSet<string> FloatIndices;
        public string[] Values;

        public bool IsEmpty => Fields == null || Fields.Count == 0;

        public string Get(string name)
        {
            if (Fields != null && Fields.TryGetValue(name, out var val))
                return val;
            return "";
        }

        public string ToPipe()
        {
            if (IsEmpty) return "";
            var parts = new string[FieldNames.Length];
            for (int i = 0; i < FieldNames.Length; i++)
            {
                string fname = FieldNames[i];
                string val = Fields.TryGetValue(fname, out var v) ? v : "";
                if (FloatIndices.Contains(fname) && val.Length > 0)
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
        private static readonly HashSet<string> FloatFields = new() { "longitude", "latitude" };

        private byte[] _data;
        private int _groupIndex;
        private string[] _fieldNames;
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
                var raw = File.ReadAllBytes(dbPath);
                _data = raw;
                ParseHeader(raw);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ushort ReadU16(byte[] d, int off)
        {
            return BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(off, 2));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint ReadU32(byte[] d, int off)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(off, 4));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ulong ReadU64(byte[] d, int off)
        {
            return BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(off, 8));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint ReadU24(byte[] d, int off)
        {
            return (uint)(d[off] | (d[off + 1] << 8) | (d[off + 2] << 16));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private long ReadU48(byte[] d, int off)
        {
            return (long)(d[off] | ((long)d[off + 1] << 8) | ((long)d[off + 2] << 16) |
                          ((long)d[off + 3] << 24) | ((long)d[off + 4] << 32) | ((long)d[off + 5] << 40));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint ReadUintWidth(byte[] d, int off, int width)
        {
            if (width <= 1) return d[off];
            if (width == 2) return ReadU16(d, off);
            if (width == 3) return ReadU24(d, off);
            return ReadU32(d, off);
        }

        private void ParseHeader(byte[] d)
        {
            if (d.Length < 192)
                throw new InvalidDataException("File too small for QZDB header");

            string magic = Encoding.ASCII.GetString(d, 0, 4);
            if (magic != "QZDB")
                throw new InvalidDataException("Invalid magic, expected QZDB");

            int fmtVer = d[4];
            if (fmtVer < 1 || fmtVer > 6)
                throw new InvalidDataException($"Unsupported format version: {fmtVer}");

            _flags = ReadU16(d, 8);
            _hasV4 = (_flags & 1) != 0;
            _hasV6 = (_flags & 2) != 0;
            _v4Node24 = (_flags & 0x10) != 0;
            _v6Node24 = (_flags & 0x20) != 0;

            _v6JumpBits = d[11];
            if (_v6JumpBits == 0) _v6JumpBits = 16;

            _poolCount = d[12];
            _poolIdxSize = d[13];
            _geoCount = ReadU16(d, 14);
            _rowCount = (int)ReadU32(d, 20);
            _v4RecCount = ReadU32(d, 24);
            _v6RecCount = ReadU32(d, 28);

            int hs = (int)ReadU32(d, 36);
            if (hs != 192)
                throw new InvalidDataException($"Unexpected header size: {hs}");

            _offRowSchema = (long)ReadU64(d, 40);
            _offGroupSchema = (long)ReadU64(d, 48);
            _offV4Jump = (long)ReadU64(d, 64);
            _offV4Nodes = (long)ReadU64(d, 72);
            _offV6Jump = (long)ReadU64(d, 80);
            _offV6Nodes = (long)ReadU64(d, 88);
            _offIPRow = (long)ReadU64(d, 96);
            _offGeoEntries = (long)ReadU64(d, 104);
            _offPools = (long)ReadU64(d, 136);
            _offMeta = (long)ReadU64(d, 144);

            _v4NodeCount = ReadU32(d, 152);
            _v6NodeCount = ReadU32(d, 156);
            _ipRowSize = (int)ReadU32(d, 160);
            _geoEntryGroupCount = (int)ReadU32(d, 164);

            _groupEntryOffsets = new long[4];
            for (int i = 0; i < 4; i++)
                _groupEntryOffsets[i] = ReadU48(d, 168 + i * 6);

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
                    _groupEntryCounts[gi] = ReadU32(d, gmOff);
                    gmOff += 4;
                }
                else
                {
                    _groupEntryCounts[gi] = ReadU16(d, gmOff);
                    gmOff += 2;
                }

                if (fmtVer == 1 || fmtVer >= 3)
                {
                    _groupDimMasks[gi] = ReadU16(d, gmOff);
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
                int gsGroupCount = ReadU16(d, sp);
                sp += 2;
                int maxGsGroups = Math.Min(gsGroupCount, actualGroups);
                for (int gi = 0; gi < maxGsGroups; gi++)
                {
                    sp += 2; // skip groupId
                    int fldCount = ReadU16(d, sp);
                    sp += 2;
                    sp += 4; // skip entryCount
                    int stride = (int)ReadU32(d, sp);
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
                            fieldIds[fi] = ReadU16(d, sp);
                            sp += 2;
                            widths[fi] = d[sp];
                            sp++;
                            int fieldFlags = d[sp];
                            sp++;
                            natives[fi] = (fieldFlags & 0x01) != 0;
                            natTypes[fi] = (fieldFlags >> 1) & 0x03;
                            offsets[fi] = (int)ReadU32(d, sp);
                            sp += 4;
                            poolSectionIds[fi] = ReadU32(d, sp);
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
                    ushort length = ReadU16(d, pos + 2);
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
            _floatFieldIndices.Clear();
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
                        int count = (int)ReadU32(d, poolCursor);
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
                            offsets[o] = ReadU32(d, poolCursor);
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
                    return (val & 0x7FFFFF) | SENTINEL;
                return val;
            }
            else
            {
                long childOff = _offV4Nodes + nodeIdx * 8 + bit * 4;
                return ReadU32(_data, (int)childOff);
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
                    return (val & 0x7FFFFF) | SENTINEL;
                return val;
            }
            else
            {
                long childOff = _offV6Nodes + nodeIdx * 8 + bit * 4;
                return ReadU32(_data, (int)childOff);
            }
        }

        private uint TrieWalkV4(uint ipInt)
        {
            uint hi16 = (ipInt >> 16) & 0xFFFF;
            uint ptr = ReadU32(_data, (int)(_offV4Jump + hi16 * 4));

            if (ptr == 0) return 0;
            if ((ptr & SENTINEL) != 0) return ptr & 0x7FFFFFFF;

            uint idx = ptr;
            uint suffix = (ipInt & 0xFFFF) << 16;
            uint steps = 0;

            while (true)
            {
                if (++steps > 32) return 0;
                uint bit = (suffix >> 31) & 1;
                uint child = GetV4Child(idx, bit);

                if (child == 0) return 0;
                if ((child & SENTINEL) != 0) return child & 0x7FFFFFFF;

                idx = child;
                suffix <<= 1;
            }
        }

        private uint TrieWalkV6(BigInteger ipInt)
        {
            int shift = 128 - _v6JumpBits;
            uint idxJump = (uint)(ipInt >> shift) & (uint)((1 << _v6JumpBits) - 1);
            uint ptr = ReadU32(_data, (int)(_offV6Jump + idxJump * 4));

            if (ptr == 0) return 0;
            if ((ptr & SENTINEL) != 0) return ptr & 0x7FFFFFFF;

            uint idx = ptr;
            int depth = _v6JumpBits;

            while (depth < 128)
            {
                uint bit = (uint)((ipInt >> (127 - depth)) & 1);
                uint child = GetV6Child(idx, bit);

                if (child == 0) return 0;
                if ((child & SENTINEL) != 0) return child & 0x7FFFFFFF;

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
            geoId = ReadU24(_data, (int)off);
            asnId = ReadU24(_data, (int)off + 3);
            if (_ipRowSize >= 9)
                usageTypeId = ReadU24(_data, (int)off + 6);
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

            var fields = new Dictionary<string, string>(fieldCount);
            for (int i = 0; i < fieldCount; i++)
            {
                int w = widths[i];
                int fo = (int)(entryOffset + baseOffsets[i]);
                bool isNative = natives != null && i < natives.Length && natives[i];

                string val;
                string fname;
                if (isNative)
                {
                    int t = (natTypes != null && i < natTypes.Length) ? natTypes[i] : 0;
                    if (t == 1)
                    {
                        if (w == 4)
                            val = BitConverter.ToSingle(d, fo).ToString(CultureInfo.InvariantCulture);
                        else
                            val = BitConverter.ToDouble(d, fo).ToString(CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        uint valNum = ReadUintWidth(d, fo, w);
                        val = valNum.ToString();
                    }
                    fname = i < _fieldNames.Length ? _fieldNames[i] : $"field_{i}";
                }
                else
                {
                    uint idx = ReadUintWidth(d, fo, w);
                    string[][] gp = _groupPools[groupIndex];
                    
                    ushort fieldId = 0;
                    if (_groupFieldIds != null && i < _groupFieldIds[groupIndex]?.Length)
                    {
                        fieldId = _groupFieldIds[groupIndex][i];
                    }
                    
                    if (i < _fieldNames.Length && fieldId < _fieldNames.Length)
                    {
                        fname = _fieldNames[fieldId];
                    }
                    else
                    {
                        fname = i < _fieldNames.Length ? _fieldNames[i] : $"field_{i}";
                    }
                    
                    // Pools are indexed by field position, not poolSectionId
                    if (gp != null && i < gp.Length && idx < (uint)gp[i].Length)
                    {
                        val = gp[i][(int)idx];
                    }
                    else
                    {
                        val = "";
                    }
                }

                fields[fname] = val;
            }

            var values = new string[fieldCount];
            for (int i = 0; i < fieldCount; i++)
            {
                string fname = i < _fieldNames.Length ? _fieldNames[i] : $"field_{i}";
                values[i] = fields[fname];
            }

            return new GeoInfo { Fields = fields, FieldNames = _fieldNames, FloatIndices = _floatFieldIndices, Values = values };
        }

        public GeoInfo Find(string ipStr)
        {
            if (string.IsNullOrEmpty(ipStr)) return null;

            if (ipStr.Contains(':'))
            {
                if (System.Net.IPAddress.TryParse(ipStr, out var addr))
                {
                    var bytes = addr.GetAddressBytes();
                    if (bytes.Length == 4)
                    {
                        uint ipInt = BinaryPrimitives.ReadUInt32BigEndian(bytes);
                        return FindUint(ipInt);
                    }

                    // Check for IPv4-mapped IPv6
                    if (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0 &&
                        bytes[4] == 0 && bytes[5] == 0 && bytes[6] == 0 && bytes[7] == 0 &&
                        bytes[8] == 0 && bytes[9] == 0 && bytes[10] == 0xff && bytes[11] == 0xff)
                    {
                        uint ipInt = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(12, 4));
                        return FindUint(ipInt);
                    }

                    BigInteger ipIntV6 = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
                    return FindV6Uint(ipIntV6);
                }
                return null;
            }

            if (!fastParseIpV4(ipStr, out uint v4Int)) return null;
            return FindUint(v4Int);
        }

        public GeoInfo FindUint(uint ipInt)
        {
            if (!_hasV4) return null;
            uint rowId = TrieWalkV4(ipInt);
            if (rowId == 0) return null;
            return ResolveRowId(rowId, _groupIndex);
        }

        public GeoInfo FindV6Uint(BigInteger ipInt)
        {
            if (!_hasV6) return null;
            uint rowId = TrieWalkV6(ipInt);
            if (rowId == 0) return null;
            return ResolveRowId(rowId, _groupIndex);
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

        private static bool fastParseIpV4(string ip, out uint v4Int)
        {
            v4Int = 0;
            uint result = 0;
            int val = 0, dots = 0;
            for (int i = 0; i < ip.Length; i++)
            {
                char c = ip[i];
                if (c >= '0' && c <= '9')
                {
                    val = val * 10 + (c - '0');
                    if (val > 255) return false;
                }
                else if (c == '.')
                {
                    if (i == 0 || ip[i - 1] == '.') return false;
                    result = (result << 8) | (uint)val;
                    val = 0;
                    dots++;
                }
                else return false;
            }
            if (dots != 3) return false;
            if (ip.Length > 0 && ip[ip.Length - 1] == '.') return false;
            v4Int = (result << 8) | (uint)val;
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
