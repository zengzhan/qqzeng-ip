using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Buffers.Binary;

namespace qqzengPgUI.ipdb8
{
    /// <summary>
    /// IPDB V14 构建器 - 包含低级别硬件性能优化：
    /// 1. IPv6 Key Eytzinger 布局：IPv6 后缀块二叉搜索优化。
    /// 2. 文件大块 64 字节对齐：降低 CPU 跨 Cache Line 加载开销。
    /// 3. 文件签名更新为 "QZ14"，版本号为 20260527。
    /// </summary>
    public class IPDBBuilderV14
    {
        struct GeoInfoStruct
        {
            public ushort ContinentIdx; public ushort CountryIdx; public ushort ProvinceIdx; public ushort CityIdx;
            public ushort DistrictIdx; public ushort IspIdx; public ushort CodeIdx; public ushort EnNameIdx;
            public float Lng; public float Lat;
        }

        struct InputRecordV4
        {
            public uint StartIP;
            public uint EndIP;
            public int GeoID;
        }

        struct InputRecordV6
        {
            public BigInteger StartIP;
            public BigInteger EndIP;
            public int GeoID;
        }

        class DimensionPool
        {
            public Dictionary<string, ushort> Map = new Dictionary<string, ushort>();
            public List<string> List = new List<string>();
            public DimensionPool() { Map[""] = 0; List.Add(""); }
            
            public ushort GetOrAdd(string val)
            {
                if (string.IsNullOrEmpty(val)) return 0;
                if (Map.TryGetValue(val, out ushort id)) return id;
                id = (ushort)List.Count;
                Map[val] = id;
                List.Add(val);
                return id;
            }
        }

        public static void Build(string sourceV4Path, string sourceV6Path, string targetDbPath)
        {
            Build(sourceV4Path, sourceV6Path, targetDbPath, null);
        }

        public static void Build(string sourceV4Path, string sourceV6Path, string targetDbPath, Predicate<string[]> filter)
        {
            Console.WriteLine($"=== 开始构建 V14.0 {(filter == null ? "(全量版)" : "(定制版)")} ===");

            var poolContinent = new DimensionPool();
            var poolCountry = new DimensionPool();
            var poolProv = new DimensionPool();
            var poolCity = new DimensionPool();
            var poolDistrict = new DimensionPool();
            var poolIsp = new DimensionPool();
            var poolCode = new DimensionPool();
            var poolEnName = new DimensionPool();
            var geoStructMap = new Dictionary<string, int>();
            var geoStructList = new List<GeoInfoStruct>();
            
            geoStructList.Add(new GeoInfoStruct());
            geoStructMap[""] = 0;

            var recsV4 = new List<InputRecordV4>();
            var recsV6 = new List<InputRecordV6>();

            Console.WriteLine("正在读取源数据...");
            if (File.Exists(sourceV4Path)) ReadRecords(sourceV4Path, false, geoStructMap, geoStructList, poolContinent, poolCountry, poolProv, poolCity, poolDistrict, poolIsp, poolCode, poolEnName, recsV4, null, filter);
            if (File.Exists(sourceV6Path)) ReadRecords(sourceV6Path, true, geoStructMap, geoStructList, poolContinent, poolCountry, poolProv, poolCity, poolDistrict, poolIsp, poolCode, poolEnName, null, recsV6, filter);

            int geoIdSize = geoStructList.Count <= 65535 ? 2 : 3;
            Console.WriteLine($"自适应 GeoID 大小: {geoIdSize} bytes");

            recsV4.Sort((a,b)=>a.StartIP.CompareTo(b.StartIP));
            recsV4 = FillGapsV4(recsV4);
            recsV4 = MergeV4(recsV4);
            Console.WriteLine($"生成 IPv4 Eytzinger 块...");
            var v4Blocks = SplitAndEytzingerV4(recsV4, geoIdSize);

            Console.WriteLine($"执行 IPv6 Eytzinger 压缩...");
            recsV6.Sort((a,b)=>a.StartIP.CompareTo(b.StartIP));
            recsV6 = MergeV6(recsV6); 
            recsV6 = SplitV6AtPrefix(recsV6);
            var v6Data = CompressIPv6(recsV6, geoIdSize);

            using (var fs = new FileStream(targetDbPath, FileMode.Create))
            using (var writer = new BinaryWriter(fs))
            {
                 // A. Global Header (96 bytes)
                 writer.Write(Encoding.ASCII.GetBytes("QZ14"));
                 writer.Write((uint)20260527); 
                 writer.Write((uint)geoStructList.Count);
                 writer.Write((uint)0); // CRC32 (预留)
                 
                 byte flags = 0; 
                 if (recsV4.Count > 0) flags |= 1;
                 if (recsV6.Count > 0) flags |= 2;
                 if (geoIdSize == 3) flags |= 4;
                 writer.Write(flags);
                 writer.Write((byte)geoIdSize);
                 writer.Write(new byte[10]); // Padding

                 long posOffsets = fs.Position;
                 for(int i=0;i<5;i++) writer.Write((ulong)0);
                 
                 writer.Write((uint)recsV4.Count);
                 writer.Write((uint)recsV6.Count);
                 writer.Write(new byte[96 - fs.Position]); // 填充至 96 字节

                 // B. 写入结构化地理信息区 (Geo Structs) - 64字节对齐
                 long startGeo = AlignOffset(fs.Position, 64);
                 PadStreamTo(fs, startGeo);
                 foreach (var g in geoStructList) {
                    writer.Write(g.ContinentIdx); writer.Write(g.CountryIdx);
                    writer.Write(g.ProvinceIdx); writer.Write(g.CityIdx);
                    writer.Write(g.DistrictIdx); writer.Write(g.IspIdx);
                    writer.Write(g.CodeIdx); writer.Write(g.EnNameIdx);
                    writer.Write(g.Lng); writer.Write(g.Lat);
                 }

                 // C. 写入字符串池 (Pools) - 64字节对齐
                 long startPools = AlignOffset(fs.Position, 64);
                 PadStreamTo(fs, startPools);
                 WritePool(writer, poolContinent.List); WritePool(writer, poolCountry.List);
                 WritePool(writer, poolProv.List); WritePool(writer, poolCity.List);
                 WritePool(writer, poolDistrict.List); WritePool(writer, poolIsp.List);
                 WritePool(writer, poolCode.List); WritePool(writer, poolEnName.List);

                 // D. 写入 IPv4 索引与数据 (Index & Data) - 64字节对齐
                 long startV4Idx = AlignOffset(fs.Position, 64);
                 PadStreamTo(fs, startV4Idx);
                 long startV4Data = 0;
                 
                 long idxStartPos = fs.Position;
                 for(int i=0;i<65537;i++) writer.Write((uint)0);
                 
                 startV4Data = AlignOffset(fs.Position, 64); 
                 PadStreamTo(fs, startV4Data);
                 uint[] indexArr = new uint[65537];
                 
                 for(int i=0; i<65536; i++)
                 {
                     indexArr[i] = (uint)(fs.Position - startV4Data);
                     if (v4Blocks.ContainsKey(i)) writer.Write(v4Blocks[i]);
                 }
                 indexArr[65536] = (uint)(fs.Position - startV4Data);
                 
                 long endDataPos = fs.Position;
                 fs.Position = idxStartPos;
                 foreach(var v in indexArr) writer.Write(v);
                 fs.Position = endDataPos;

                 // E. 写入 IPv6 数据 - 64字节对齐
                 long startV6Data = AlignOffset(fs.Position, 64);
                 PadStreamTo(fs, startV6Data);
                 if (v6Data.Length > 0) writer.Write(v6Data);

                 // F. 回填文件头部偏移量
                 fs.Position = posOffsets;
                 writer.Write((ulong)startGeo);
                 writer.Write((ulong)startPools);
                 writer.Write((ulong)startV4Idx);
                 writer.Write((ulong)startV4Data);
                 writer.Write((ulong)startV6Data);
            }

            UpdateFileCrc32(targetDbPath);
            Console.WriteLine("V14.0 构建完成。");
        }

        static long AlignOffset(long position, int alignment)
        {
            long remainder = position % alignment;
            if (remainder == 0) return position;
            return position + (alignment - remainder);
        }

        static void PadStreamTo(Stream fs, long targetPosition)
        {
            long padBytes = targetPosition - fs.Position;
            if (padBytes > 0)
            {
                fs.Write(new byte[padBytes], 0, (int)padBytes);
            }
        }

        static void UpdateFileCrc32(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            bytes[12] = 0; bytes[13] = 0; bytes[14] = 0; bytes[15] = 0;
            
            uint crc = Crc32Algorithm.Compute(bytes);
            
            using(var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
            {
                fs.Seek(12, SeekOrigin.Begin);
                var b = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(b, crc);
                fs.Write(b, 0, 4);
            }
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

        static Dictionary<int, byte[]> SplitAndEytzingerV4(List<InputRecordV4> recs, int geoIdSize)
        {
            var blocks = new Dictionary<int, byte[]>();
            var groups = new Dictionary<int, List<InputRecordV4>>();
            
            foreach(var r in recs)
            {
                int highStart = (int)(r.StartIP >> 16);
                int highEnd = (int)(r.EndIP >> 16);
                for (int h = highStart; h <= highEnd; h++)
                {
                    if (!groups.ContainsKey(h)) groups[h] = new List<InputRecordV4>();
                    uint s = (h == highStart) ? r.StartIP : (uint)(h << 16);
                    uint e = (h == highEnd) ? r.EndIP : (uint)((h << 16) | 0xFFFF);
                    groups[h].Add(new InputRecordV4 { StartIP = s, EndIP = e, GeoID = r.GeoID });
                }
            }

            foreach(var kv in groups)
            {
                var list = kv.Value;
                int count = list.Count;
                if (count == 0) continue;

                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms))
                {
                    var arr = list.ToArray();
                    var eytzingerArr = new InputRecordV4[count + 1];
                    int sortedIdx = 0;
                    
                    Action<int> recurse = null;
                    recurse = (k) => {
                        if (k > count) return;
                        recurse(2*k);
                        eytzingerArr[k] = arr[sortedIdx++];
                        recurse(2*k + 1);
                    };
                    recurse(1);

                    w.Write((ushort)count);
                    for(int k=1; k<=count; k++)
                    {
                        var r = eytzingerArr[k];
                        w.Write((ushort)(r.StartIP & 0xFFFF));
                        if (geoIdSize == 2) w.Write((ushort)r.GeoID);
                        else { w.Write((byte)r.GeoID); w.Write((byte)(r.GeoID >> 8)); w.Write((byte)(r.GeoID >> 16)); }
                    }
                    blocks[kv.Key] = ms.ToArray();
                }
            }
            return blocks;
        }

        static byte[] CompressIPv6(List<InputRecordV6> recs, int geoIdSize)
        {
             if (recs.Count == 0) return new byte[0];
             
             using (var ms = new MemoryStream())
             using (var w = new BinaryWriter(ms))
             {
                 var blocks = new List<(ulong Prefix, byte[] Data)>();
                 int i = 0;
                 while(i < recs.Count)
                 {
                     var r0 = recs[i];
                     ulong prefix = GetPrefix64(r0.StartIP);
                     
                     var blockRecs = new List<InputRecordV6>();
                     while(i < recs.Count && GetPrefix64(recs[i].StartIP) == prefix)
                     {
                         blockRecs.Add(recs[i]);
                         i++;
                     }
                     
                     using (var bms = new MemoryStream())
                     using (var bw = new BinaryWriter(bms))
                     {
                         ulong curSuffix = 0;
                         var finalItems = new List<(ulong Suffix, int GeoID)>();
                         
                         foreach(var br in blockRecs)
                         {
                             ulong s = GetSuffix64(br.StartIP);
                             ulong e = GetSuffix64(br.EndIP);
                             
                             if (s > curSuffix) finalItems.Add((curSuffix, 0));
                             finalItems.Add((s, br.GeoID));
                             
                             if (e < ulong.MaxValue) curSuffix = e + 1;
                             else curSuffix = 0; 
                         }
                         
                         // V14 优化：将 Suffix Block 转换为 Eytzinger 布局
                         int finalCount = finalItems.Count;
                         var eytzingerArr = new (ulong Suffix, int GeoID)[finalCount + 1];
                         int sortedIdx = 0;
                         Action<int> recurse = null;
                         recurse = (k) => {
                             if (k > finalCount) return;
                             recurse(2 * k);
                             eytzingerArr[k] = finalItems[sortedIdx++];
                             recurse(2 * k + 1);
                         };
                         recurse(1);
                         
                         bw.Write((ushort)finalCount);
                         for (int k = 1; k <= finalCount; k++)
                         {
                             var item = eytzingerArr[k];
                             bw.Write(BinaryPrimitives.ReverseEndianness(item.Suffix));
                             if (geoIdSize == 2) bw.Write((ushort)item.GeoID);
                             else { bw.Write((byte)item.GeoID); bw.Write((byte)(item.GeoID >> 8)); bw.Write((byte)(item.GeoID >> 16)); }
                         }
                         
                         blocks.Add((prefix, bms.ToArray()));
                     }
                 }
                 
                 w.Write((uint)blocks.Count);
                 long curOffset = 0;
                 foreach(var b in blocks)
                 {
                     w.Write(BinaryPrimitives.ReverseEndianness(b.Prefix));
                     w.Write((uint)curOffset);
                     curOffset += b.Data.Length;
                 }
                 foreach(var b in blocks) w.Write(b.Data);
                 return ms.ToArray();
             }
        }

        static ulong GetPrefix64(BigInteger ip)
        {
            var b = ip.ToByteArray();
            var full = new byte[16];
            Array.Copy(b, full, Math.Min(b.Length, 16));
            return BitConverter.ToUInt64(full, 8);
        }
        
        static ulong GetSuffix64(BigInteger ip)
        {
            var b = ip.ToByteArray();
            var full = new byte[16];
            Array.Copy(b, full, Math.Min(b.Length, 16));
            return BitConverter.ToUInt64(full, 0);
        }

        static List<InputRecordV4> FillGapsV4(List<InputRecordV4> list) {
            var result = new List<InputRecordV4>();
            if (list.Count == 0) return result;
            uint next = 0;
            foreach(var r in list) {
                if (r.StartIP > next) result.Add(new InputRecordV4{StartIP=next, EndIP=r.StartIP-1, GeoID=0});
                result.Add(r);
                if(r.EndIP == 0xFFFFFFFF) { next=0; break; }
                next = r.EndIP + 1;
            }
            if(next > 0) result.Add(new InputRecordV4{StartIP=next, EndIP=0xFFFFFFFF, GeoID=0});
            return result;
        }

        static List<InputRecordV4> MergeV4(List<InputRecordV4> list) {
            var merged = new List<InputRecordV4>();
            if (list.Count == 0) return merged;
            var cur = list[0];
            for(int i=1; i<list.Count; i++) {
                var next = list[i];
                if (cur.EndIP + 1 == next.StartIP && cur.GeoID == next.GeoID) cur.EndIP = next.EndIP;
                else { merged.Add(cur); cur = next; }
            }
            merged.Add(cur);
            return merged;
        }

        static List<InputRecordV6> MergeV6(List<InputRecordV6> list) {
            var merged = new List<InputRecordV6>();
            if (list.Count == 0) return merged;
            var cur = list[0];
            for(int i=1; i<list.Count; i++) {
                var next = list[i];
                if (cur.EndIP + 1 >= next.StartIP) {
                    if (cur.GeoID == next.GeoID && next.EndIP > cur.EndIP) cur.EndIP = next.EndIP;
                    else if (cur.GeoID != next.GeoID) { merged.Add(cur); cur = next; }
                } else { merged.Add(cur); cur = next; }
            }
            merged.Add(cur);
            return merged;
        }

        static List<InputRecordV6> SplitV6AtPrefix(List<InputRecordV6> list) {
             var res = new List<InputRecordV6>();
             foreach(var r in list) {
                 BigInteger s = r.StartIP;
                 BigInteger e = r.EndIP;
                 
                 BigInteger range = e - s + 1;
                 BigInteger blockCount = range >> 64;
                 if (blockCount > 10000) continue; 

                 BigInteger nextPrefixStart = ((s >> 64) + 1) << 64;
                 while (e >= nextPrefixStart) {
                     res.Add(new InputRecordV6 { StartIP = s, EndIP = nextPrefixStart - 1, GeoID = r.GeoID });
                     s = nextPrefixStart;
                     nextPrefixStart = ((s >> 64) + 1) << 64;
                 }
                 res.Add(new InputRecordV6 { StartIP = s, EndIP = e, GeoID = r.GeoID });
             }
             return res;
        }

        static void ReadRecords(string path, bool isV6, Dictionary<string,int> map, List<GeoInfoStruct> list, 
                                 DimensionPool p1, DimensionPool p2, DimensionPool p3, DimensionPool p4,
                                 DimensionPool p5, DimensionPool p6, DimensionPool p7, DimensionPool p8,
                                 List<InputRecordV4> v4List, List<InputRecordV6> v6List,
                                 Predicate<string[]> filter = null)
        {
            using (var reader = new StreamReader(path, Encoding.UTF8)) {
                string line;
                while((line = reader.ReadLine())!=null) {
                     if(string.IsNullOrWhiteSpace(line)) continue;
                     var parts = line.Split('\t');
                     if(parts.Length < 5) continue;
                     string geoStr = string.Join("\t", parts.Skip(4));
                     var gParts = geoStr.Split('|');
                     
                     if (filter != null && !filter(gParts)) continue;

                     if(!map.TryGetValue(geoStr, out int geoId)) {
                         var s = new GeoInfoStruct {
                             ContinentIdx=p1.GetOrAdd(gParts.Length>0?gParts[0]:""),
                             CountryIdx=p2.GetOrAdd(gParts.Length>1?gParts[1]:""),
                             ProvinceIdx=p3.GetOrAdd(gParts.Length>2?gParts[2]:""),
                             CityIdx=p4.GetOrAdd(gParts.Length>3?gParts[3]:""),
                             DistrictIdx=p5.GetOrAdd(gParts.Length>4?gParts[4]:""),
                             IspIdx=p6.GetOrAdd(gParts.Length>5?gParts[5]:""),
                             EnNameIdx=p8.GetOrAdd(gParts.Length>7?gParts[7]:""),
                             CodeIdx=p7.GetOrAdd(gParts.Length>8?gParts[8]:""),
                         };
                         float.TryParse(gParts.Length>9?gParts[9]:"0",out s.Lng);
                         float.TryParse(gParts.Length>10?gParts[10]:"0",out s.Lat);
                         geoId = list.Count;
                         list.Add(s);
                         map[geoStr] = geoId;
                     }
                     if (!isV6 && uint.TryParse(parts[2], out uint s4) && uint.TryParse(parts[3], out uint e4))
                         v4List.Add(new InputRecordV4{StartIP=s4, EndIP=e4, GeoID=geoId});
                     else if (isV6 && BigInteger.TryParse(parts[2], out BigInteger s6) && BigInteger.TryParse(parts[3], out BigInteger e6))
                         v6List.Add(new InputRecordV6{StartIP=s6, EndIP=e6, GeoID=geoId});
                }
            }
        }

        static void WritePool(BinaryWriter writer, List<string> list)
        {
            writer.Write((uint)list.Count);
            var offsets = new List<uint>();
            var ms = new MemoryStream();
            uint cur = 0;
            offsets.Add(0);
            foreach(var s in list) {
                var b = Encoding.UTF8.GetBytes(s);
                ms.Write(b, 0, b.Length);
                cur += (uint)b.Length;
                offsets.Add(cur);
            }
            foreach(var off in offsets) writer.Write(off);
            ms.Position = 0;
            ms.CopyTo(writer.BaseStream);
        }
    }
}
