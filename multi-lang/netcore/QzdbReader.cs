namespace QQZeng.Qzdb;

using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

/// <summary>
/// QZDB high-performance IP geolocation reader (.NET 10).
/// Lock-free snapshot architecture: queries never block each other; reload is atomic via Interlocked.Exchange.
/// </summary>
public sealed class QzdbReader : IDisposable
{
    private const int HeaderSize = 192;
    private const uint Sentinel = 0x80000000;
    private const uint SentinelMask24 = 0x7FFFFF;
    private const uint SentinelMask31 = 0x7FFFFFFF;
    private const int MaxPoolCount = 1 << 24;

    // ------------------------------------------------------------------
    // Edition registry (FORMAT §3.1 / §10.3).
    //
    // The file is self-describing: Header.VersionMask (offset 6) and every
    // GROUP_SCHEMA.groupId carry a one-hot edition bitmask. That bitmask — not
    // the field count — is the authoritative edition signal. EditionByBit is the
    // spec's bit -> name registry; adding a future edition means appending one
    // bit here and one row to EditionFieldNames, with no parser changes anywhere.
    // ------------------------------------------------------------------
    internal static readonly string[] EditionByBit = ["std", "asn", "pro", "max", "ult"]; // bit0..bit4

    /// <summary>Canonical field order per edition (FORMAT appendix 1). Used ONLY when a file
    /// carries no Metadata field_names; never overrides the file's own names.</summary>
    internal static readonly Dictionary<string, string[]> EditionFieldNames = new()
    {
        ["std"] = ["continent", "country_code", "country", "province", "city", "isp"],
        ["asn"] = ["continent", "country_code", "country", "isp", "asn", "as_name", "as_domain", "usage_type"],
        ["pro"] = ["continent", "country_code", "country", "province", "city", "district", "geo_id", "longitude", "latitude", "timezone", "isp"],
        ["max"] = ["continent", "country_code", "country", "province", "city", "district", "geo_id", "longitude", "latitude", "timezone", "isp", "asn", "as_name", "as_domain", "usage_type"],
        ["ult"] = ["continent", "continent_en", "country_code", "country_alpha3", "country", "country_en", "province", "province_en", "city", "city_en", "district", "district_en", "geo_id", "longitude", "latitude", "timezone", "languages", "currency_code", "phone_prefix", "emoji_flag", "isp", "asn", "as_name", "as_domain", "usage_type"],
    };

    // Provenance markers. Identical string values in all 8 SDKs so cross-language
    // verification can compare them directly.
    /// <summary>Edition came from VersionMask/groupId (authoritative).</summary>
    public const string EditionSourceVersionMask = "version_mask";
    /// <summary>Edition came from Metadata primary_version/version_list.</summary>
    public const string EditionSourceMetadata = "metadata";
    /// <summary>Edition was inferred from an unambiguous field count (last resort).</summary>
    public const string EditionSourceInferred = "inferred";
    /// <summary>Edition is genuinely undeterminable.</summary>
    public const string EditionSourceUnknown = "unknown";

    /// <summary>Field names came from the file's own Metadata.</summary>
    public const string FieldNamesSourceMetadata = "metadata";
    /// <summary>Field names came from the canonical table of a known edition.</summary>
    public const string FieldNamesSourceEdition = "edition";
    /// <summary>Field names are field_0..field_N-1 placeholders.</summary>
    public const string FieldNamesSourceSynthetic = "synthetic";

    /// <summary>Reverse index: field count -> edition, only when the count is unambiguous.</summary>
    private static readonly Dictionary<int, string?> EditionByFieldCount = BuildEditionByFieldCount();

    private static Dictionary<int, string?> BuildEditionByFieldCount()
    {
        var map = new Dictionary<int, string?>();
        foreach (var (edition, names) in EditionFieldNames)
            map[names.Length] = map.ContainsKey(names.Length) ? null : edition; // arity clash -> unusable
        return map;
    }

    /// <summary>Resolve a one-hot edition bitmask to its name, or "" if not one-hot.</summary>
    internal static string EditionFromMask(int mask)
    {
        if (mask <= 0 || (mask & (mask - 1)) != 0) return ""; // zero, or more than one bit set
        int bit = BitOperations.TrailingZeroCount(mask);
        return bit < EditionByBit.Length ? EditionByBit[bit] : "";
    }

    /// <summary>Return the sole entry of a comma-separated version_list, or null when it is not exactly one.</summary>
    private static string? SingleVersionToken(string versionList)
    {
        if (string.IsNullOrEmpty(versionList)) return null;
        string? only = null;
        foreach (var part in versionList.Split(','))
        {
            var t = part.Trim();
            if (t.Length == 0) continue;
            if (only != null) return null; // more than one
            only = t;
        }
        return only;
    }

    // Bounded, lock-free per-snapshot cache of resolved GeoInfo keyed by entryId.
    // Power of two; collisions cause a recompute, never a wrong value (see ResolveGeo).
    private const int GeoCacheSize = 1 << 14; // 16384 slots ≈ 196 KB per snapshot

    private Snapshot? _activeSnapshot;
    private int _lifecycleState;
    private readonly object _lifecycleGate = new();

    private QzdbReader(Snapshot snap)
    {
        _activeSnapshot = snap;
    }

    /// <summary>Open a QZDB file with a copied, immutable snapshot.</summary>
    public static QzdbReader Open(string path, ReaderOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var normalized = NormalizeOptions(options);
        return new QzdbReader(LoadPath(path, normalized.GroupIndex, normalized.VerifyCrc));
    }

    private static Snapshot LoadPath(string path, int groupIndex, bool verifyCrc)
    {
        try { return Snapshot.FromPath(path, groupIndex, verifyCrc); }
        catch (QzdbException) { throw; }
        catch (FileNotFoundException ex)
        {
            throw new QzdbException(ErrorCode.FileNotFound, $"QZDB file not found: {path}", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new QzdbException(ErrorCode.FileNotFound, $"QZDB directory not found: {path}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new QzdbException(ErrorCode.FileNotFound, $"QZDB file is not readable: {path}", ex);
        }
    }

    /// <summary>Open a QZDB byte buffer. The input is copied before this method returns.</summary>
    public static QzdbReader OpenBuffer(byte[] buffer, ReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var normalized = NormalizeOptions(options);
        return new QzdbReader(Snapshot.FromBuffer(buffer, normalized.GroupIndex, normalized.VerifyCrc));
    }

    private static ReaderOptions NormalizeOptions(ReaderOptions? options)
    {
        options ??= new ReaderOptions();
        if (options.GroupIndex < 0)
            throw new QzdbException(ErrorCode.InvalidParam, "groupIndex cannot be negative");
        return options;
    }

    /// <summary>Volatile read of the active snapshot (acquire semantics for lock-free reload/dispose).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Snapshot RequireSnapshot()
    {
        var s = Volatile.Read(ref _activeSnapshot);
        if (s == null) throw new ObjectDisposedException(nameof(QzdbReader));
        return s;
    }

    #region Builder

    public sealed class Builder
    {
        internal string? _path;
        internal byte[]? _buffer;
        internal int _groupIndex;
        internal bool _verifyCrc = true;

        public Builder(string path) { _path = path ?? throw new ArgumentNullException(nameof(path)); }
        public Builder(byte[] buffer) { _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer)); }
        public Builder GroupIndex(int idx) { _groupIndex = idx; return this; }
        public Builder VerifyCrc(bool enabled) { _verifyCrc = enabled; return this; }

        public QzdbReader Build()
        {
            if (_path != null)
                return Open(_path, new ReaderOptions { GroupIndex = _groupIndex, VerifyCrc = _verifyCrc });
            if (_buffer != null)
                return OpenBuffer(_buffer, new ReaderOptions { GroupIndex = _groupIndex, VerifyCrc = _verifyCrc });
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
        /// <summary>mmap 路径的稳定指针（byte[] 路径为 null，走 fixed）。
        /// 生命周期由 _dataOwner（MemoryManager→Memory→Span 引用链）保活：
        /// 读者持 Span/Memory 即保持映射可达，释放由 SafeHandle 终结器兜底。</summary>
        internal unsafe byte* _dataPtr;
        /// <summary>mmap 属主（MemoryManager，持有 view/mmf 句柄）；byte[] 路径为 null。</summary>
        internal MemoryManager<byte>? _dataOwner;

        internal int _flags;
        internal bool _hasV4, _hasV6, _v4Node24, _v6Node24;
        internal int _v6JumpBits, _poolCount, _poolIdxSize;
        internal int _rowCount, _v4NodeCount, _v6NodeCount, _ipRowSize, _buildDate;

        internal long _offRowSchema, _offGroupSchema;
        internal long _offV4Jump, _offV4Nodes, _offV6Jump, _offV6Nodes;
        internal long _offIPRow, _offGeoEntries, _offPools, _offMeta;

        internal int _rowGeoWidth, _rowAsnWidth, _rowUsageWidth;

        internal int _actualGroups;
        internal int[] _groupFieldCounts = null!;
        internal long[] _groupEntryCounts = null!;
        internal int[] _groupDimMasks = null!;
        internal long[] _groupEntryOffsets = null!;
        internal int[] _groupStrides = null!;
        internal int[][] _groupFieldWidths = null!;
        internal int[][] _groupFieldOffsets = null!;
        internal bool[][] _groupFieldNative = null!;
        internal int[][] _groupFieldNativeType = null!;
        /// <summary>Per-group one-hot edition bitmask (GROUP_SCHEMA.groupId; 0 = not declared).</summary>
        internal int[] _groupIds = null!;
        /// <summary>Field names resolved per group — dimensionMask repair needs names of other groups too.</summary>
        internal string[][] _groupFieldNames = null!;

        internal string[][][] _pools = null!;
        internal string[] _fieldNames = null!;
        internal Dictionary<string, int> _normMap = null!;
        internal bool[] _numericFlags = null!;

        internal CacheEntry?[]? _cache;

        internal string _version = "", _description = "", _dataMonth = "", _buildTimeStr = "", _edition = "", _scope = "";
        /// <summary>Header offset 6: file-level one-hot edition bitmask.</summary>
        internal int _versionMask;
        /// <summary>Which rule produced <see cref="_edition"/>.</summary>
        internal string _editionSource = EditionSourceUnknown;
        /// <summary>Whether <see cref="_fieldNames"/> was read from the file or filled in by the SDK.</summary>
        internal string _fieldNamesSource = FieldNamesSourceSynthetic;
        internal long _storedCrc;
        internal long? _canonicalCrc;

        internal sealed class CacheEntry
        {
            internal readonly uint Key;
            internal readonly GeoInfo Value;

            internal CacheEntry(uint key, GeoInfo value)
            {
                Key = key;
                Value = value;
            }
        }

        public static unsafe Snapshot FromPath(string path, int groupIndex, bool verifyCrc)
        {
            // mmap 加载：122MB 库不再整块进 LOH（GC.AllocateUninitializedArray + ReadExactly
            // 的整文件读取与拷贝一并消除），且多进程可共享物理页。
            // FromBuffer 保留 byte[] 拷贝语义（契约要求）。
            var fs = File.OpenRead(path);
            if (fs.Length > int.MaxValue)
            {
                fs.Dispose();
                throw new QzdbException(ErrorCode.Corrupted, "QZDB file is too large");
            }
            var len = (int)fs.Length;
            var mmf = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.Read,
                HandleInheritability.None, leaveOpen: false);
            var view = mmf.CreateViewAccessor(0, len, MemoryMappedFileAccess.Read);
            byte* ptr = null;
            try
            {
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                var manager = new MmapManager(mmf, view, ptr, len);
                return new Snapshot(manager.Memory, manager.Pointer, manager, groupIndex, verifyCrc);
            }
            catch
            {
                if (ptr != null) { try { view.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { /* best effort */ } }
                view.Dispose();
                mmf.Dispose();
                throw;
            }
        }

        /// <summary>mmap 视图的 MemoryManager：内存由 OS 映射保持稳定（天然 pinned），
        /// 句柄释放走 SafeHandle 终结器（读者不再持 Span 后由 GC 兜底，与 Go 侧
        /// finalizer 模型同语义）。</summary>
        private sealed unsafe class MmapManager : MemoryManager<byte>
        {
            private readonly MemoryMappedFile _mmf;
            private readonly MemoryMappedViewAccessor _view;
            private readonly byte* _ptr;
            private readonly int _length;
            private bool _disposed;

            internal MmapManager(MemoryMappedFile mmf, MemoryMappedViewAccessor view, byte* ptr, int length)
            {
                _mmf = mmf; _view = view; _ptr = ptr; _length = length;
            }

            internal Memory<byte> Memory => CreateMemory(_length);
            internal byte* Pointer => _ptr;

            public override Span<byte> GetSpan() => new(_ptr, _length);
            public override MemoryHandle Pin(int elementIndex = 0) => new(_ptr + elementIndex, default, this);
            public override void Unpin() { }

            protected override void Dispose(bool disposing)
            {
                if (_disposed) return;
                _disposed = true;
                try { _view.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { /* best effort */ }
                _view.Dispose();
                _mmf.Dispose();
            }
        }

        public static Snapshot FromBuffer(byte[] buffer, int groupIndex, bool verifyCrc)
        {
            var copy = GC.AllocateUninitializedArray<byte>(buffer.Length);
            Buffer.BlockCopy(buffer, 0, copy, 0, buffer.Length);
            return new Snapshot(copy, groupIndex, verifyCrc, true);
        }

        internal unsafe Snapshot(byte[] buffer, int groupIndex, bool verifyCrc, bool _)
            : this(buffer, null, null, groupIndex, verifyCrc)
        {
        }

        internal unsafe Snapshot(ReadOnlyMemory<byte> data, byte* dataPtr, MemoryManager<byte>? owner,
            int groupIndex, bool verifyCrc)
        {
            _data = data;
            _dataPtr = dataPtr;
            _dataOwner = owner;
            _dataLen = data.Length;
            _groupIndex = groupIndex;

            ValidateHeader();
            ValidateSectionBounds();
            ParseRowSchema();
            ParseGroups();
            ParseMetadata();
            ParsePools();

            _cache = new CacheEntry?[GeoCacheSize];

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

            // VersionMask (offset 6): file-level one-hot edition bitmask, the
            // authoritative edition signal (FORMAT §3.1).
            _versionMask = BinaryPrimitives.ReadUInt16LittleEndian(span[6..]);

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
            if (_rowCount < 0 || _v4NodeCount < 0 || _v6NodeCount < 0)
                throw new QzdbException(ErrorCode.Corrupted, "Negative row or trie node count");

            _ipRowSize = BinaryPrimitives.ReadInt32LittleEndian(span[160..]);
            if (_ipRowSize < 1 || _ipRowSize > 64)
                throw new QzdbException(ErrorCode.InvalidParam, $"ipRowSize out of range: {_ipRowSize}");
        }

        internal void ValidateSectionBounds()
        {
            long dlen = _dataLen;
            int v4NodeSize = _v4Node24 ? 6 : 8;
            int v6NodeSize = _v6Node24 ? 6 : 8;
            if (_hasV4)
            {
                CheckSection(_offV4Jump, 65536L * 4, dlen, "v4_jump", required: true);
                if (_v4NodeCount > 0) CheckSection(_offV4Nodes, (long)_v4NodeCount * v4NodeSize, dlen, "v4_nodes", required: true);
            }
            if (_hasV6)
            {
                CheckSection(_offV6Jump, (1L << _v6JumpBits) * 4, dlen, "v6_jump", required: true);
                if (_v6NodeCount > 0) CheckSection(_offV6Nodes, (long)_v6NodeCount * v6NodeSize, dlen, "v6_nodes", required: true);
            }
            CheckSection(_offIPRow, (long)_rowCount * _ipRowSize, dlen, "ip_row", required: true);
            CheckSection(_offGeoEntries, 1, dlen, "geo_entries", required: true);
            if (_poolCount > 0) CheckSection(_offPools, 1, dlen, "pools", required: true);
            if (_offRowSchema > 0) CheckSection(_offRowSchema, 4, dlen, "row_schema", required: true);
            if (_offGroupSchema > 0) CheckSection(_offGroupSchema, 2, dlen, "group_schema", required: true);
            if (_offMeta > 0) CheckSection(_offMeta, 4, dlen, "meta", required: true);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CheckSection(long off, long size, long dlen, string name, bool required = false)
        {
            if ((!required && off == 0) || (off >= HeaderSize && size >= 0 && off <= dlen && size <= dlen - off))
                return;
            if (required || off != 0)
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
            if (fCount < 1 || fCount > 8 || sp + 4 + fCount * 4 > _dataLen || stride != _ipRowSize)
                throw new QzdbException(ErrorCode.Corrupted, "Invalid row schema");

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
            if (!ok || total != _ipRowSize || g2 <= 0)
                throw new QzdbException(ErrorCode.Corrupted, "Invalid row schema widths");
            _rowGeoWidth = g2;
            _rowAsnWidth = a2;
            _rowUsageWidth = u2;
        }

        internal void ParseGroups()
        {
            var span = _data.Span;
            int gCount = BinaryPrimitives.ReadInt32LittleEndian(span[164..]);
            if (gCount < 1 || gCount > 4)
                throw new QzdbException(ErrorCode.Corrupted, $"Invalid group count: {gCount}");

            long[] headerGeoOffsets = new long[4];
            for (int i = 0; i < 4; i++)
            {
                headerGeoOffsets[i] = ReadU48(span, 168 + i * 6);
            }

            int gmOff = checked((int)_offGeoEntries);
            if (gmOff < HeaderSize || gmOff >= _dataLen)
                throw new QzdbException(ErrorCode.Corrupted, "Group table is out of bounds");
            int tableGroups = span[gmOff];
            gmOff++;

            if (tableGroups < 1 || tableGroups > 4 || tableGroups != gCount)
                throw new QzdbException(ErrorCode.Corrupted, $"Invalid group table count: {tableGroups}");
            int groups = tableGroups;
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
            _groupIds = new int[groups];

            for (int gi = 0; gi < groups; gi++)
            {
                if (gmOff + 7 > _dataLen)
                    throw new QzdbException(ErrorCode.Corrupted, "Group table is truncated");
                _groupFieldCounts[gi] = span[gmOff];
                if (_groupFieldCounts[gi] < 1 || _groupFieldCounts[gi] > 256)
                    throw new QzdbException(ErrorCode.Corrupted, "Invalid group field count");
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
                if (gsGroupCount != groups)
                    throw new QzdbException(ErrorCode.Corrupted, "Group schema count does not match group table");
                sp += 2;
                for (int gi = 0; gi < groups; gi++)
                {
                    if (sp + 14 > _dataLen)
                        throw new QzdbException(ErrorCode.Corrupted, "Group schema is truncated");
                    // groupId is this group's one-hot edition bitmask (FORMAT §3.1) — the
                    // authoritative edition signal for the group, consumed by ParseMetadata().
                    _groupIds[gi] = BinaryPrimitives.ReadUInt16LittleEndian(span[sp..]);
                    sp += 2;
                    int fldCount = BinaryPrimitives.ReadUInt16LittleEndian(span[sp..]);
                    sp += 2;
                    sp += 4;
                    int stride = BinaryPrimitives.ReadInt32LittleEndian(span[sp..]);
                    sp += 4;
                    sp += 4;
                    if (stride <= 0 || stride > 4096)
                        throw new QzdbException(ErrorCode.Corrupted, "Invalid group schema stride");

                    if (fldCount != _groupFieldCounts[gi] || fldCount > 256 || sp + (long)fldCount * 12 > _dataLen)
                        throw new QzdbException(ErrorCode.Corrupted, "Invalid group schema field count");

                    _groupStrides[gi] = stride;
                    var widths = new int[fldCount];
                    var offsets = new int[fldCount];
                    var natives = new bool[fldCount];
                    var natTypes = new int[fldCount];
                    for (int fi = 0; fi < fldCount; fi++)
                    {
                        // fieldId is just the slot ordinal (0..N-1); no cross-edition meaning.
                        sp += 2;
                        widths[fi] = span[sp];
                        if (widths[fi] is < 1 or > 8)
                            throw new QzdbException(ErrorCode.Corrupted, "Invalid group field width");
                        sp++;
                        int fieldFlags = span[sp];
                        sp++;
                        bool native = (fieldFlags & 0x01) != 0;
                        int nativeType = (fieldFlags >> 1) & 0x03;
                        if ((!native && widths[fi] > 4) ||
                            (native && nativeType == 1 && widths[fi] is not (4 or 8)) ||
                            (native && nativeType != 1 && widths[fi] > 4))
                            throw new QzdbException(ErrorCode.Corrupted, "Invalid native field width");
                        natives[fi] = native;
                        natTypes[fi] = nativeType;
                        offsets[fi] = BinaryPrimitives.ReadInt32LittleEndian(span[sp..]);
                        if (offsets[fi] < 0 || offsets[fi] > stride - widths[fi])
                            throw new QzdbException(ErrorCode.Corrupted, "Group field exceeds stride");
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
                if (_groupStrides[g] <= 0 || _groupStrides[g] > 4096)
                    throw new QzdbException(ErrorCode.Corrupted, "Invalid group stride");
                CheckSection(_groupEntryOffsets[g], _groupEntryCounts[g] * (long)_groupStrides[g], _dataLen,
                    $"group_{g}_entries", required: true);
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
                    if (cursor + 4L + length > _dataLen)
                        throw new QzdbException(ErrorCode.Corrupted, "Metadata record is truncated");
                    var val = Encoding.UTF8.GetString(span.Slice(cursor + 4, length));
                    switch (type)
                    {
                        case 1: _version = val; break;
                        case 2: metaFields = val.Split('|'); break;
                        case 3: _description = val; break;
                        case 4: _edition = val; break; // legacy primary edition/version
                        case 5: _dataMonth = val; break; // v2.4 explicit data month
                        case 6: _scope = val; break; // v2.4 explicit scope
                    }
                    cursor += 4 + length;
                }
            }

            // --- Resolve edition + field names for every group ------------------
            //
            // Both answers come only from what the file declares about itself, with an
            // identical priority order in all 8 SDKs (FORMAT §10.3):
            //
            //   edition      1. GROUP_SCHEMA.groupId / Header.VersionMask one-hot bit
            //                2. Metadata primary_version, or a single-entry version_list
            //                3. unambiguous field-count match (last resort)
            //                4. "" (unknown) — we do not invent an answer
            //   fieldNames   1. Metadata field_names, when its arity matches the group
            //                2. canonical table for a *known* edition of matching arity
            //                3. field_0..field_N-1 placeholders
            //
            // EditionSource / FieldNamesSource report which rule fired, so callers can
            // tell a name read off disk from one filled in by the SDK.
            string metaPrimary = _edition; // Metadata type=4, captured above
            _groupFieldNames = new string[_actualGroups][];
            var groupEditions = new string[_actualGroups];
            var groupEditionSources = new string[_actualGroups];
            var groupNameSources = new string[_actualGroups];

            for (int g = 0; g < _actualGroups; g++)
            {
                int nFields = _groupFieldCounts[g];

                // edition: this group's own bitmask first, then the file-level mask.
                int mask = _groupIds[g] != 0 ? _groupIds[g] : _versionMask;
                string edition = EditionFromMask(mask);
                string source = EditionSourceVersionMask;
                if (edition.Length == 0)
                {
                    edition = metaPrimary.Trim();
                    if (edition.Length == 0)
                        edition = SingleVersionToken(_version) ?? "";
                    if (edition.Length != 0) source = EditionSourceMetadata;
                }
                if (edition.Length == 0)
                {
                    edition = EditionByFieldCount.TryGetValue(nFields, out var byCount) && byCount != null
                        ? byCount
                        : "";
                    source = edition.Length == 0 ? EditionSourceUnknown : EditionSourceInferred;
                }

                // field names
                string[] names;
                string namesSource;
                if (metaFields != null && metaFields.Length == nFields)
                {
                    names = (string[])metaFields.Clone();
                    namesSource = FieldNamesSourceMetadata;
                }
                else if (EditionFieldNames.TryGetValue(edition, out var canonical) && canonical.Length == nFields)
                {
                    names = (string[])canonical.Clone();
                    namesSource = FieldNamesSourceEdition;
                }
                else
                {
                    names = new string[nFields];
                    for (int i = 0; i < nFields; i++) names[i] = $"field_{i}";
                    namesSource = FieldNamesSourceSynthetic;
                }

                _groupFieldNames[g] = names;
                groupEditions[g] = edition;
                groupEditionSources[g] = source;
                groupNameSources[g] = namesSource;
            }

            int gi2 = _groupIndex >= 0 && _groupIndex < _actualGroups ? _groupIndex : 0;
            _fieldNames = _groupFieldNames[gi2];
            _edition = groupEditions[gi2];
            _editionSource = groupEditionSources[gi2];
            _fieldNamesSource = groupNameSources[gi2];

            _normMap = GeoInfo.BuildNormalizedMap(_fieldNames);
            _numericFlags = new bool[_fieldNames.Length];
            for (int i = 0; i < _fieldNames.Length; i++)
                _numericFlags[i] = GeoInfo.IsNumericFieldName(_fieldNames[i]);

            // Repair dimensionMask (§5.4 / §6.2).
            //
            // A valid current-format file always stores a non-zero dimensionMask, so this
            // normally does nothing. When a group's mask is 0 (malformed/legacy), derive it
            // from that group's *resolved field names* — never from fieldId (just a slot
            // ordinal) and never from the group index (the real asn file keeps its asn group
            // at index 0 with a stored mask of 0x02, so an index rule would be wrong).
            string asnKey = GeoInfo.NormalizeKey("asn");
            for (int g = 0; g < _actualGroups; g++)
            {
                if (_groupDimMasks[g] != 0) continue;
                bool hasAsn = false;
                foreach (var fn in _groupFieldNames[g])
                {
                    if (GeoInfo.NormalizeKey(fn) == asnKey) { hasAsn = true; break; }
                }
                _groupDimMasks[g] = hasAsn ? 0x02 : 0x01;
            }

            if (_buildDate > 0)
            {
                int y = _buildDate / 10000;
                int m = (_buildDate / 100) % 100;
                int dd = _buildDate % 100;
                if (_dataMonth.Length == 0) _dataMonth = $"{y:D4}-{m:D2}";
                _buildTimeStr = $"{y:D4}-{m:D2}-{dd:D2}";
            }
        }

        internal void ParsePools()
        {
            var span = _data.Span;
            _pools = new string[_actualGroups][][];
            if (_offPools <= 0) return;

            long poolCursor = _offPools;
            long poolEnd = _offMeta > 0 ? _offMeta : _dataLen;
            if (poolEnd < poolCursor)
                throw new QzdbException(ErrorCode.Corrupted, "Pool section precedes its start");

            for (int g = 0; g < _actualGroups; g++)
            {
                int fieldCount = _groupFieldCounts[g];
                var groupPoolList = new string[fieldCount][];
                bool[] natives = _groupFieldNative[g];

                for (int f = 0; f < fieldCount; f++)
                {
                    if (natives.Length > f && natives[f]) { groupPoolList[f] = []; continue; }
                    if (poolCursor < HeaderSize || poolCursor > poolEnd || poolEnd - poolCursor < 4)
                        throw new QzdbException(ErrorCode.Corrupted, "Pool header is out of bounds");

                    int count = BinaryPrimitives.ReadInt32LittleEndian(span[(int)poolCursor..]);
                    poolCursor += 4;
                    if (_offRowSchema > 0)
                    {
                        if (poolEnd - poolCursor < 4) throw new QzdbException(ErrorCode.Corrupted, "Pool header is truncated");
                        poolCursor += 4;
                    }
                    if (count < 0 || count > MaxPoolCount)
                        throw new QzdbException(ErrorCode.Corrupted, "Invalid pool count");
                    if (count == 0) { groupPoolList[f] = []; continue; }

                    int cnt = count;
                    long indexBytes = ((long)count + 1) * 4;
                    if (indexBytes > poolEnd - poolCursor)
                        throw new QzdbException(ErrorCode.Corrupted, "Pool index table is out of bounds");
                    long stringDataStart = poolCursor + indexBytes;

                    // 偏移表是累积结构：offsets[i+1] >= offsets[i]。单调性必须跨条目强制，
                    // 只做单条 [strOff, nextOff] 合法性判断是不够的 —— 伪造表可让每一项都取
                    // [0, sectionLen]，逐条都"合法"，但 count 段 × section 长度会放大成 GB 级
                    // UTF8.GetString 分配（同类构造实测达 7.2 GB → OOM）。
                    // 加上 strOff >= prevEnd 后各段互不重叠，总解码量必 <= section 长度。
                    var strings = new string[cnt];
                    int prevEnd = 0;
                    for (int i = 0; i < cnt; i++)
                    {
                        int strOff = BinaryPrimitives.ReadInt32LittleEndian(span[(int)(poolCursor + i * 4)..]);
                        int nextOff = BinaryPrimitives.ReadInt32LittleEndian(span[(int)(poolCursor + (i + 1) * 4)..]);
                        if (strOff < prevEnd || nextOff < strOff || stringDataStart + nextOff > poolEnd)
                            throw new QzdbException(ErrorCode.Corrupted, "Pool string offset is out of bounds");
                        prevEnd = nextOff;
                        int len = nextOff - strOff;
                        strings[i] = len == 0 ? "" : Encoding.UTF8.GetString(span.Slice((int)(stringDataStart + strOff), len));
                    }
                    groupPoolList[f] = strings;
                    int finalOffset = BinaryPrimitives.ReadInt32LittleEndian(span[(int)(poolCursor + count * 4)..]);
                    if (finalOffset < 0 || stringDataStart + finalOffset > poolEnd)
                        throw new QzdbException(ErrorCode.Corrupted, "Pool terminator offset is out of bounds");
                    poolCursor = stringDataStart + finalOffset;
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
        var snap = RequireSnapshot();
        if (string.IsNullOrEmpty(ipStr) || !TryParseIp(ipStr, out var v4, out var v6High, out var v6Low, out var isV4))
            throw new QzdbException(ErrorCode.InvalidIp, $"Invalid IP address: '{ipStr}'");

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

    public GeoInfo? Find(System.Net.IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return FindBytes(address.GetAddressBytes());
    }

    public bool TryFind(string ipStr, out GeoInfo? info)
    {
        try
        {
            info = Find(ipStr);
            return info != null;
        }
        catch (QzdbException e) when (e.ErrorCode == ErrorCode.InvalidIp)
        {
            info = null;
            return false;
        }
    }

    public GeoInfo? FindBytes(byte[]? ipBytes)
    {
        var snap = RequireSnapshot();
        if (ipBytes == null || (ipBytes.Length != 4 && ipBytes.Length != 16))
            throw new QzdbException(ErrorCode.InvalidIp, "IP bytes must contain exactly 4 or 16 bytes");

        uint rowId = 0;
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

        return rowId > 0 ? ResolveRowId(snap, rowId) : null;
    }

    public string FindStr(string ipStr)
    {
        try
        {
            var info = Find(ipStr);
            return info == null ? "" : info.ToPipe();
        }
        catch (QzdbException)
        {
            // 非法 IP：宽松语义，对齐 findStr 规范（§3 未命中/非法统一返回 ""）
            return "";
        }
    }

    public uint LookupRowId(string ipStr)
    {
        if (string.IsNullOrEmpty(ipStr)) return 0;
        if (!TryParseIp(ipStr, out var v4, out var v6High, out var v6Low, out var isV4)) return 0;

        var snap = RequireSnapshot();
        if (snap == null) return 0;

        return isV4 ? TrieWalkV4(snap, v4) : TrieWalkV6(snap, v6High, v6Low);
    }

    public GeoInfo? FindUint(uint ipInt)
    {
        var snap = RequireSnapshot();
        uint rowId = TrieWalkV4(snap, ipInt);
        return rowId > 0 ? ResolveRowId(snap, rowId) : null;
    }

    public uint LookupRowIdUint(uint ipInt)
    {
        var snap = RequireSnapshot();
        return snap == null ? 0u : TrieWalkV4(snap, ipInt);
    }

    public uint LookupRowIdBytes(byte[]? ipBytes)
    {
        if (ipBytes == null) return 0;
        var snap = RequireSnapshot();
        if (snap == null) return 0;

        if (ipBytes.Length == 16)
        {
            if (IsV4Mapped(ipBytes)) return TrieWalkV4(snap, V4FromMapped(ipBytes));
            var (hi, lo) = V6FromBytes(ipBytes);
            return TrieWalkV6(snap, hi, lo);
        }
        if (ipBytes.Length == 4)
        {
            uint v4 = (uint)((ipBytes[0] << 24) | (ipBytes[1] << 16) | (ipBytes[2] << 8) | ipBytes[3]);
            return TrieWalkV4(snap, v4);
        }
        return 0;
    }

    public string LookupCidr(string ipStr)
    {
        if (string.IsNullOrEmpty(ipStr)) return "";
        if (!TryParseIp(ipStr, out var v4, out var v6High, out var v6Low, out var isV4)) return "";

        var snap = Volatile.Read(ref _activeSnapshot);
        if (snap == null) return "";

        if (isV4)
        {
            int n = TrieWalkV4PrefixLen(snap, v4);
            return n < 0 ? "" : FormatV4Cidr(v4, n);
        }
        int n6 = TrieWalkV6PrefixLen(snap, v6High, v6Low);
        return n6 < 0 ? "" : FormatV6Cidr(v6High, v6Low, n6);
    }

    public string LookupCidrUint(uint ipInt)
    {
        var snap = Volatile.Read(ref _activeSnapshot);
        if (snap == null) return "";
        int n = TrieWalkV4PrefixLen(snap, ipInt);
        return n < 0 ? "" : FormatV4Cidr(ipInt, n);
    }

    public string LookupCidrBytes(byte[]? ipBytes)
    {
        if (ipBytes == null) return "";
        var snap = Volatile.Read(ref _activeSnapshot);
        if (snap == null) return "";

        if (ipBytes.Length == 16)
        {
            if (IsV4Mapped(ipBytes))
            {
                uint v4 = V4FromMapped(ipBytes);
                int n4 = TrieWalkV4PrefixLen(snap, v4);
                return n4 < 0 ? "" : FormatV4Cidr(v4, n4);
            }
            var (hi, lo) = V6FromBytes(ipBytes);
            int n6 = TrieWalkV6PrefixLen(snap, hi, lo);
            return n6 < 0 ? "" : FormatV6Cidr(hi, lo, n6);
        }
        if (ipBytes.Length == 4)
        {
            uint v4 = (uint)((ipBytes[0] << 24) | (ipBytes[1] << 16) | (ipBytes[2] << 8) | ipBytes[3]);
            int n4 = TrieWalkV4PrefixLen(snap, v4);
            return n4 < 0 ? "" : FormatV4Cidr(v4, n4);
        }
        return "";
    }

    public (uint Geo, uint Asn, uint Usage) LookupIds(uint rowId)
    {
        var snap = RequireSnapshot();
        if (rowId >= (uint)snap._rowCount) return default;

        var span = snap._data.Span;
        long rOff = snap._offIPRow + (long)rowId * snap._ipRowSize;

        uint geoId = ReadUintWidth(span, (int)rOff, snap._rowGeoWidth);
        uint asnId = snap._rowAsnWidth > 0 ? ReadUintWidth(span, (int)(rOff + snap._rowGeoWidth), snap._rowAsnWidth) : 0;
        uint usageId = snap._rowUsageWidth > 0 ? ReadUintWidth(span, (int)(rOff + snap._rowGeoWidth + snap._rowAsnWidth), snap._rowUsageWidth) : 0;

        return (geoId, asnId, usageId);
    }

    public GeoInfo? FindFields(string ipStr, string[]? fields)
    {
        var snap = RequireSnapshot();
        if (string.IsNullOrEmpty(ipStr) || !TryParseIp(ipStr, out var v4, out var v6High, out var v6Low, out var isV4))
            throw new QzdbException(ErrorCode.InvalidIp, $"Invalid IP address: '{ipStr}'");

        uint rowId = isV4 ? TrieWalkV4(snap, v4) : TrieWalkV6(snap, v6High, v6Low);
        if (rowId == 0) return null;

        var full = ResolveRowId(snap, rowId); // rides decode cache
        if (full == null) return null;
        if (fields == null || fields.Length == 0) return full;

        // 字段投影：按请求顺序从全字段结果切片（对齐 Java golden）。
        // 未知字段在该位置补 ""（不跳过）、保留重复字段、全部未知仍返回 GeoInfo。
        var normMap = snap._normMap;
        var values = new string[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            values[i] = normMap.TryGetValue(GeoInfo.NormalizeKey(fields[i]), out var fi)
                ? full.Get(fields[i])
                : "";
        }
        return new GeoInfo(fields, values, GeoInfo.BuildNormalizedMap(fields), null);
    }

    public BatchResult[] FindBatch(string[] ipStrs)
    {
        ArgumentNullException.ThrowIfNull(ipStrs);
        var results = new BatchResult[ipStrs.Length];
        for (int i = 0; i < ipStrs.Length; i++) results[i] = FindResult(ipStrs[i]);
        return results;
    }

    public BatchResult[] FindBatch(IEnumerable<string> ipStrs) => FindBatch(ipStrs?.ToArray() ?? throw new ArgumentNullException(nameof(ipStrs)));

    public BatchResult[] FindBatchFields(string[] ipStrs, string[]? fields)
    {
        ArgumentNullException.ThrowIfNull(ipStrs);
        var results = new BatchResult[ipStrs.Length];
        for (int i = 0; i < ipStrs.Length; i++)
        {
            try
            {
                var info = FindFields(ipStrs[i], fields);
                results[i] = new BatchResult(info, null, ipStrs[i]);
            }
            catch (QzdbException e) { results[i] = new BatchResult(null, e, ipStrs[i]); }
        }
        return results;
    }

    public BatchResult[] FindBatchFields(IEnumerable<string> ipStrs, IEnumerable<string>? fields) =>
        FindBatchFields(ipStrs?.ToArray() ?? throw new ArgumentNullException(nameof(ipStrs)), fields?.ToArray());

    public IEnumerable<BatchResult> FindStream(IEnumerable<string> ipStrs)
    {
        if (ipStrs == null) yield break;
        foreach (var ip in ipStrs) yield return FindResult(ip);
    }

    private BatchResult FindResult(string ip)
    {
        try
        {
            var info = Find(ip);
            return new BatchResult(info, null, ip);
        }
        catch (QzdbException e) { return new BatchResult(null, e, ip); }
    }

    #endregion

    #region Unsafe Trie Walk (zero allocation, bypass bounds check)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe uint TrieWalkV4(Snapshot snap, uint ipInt)
    {
        if (!snap._hasV4 || snap._offV4Jump <= 0) return 0;
        if (snap._dataPtr != null) return TrieWalkV4Core(snap, snap._dataPtr, ipInt);
        fixed (byte* bp = snap._data.Span) return TrieWalkV4Core(snap, bp, ipInt);
    }

    private static unsafe uint TrieWalkV4Core(Snapshot snap, byte* bp, uint ipInt)
    {
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
                byte* nodesEnd = nodes + (long)snap._v4NodeCount * 6;
                for (int step = 0; step < 16; step++)
                {
                    byte* node = nodes + idx * 6;
                    if (node >= nodesEnd) return 0;
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
                uint* nodesEnd = (uint*)(nodes + (long)snap._v4NodeCount * 8);
                for (int step = 0; step < 16; step++)
                {
                    uint* node = (uint*)(nodes + idx * 8);
                    if (node >= nodesEnd) return 0;
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
        if (snap._dataPtr != null) return TrieWalkV6Core(snap, snap._dataPtr, ipHigh, ipLow);
        fixed (byte* bp = snap._data.Span) return TrieWalkV6Core(snap, bp, ipHigh, ipLow);
    }

    private static unsafe uint TrieWalkV6Core(Snapshot snap, byte* bp, ulong ipHigh, ulong ipLow)
    {
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
                byte* nodesEnd = nodes + (long)snap._v6NodeCount * 6;
                for (int depth = jumpBits; depth < 128; depth++)
                {
                    uint bit2 = depth <= 63 ? (uint)((ipHigh >> (63 - depth)) & 1) : (uint)((ipLow >> (127 - depth)) & 1);
                    byte* node = nodes + idx * 6;
                    if (node >= nodesEnd) return 0;
                    int off = bit2 == 0 ? 0 : 3;
                    uint child = (uint)(node[off] | (node[off + 1] << 8) | (node[off + 2] << 16));
                    if ((child & 0x800000) != 0) return child & SentinelMask24;
                    if (child == 0) return 0;
                    idx = child;
                }
            }
            else
            {
                uint* nodesEnd = (uint*)(nodes + (long)snap._v6NodeCount * 8);
                for (int depth = jumpBits; depth < 128; depth++)
                {
                    uint bit = depth <= 63 ? (uint)((ipHigh >> (63 - depth)) & 1) : (uint)((ipLow >> (127 - depth)) & 1);
                    uint* node = (uint*)(nodes + idx * 8);
                    if (node >= nodesEnd) return 0;
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

    #region CIDR (prefix-length reconstruction + RFC 5952 formatting)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe int TrieWalkV4PrefixLen(Snapshot snap, uint ipInt)
    {
        if (!snap._hasV4 || snap._offV4Jump <= 0) return -1;
        if (snap._dataPtr != null) return TrieWalkV4PrefixLenCore(snap, snap._dataPtr, ipInt);
        fixed (byte* bp = snap._data.Span) return TrieWalkV4PrefixLenCore(snap, bp, ipInt);
    }

    private static unsafe int TrieWalkV4PrefixLenCore(Snapshot snap, byte* bp, uint ipInt)
    {
        uint* jump = (uint*)(bp + snap._offV4Jump);
        uint hi16 = (ipInt >> 16) & 0xFFFF;
        uint ptr = jump[hi16];
        if (ptr == 0) return -1;
        if ((ptr & Sentinel) != 0) return WalkV4Depth(snap, bp, ipInt, 0, 0, 16);
        return WalkV4Depth(snap, bp, ipInt, ptr & SentinelMask31, 16, 32);
    }

    private static unsafe int WalkV4Depth(Snapshot snap, byte* bp, uint ipInt, uint startIdx, int startDepth, int maxDepth)
    {
        if (startDepth >= maxDepth) return -1;
        uint idx = startIdx;
        byte* nodes = bp + snap._offV4Nodes;

        if (snap._v4Node24)
        {
            byte* nodesEnd = nodes + (long)snap._v4NodeCount * 6;
            for (int depth = startDepth; depth < maxDepth; depth++)
            {
                if (idx >= snap._v4NodeCount) return -1;
                uint bit = (ipInt >> (31 - depth)) & 1;
                byte* node = nodes + idx * 6;
                if (node >= nodesEnd) return -1;
                int off = bit == 0 ? 0 : 3;
                uint child = (uint)(node[off] | (node[off + 1] << 8) | (node[off + 2] << 16));
                if ((child & 0x800000) != 0) return depth + 1;
                if (child == 0) return -1;
                idx = child;
            }
        }
        else
        {
            uint* nodesEnd = (uint*)(nodes + (long)snap._v4NodeCount * 8);
            for (int depth = startDepth; depth < maxDepth; depth++)
            {
                if (idx >= snap._v4NodeCount) return -1;
                uint bit = (ipInt >> (31 - depth)) & 1;
                uint* node = (uint*)(nodes + idx * 8);
                if (node >= nodesEnd) return -1;
                uint child = node[bit];
                if ((child & Sentinel) != 0) return depth + 1;
                if (child == 0) return -1;
                idx = child;
            }
        }
        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe int TrieWalkV6PrefixLen(Snapshot snap, ulong ipHigh, ulong ipLow)
    {
        if (!snap._hasV6 || snap._offV6Jump <= 0) return -1;
        if (snap._dataPtr != null) return TrieWalkV6PrefixLenCore(snap, snap._dataPtr, ipHigh, ipLow);
        fixed (byte* bp = snap._data.Span) return TrieWalkV6PrefixLenCore(snap, bp, ipHigh, ipLow);
    }

    private static unsafe int TrieWalkV6PrefixLenCore(Snapshot snap, byte* bp, ulong ipHigh, ulong ipLow)
    {
        int jumpBits = snap._v6JumpBits;
        uint idxJump = (uint)(ipHigh >> (64 - jumpBits));
        uint* jump = (uint*)(bp + snap._offV6Jump);
        uint ptr = jump[idxJump];
        if (ptr == 0) return -1;
        if ((ptr & Sentinel) != 0) return WalkV6Depth(snap, bp, ipHigh, ipLow, 0, 0, jumpBits);
        return WalkV6Depth(snap, bp, ipHigh, ipLow, ptr & SentinelMask31, jumpBits, 128);
    }

    private static unsafe int WalkV6Depth(Snapshot snap, byte* bp, ulong ipHigh, ulong ipLow, uint startIdx, int startDepth, int maxDepth)
    {
        if (startDepth >= maxDepth) return -1;
        uint idx = startIdx;
        byte* nodes = bp + snap._offV6Nodes;

        if (snap._v6Node24)
        {
            byte* nodesEnd = nodes + (long)snap._v6NodeCount * 6;
            for (int depth = startDepth; depth < maxDepth; depth++)
            {
                if (idx >= snap._v6NodeCount) return -1;
                uint bit = depth <= 63 ? (uint)((ipHigh >> (63 - depth)) & 1) : (uint)((ipLow >> (127 - depth)) & 1);
                byte* node = nodes + idx * 6;
                if (node >= nodesEnd) return -1;
                int off = bit == 0 ? 0 : 3;
                uint child = (uint)(node[off] | (node[off + 1] << 8) | (node[off + 2] << 16));
                if ((child & 0x800000) != 0) return depth + 1;
                if (child == 0) return -1;
                idx = child;
            }
        }
        else
        {
            uint* nodesEnd = (uint*)(nodes + (long)snap._v6NodeCount * 8);
            for (int depth = startDepth; depth < maxDepth; depth++)
            {
                if (idx >= snap._v6NodeCount) return -1;
                uint bit = depth <= 63 ? (uint)((ipHigh >> (63 - depth)) & 1) : (uint)((ipLow >> (127 - depth)) & 1);
                uint* node = (uint*)(nodes + idx * 8);
                if (node >= nodesEnd) return -1;
                uint child = node[bit];
                if ((child & Sentinel) != 0) return depth + 1;
                if (child == 0) return -1;
                idx = child;
            }
        }
        return -1;
    }

    private static string FormatV4Cidr(uint ip, int prefixLen)
    {
        uint net = prefixLen > 0 ? ip & (0xFFFFFFFFu << (32 - prefixLen)) : 0u;
        return $"{(net >> 24) & 0xFF}.{(net >> 16) & 0xFF}.{(net >> 8) & 0xFF}.{net & 0xFF}/{prefixLen}";
    }

    private static string FormatV6Cidr(ulong ipHigh, ulong ipLow, int prefixLen)
    {
        byte[] net = new byte[16];
        for (int i = 0; i < 8; i++)
        {
            net[i] = (byte)(ipHigh >> (56 - 8 * i));
            net[8 + i] = (byte)(ipLow >> (56 - 8 * i));
        }
        for (int bit = prefixLen; bit < 128; bit++)
            net[bit >> 3] &= (byte)~(1 << (7 - (bit & 7)));

        int[] g = new int[8];
        for (int i = 0; i < 8; i++)
            g[i] = (net[2 * i] << 8) | net[2 * i + 1];

        int bestStart = -1, bestLen = 0, curStart = -1, curLen = 0;
        for (int i = 0; i < 8; i++)
        {
            if (g[i] == 0)
            {
                if (curStart < 0) { curStart = i; curLen = 1; }
                else curLen++;
            }
            else
            {
                if (curLen > bestLen) { bestStart = curStart; bestLen = curLen; }
                curStart = -1; curLen = 0;
            }
        }
        if (curLen > bestLen) { bestStart = curStart; bestLen = curLen; }

        var sb = new System.Text.StringBuilder();
        if (bestLen >= 2)
        {
            for (int i = 0; i < bestStart; i++)
            {
                if (i > 0) sb.Append(':');
                sb.Append(g[i].ToString("x"));
            }
            sb.Append("::");
            for (int i = bestStart + bestLen; i < 8; i++)
            {
                if (i > bestStart + bestLen) sb.Append(':');
                sb.Append(g[i].ToString("x"));
            }
        }
        else
        {
            for (int i = 0; i < 8; i++)
            {
                if (i > 0) sb.Append(':');
                sb.Append(g[i].ToString("x"));
            }
        }
        sb.Append('/');
        sb.Append(prefixLen);
        return sb.ToString();
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

    // Bounded, lock-free per-snapshot cache of resolved GeoInfo keyed by entryId.
    // A complete immutable CacheEntry is published as one reference. This prevents a
    // colliding writer from exposing a key from one result with the value of another.
    private static GeoInfo? ResolveGeo(Snapshot snap, uint entryId)
    {
        if (entryId == 0) return null;
        if (entryId >= snap._groupEntryCounts[snap._groupIndex]) return null;

        var cache = snap._cache!;
        int h = (int)(entryId & (uint)(GeoCacheSize - 1));

        var cached = Volatile.Read(ref cache[h]);
        if (cached != null && cached.Key == entryId)
            return cached.Value;

        var geo = BuildGeo(snap, entryId);
        Volatile.Write(ref cache[h], new Snapshot.CacheEntry(entryId, geo));
        return geo;
    }

    private static GeoInfo BuildGeo(Snapshot snap, uint entryId)
    {
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
                         ? FormatFloat6(Unsafe.ReadUnaligned<float>(ref r))
                         : FormatFloat6(Unsafe.ReadUnaligned<double>(ref r));
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

        return new GeoInfo(snap._fieldNames, values, snap._normMap, snap._numericFlags, takeOwnership: true);
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string FormatFloat6(float v)
    {
        if (float.IsNaN(v) || float.IsInfinity(v)) return "";
        if (v == MathF.Truncate(v)) return ((long)v).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return v.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string FormatFloat6(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "";
        if (v == Math.Truncate(v)) return ((long)v).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return v.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadUintWidth(ReadOnlySpan<byte> s, int off, int width)
    {
        int need = width switch { <= 1 => 1, 2 => 2, 3 => 3, _ => 4 };
        if (off < 0 || off + need > s.Length)
            throw new QzdbException(ErrorCode.Corrupted, $"ReadUintWidth out of bounds: off={off} width={width} len={s.Length}");
        return width switch
        {
            <= 1 => s[off],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(s[off..]),
            3 => (uint)(s[off] | (s[off + 1] << 8) | (s[off + 2] << 16)),
            _ => BinaryPrimitives.ReadUInt32LittleEndian(s[off..])
        };
    }

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

    private static (ulong hi, ulong lo) V6FromBytes(byte[] b) =>
        (BinaryPrimitives.ReadUInt64BigEndian(b), BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(8)));

    #endregion

    #region IP Parsing (zero-alloc for IPv4, minimal for IPv6)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryParseIp(ReadOnlySpan<char> s, out uint v4, out ulong v6High, out ulong v6Low, out bool isV4)
    {
        v4 = 0; v6High = 0; v6Low = 0; isV4 = false;
        if (s.IsEmpty || s.Length > 45) return false;

        if (TryParseV4(s, out v4, out bool hasColon))
        {
            isV4 = true;
            return true;
        }
        if (hasColon)
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
        return false;
    }

    private static readonly byte[] HexLUT = new byte[128];
    static QzdbReader()
    {
        for (int i = 0; i < 10; i++) HexLUT[48 + i] = (byte)i;
        for (int i = 0; i < 6; i++) { HexLUT[97 + i] = (byte)(10 + i); HexLUT[65 + i] = (byte)(10 + i); }
    }

    private static bool TryParseV4(ReadOnlySpan<char> s, out uint v4, out bool hasColon)
    {
        v4 = 0;
        int n = s.Length;
        if (n == 0) { hasColon = false; return false; }
        hasColon = s.Contains(':');
        if (hasColon || n > 15) return false;
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
            else if (c < '0' || c > '9') return false;
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
        if (s.IsEmpty || s.Length > 45 || s.Contains('%')) return r;

        int dc = s.IndexOf("::");
        if (dc >= 0 && s[(dc + 2)..].IndexOf("::") >= 0) return r;
        if (dc < 0 && (s[0] == ':' || s[^1] == ':')) return r;

        ReadOnlySpan<char> left = dc >= 0 ? s[..dc] : s;
        ReadOnlySpan<char> right = dc >= 0 ? s[(dc + 2)..] : ReadOnlySpan<char>.Empty;

        Span<ushort> leftGroups = stackalloc ushort[8];
        Span<ushort> rightGroups = stackalloc ushort[8];
        int leftCount = ParseV6Side(left, leftGroups, allowV4: dc < 0, out bool leftHasV4, out uint leftV4);
        if (leftCount < 0) return r;
        int rightCount = ParseV6Side(right, rightGroups, allowV4: dc >= 0, out bool rightHasV4, out uint rightV4);
        if (rightCount < 0 || (leftHasV4 && rightHasV4)) return r;
        bool hasV4 = leftHasV4 || rightHasV4;
        uint v4Int = leftHasV4 ? leftV4 : rightV4;

        int totalGroups = leftCount + rightCount;
        int v4Slots = hasV4 ? 2 : 0;
        int zeros;
        if (dc >= 0)
        {
            if (totalGroups + v4Slots >= 8) return r;
            zeros = 8 - totalGroups - v4Slots;
        }
        else
        {
            if (totalGroups + v4Slots != 8) return r;
            zeros = 0;
        }

        // Compose 16 bytes: left groups, zeros, right groups, optional IPv4 tail.
        Span<byte> buf = stackalloc byte[16];
        int off = 0;
        for (int g = 0; g < leftCount; g++, off += 2)
        {
            buf[off] = (byte)(leftGroups[g] >> 8); buf[off + 1] = (byte)leftGroups[g];
        }
        off += zeros * 2;
        for (int g = 0; g < rightCount; g++, off += 2)
        {
            buf[off] = (byte)(rightGroups[g] >> 8); buf[off + 1] = (byte)rightGroups[g];
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

        ulong hi = 0, lo = 0;
        for (int i = 0; i < 8; i++) hi = (hi << 8) | buf[i];
        for (int i = 8; i < 16; i++) lo = (lo << 8) | buf[i];
        r.High = hi;
        r.Low = lo;
        r.Valid = true;
        return r;
    }

    private static int ParseV6Side(ReadOnlySpan<char> side, Span<ushort> groups, bool allowV4,
        out bool hasV4, out uint v4)
    {
        hasV4 = false;
        v4 = 0;
        if (side.IsEmpty) return 0;

        int count = 0;
        int start = 0;
        for (int i = 0; i <= side.Length; i++)
        {
            if (i != side.Length && side[i] != ':') continue;
            int length = i - start;
            if (length == 0) return -1;
            var segment = side.Slice(start, length);
            if (segment.Contains('.'))
            {
                if (!allowV4 || i != side.Length || hasV4 || !TryParseV4(segment, out v4, out _)) return -1;
                hasV4 = true;
            }
            else
            {
                if (count >= groups.Length || !TryParseHexGroup(segment, out groups[count])) return -1;
                count++;
            }
            start = i + 1;
        }
        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryParseHexGroup(ReadOnlySpan<char> g, out ushort val)
    {
        val = 0;
        int len = g.Length;
        if (len == 0 || len > 4) return false;
        for (int i = 0; i < len; i++)
        {
            char c = g[i];
            if (c >= 128) return false;
            byte h = HexLUT[c];
            if (h == 0 && c != '0') return false;
            val = (ushort)((val << 4) | h);
        }
        return true;
    }

    #endregion

    #region CRC32

    internal static long ComputeCanonicalCrc(Snapshot snap)
    {
        var span = snap._data.Span;
        var crc = new System.IO.Hashing.Crc32();
        crc.Append(span.Slice(0, 16));
        Span<byte> zeros = stackalloc byte[4];
        crc.Append(zeros);
        crc.Append(span.Slice(20));
        return crc.GetCurrentHashAsUInt32();
    }

    #endregion

    #region Public Properties

    public string Version => RequireSnapshot()._version;
    public string DataMonth => RequireSnapshot()._dataMonth;
    /// <summary>
    /// Edition name ("std"|"pro"|"asn"|"max"|"ult"), or "" when undeterminable (never invented).
    /// Priority per FORMAT §10.3: groupId/VersionMask → Metadata → unambiguous field count.
    /// Use <see cref="EditionSource"/> to learn which rule fired.
    /// </summary>
    public string Edition => RequireSnapshot()._edition;

    /// <summary>
    /// File-level one-hot edition bitmask (Header offset 6).
    /// bit0=std(1) bit1=asn(2) bit2=pro(4) bit3=max(8) bit4=ult(16).
    /// </summary>
    public int VersionMask => RequireSnapshot()._versionMask;

    /// <summary>How <see cref="Edition"/> was resolved: version_mask | metadata | inferred | unknown.</summary>
    public string EditionSource => RequireSnapshot()._editionSource;

    /// <summary>Where <see cref="FieldNames"/> came from: metadata | edition | synthetic.</summary>
    public string FieldNamesSource => RequireSnapshot()._fieldNamesSource;

    public string Scope => RequireSnapshot()._scope;
    public string BuildTime => RequireSnapshot()._buildTimeStr;
    public string Description => RequireSnapshot()._description;
    public string FileHash
    {
        get
        {
            var snapshot = RequireSnapshot();
            return (snapshot._canonicalCrc ?? ComputeCanonicalCrc(snapshot)).ToString("x8");
        }
    }
    public string[] FieldNames => (string[])RequireSnapshot()._fieldNames.Clone();
    public int GroupCount => RequireSnapshot()._actualGroups;
    public int PoolCount => RequireSnapshot()._poolCount;

    public bool HasField(string name) => RequireSnapshot()._normMap.ContainsKey(GeoInfo.NormalizeKey(name));
    public bool VerifyCrc()
    {
        var s = RequireSnapshot();
        return ComputeCanonicalCrc(s) == s._storedCrc;
    }

    public bool VerifyCRC() => VerifyCrc();

    #endregion

    #region Lifecycle

    /// <summary>Atomically swap in a freshly loaded snapshot from <paramref name="path"/> (CRC always enforced).</summary>
    public void Reload(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        int groupIndex = RequireSnapshot()._groupIndex;
        var snap = LoadPath(path, groupIndex, verifyCrc: true);
        PublishSnapshot(snap);
    }

    /// <summary>Atomically swap in a freshly loaded snapshot from <paramref name="buffer"/> (CRC always enforced).</summary>
    public void ReloadBuffer(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        int groupIndex = RequireSnapshot()._groupIndex;
        var snap = Snapshot.FromBuffer(buffer, groupIndex, verifyCrc: true);
        PublishSnapshot(snap);
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (Interlocked.Exchange(ref _lifecycleState, 1) != 0) return;
            Interlocked.Exchange(ref _activeSnapshot, null);
        }
    }

    private void PublishSnapshot(Snapshot snapshot)
    {
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _lifecycleState) != 0)
                throw new ObjectDisposedException(nameof(QzdbReader));
            Interlocked.Exchange(ref _activeSnapshot, snapshot);
        }
    }

    #endregion
}
