using System;
using System.IO;
using System.Text;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Buffers.Binary;

namespace qqzengPgUI.ipdb8
{
    public class IPDBSearcherV18
    {
        public struct IPInfo
        {
            public string Continent; public string Country;
            public string Province;  public string City;
            public string District;  public string ISP;
            public string AreaCode;  public string EnName;
            public string Code;
            public float Lng;        public float Lat;

            public bool IsEmpty => Country == null;

            public override string ToString()
            {
                if (Country == null) return "";
                return $"{Continent}|{Country}|{Province}|{City}|{District}|{ISP}|{AreaCode}|{EnName}|{Code}|{Lng}|{Lat}";
            }
        }

        private static IPDBSearcherV18 _instance;
        private static readonly object _lock = new object();

        public static IPDBSearcherV18 Instance
        {
            get
            {
                if (_instance == null) throw new InvalidOperationException("请先调用 IPDBSearcherV18.Load(path)");
                return _instance;
            }
        }

        public static void Load(string dbPath)
        {
            if (_instance != null) return;
            lock (_lock)
            {
                if (_instance == null)
                    _instance = new IPDBSearcherV18(dbPath, true);
            }
        }

        private byte[] _data;
        private long _offGeo, _offPools, _offV4Idx, _offV4Data, _offV6Data;
        private int _poolCount;
        private int _geoCount;
        private int _geoIdSize;
        private int _nodeSize;
        private int _dbVersionCode;
        private string[][] _pools;

        public int Version { get; private set; }
        public DateTime CreationDate { get; private set; }
        public int GeoCount => _geoCount;
        public int PoolCount => _poolCount;

        // ── IPInfo 字段枚举 ──
        private const int FI_CONTINENT = 0, FI_COUNTRY = 1, FI_PROVINCE = 2, FI_CITY = 3;
        private const int FI_DISTRICT = 4, FI_ISP = 5, FI_AREACODE = 6, FI_ENNAME = 7;
        private const int FI_CODE = 8, FI_LNG = 9, FI_LAT = 10;

        // ── V2 版本字段映射表 (pool index → IPInfo field index, -1 = skip) ──
        // std  (5): continent(0), country(1), province(2), city(3), isp(5)
        private static readonly sbyte[] MapStd = { FI_CONTINENT, FI_COUNTRY, FI_PROVINCE, FI_CITY, FI_ISP };
        // ult  (11): continent, country, province, city, district, isp, area_code, country_english, country_code, longitude, latitude
        private static readonly sbyte[] MapUlt = { FI_CONTINENT, FI_COUNTRY, FI_PROVINCE, FI_CITY, FI_DISTRICT, FI_ISP, FI_AREACODE, FI_ENNAME, FI_CODE, FI_LNG, FI_LAT };
        // asn  (7→8, 取决于文件版本):
        //   旧版7字段: asn(-1), asn_org(7→EnName), asn_domain(8→Code), usage_type(-1跳过), country(1), country_code(-1), isp(5)
        //   当前8字段(Metadata驱动): continent, country_code, country, isp, asn, as_name, as_domain, usage_type
        //   ★ V18 硬编码映射仅作无Metadata旧文件回退；新文件应从Metadata type=2读取field_names
        private static readonly sbyte[] MapAsn = { -1, FI_ENNAME, FI_CODE, -1, FI_COUNTRY, -1, FI_ISP };
        // max  (25): continent(0), country(1), province(2), city(3), district(4), isp(5), area_code(6), en_name(7), code(8),
        //             c_alpha3(-1), province_en(-1), city_en(-1), lng(9), lat(10), rest(-1)
        private static readonly sbyte[] MapMax = {
            FI_CONTINENT, FI_COUNTRY, FI_PROVINCE, FI_CITY, FI_DISTRICT,
            FI_ISP, FI_AREACODE, FI_ENNAME, FI_CODE,
            -1, -1, -1, FI_LNG, FI_LAT,
            -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1
        };

        private static readonly sbyte[][] FieldMaps = { MapStd, MapUlt, MapAsn, MapMax };

        public IPDBSearcherV18(string dbPath, bool verifyCrc = false)
        {
            _data = File.ReadAllBytes(dbPath);
            Init(verifyCrc);
        }

        public IPDBSearcherV18(byte[] bytes, bool verifyCrc = false)
        {
            _data = bytes;
            Init(verifyCrc);
        }

        public static void Reload(string dbPath, bool verifyCrc = true)
        {
            lock (_lock)
            {
                var newSearcher = new IPDBSearcherV18(dbPath, verifyCrc);
                _instance = newSearcher;
            }
        }

        private void Init(bool verifyCrc)
        {
            if (_data.Length < 128) throw new Exception("无效的数据库文件: 文件过小");

            // [0-3] Magic
            if (_data[0] != (byte)'Q' || _data[1] != (byte)'Z' || _data[2] != (byte)'1' || _data[3] != (byte)'8')
                throw new Exception("无效的数据库文件: 期望签名为 QZ18");

            // [4-7] Version (date as int32 LE)
            int ver = BitConverter.ToInt32(_data, 4);
            Version = ver;
            if (ver > 20000101 && ver < 21000101)
            {
                try {
                    int y = ver / 10000;
                    int m = (ver % 10000) / 100;
                    int d = ver % 100;
                    CreationDate = new DateTime(y, m, d);
                } catch { }
            }

            // [5] DB Version Code (0=std, 1=ult, 2=asn, 3=max)
            _dbVersionCode = _data[5];

            // [6] Pool Count
            _poolCount = _data[6];

            // [7] Geo ID Size
            _geoIdSize = _data[7];

            // [8-9] Flags
            // [10-11] Reserved

            // [12-15] CRC32
            uint fileCrc = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(_data, 12, 4));

            // [16-19] Geo Count
            _geoCount = BitConverter.ToInt32(_data, 16);

            _nodeSize = 2 + _geoIdSize;

            // V2 offsets at bytes 64-103
            _offGeo = (long)BitConverter.ToUInt64(_data, 64);
            _offPools = (long)BitConverter.ToUInt64(_data, 72);
            _offV4Idx = (long)BitConverter.ToUInt64(_data, 80);
            _offV4Data = (long)BitConverter.ToUInt64(_data, 88);
            _offV6Data = (long)BitConverter.ToUInt64(_data, 96);

            long limit = _data.Length;
            if (_offGeo >= limit || _offPools >= limit || _offV4Idx >= limit ||
                _offV4Data >= limit || _offV6Data > limit)
                throw new Exception("数据库文件已损坏: Offset 指向文件外");

            // CRC 校验
            if (verifyCrc && fileCrc != 0)
            {
                byte msg0 = _data[12], msg1 = _data[13], msg2 = _data[14], msg3 = _data[15];
                _data[12] = 0; _data[13] = 0; _data[14] = 0; _data[15] = 0;

                uint calc = Crc32Algorithm.Compute(_data);

                _data[12] = msg0; _data[13] = msg1; _data[14] = msg2; _data[15] = msg3;

                if (calc != fileCrc) throw new Exception($"数据库文件完整性校验失败! HeaderCRC={fileCrc:X8}, Calc={calc:X8}");
            }

            InitPools();
        }

        /// <summary>
        /// 兼容旧版 API — V2 格式无多语言，始终返回 true
        /// </summary>
        public bool SetLanguage(int langIdx) => langIdx == 0;

        private void InitPools()
        {
            _pools = new string[_poolCount][];
            int offset = (int)_offPools;
            for (int i = 0; i < _poolCount; i++)
                _pools[i] = ReadPool(ref offset);
        }

        private string[] ReadPool(ref int offset)
        {
            int count = BitConverter.ToInt32(_data, offset);
            offset += 4;

            var pool = new string[count];
            int offsetTableMetadataSize = (count + 1) * 4;
            int dataStart = offset + offsetTableMetadataSize;

            ReadOnlySpan<byte> offsetSpan = new ReadOnlySpan<byte>(_data, offset, offsetTableMetadataSize);

            for (int i = 0; i < count; i++)
            {
                int start = BinaryPrimitives.ReadInt32LittleEndian(offsetSpan.Slice(i * 4, 4));
                int end = BinaryPrimitives.ReadInt32LittleEndian(offsetSpan.Slice((i + 1) * 4, 4));
                pool[i] = Encoding.UTF8.GetString(_data, dataStart + start, end - start);
            }

            offset = dataStart + BinaryPrimitives.ReadInt32LittleEndian(offsetSpan.Slice(count * 4, 4));
            return pool;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IPInfo FindUint(uint ip) => Find(ip);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string FindStr(string ip) => Find(ip).ToString();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IPInfo Find(uint ip)
        {
            uint high = ip >> 16;
            int blkRelOffset = Unsafe.ReadUnaligned<int>(ref _data[(int)_offV4Idx + (int)high * 4]);

            int blkAbsStart = (int)_offV4Data + blkRelOffset;
            ref byte blockBase = ref _data[blkAbsStart];
            int count = Unsafe.ReadUnaligned<ushort>(ref blockBase);

            if (count == 0) return default;

            if (blkAbsStart + 2 + count * _nodeSize > _data.Length) return default;

            int k = 1;
            ushort key = (ushort)(ip & 0xFFFF);
            int bestGeo = 0;

            while (k <= count)
            {
                int offset = 2 + (k - 1) * _nodeSize;
                ref byte nodePtr = ref Unsafe.Add(ref blockBase, offset);
                ushort nodeKey = Unsafe.ReadUnaligned<ushort>(ref nodePtr);

                if (key < nodeKey)
                    k = 2 * k;
                else
                {
                    if (_geoIdSize == 2)
                        bestGeo = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref nodePtr, 2));
                    else
                    {
                        bestGeo = Unsafe.Add(ref nodePtr, 2)
                                | (Unsafe.Add(ref nodePtr, 3) << 8)
                                | (Unsafe.Add(ref nodePtr, 4) << 16);
                    }
                    k = 2 * k + 1;
                }
            }

            return GetGeoInfo(bestGeo);
        }

        public IPInfo Find(string ip)
        {
            if (string.IsNullOrEmpty(ip) || ip.Length > 64) return default;
            if (IsIPv4Fast(ip, out uint val)) return Find(val);

            if (IPAddress.TryParse(ip, out var addr))
            {
                if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                    return FindV6(addr);

                Span<byte> buf = stackalloc byte[4];
                if (addr.TryWriteBytes(buf, out _))
                    return Find(BinaryPrimitives.ReadUInt32BigEndian(buf));
            }
            return default;
        }

        public IPInfo FindV6(IPAddress addr)
        {
            Span<byte> buf = stackalloc byte[16];
            if (!addr.TryWriteBytes(buf, out int written) || written < 16) return default;

            if (IsV4Mapped(buf))
                return Find(BinaryPrimitives.ReadUInt32BigEndian(buf.Slice(12)));

            ulong ipHigh = BinaryPrimitives.ReadUInt64BigEndian(buf);
            ulong ipLow = BinaryPrimitives.ReadUInt64BigEndian(buf.Slice(8));

            return SearchV6(ipHigh, ipLow);
        }

        private IPInfo SearchV6(ulong targetHigh, ulong targetLow)
        {
            if (_offV6Data >= _data.Length) return default;

            int idxStart = (int)_offV6Data;
            int count = BitConverter.ToInt32(_data, idxStart);
            if (count == 0) return default;

            int entrySize = 32 + _geoIdSize;
            int dataStart = idxStart + 4;

            if (dataStart + count * entrySize > _data.Length) return default;

            int L = 0, R = count - 1;
            int result = -1;

            while (L <= R)
            {
                int mid = L + (R - L) / 2;
                int p = dataStart + mid * entrySize;

                ulong sHiRaw = Unsafe.ReadUnaligned<ulong>(ref _data[p]);
                ulong sHi = BinaryPrimitives.ReverseEndianness(sHiRaw);
                if (sHi < targetHigh) { result = mid; L = mid + 1; continue; }
                if (sHi > targetHigh) { R = mid - 1; continue; }

                ulong sLoRaw = Unsafe.ReadUnaligned<ulong>(ref _data[p + 8]);
                if (BinaryPrimitives.ReverseEndianness(sLoRaw) <= targetLow)
                    { result = mid; L = mid + 1; }
                else
                    R = mid - 1;
            }

            if (result < 0) return default;

            int rp = dataStart + result * entrySize;
            ulong eHiRaw = Unsafe.ReadUnaligned<ulong>(ref _data[rp + 16]);
            ulong eHi = BinaryPrimitives.ReverseEndianness(eHiRaw);
            if (eHi < targetHigh) return default;

            ulong eLoRaw = Unsafe.ReadUnaligned<ulong>(ref _data[rp + 24]);
            if (eHi == targetHigh && BinaryPrimitives.ReverseEndianness(eLoRaw) < targetLow) return default;

            int geoId;
            if (_geoIdSize == 2)
                geoId = Unsafe.ReadUnaligned<ushort>(ref _data[rp + 32]);
            else
                geoId = _data[rp + 32] | (_data[rp + 33] << 8) | (_data[rp + 34] << 16);

            return GetGeoInfo(geoId);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private IPInfo GetGeoInfo(int geoId)
        {
            if (geoId == 0 || geoId >= _geoCount) return default;

            int p = (int)_offGeo + geoId * _poolCount * 2;
            var info = new IPInfo();

            sbyte[] fieldMap = _dbVersionCode >= 0 && _dbVersionCode < FieldMaps.Length
                ? FieldMaps[_dbVersionCode]
                : MapMax;

            int mapLen = Math.Min(fieldMap.Length, _poolCount);
            for (int i = 0; i < mapLen; i++)
            {
                ushort poolIdx = Unsafe.ReadUnaligned<ushort>(ref _data[p + i * 2]);
                sbyte field = fieldMap[i];
                if (field < 0 || poolIdx == 0) continue;

                string val = poolIdx < _pools[i].Length ? _pools[i][poolIdx] : "";
                SetField(ref info, field, val);
            }

            return info;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SetField(ref IPInfo info, int field, string val)
        {
            switch (field)
            {
                case FI_CONTINENT: info.Continent = val; break;
                case FI_COUNTRY:   info.Country = val; break;
                case FI_PROVINCE:  info.Province = val; break;
                case FI_CITY:      info.City = val; break;
                case FI_DISTRICT:  info.District = val; break;
                case FI_ISP:       info.ISP = val; break;
                case FI_AREACODE:  info.AreaCode = val; break;
                case FI_ENNAME:    info.EnName = val; break;
                case FI_CODE:      info.Code = val; break;
                case FI_LNG:       float.TryParse(val, out info.Lng); break;
                case FI_LAT:       float.TryParse(val, out info.Lat); break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsIPv4Fast(ReadOnlySpan<char> ip, out uint result)
        {
            result = 0;
            int value = 0, shift = 24, i = 0, len = ip.Length;
            while (i < len && ip[i] <= ' ') i++;
            if (i == len) return false;
            bool hasDigit = false;
            int dots = 0;
            for (; i < len; i++)
            {
                char c = ip[i];
                if (c >= '0' && c <= '9') { value = value * 10 + (c - '0'); hasDigit = true; if (value > 255) return false; }
                else if (c == '.')
                {
                    if (!hasDigit || dots == 3) return false;
                    result |= (uint)value << shift; shift -= 8; value = 0; hasDigit = false; dots++;
                }
                else break;
            }
            if (dots != 3 || !hasDigit) return false;
            result |= (uint)value;
            while (i < len) { if (ip[i] > ' ') return false; i++; }
            return true;
        }

        private bool IsV4Mapped(Span<byte> buf)
        {
            return BinaryPrimitives.ReadUInt64LittleEndian(buf) == 0
                && BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(8)) == 0
                && BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(10)) == 0xFFFF;
        }

        static class Crc32Algorithm
        {
            private static readonly uint[] Table;
            static Crc32Algorithm()
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
                    crc = (crc >> 8) ^ Table[(crc ^ buffer[i]) & 0xff];
                return ~crc;
            }
        }
    }
}
