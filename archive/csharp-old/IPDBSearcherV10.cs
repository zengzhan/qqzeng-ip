using System;
using System.IO;
using System.IO.Hashing;
using System.Text;
using System.Buffers.Binary;

namespace qqzengPgUI.ipdb8
{
    /// <summary>
    /// IPDB v10.0 Searcher - 极致优化版本
    /// 
    /// 核心优化:
    /// 1. 分块索引 (Sectional Indexing): 65536 个块，每块独立二分
    /// 2. 5字节记录: [StartIP_Low(2B)][GeoID(3B)]
    /// 3. 内存优化: 仅需 ~10MB 内存 (V9 需 14MB+)
    /// </summary>
    public class IPDBSearcherV10
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

        private uint[] _index; // Block Offsets
        private byte[] _data;  // Data Area (5 bytes per record)
        
        private byte[] _geoStructBytes;
        private StringPool[] _pools;
        
        private uint _storedCrc32;
        private uint _v4Count;

        public IPDBSearcherV10(string dbPath, bool verifyCrc = false)
        {
            var bytes = File.ReadAllBytes(dbPath);
            using (var ms = new MemoryStream(bytes))
            using (var r = new BinaryReader(ms))
            {
                // Verify signature
                var sig = Encoding.ASCII.GetString(bytes, 0, 4);
                if (sig != "QZ10")
                {
                    throw new InvalidDataException($"Invalid file signature: {sig}. Expected 'QZ10'.");
                }

                ms.Seek(4+4, SeekOrigin.Begin); // Skip Sig, Ver
                uint geoCount = r.ReadUInt32();
                _storedCrc32 = r.ReadUInt32(); 
                ulong offGeoStruct = r.ReadUInt64();
                ulong offPools = r.ReadUInt64();
                ulong offIndex = r.ReadUInt64();
                ulong offData = r.ReadUInt64();
                _v4Count = r.ReadUInt32(); 

                // 1. Geo Structs
                int structLen = (int)geoCount * 24;
                _geoStructBytes = new byte[structLen];
                Buffer.BlockCopy(bytes, (int)offGeoStruct, _geoStructBytes, 0, structLen);

                // 2. Pools
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

                // 3. Index (V10: 65537 uints)
                _index = new uint[65537];
                Buffer.BlockCopy(bytes, (int)offIndex, _index, 0, 65537 * 4);

                // 4. Data (Raw Bytes)
                // Calculate Data Length from FileSize or Index
                int dataLen = bytes.Length - (int)offData;
                _data = new byte[dataLen];
                Buffer.BlockCopy(bytes, (int)offData, _data, 0, dataLen);

                // Optional CRC32 verification
                if (verifyCrc)
                {
                    VerifyCrc32(bytes, offGeoStruct, offPools, offIndex, offData);
                }
            }
        }

        private void VerifyCrc32(byte[] bytes, ulong offGeoStruct, ulong offPools, ulong offIndex, ulong offData)
        {
            var crc32 = new Crc32();
            
            // Calculate range: from GeoStruct to end of Data
            int dataEnd = bytes.Length;
            int geoStart = (int)offGeoStruct;
            
            crc32.Append(bytes.AsSpan(geoStart, dataEnd - geoStart));
            uint computed = BitConverter.ToUInt32(crc32.GetCurrentHash());
            
            if (computed != _storedCrc32)
            {
                throw new InvalidDataException($"CRC32 mismatch! Stored: 0x{_storedCrc32:X8}, Computed: 0x{computed:X8}");
            }
        }

        /// <summary>
        /// V10 核心查询算法: 分块索引 + 5字节记录二分
        /// </summary>
        public string Find(uint ip)
        {
            var info = FindInfo(ip);
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
            uint high16 = ip >> 16;
            ushort low16 = (ushort)(ip & 0xFFFF);

            // 1. Get Block Range
            uint startOff = _index[high16];
            uint endOff = _index[high16 + 1];
            uint len = endOff - startOff;

            if (len == 0) return null;

            // 2. Binary Search in Block
            int left = 0;
            int right = (int)(len / 5) - 1; 
            
            int resultIdx = -1;

            fixed (byte* pData = _data)
            {
                byte* basePtr = pData + startOff;
                while (left <= right)
                {
                    int mid = (left + right) >> 1;
                    byte* p = basePtr + mid * 5; 
                    
                    // Read StartIP_Low (2 bytes)
                    // Use Unaligned read for safety across architectures, though x64/arm64 usually support unaligned.
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
                    byte* p = basePtr + resultIdx * 5;
                    // Read GeoID (3 bytes) at offset + 2
                    int geoId = *(p + 2) | (*(p + 3) << 8) | (*(p + 4) << 16);
                    return GetIPInfo(geoId);
                }
            }

            return null;
        }

        private IPInfo GetIPInfo(int geoId)
        {
            if (geoId == 0) return null;
            int offset = geoId * 24;
            
            // Read all indices
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
                AreaCode = "", // Not in struct
                Lng = lng,
                Lat = lat
            };
        }
    }
}
