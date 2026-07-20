using System;
using System.IO;
using System.IO.Hashing;
using System.Text;
using System.Net;
using System.Buffers.Binary;
using System.Numerics;

namespace qqzengPgUI.ipdb8
{
    /// <summary>
    /// IPDB v11.0 Searcher - 支持 IPv6 与 自适应 GeoID 压缩
    /// 
    /// 核心特性:
    /// 1. 双栈支持: 同时支持 IPv4 (分块索引) 和 IPv6 (二分查找)
    /// 2. 极致压缩: 
    ///    - IPv4 记录: 4字节或5字节 (当 GeoCount <= 65535 时仅需 4字节)
    ///    - IPv6 记录: 18字节或19字节
    /// 3. 内存高效: 保持极低的内存占用
    /// </summary>
    public class IPDBSearcherV11
    {
        class StringPool
        {
            public uint[] Offsets;
            public byte[] Blob;
            public string[] Cache;

            public void InitCache(int count) { Cache = new string[count]; }
            
            public string Get(int idx)
            {
                if (idx >= Offsets.Length - 1) return "";
                
                var s = Cache[idx];
                if (s != null) return s;

                uint start = Offsets[idx];
                uint len = Offsets[idx+1] - start;
                s = Encoding.UTF8.GetString(Blob, (int)start, (int)len);
                Cache[idx] = s;
                return s;
            }
        }

        private uint[] _v4Index; // IPv4 块索引
        private byte[] _v4Data;  // IPv4 数据区
        private byte[] _v6Data;  // IPv6 数据区
        
        private byte[] _geoStructBytes;
        private StringPool[] _pools;
        
        private uint _v4Count;
        private uint _v6Count;
        private int _geoIdSize;  // GeoID 字节数 (2 或 3)
        private int _v4RecSize;  // IPv4 记录长度 (2 + GeoIDSize)
        private int _v6RecSize;  // IPv6 记录长度 (16 + GeoIDSize)

        public IPDBSearcherV11(string dbPath)
        {
            var bytes = File.ReadAllBytes(dbPath);
            Init(bytes);
        }

        public IPDBSearcherV11(byte[] bytes)
        {
            Init(bytes);
        }

        private void Init(byte[] bytes)
        {
            using (var ms = new MemoryStream(bytes))
            using (var r = new BinaryReader(ms))
            {
                // 1. 校验签名
                var sig = Encoding.ASCII.GetString(bytes, 0, 4);
                if (sig != "QZ11")
                {
                    throw new InvalidDataException($"无效文件签名: {sig}. 期望 'QZ11'.");
                }

                ms.Seek(4+4, SeekOrigin.Begin); // 跳过 Sig, Ver
                uint geoCount = r.ReadUInt32();
                ms.Seek(4, SeekOrigin.Current); // 跳过 CRC
                
                byte flags = r.ReadByte();
                bool hasV4 = (flags & 1) != 0;
                bool hasV6 = (flags & 2) != 0;
                bool isGeoId3 = (flags & 4) != 0;
                
                _geoIdSize = r.ReadByte(); 
                // Double check flag consistency
                if (isGeoId3 && _geoIdSize != 3) _geoIdSize = 3;
                if (!isGeoId3 && _geoIdSize != 2) _geoIdSize = 2; // Default to 2 if not flagged as 3? Actually flag bit 2 is explicit.
                // Trust the byte size field primarily
                
                _v4RecSize = 2 + _geoIdSize;
                _v6RecSize = 16 + _geoIdSize;

                ms.Seek(10, SeekOrigin.Current); // Padding

                ulong offGeoStruct = r.ReadUInt64();
                ulong offPools = r.ReadUInt64();
                ulong offV4Index = r.ReadUInt64();
                ulong offV4Data = r.ReadUInt64();
                ulong offV6Data = r.ReadUInt64();
                
                _v4Count = r.ReadUInt32(); 
                _v6Count = r.ReadUInt32();

                // 2. 加载 Geo Structs
                int structLen = (int)geoCount * 24; // V11 结构与 V10 保持一致 (24 bytes)
                _geoStructBytes = new byte[structLen];
                Buffer.BlockCopy(bytes, (int)offGeoStruct, _geoStructBytes, 0, structLen);

                // 3. 加载 Pools
                _pools = new StringPool[8];
                ms.Seek((long)offPools, SeekOrigin.Begin);
                for(int i=0; i<8; i++)
                {
                    _pools[i] = new StringPool();
                    uint count = r.ReadUInt32();
                    _pools[i].InitCache((int)count);
                    
                    int offTableLen = (int)(count + 1) * 4;
                    _pools[i].Offsets = new uint[count + 1];
                    long curPos = ms.Position;
                    Buffer.BlockCopy(bytes, (int)curPos, _pools[i].Offsets, 0, offTableLen);
                    ms.Seek(offTableLen, SeekOrigin.Current);
                    
                    uint blobLen = _pools[i].Offsets[count];
                    _pools[i].Blob = new byte[blobLen];
                    curPos = ms.Position;
                    Buffer.BlockCopy(bytes, (int)curPos, _pools[i].Blob, 0, (int)blobLen);
                    ms.Seek(blobLen, SeekOrigin.Current);
                }

                // 4. 加载 IPv4 数据
                if (hasV4)
                {
                    _v4Index = new uint[65537];
                    Buffer.BlockCopy(bytes, (int)offV4Index, _v4Index, 0, 65537 * 4);
                    
                    int v4Len = (int)offV6Data - (int)offV4Data; // Assuming V6 follows V4
                    if (offV6Data == 0) v4Len = bytes.Length - (int)offV4Data; // No V6
                    
                    _v4Data = new byte[v4Len];
                    Buffer.BlockCopy(bytes, (int)offV4Data, _v4Data, 0, v4Len);
                }

                // 5. 加载 IPv6 数据
                if (hasV6)
                {
                    int v6Len = bytes.Length - (int)offV6Data;
                    _v6Data = new byte[v6Len];
                    Buffer.BlockCopy(bytes, (int)offV6Data, _v6Data, 0, v6Len);
                }
            }
        }

        public string Find(string ipStr)
        {
            if (string.IsNullOrEmpty(ipStr)) return "";
            if (ipStr.Contains(":")) return FindV6(ipStr);
            if (IPAddress.TryParse(ipStr, out var ip))
            {
                 // Handle standard IPv4 map to uint
#pragma warning disable CS0618 // Type or member is obsolete
                 long val = ip.Address;
#pragma warning restore CS0618
                 // Correct endianness issue if on Little Endian system (Address is usually little endian int64 but we need uint32 host order or consistent)
                 // IPAddress.Address is Obsolete but convenient. Better: GetAddressBytes.
                 byte[] b = ip.GetAddressBytes();
                 if (b.Length == 4)
                 {
                     uint ipInt = (uint)(b[3] | (b[2] << 8) | (b[1] << 16) | (b[0] << 24));
                     return Find(ipInt);
                 }
            }
            return "";
        }

        public string Find(uint ip)
        {
            var info = FindInfo(ip);
            return FormatInfo(info);
        }

        public string FindV6(string ipStr)
        {
            if (!IPAddress.TryParse(ipStr, out var ip)) return "";
            if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return "";
            
            var info = FindInfoV6(ip);
            return FormatInfo(info);
        }

        private string FormatInfo(IPInfo info)
        {
            if (info == null) return "";
            return $"{info.Continent}|{info.Country}|{info.Province}|{info.City}|{info.District}|{info.ISP}|{info.AreaCode}|{info.EnName}|{info.Code}|{info.Lng}|{info.Lat}";
        }

        public class IPInfo
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
        }

        public unsafe IPInfo FindInfo(uint ip)
        {
            if (_v4Data == null) return null;

            uint high16 = ip >> 16;
            ushort low16 = (ushort)(ip & 0xFFFF);

            uint startOff = _v4Index[high16];
            uint endOff = _v4Index[high16 + 1];
            uint len = endOff - startOff;

            if (len == 0) return null;

            int left = 0;
            int right = (int)(len / _v4RecSize) - 1; 

            fixed (byte* pData = _v4Data)
            {
                byte* basePtr = pData + startOff;
                int resultIdx = -1;

                while (left <= right)
                {
                    int mid = (left + right) >> 1;
                    byte* p = basePtr + mid * _v4RecSize;
                    
                    ushort val = (ushort)(*p | (*(p + 1) << 8));
                    
                    if (val > low16)
                    {
                        right = mid - 1;
                    }
                    else
                    {
                        resultIdx = mid;
                        left = mid + 1;
                    }
                }

                if (resultIdx >= 0)
                {
                    byte* p = basePtr + resultIdx * _v4RecSize;
                    int geoId = ReadGeoID(p + 2);
                    return GetIPInfo(geoId);
                }
            }
            return null;
        }

        public unsafe IPInfo FindInfoV6(IPAddress ip)
        {
            if (_v6Data == null) return null;
            
            byte[] ipBytes = ip.GetAddressBytes();
            
            // Compare 128 bit keys
            // Data format: [High64][Low64][GeoID]
            // Need efficient comparison.
            
            ulong keyHigh = BinaryPrimitives.ReadUInt64BigEndian(ipBytes.AsSpan(0, 8));
            ulong keyLow = BinaryPrimitives.ReadUInt64BigEndian(ipBytes.AsSpan(8, 8));

            int left = 0;
            int right = (int)_v6Count - 1;
            int resultIdx = -1;

            fixed (byte* pData = _v6Data)
            {
                while (left <= right)
                {
                    int mid = (left + right) >> 1;
                    byte* p = pData + mid * _v6RecSize;
                    
                    // Direct pointer read for speed? Note endianness.
                    // Stored as Big Endian 16 bytes? In Builder we wrote `r.StartIP.ToByteArray()`.
                    // BigInteger.ToByteArray is Little Endian!
                    // So we must compare as Little Endian or fix Builder.
                    // Fix: Let's assume we want to support standard network byte order (Big Endian) for sorting?
                    // Actually, BigInteger bytes are Little Endian. comparing Little Endian bytes is tricky if we want lexical order.
                    // Wait, Builder sorted by BigInteger.CompareTo.
                    // BigInteger treats bytes as Little Endian value.
                    // So we should read back as Little Endian UInt64s or BigInteger.
                    // Performance hint: Better to store as BigEndian in DB formemcmp style comparison.
                    // But for V11 now, let's respect what Builder wrote (Little Endian 16 bytes).
                    
                    // In Builder: writer.Write(GetIPV6Bytes(r.StartIP)); -> Little Endian.
                    // So bytes on disk: [Byte0 (LSB)] ... [Byte15 (MSB)]
                    
                    // To compare correctly, we need to compare starting from MSB (Byte 15) down to LSB (Byte 0).
                    
                    int cmp = 0;
                    for(int i = 15; i >= 0; i--)
                    {
                        byte bData = *(p + i);
                        byte bKey = ipBytes[15 - i]; // Standard IP bytes are Big Endian! 
                        // Wait. ip.GetAddressBytes() returns Network Order (Big Endian).
                        // [0] is Most Significant Byte.
                        // Disk [15] is Most Significant Byte (from BigInt Little Endian).
                        
                        // So Disk[15] compares to Key[0].
                        // Disk[14] compares to Key[1].
                        
                        byte bKeyMapped = ipBytes[15 - i]; 
                        
                        if (bData < bKeyMapped) { cmp = -1; break; }
                        if (bData > bKeyMapped) { cmp = 1; break; }
                    }
                    
                    if (cmp > 0) // Data > Key
                    {
                        right = mid - 1;
                    }
                    else // Data <= Key
                    {
                        resultIdx = mid; // Potential candidate
                        left = mid + 1;
                    }
                }
                
                if (resultIdx >= 0)
                {
                    byte* p = pData + resultIdx * _v6RecSize;
                    // GeoID is at offset 16
                    int geoId = ReadGeoID(p + 16);
                    return GetIPInfo(geoId);
                }
            }
            
            return null;
        }

        private unsafe int ReadGeoID(byte* p)
        {
            if (_geoIdSize == 2)
                return *p | (*(p + 1) << 8);
            else
                return *p | (*(p + 1) << 8) | (*(p + 2) << 16);
        }

        private IPInfo GetIPInfo(int geoId)
        {
            if (geoId == 0) return null;
            int offset = geoId * 24;
            
            ushort[] idxs = new ushort[8];
            for(int i=0; i<8; i++)
                idxs[i] = BinaryPrimitives.ReadUInt16LittleEndian(_geoStructBytes.AsSpan(offset + i * 2));
            
            float lng = BinaryPrimitives.ReadSingleLittleEndian(_geoStructBytes.AsSpan(offset + 16));
            float lat = BinaryPrimitives.ReadSingleLittleEndian(_geoStructBytes.AsSpan(offset + 20));

            return new IPInfo 
            {
                Continent = _pools[0].Get(idxs[0]),
                Country = _pools[1].Get(idxs[1]),
                Province = _pools[2].Get(idxs[2]),
                City = _pools[3].Get(idxs[3]),
                District = _pools[4].Get(idxs[4]),
                ISP = _pools[5].Get(idxs[5]),
                Code = _pools[6].Get(idxs[6]),
                EnName = _pools[7].Get(idxs[7]),
                AreaCode = "", 
                Lng = lng,
                Lat = lat
            };
        }
    }
}
