namespace Qzdb;

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// QZDB high-performance IP geolocation reader (.NET 10).
/// Lock-free snapshot architecture: queries never block each other; reload is atomic via Interlocked.Exchange.
/// </summary>
public sealed class DatabaseReader : IDisposable
{
    private const int HeaderSize = 192;
    private const uint Sentinel = 0x80000000;
    private const uint SentinelMask24 = 0x7FFFFF;
    private const uint SentinelMask31 = 0x7FFFFFFF;
    private const int MaxPoolCount = 1 << 24;
    private const int CrcChunk = 1 << 20;
    private static readonly byte[] Zero4 = new byte[4];

    private Snapshot? _activeSnapshot;
    private readonly bool _ownsMemory;

    private DatabaseReader(Snapshot snap, bool ownsMemory)
    {
        _activeSnapshot = snap;
        _ownsMemory = ownsMemory;
    }

    internal static Snapshot VolatileReadSnapshot(ref Snapshot? s) => Unsafe.As<Snapshot?, VolatileBox>(ref s)!.Value;

    private sealed class VolatileBox { public Snapshot Value; }

    private Snapshot SnapshotVolatileRead() => VolatileReadSnapshot(ref _activeSnapshot);

    private Snapshot RequireSnapshot()
    {
        var s = _activeSnapshot;
        if (s == null) throw new ObjectDisposedException(nameof(DatabaseReader));
        return s;
    }

    #region Builder

    public sealed class Builder
    {
        internal FileStream? _fileStream;
        internal byte[]? _buffer;
        internal int _groupIndex;
        internal bool _verifyCrc = true;

        public Builder(string path) { _fileStream = File.OpenRead(path); }
        public Builder(byte[] buffer) { _buffer = buffer; }
        public Builder GroupIndex(int idx) { _groupIndex = idx; return this; }
        public Builder VerifyCrc(bool enabled) { _verifyCrc = enabled; return this; }

        public DatabaseReader Build()
        {
            if (_fileStream != null)
            {
                var snap = Snapshot.FromStream(_fileStream, _groupIndex, _verifyCrc);
                return new DatabaseReader(snap, true);
            }
            if (_buffer != null)
            {
                var snap = Snapshot.FromBuffer(_buffer, _groupIndex, _verifyCrc);
                return new DatabaseReader(snap, false);
            }
            throw new QzdbException(ErrorCode.InvalidParam, "Neither file path nor buffer was provided");
        }
    }

    #endregion

    #region Core snapshot (immutable after construction)

    internal sealed class Snapshot
    {
        internal ReadOnlyMemory<byte> _data;
        internal int _dataLen;
        internal int _groupIndex;

        internal int _flags;
        internal bool _hasV4, _hasV6, _v4Node24, _v6Node24;
        internal int _v6JumpBits, _poolCount, _poolIdxSize;
        internal int _rowCount, _v4NodeCount, _v6NodeCount, _ipRowSize, _buildDate;

        internal long _offRowSchema, _offGroupSchema;
        internal long _offV4Jump, _offV4Nodes, _offV6Jump, _offV6Nodes;
        internal long _offIPRow, _offGeoEntries, _offPools, _offMeta;

        internal int _rowGeoWidth, _rowAsnWidth, _rowUsageWidth;

        internal int _actualGroups;
        internal int[] _groupFieldCounts;
        internal long[] _groupEntryCounts;
        internal int[] _groupDimMasks;
        internal long[] _groupEntryOffsets;
        internal int[] _groupStrides;
        internal int[][] _groupFieldWidths;
        internal int[][] _groupFieldOffsets;
        internal bool[][] _groupFieldNative;
        internal int[][] _groupFieldNativeType;

        internal string[][][] _pools;
        internal string[] _fieldNames;
        internal Dictionary<string, int> _normMap;
        internal bool[] _numericFlags;

        internal string _version, _description, _dataMonth, _buildTimeStr, _edition, _scope;
        internal long _storedCrc;
        internal long? _canonicalCrc;

        public static Snapshot FromStream(FileStream fs, int groupIndex, bool verifyCrc)
        {
            var len = (int)fs.Length;
            byte[] buf = GC.AllocateUninitializedArray<byte>(len);
            fs.ReadExactly(buf);
            fs.Dispose();
            return new Snapshot(buf, groupIndex, verifyCrc, true);
        }

        public static Snapshot FromBuffer(byte[] buffer, int groupIndex, bool verifyCrc)
        {
            var copy = GC.AllocateUninitializedArray<byte>(buffer.Length);
            Buffer.BlockCopy(buffer, 0, copy, 0, buffer.Length);
            return new Snapshot(copy, groupIndex, verifyCrc, true);
        }

        internal Snapshot(byte[] buffer, int groupIndex, bool verifyCrc, bool _)
        {
            _data = buffer;
            _dataLen = buffer.Length;
            _groupIndex = groupIndex;

            ValidateHeader();
            ValidateSectionBounds();
            ParseRowSchema();
            ParseGroups();
            ParseMetadata();
            ParsePools();

            if (verifyCrc)
            {
                long calc = ComputeCanonicalCrc(this);
                _canonicalCrc = calc;
                if (calc != _storedCrc)
                    throw new QzdbException(ErrorCode.Corrupted,
                        $"CRC32 mismatch: stored=0x{_storedCrc:x8} calculated=0x{calc:x8}");
            }
        }

        #region Parse sub-methods

        internal void ValidateHeader()
        {
            if (_dataLen < HeaderSize) throw new QzdbException(ErrorCode.Corrupted, "File too small");
            if (_data.Span[0] != 'Q' || _data.Span[1] != 'Z' || _data.Span[2] != 'D' || _data.Span[3] != 'B')
                throw new QzdbException(ErrorCode.BadMagic, "Invalid magic");

            var span = _data.Span;
            int fmtVer = span[4];
            if (fmtVer != 1) throw new QzdbException(ErrorCode.Unsupported, $"Unsupported version: {fmtVer}");

            _flags = BinaryPrimitives.ReadUInt16LittleEndian(span[8..]);
            _hasV4 = (_flags & 1) != 0;
            _hasV6 = (_flags & 2) != 0;
            _v4Node24 = (_flags & 0x10) != 0;
            _v6Node24 = (_flags & 0x20) != 0;

            _v6JumpBits = span[11];
            if (_v6JumpBits == 0) _v6JumpBits = 16;
            if (_v6JumpBits < 8 || _v6JumpBits > 20)
                throw new QzdbException(ErrorCode.InvalidParam, $"v6JumpBits out of range: {_v6JumpBits}");

            _poolCount = span[12];
            _poolIdxSize = span[13];
            if (_poolIdxSize != 2 && _poolIdxSize != 3)
                throw new QzdbException(ErrorCode.InvalidParam, $"poolIdxSize must be 2 or 3");

            _buildDate = BinaryPrimitives.ReadInt32LittleEndian(span[32..]);
            _rowCount = BinaryPrimitives.ReadInt32LittleEndian(span[20..]);
            _storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(span[16..]) & 0xFFFFFFFF;

            int hs = BinaryPrimitives.ReadInt32LittleEndian(span[36..]);
            if (hs != HeaderSize) throw new QzdbException(ErrorCode.BadHeader, $"Unexpected header size: {hs}");

            _offRowSchema = BinaryPrimitives.ReadInt64LittleEndian(span[40..]);
            _offGroupSchema = BinaryPrimitives.ReadInt64LittleEndian(span[48..]);
            _offV4Jump = BinaryPrimitives.ReadInt64LittleEndian(span[64..]);
            _offV4Nodes = BinaryPrimitives.ReadInt64LittleEndian(span[72..]);
            _offV6Jump = BinaryPrimitives.ReadInt64LittleEndian(span[80..]);
            _offV6Nodes = BinaryPrimitives.ReadInt64LittleEndian(span[88..]);
            _offIPRow = BinaryPrimitives.ReadInt64LittleEndian(span[96..]);
            _offGeoEntries = BinaryPrimitives.ReadInt64LittleEndian(span[104..]);
            _offPools = BinaryPrimitives.ReadInt64LittleEndian(span[136..]);
            _offMeta = BinaryPrimitives.ReadInt64LittleEndian(span[144..]);

            _v4NodeCount = BinaryPrimitives.ReadInt32LittleEndian(span[152..]);
            _v6NodeCount = BinaryPrimitives.ReadInt32LittleEndian(span[156..]);

            _ipRowSize = BinaryPrimitives.ReadInt32LittleEndian(span[160..]);
            if (_ipRowSize < 1 || _ipRowSize > 64)
                throw new QzdbException(ErrorCode.InvalidParam, $"ipRowSize out of range: {_ipRowSize}");
        }

        internal void ValidateSectionBounds()
        {
            var span = _data.Span;
            long dlen = _dataLen;
            int v4NodeSize = _v4Node24 ? 6 : 8;
            int v6NodeSize = _v6Node24 ? 6 : 8;
            CheckSection(_offV4Jump, 65536L * 4, dlen, "v4_jump");
            CheckSection(_offV4Nodes, (long)_v4NodeCount * v4NodeSize, dlen, "v4_nodes");
            CheckSection(_offV6Jump, (1L << _v6JumpBits) * 4, dlen, "v6_jump");
            CheckSection(_offV6Nodes, (long)_v6NodeCount * v6NodeSize, dlen, "v6_nodes");
            CheckSection(_offIPRow, (long)_rowCount * _ipRowSize, dlen, "ip_row");

            if (_offGeoEntries > 0 && _offGeoEntries >= dlen) throw new QzdbException(ErrorCode.Corrupted, "geo_entries out of bounds");
            if (_offPools > 0 && _offPools >= dlen) throw new QzdbException(ErrorCode.Corrupted, "pools out of bounds");
            if (_offMeta > 0 && _offMeta > dlen) throw new QzdbException(ErrorCode.Corrupted, "meta out of bounds");

            if (_hasV4 && _offV4Jump <= 0) throw new QzdbException(ErrorCode.Corrupted, "hasV4 but V4 jump offset is zero");
            if (_hasV4 && _v4NodeCount > 0 && _offV4Nodes <= 0) throw new QzdbException(ErrorCode.Corrupted, "V4 node offset is zero");
            if (_hasV6 && _offV6Jump <= 0) throw new QzdbException(ErrorCode.Corrupted, "hasV6 but V6 jump offset is zero");
            if (_hasV6 && _v6NodeCount > 0 && _offV6Nodes <= 0) throw new QzdbException(ErrorCode.Corrupted, "V6 node offset is zero");
            if (_offIPRow <= 0) throw new QzdbException(ErrorCode.Corrupted, "Missing IPRow section");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CheckSection(long off, long size, long dlen, string name)
        {
            if (off > 0 && off + size > dlen)
                throw new QzdbException(ErrorCode.Corrupted, $"Section {name} out of bounds");
        }

        internal void ParseRowSchema()
        {
            _rowGeoWidth = 3; _rowAsnWidth = 3; _rowUsageWidth = 0;
            var span = _data.Span;
            if (_offRowSchema <= 0 || _offRowSchema + 4 > _dataLen) return;
            int sp = (int)_offRowSchema;
            int fCount = span[sp];
            int stride = span[sp + 1];
            if (fCount < 1 || fCount > 8 || sp + 4 + fCount * 4 > _dataLen || stride != _ipRowSize) return;

            int g2 = 0, a2 = 0, u2 = 0, total = 0;
            int wpos = sp + 4;
            bool ok = true;
            for (int i = 0; i < fCount; i++)
            {
                int fid = span[wpos];
                int w = span[wpos + 1];
                if (w < 1 || w > 4) ok = false;
                if (fid == 0) g2 = w;
                else if (fid == 1) a2 = w;
                else if (fid == 2) u2 = w;
                wpos += 4;
                total += w;
            }
            if (ok && total == _ipRowSize)
            {
                _rowGeoWidth = g2;
                _rowAsnWidth = a2;
                _rowUsageWidth = u2;
            }
        }

        internal void ParseGroups()
        {
            var span = _data.Span;
            int gCount = BinaryPrimitives.ReadInt32LittleEndian(span[164..]);

            long[] headerGeoOffsets = new long[4];
            for (int i = 0; i < 4; i++)
            {
                headerGeoOffsets[i] = ReadU48(span, 168 + i * 6);
            }

            int gmOff = (int)_offGeoEntries;
            int tableGroups = span[gmOff];
            gmOff++;

            int groups = Math.Min(tableGroups, gCount);
            if (groups > 4) groups = 4;
            if (groups < 1) throw new QzdbException(ErrorCode.Corrupted, "Group count is 0");
            if (_groupIndex < 0 || _groupIndex >= groups)
                throw new QzdbException(ErrorCode.InvalidParam, $"groupIndex {_groupIndex} out of range");

            _actualGroups = groups;
            _groupFieldCounts = new int[groups];
            _groupEntryCounts = new long[groups];
            _groupEntryOffsets = new long[groups];
            _groupDimMasks = new int[groups];
            _groupStrides = new int[groups];
            _groupFieldWidths = new int[groups][];
            _groupFieldOffsets = new int[groups][];
            _groupFieldNative = new bool[groups][];
            _groupFieldNativeType = new int[groups][];

            for (int gi = 0; gi < groups; gi++)
            {
                _groupFieldCounts[gi] = span[gmOff];
                gmOff++;
                _groupEntryCounts[gi] = BinaryPrimitives.ReadUInt32LittleEndian(span[gmOff..]) & 0xFFFFFFFF;
                gmOff += 4;
                _groupDimMasks[gi] = BinaryPrimitives.ReadUInt16LittleEndian(span[gmOff..]);
                gmOff += 2;
                _groupEntryOffsets[gi] = _offGeoEntries + headerGeoOffsets[gi];
            }

            if (_offGroupSchema > 0 && _offGroupSchema + 2 <= _dataLen)
            {
                int sp = (int)_offGroupSchema;
                int gsGroupCount = BinaryPrimitives.ReadUInt16LittleEndian(span[sp..]);
                sp += 2;
                int maxGsGroups = Math.Min(gsGroupCount, groups);
                for (int gi = 0; gi < maxGsGroups; gi++)
                {
                    if (sp + 14 > _dataLen) break;
                    sp += 2;
                    int fldCount = BinaryPrimitives.ReadUInt16LittleEndian(span[sp..]);
                    sp += 2;
                    sp += 4;
                    int stride = BinaryPrimitives.ReadInt32LittleEndian(span[sp..]);
                    sp += 4;
                    sp += 4;

                    if (sp + (long)fldCount * 12 > _dataLen) break;

                    _groupStrides[gi] = stride;
                    var widths = new int[fldCount];
                    var offsets = new int[fldCount];
                    var natives = new bool[fldCount];
                    var natTypes = new int[fldCount];
                    for (int fi = 0; fi < fldCount; fi++)
                    {
                        sp += 2;
                        widths[fi] = span[sp];
                        sp++;
                        int fieldFlags = span[sp];
                        sp++;
                        natives[fi] = (fieldFlags & 0x01) != 0;
                        natTypes[fi] = (fieldFlags >> 1) & 0x03;
                        offsets[fi] = BinaryPrimitives.ReadInt32LittleEndian(span[sp..]);
                        sp += 4;
                        sp += 4;
                    }
                    _groupFieldWidths[gi] = widths;
                    _groupFieldOffsets[gi] = offsets;
                    _groupFieldNative[gi] = natives;
                    _groupFieldNativeType[gi] = natTypes;
                }
            }

            for (int g = 0; g < groups; g++)
            {
                int fc = _groupFieldCounts[g];
                if (_groupStrides[g] == 0) _groupStrides[g] = fc * _poolIdxSize;
                if (_groupFieldWidths[g] == null) _groupFieldWidths[g] = Enumerable.Repeat(_poolIdxSize, fc).ToArray();
                if (_groupFieldOffsets[g] == null)
                {
                    var o = new int[fc];
                    for (int i = 0; i < fc; i++) o[i] = i * _poolIdxSize;
                    _groupFieldOffsets[g] = o;
                }
                if (_groupFieldNative[g] == null) _groupFieldNative[g] = new bool[fc];
                if (_groupFieldNativeType[g] == null) _groupFieldNativeType[g] = new int[fc];
            }
        }

        internal void ParseMetadata()
        {
            var span = _data.Span;
            _version = "";
            _description = "";
            _dataMonth = "";
            _buildTimeStr = "";
            _edition = "";
            _scope = "";

            string[]? metaFields = null;
            if ((_flags & 4) != 0 && _offMeta > 0 && _offMeta + 4 <= _dataLen)
            {
                int cursor = (int)_offMeta;
                while (cursor + 4 <= _dataLen)
                {
                    int type = span[cursor];
                    int length = BinaryPrimitives.ReadUInt16LittleEndian(span[(cursor + 2)..]);
                    if (type == 0 || length == 0) break;
                    if (cursor + 4L + length > _dataLen) break;
                    var val = Encoding.UTF8.GetString(span.Slice(cursor + 4, length));
                    switch (type)
                    {
                        case 1: _version = val; break;
                        case 2: metaFields = val.Split('|'); break;
                        case 3: _description = val; break;
                    }
                    cursor += 4 + length;
                }
            }

            int numFields = _groupFieldCounts[_groupIndex];
            _fieldNames = metaFields != null && metaFields.Length == numFields
                ? metaFields
                : FallbackFieldNames(numFields);

            _normMap = GeoInfo.BuildNormalizedMap(_fieldNames);
            _numericFlags = new bool[_fieldNames.Length];
            for (int i = 0; i < _fieldNames.Length; i++)
                _numericFlags[i] = GeoInfo.IsNumericFieldName(_fieldNames[i]);

            // Repair dimension masks
            for (int g = 0; g < _actualGroups; g++)
            {
                if (_groupDimMasks[g] != 0) continue;
                bool hasAsn = false;
                for (int fi = 0; fi < _fieldNames.Length; fi++)
                {
                    if (_fieldNames[fi] == "asn") { hasAsn = true; break; }
                }
                _groupDimMasks[g] = hasAsn ? 0x02 : 0x01;
            }

            if (_buildDate > 0)
            {
                int y = _buildDate / 10000;
                int m = (_buildDate / 100) % 100;
                int dd = _buildDate % 100;
                _dataMonth = $"{y:D4}-{m:D2}";
                _buildTimeStr = $"{y:D4}-{m:D2}-{dd:D2}";
            }

            _edition = InferEdition(numFields);
        }

        private static string[] FallbackFieldNames(int count) => count switch
        {
            6 => ["continent", "country_code", "country", "province", "city", "isp"],
            8 => ["continent", "country_code", "country", "isp", "asn", "as_name", "as_domain", "usage_type"],
            11 => ["continent", "country_code", "country", "province", "city", "district", "geo_id", "longitude", "latitude", "timezone", "isp"],
            15 => ["continent", "country_code", "country", "province", "city", "district", "geo_id", "longitude", "latitude", "timezone", "isp", "asn", "as_name", "as_domain", "usage_type"],
            25 => ["continent", "continent_en", "country_code", "country_alpha3", "country", "country_en", "province", "province_en", "city", "city_en", "district", "district_en", "geo_id", "longitude", "latitude", "timezone", "languages", "currency_code", "phone_prefix", "emoji_flag", "isp", "asn", "as_name", "as_domain", "usage_type"],
            _ => Enumerable.Range(0, count).Select(i => $"field_{i}").ToArray()
        };

        internal string InferEdition(int count) => count switch
        {
            6 => "std",
            8 => "asn",
            11 => "pro",
            15 => "max",
            25 => "ult",
            _ => "std"
        };

        internal void ParsePools()
        {
            var span = _data.Span;
            _pools = new string[_actualGroups][][];
            if (_offPools <= 0) return;

            long poolCursor = _offPools;
            long poolEnd = _offMeta > 0 ? _offMeta : _dataLen;

            for (int g = 0; g < _actualGroups; g++)
            {
                int fieldCount = _groupFieldCounts[g];
                var groupPoolList = new string[fieldCount][];
                bool[] natives = _groupFieldNative[g];

                for (int f = 0; f < fieldCount; f++)
                {
                    if (natives.Length > f && natives[f]) { groupPoolList[f] = []; continue; }
                    if (poolCursor + 4 > poolEnd) { groupPoolList[f] = []; continue; }

                    int count = BinaryPrimitives.ReadInt32LittleEndian(span[(int)poolCursor..]);
                    poolCursor += 4;
                    if (_offRowSchema > 0) poolCursor += 4;
                    if (count <= 0 || count > MaxPoolCount) { groupPoolList[f] = []; continue; }

                    int cnt = count;
                    long stringDataStart = poolCursor + (count + 1) * 4;
                    if (stringDataStart > poolEnd) { groupPoolList[f] = []; continue; }

                    var strings = new string[cnt];
                    for (int i = 0; i < cnt; i++)
                    {
                        int strOff = BinaryPrimitives.ReadInt32LittleEndian(span[(int)(poolCursor + i * 4)..]);
                        int nextOff = BinaryPrimitives.ReadInt32LittleEndian(span[(int)(poolCursor + (i + 1) * 4)..]);
                        int len = nextOff - strOff;
                        if (len > 0 && stringDataStart + strOff + len <= _dataLen)
                            strings[i] = Encoding.UTF8.GetString(span.Slice((int)(stringDataStart + strOff), len));
                        else
                            strings[i] = "";
                    }
                    groupPoolList[f] = strings;
                    poolCursor = stringDataStart + BinaryPrimitives.ReadUInt32LittleEndian(span[(int)(poolCursor + count * 4)..]);
                }
                _pools[g] = groupPoolList;
            }
        }

        #endregion

        #region Helpers

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long ReadU48(ReadOnlySpan<byte> s, int off) =>
            (s[off] | ((long)s[off + 1] << 8) | ((long)s[off + 2] << 16) |
             ((long)s[off + 3] << 24) | ((long)s[off + 4] << 32) | ((long)s[off + 5] << 40));

        #endregion
    }

    #endregion

    #region Public query API (lock-free, no contention)

    public GeoInfo? Find(string ipStr)
    {
        if (string.IsNullOrEmpty(ipStr)) return null;
        if (!TryParseIp(ipStr, out var v4, out var v6High, out var v6Low, out var isV4)) return null;

        var snap = RequireSnapshot();
        if (snap == null) return null;

        if (isV4)
        {
            uint rowId = TrieWalkV4(snap, v4);
            return rowId > 0 ? ResolveRowId(snap, rowId) : null;
        }
        else
        {
            uint rowId = TrieWalkV6(snap, v6High, v6Low);
            return rowId > 0 ? ResolveRowId(snap, rowId) : null;
        }
    }

    public GeoInfo? FindBytes(byte[]? ipBytes)
    {
        if (ipBytes == null) return null;
        var snap = RequireSnapshot();
        if (snap == null) return null;

        uint rowId;
        if (ipBytes.Length == 16)
        {
            bool mapped = IsV4Mapped(ipBytes);
            if (mapped) { rowId = TrieWalkV4(snap, V4FromMapped(ipBytes)); }
            else
            {
                var (hi, lo) = V6FromBytes(ipBytes);
                rowId = TrieWalkV6(snap, hi, lo);
            }
        }
        else if (ipBytes.Length == 4)
        {
            uint v4 = (uint)((ipBytes[0] << 24) | (ipBytes[1] << 16) | (ipBytes[2] << 8) | ipBytes[3]);
            rowId = TrieWalkV4(snap, v4);
        }
        else return null;

        return rowId > 0 ? ResolveRowId(snap, rowId) : null;
    }

    public string FindStr(string ipStr)
    {
        var info = Find(ipStr);
        return info == null ? "" : info.ToPipe();
    }

    public uint LookupRowId(string ipStr)
    {
        if (string.IsNullOrEmpty(ipStr)) return 0;
        if (!TryParseIp(ipStr, out var v4, out var v6High, out var v6Low, out var isV4)) return 0;

        var snap = RequireSnapshot();
        if (snap == null) return 0;

        return isV4 ? TrieWalkV4(snap, v4) : TrieWalkV6(snap, v6High, v6Low);
    }

    #endregion

    #region Unsafe Trie Walk (zero allocation, bypass bounds check)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe uint TrieWalkV4(Snapshot snap, uint ipInt)
    {
        if (!snap._hasV4 || snap._offV4Jump <= 0) return 0;

        fixed (byte* bp = snap._data.Span)
        {
            uint* jump = (uint*)(bp + snap._offV4Jump);
            uint hi16 = (ipInt >> 16) & 0xFFFF;
            uint ptr = jump[hi16];

            if (ptr == 0) return 0;
            if ((ptr & Sentinel) != 0) return ptr & SentinelMask31;

            uint idx = ptr;
            uint suffix = (ipInt & 0xFFFF) << 16;
            byte* nodes = bp + snap._offV4Nodes;

            if (snap._v4Node24)
            {
                for (int step = 0; step < 16; step++)
                {
                    if (idx >= (uint)snap._v4NodeCount) return 0;
                    byte* node = nodes + idx * 6;
                    int off = ((suffix >> 31) & 1) == 0 ? 0 : 3;
                    uint child = (uint)(node[off] | (node[off + 1] << 8) | (node[off + 2] << 16));
                    if ((child & 0x800000) != 0) return child & SentinelMask24;
                    if (child == 0) return 0;
                    idx = child;
                    suffix <<= 1;
                }
            }
            else
            {
                for (int step = 0; step < 16; step++)
                {
                    if (idx >= (uint)snap._v4NodeCount) return 0;
                    uint* node = (uint*)(nodes + idx * 8);
                    uint bit = (suffix >> 31) & 1;
                    uint child = node[bit];
                    if ((child & Sentinel) != 0) return child & SentinelMask31;
                    if (child == 0) return 0;
                    idx = child;
                    suffix <<= 1;
                }
            }
        }
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe uint TrieWalkV6(Snapshot snap, ulong ipHigh, ulong ipLow)
    {
        if (!snap._hasV6 || snap._offV6Jump <= 0) return 0;

        fixed (byte* bp = snap._data.Span)
        {
            int jumpBits = snap._v6JumpBits;
            uint idxJump = (uint)(ipHigh >> (64 - jumpBits));
            uint* jump = (uint*)(bp + snap._offV6Jump);
            uint ptr = jump[idxJump];

            if (ptr == 0) return 0;
            if ((ptr & Sentinel) != 0) return ptr & SentinelMask31;

            uint idx = ptr;
            byte* nodes = bp + snap._offV6Nodes;

            if (snap._v6Node24)
            {
                for (int depth = jumpBits; depth < 128; depth++)
                {
                     if (idx >= (uint)snap._v6NodeCount) return 0;
                     uint bit2 = depth <= 63 ? (uint)((ipHigh >> (63 - depth)) & 1) : (uint)((ipLow >> (127 - depth)) & 1);
                    byte* node = nodes + idx * 6;
                    int off = bit2 == 0 ? 0 : 3;
                    uint child = (uint)(node[off] | (node[off + 1] << 8) | (node[off + 2] << 16));
                    if ((child & 0x800000) != 0) return child & SentinelMask24;
                    if (child == 0) return 0;
                    idx = child;
                }
            }
            else
            {
                for (int depth = jumpBits; depth < 128; depth++)
                {
                    if (idx >= (uint)snap._v6NodeCount) return 0;
                    uint bit = depth <= 63 ? (uint)((ipHigh >> (63 - depth)) & 1) : (uint)((ipLow >> (127 - depth)) & 1);
                    uint* node = (uint*)(nodes + idx * 8);
                    uint child = node[bit];
                    if ((child & Sentinel) != 0) return child & SentinelMask31;
                    if (child == 0) return 0;
                    idx = child;
                }
            }
        }
        return 0;
    }

    #endregion

    #region Resolve

    private static GeoInfo? ResolveRowId(Snapshot snap, uint rowId)
    {
        if (rowId >= snap._rowCount) return null;

        var span = snap._data.Span;
        long rOff = snap._offIPRow + (long)rowId * snap._ipRowSize;

        uint geoId = ReadUintWidth(span, (int)rOff, snap._rowGeoWidth);
        uint asnId = snap._rowAsnWidth > 0 ? ReadUintWidth(span, (int)(rOff + snap._rowGeoWidth), snap._rowAsnWidth) : 0;
        uint usageId = snap._rowUsageWidth > 0 ? ReadUintWidth(span, (int)(rOff + snap._rowGeoWidth + snap._rowAsnWidth), snap._rowUsageWidth) : 0;

        int mask = snap._groupDimMasks[snap._groupIndex];
        uint entryId = (mask & 0x02) != 0 ? asnId : (mask & 0x04) != 0 ? usageId : geoId;

        if (entryId == 0) return null;
        return ResolveGeo(snap, entryId);
    }

    private static GeoInfo ResolveGeo(Snapshot snap, uint entryId)
    {
        if (entryId >= snap._groupEntryCounts[snap._groupIndex]) return null;

        int gi = snap._groupIndex;
        int fc = snap._groupFieldCounts[gi];
        long entryOff = snap._groupEntryOffsets[gi] + (long)entryId * snap._groupStrides[gi];

        var span = snap._data.Span;
        var widths = snap._groupFieldWidths[gi];
        var offsets = snap._groupFieldOffsets[gi];
        var natives = snap._groupFieldNative[gi];
        var natTypes = snap._groupFieldNativeType[gi];
        var groupPools = snap._pools[gi];

        var values = new string[fc];
        for (int fi = 0; fi < fc; fi++)
        {
            int w = widths[fi];
            int fo = (int)(entryOff + offsets[fi]);

            if (natives[fi])
            {
                int nt = natTypes[fi];
                if (nt == 1)
                {
                     ref var r = ref Unsafe.Add(ref MemoryMarshal.GetReference(span), fo);
                     values[fi] = w == 4
                         ? Unsafe.ReadUnaligned<float>(ref r).ToString("F6", System.Globalization.CultureInfo.InvariantCulture)
                         : Unsafe.ReadUnaligned<double>(ref r).ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
                }
                else
                {
                    values[fi] = ReadUintWidth(span, fo, w).ToString();
                }
            }
            else
            {
                uint idx = ReadUintWidth(span, fo, w);
                var pool = groupPools[fi];
                values[fi] = idx < (uint)pool.Length ? pool[(int)idx] : "";
            }
        }

        return new GeoInfo(snap._fieldNames, values, snap._normMap, snap._numericFlags);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadUintWidth(ReadOnlySpan<byte> s, int off, int width) => width switch
    {
        <= 1 => s[off],
        2 => BinaryPrimitives.ReadUInt16LittleEndian(s[off..]),
        3 => (uint)(s[off] | (s[off + 1] << 8) | (s[off + 2] << 16)),
        _ => BinaryPrimitives.ReadUInt32LittleEndian(s[off..])
    };

    #endregion

    #region IPv4-mapped detection

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsV4Mapped(byte[] b) =>
        b[10] == 0xFF && b[11] == 0xFF &&
        b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 0 &&
        b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0 &&
        b[8] == 0 && b[9] == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint V4FromMapped(byte[] b) =>
        (uint)((b[12] << 24) | (b[13] << 16) | (b[14] << 8) | b[15]);

    private static (ulong hi, ulong lo) V6FromBytes(byte[] b)
    {
        ulong hi = 0;
        for (int i = 0; i < 8; i++) hi = (hi << 8) | b[i];
        ulong lo = 0;
        for (int i = 8; i < 16; i++) lo = (lo << 8) | b[i];
        return (hi, lo);
    }

    #endregion

    #region IP Parsing (zero-alloc for IPv4, minimal for IPv6)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryParseIp(ReadOnlySpan<char> s, out uint v4, out ulong v6High, out ulong v6Low, out bool isV4)
    {
        v4 = 0; v6High = 0; v6Low = 0; isV4 = false;
        if (s.IsEmpty || s.Length > 45) return false;
        if (s.Contains(':'))
        {
            var r = TryParseV6(s);
            if (!r.Valid) return false;
            if (r.IsMappedV4)
            {
                v4 = r.V4;
                isV4 = true;
                return true;
            }
            v6High = r.High;
            v6Low = r.Low;
            return true;
        }
        return TryParseV4(s, out v4) && (isV4 = true) == true;
    }

    private static readonly byte[] HexLUT = new byte[128];
    private static readonly uint[] CrcTable = new uint[256];
    static DatabaseReader()
    {
        for (int i = 0; i < 10; i++) HexLUT[48 + i] = (byte)i;
        for (int i = 0; i < 6; i++) { HexLUT[97 + i] = (byte)(10 + i); HexLUT[65 + i] = (byte)(10 + i); }
        for (uint i = 0; i < 256; i++)
        {
            uint entry = i;
            for (int j = 0; j < 8; j++)
                entry = (entry & 1) == 1 ? (entry >> 1) ^ 0xEDB88320 : entry >> 1;
            CrcTable[i] = entry;
        }
    }

    private static bool TryParseV4(ReadOnlySpan<char> s, out uint v4)
    {
        v4 = 0;
        int n = s.Length;
        if (n == 0 || n > 15) return false;
        uint result = 0;
        int val = 0, dots = 0, start = 0;
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
                dots++;
                start = i + 1;
            }
        }
        if (dots != 4) return false;
        v4 = result;
        return true;
    }

    private struct V6Result
    {
        public bool Valid;
        public bool IsMappedV4;
        public uint V4;
        public ulong High;
        public ulong Low;
    }

    private static V6Result TryParseV6(ReadOnlySpan<char> s)
    {
        V6Result r = default;
        if (s.IsEmpty) return r;
        if (s.Contains('%')) return r;

        int dc = s.IndexOf("::");
        if (dc >= 0 && s[(dc + 2)..].IndexOf("::") >= 0) return r;

        ReadOnlySpan<char> left = dc >= 0 ? s[..dc] : s;
        ReadOnlySpan<char> right = dc >= 0 ? s[(dc + 2)..] : ReadOnlySpan<char>.Empty;

        string[] leftStrs = left.IsEmpty ? Array.Empty<string>() : left.ToString().Split(':');
        string[] rightStrs = right.IsEmpty ? Array.Empty<string>() : right.ToString().Split(':');

        List<string> leftGroups = new(8);
        foreach (var seg in leftStrs) { if (seg.Length > 0) leftGroups.Add(seg); }

        List<string> rightGroups = new(8);
        foreach (var seg in rightStrs) { if (seg.Length > 0) rightGroups.Add(seg); }

        bool hasV4 = false;
        uint v4Int = 0;
        if (rightGroups.Count > 0 && rightGroups[^1].Contains('.'))
        {
            if (!TryParseV4(rightGroups[^1], out v4Int)) return r;
            hasV4 = true;
            rightGroups.RemoveAt(rightGroups.Count - 1);
        }

        int totalGroups = leftGroups.Count + rightGroups.Count;
        int v4Slots = hasV4 ? 2 : 0;
        int zeros;
        if (dc >= 0)
        {
            if (totalGroups + v4Slots > 7) return r;
            zeros = 8 - totalGroups - v4Slots;
        }
        else
        {
            if (totalGroups + v4Slots != 8) return r;
            zeros = 0;
        }

        foreach (var g in leftGroups)
        {
            if (g.Length == 0 || g.Length > 4) return r;
            foreach (var cc in g) { if (cc >= 128 || (HexLUT[cc] == 0 && cc != '0')) return r; }
        }
        foreach (var g in rightGroups)
        {
            if (g.Length == 0 || g.Length > 4) return r;
            foreach (var cc in g) { if (cc >= 128 || (HexLUT[cc] == 0 && cc != '0')) return r; }
        }

        byte[] buf = new byte[16];
        int off = 0;
        foreach (var g in leftGroups)
        {
            int v = 0;
            foreach (var c in g) v = (v << 4) | HexLUT[c];
            buf[off++] = (byte)(v >> 8); buf[off++] = (byte)v;
        }
        off += zeros * 2;
        foreach (var g in rightGroups)
        {
            int v = 0;
            foreach (var c in g) v = (v << 4) | HexLUT[c];
            buf[off++] = (byte)(v >> 8); buf[off++] = (byte)v;
        }
        if (hasV4)
        {
            buf[12] = (byte)(v4Int >> 24); buf[13] = (byte)(v4Int >> 16);
            buf[14] = (byte)(v4Int >> 8); buf[15] = (byte)v4Int;
        }

        if (buf[10] == 0xFF && buf[11] == 0xFF &&
            buf[0] == 0 && buf[1] == 0 && buf[2] == 0 && buf[3] == 0 &&
            buf[4] == 0 && buf[5] == 0 && buf[6] == 0 && buf[7] == 0 &&
            buf[8] == 0 && buf[9] == 0)
        {
            r.IsMappedV4 = true;
            r.V4 = (uint)((buf[12] << 24) | (buf[13] << 16) | (buf[14] << 8) | buf[15]);
            r.Valid = true;
            return r;
        }

        for (int i = 0; i < 8; i++) r.High = (r.High << 8) | buf[i];
        for (int i = 8; i < 16; i++) r.Low = (r.Low << 8) | buf[i];
        r.Valid = true;
        return r;
    }

    #endregion

    #region CRC32

    internal static long ComputeCanonicalCrc(Snapshot snap)
    {
        var span = snap._data.Span;
        int len = span.Length;
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < 16; i++) crc = CrcUpdate(crc, span[i]);
        crc = CrcUpdate(crc, 0); crc = CrcUpdate(crc, 0); crc = CrcUpdate(crc, 0); crc = CrcUpdate(crc, 0);
        for (int i = 20; i < len; i++) crc = CrcUpdate(crc, span[i]);
        return (crc ^ 0xFFFFFFFF);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint CrcUpdate(uint crc, byte val) => CrcTable[(crc ^ val) & 0xFF] ^ (crc >> 8);

    #endregion

    #region Public Properties

    public string Version => RequireSnapshot()._version;
    public string DataMonth => RequireSnapshot()._dataMonth;
    public string Edition => RequireSnapshot()._edition;
    public string Scope => RequireSnapshot()._scope;
    public string BuildTime => RequireSnapshot()._buildTimeStr;
    public string Description => RequireSnapshot()._description;
    public string FileHash => RequireSnapshot()._canonicalCrc?.ToString("x8") ?? "N/A";
    public string[] FieldNames => (string[])RequireSnapshot()._fieldNames.Clone();
    public int GroupCount => RequireSnapshot()._actualGroups;
    public int PoolCount => RequireSnapshot()._poolCount;

    public bool HasField(string name) => RequireSnapshot()._normMap.ContainsKey(GeoInfo.NormalizeKey(name));
    public bool VerifyCrc()
    {
        var s = RequireSnapshot();
        return ComputeCanonicalCrc(s) == s._storedCrc;
    }

    #endregion

    #region Lifecycle

    public void Dispose()
    {
        Interlocked.Exchange(ref _activeSnapshot, null);
    }

    #endregion
}
