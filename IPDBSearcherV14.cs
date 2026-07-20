using System;
using System.IO;
using System.Text;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Buffers.Binary;

namespace qqzengPgUI.ipdb8
{
    /// <summary>
    /// IPDB V14 终极高性能硬件优化搜索器
    /// 改进特性：
    /// 1. 支持 QZ14 签名验证与 64 字节对齐读取。
    /// 2. IPv6 后缀检索 Eytzinger 化：大幅优化密集 IP 段下的缓存局部性。
    /// </summary>
    public class IPDBSearcherV14
    {
        public struct IPInfo
        {
            public string Continent; 
            public string Country;   
            public string Province;  
            public string City;      
            public string District;  
            public string ISP;       
            public string AreaCode;  
            public string EnName;    
            public string Code;      
            public float Lng;        
            public float Lat;        
            
            public bool IsEmpty => Country == null;

            public override string ToString()
            {
                if (Country == null) return "";
                return $"{Continent}|{Country}|{Province}|{City}|{District}|{ISP}|{AreaCode}|{EnName}|{Code}|{Lng}|{Lat}";
            }
        }

        private static IPDBSearcherV14 _instance;
        private static readonly object _lock = new object();

        public static IPDBSearcherV14 Instance
        {
            get
            {
                if (_instance == null) throw new InvalidOperationException("请先调用 IPDBSearcherV14.Load(path) 进行初始化");
                return _instance;
            }
        }

        public static void Load(string dbPath)
        {
            if (_instance != null) return;
            lock (_lock)
            {
                if (_instance == null)
                {
                    _instance = new IPDBSearcherV14(dbPath, true);
                }
            }
        }

        private byte[] _data;
        private long _offGeo, _offPools, _offV4Idx, _offV4Data, _offV6Data;
        private int _geoCount;      
        private int _geoIdSize;     
        private string[][] _pools;

        public int Version { get; private set; }
        public DateTime CreationDate { get; private set; }
        public int GeoCount => _geoCount;

        public IPDBSearcherV14(string dbPath, bool verifyCrc = false)
        {
            _data = File.ReadAllBytes(dbPath);
            Init(verifyCrc);
        }

        public IPDBSearcherV14(byte[] bytes, bool verifyCrc = false)
        {
            _data = bytes;
            Init(verifyCrc);
        }

        public static void Reload(string dbPath, bool verifyCrc = true)
        {
            lock (_lock)
            {
                var newSearcher = new IPDBSearcherV14(dbPath, verifyCrc);
                _instance = newSearcher;
            }
        }

        private void Init(bool verifyCrc)
        {
            if (_data.Length < 96) throw new Exception("无效的数据库文件: 文件过小");
            var span = new ReadOnlySpan<byte>(_data);
            if (span[0] != (byte)'Q' || span[1] != (byte)'Z' || span[2] != (byte)'1' || span[3] != (byte)'4')
            {
                throw new Exception("无效的数据库文件: 期望签名为 QZ14");
            }

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

            _geoCount = BitConverter.ToInt32(_data, 8);
            uint fileCrc = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12));
            _geoIdSize = _data[17];
            
            if (verifyCrc && fileCrc != 0)
            {
                byte msg0 = _data[12], msg1 = _data[13], msg2 = _data[14], msg3 = _data[15];
                _data[12] = 0; _data[13] = 0; _data[14] = 0; _data[15] = 0;
                
                uint calc = Crc32Algorithm.Compute(_data);
                
                _data[12] = msg0; _data[13] = msg1; _data[14] = msg2; _data[15] = msg3;

                if (calc != fileCrc) throw new Exception($"数据库文件完整性校验失败! HeaderCRC={fileCrc:X8}, Calc={calc:X8}");
            }
            
            _offGeo = (long)BitConverter.ToUInt64(_data, 28);
            _offPools = (long)BitConverter.ToUInt64(_data, 36);
            _offV4Idx = (long)BitConverter.ToUInt64(_data, 44);
            _offV4Data = (long)BitConverter.ToUInt64(_data, 52);
            _offV6Data = (long)BitConverter.ToUInt64(_data, 60);

            long limit = _data.Length;
            if (_offGeo >= limit || _offPools >= limit || _offV4Idx >= limit ||
                _offV4Data >= limit || _offV6Data > limit) 
            {
                 throw new Exception("数据库文件已损坏: Offset 指向文件外");
            }

            InitPools();
        }

        static class Crc32Algorithm
        {
            private static readonly uint[] Table;
            static Crc32Algorithm()
            {
                Table = new uint[256];
                for (uint i = 0; i < 256; i++) {
                    uint entry = i;
                    for (int j = 0; j < 8; j++)
                        if ((entry & 1) == 1) entry = (entry >> 1) ^ 0xEDB88320;
                        else entry = entry >> 1;
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

        private void InitPools()
        {
            _pools = new string[8][];
            int offset = (int)_offPools;
            for (int i = 0; i < 8; i++)
            {
                _pools[i] = ReadPool(ref offset);
            }
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
                int len = end - start;
                
                pool[i] = Encoding.UTF8.GetString(_data, dataStart + start, len);
            }
            
            int totalLen = BinaryPrimitives.ReadInt32LittleEndian(offsetSpan.Slice(count * 4, 4));
            offset = dataStart + totalLen;
            return pool;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IPInfo Find(uint ip)
        {
            uint high = ip >> 16;
            int blkRelOffset = Unsafe.ReadUnaligned<int>(ref _data[(int)_offV4Idx + (int)high * 4]);
            
            int blkAbsStart = (int)_offV4Data + blkRelOffset;
            ref byte blockBase = ref _data[blkAbsStart];
            int count = Unsafe.ReadUnaligned<ushort>(ref blockBase);
            
            if (count == 0) return default;

            int nodeSize = 2 + _geoIdSize;
            if (blkAbsStart + 2 + count * nodeSize > _data.Length) return default;

            int k = 1;
            ushort key = (ushort)(ip & 0xFFFF);
            int bestGeo = 0;
            
            while (k <= count)
            {
                int offset = 2 + (k - 1) * nodeSize;
                ref byte nodePtr = ref Unsafe.Add(ref blockBase, offset);
                ushort nodeKey = Unsafe.ReadUnaligned<ushort>(ref nodePtr);

                if (key < nodeKey)
                {
                    k = 2 * k;
                }
                else
                {
                    if (_geoIdSize == 2)
                    {
                        bestGeo = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref nodePtr, 2));
                    }
                    else
                    {
                        byte b0 = Unsafe.Add(ref nodePtr, 2);
                        byte b1 = Unsafe.Add(ref nodePtr, 3);
                        byte b2 = Unsafe.Add(ref nodePtr, 4);
                        bestGeo = b0 | (b1 << 8) | (b2 << 16);
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
                 {
                     uint u = BinaryPrimitives.ReadUInt32BigEndian(buf);
                     return Find(u);
                 }
            }
            return default;
        }

        public IPInfo FindV6(IPAddress addr)
        {
             Span<byte> buf = stackalloc byte[16];
             if (!addr.TryWriteBytes(buf, out int written) || written < 16) return default;
             
             if (IsV4Mapped(buf))
             {
                 uint v4 = BinaryPrimitives.ReadUInt32BigEndian(buf.Slice(12));
                 return Find(v4);
             }

             ulong ipHigh = BinaryPrimitives.ReadUInt64BigEndian(buf.Slice(0));
             ulong ipLow = BinaryPrimitives.ReadUInt64BigEndian(buf.Slice(8));

             return SearchV6(ipHigh, ipLow);
        }

        private IPInfo SearchV6(ulong targetHigh, ulong targetLow)
        {
            if (_offV6Data >= _data.Length) return default;

            int idxStart = (int)_offV6Data;
            int count = BitConverter.ToInt32(_data, idxStart);
            if (count == 0) return default;

            int idxBase = idxStart + 4;
            int entrySize = 12;
            
            if (idxBase + count * entrySize > _data.Length) return default;

            int L = 0, R = count - 1;
            int blkOff = -1;

            while (L <= R)
            {
                int mid = (L + R) / 2;
                int p = idxBase + mid * entrySize;
                
                ulong pfx = Unsafe.ReadUnaligned<ulong>(ref _data[p]);
                if (BitConverter.IsLittleEndian) pfx = BinaryPrimitives.ReverseEndianness(pfx);
                
                if (pfx < targetHigh) L = mid + 1;
                else if (pfx > targetHigh) R = mid - 1;
                else
                {
                    blkOff = Unsafe.ReadUnaligned<int>(ref _data[p + 8]);
                    break;
                }
            }

            if (blkOff == -1) return default;

            int headerTotalSize = 4 + count * 12;
            int blkAbs = (int)((long)_offV6Data + headerTotalSize + (uint)blkOff);
            
            ushort bCount = Unsafe.ReadUnaligned<ushort>(ref _data[blkAbs]);
            int recSize = 8 + _geoIdSize;
            int bBase = blkAbs + 2;
            
            if (bBase + bCount * recSize > _data.Length) return default;
            
            // V14 优化：在 IPv6 后缀块中使用 Eytzinger 布局检索
            int k = 1;
            int bestGeo = 0;
            
            while (k <= bCount)
            {
                int offset = bBase + (k - 1) * recSize;
                ulong sfx = Unsafe.ReadUnaligned<ulong>(ref _data[offset]);
                if (BitConverter.IsLittleEndian) sfx = BinaryPrimitives.ReverseEndianness(sfx);
                
                if (targetLow < sfx)
                {
                    k = 2 * k; // 左子树
                }
                else
                {
                    // targetLow >= sfx: 记录当前最佳匹配，并往右子树走查找是否有更大的 Suffix 依然满足条件
                    if (_geoIdSize == 2)
                    {
                        bestGeo = Unsafe.ReadUnaligned<ushort>(ref _data[offset + 8]);
                    }
                    else
                    {
                        bestGeo = _data[offset + 8] | (_data[offset + 9] << 8) | (_data[offset + 10] << 16);
                    }
                    k = 2 * k + 1;
                }
            }

            return GetGeoInfo(bestGeo);
        }

        private IPInfo GetGeoInfo(int geoId)
        {
            if (geoId == 0 || geoId >= _geoCount) return default;
            
            int p = (int)_offGeo + geoId * 24;
            var span = new ReadOnlySpan<byte>(_data, p, 24);
            
            var info = new IPInfo();
            info.Continent = GetStr(BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(0, 2)), 0);
            info.Country = GetStr(BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(2, 2)), 1);
            info.Province = GetStr(BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(4, 2)), 2);
            info.City = GetStr(BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(6, 2)), 3);
            info.District = GetStr(BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(8, 2)), 4);
            info.ISP = GetStr(BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(10, 2)), 5);
            info.Code = GetStr(BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(12, 2)), 6);
            info.EnName = GetStr(BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(14, 2)), 7);
            
            info.Lng = BitConverter.ToSingle(span.Slice(16, 4));
            info.Lat = BitConverter.ToSingle(span.Slice(20, 4));
            
            return info;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private string GetStr(int idx, int poolIdx)
        {
            var p = _pools[poolIdx];
            if (idx >= p.Length) return "";
            return p[idx];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsIPv4Fast(ReadOnlySpan<char> ip, out uint result)
        {
            result = 0;
            int value = 0;
            int shift = 24;
            int i = 0;
            int len = ip.Length;

            while (i < len && ip[i] <= ' ') i++;
            if (i == len) return false; 

            bool hasDigit = false;
            int dots = 0;
            
            for (; i < len; i++)
            {
                char c = ip[i];
                if (c >= '0' && c <= '9')
                {
                    value = value * 10 + (c - '0');
                    hasDigit = true;
                    if (value > 255) return false;
                }
                else if (c == '.')
                {
                     if (!hasDigit) return false;
                     if (dots == 3) return false;
                     
                     result |= (uint)value << shift;
                     shift -= 8;
                     value = 0;
                     hasDigit = false;
                     dots++;
                }
                else
                {
                    break;
                }
            }
            
            if (dots != 3 || !hasDigit) return false;
            result |= (uint)value;

            while (i < len)
            {
                if (ip[i] > ' ') return false; 
                i++;
            }

            return true;
        }

        private bool IsV4Mapped(Span<byte> buf)
        {
            ulong high = BinaryPrimitives.ReadUInt64LittleEndian(buf);
            ushort mid = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(8));
            ushort ffff = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(10));
            return high == 0 && mid == 0 && ffff == 0xFFFF;
        }
    }
}
