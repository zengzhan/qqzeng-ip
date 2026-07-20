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
    /// IPDB V13 构建器 - 专为 V13 搜索器优化
    /// 主要改进：
    /// 1. IPv6 Key 强制使用 Big Endian 存储，支持 SIMD 直接比较。
    /// 2. 文件签名更新为 "QZ13"。
    /// 3. IPv4 数据区采用紧凑的 Eytzinger 布局。
    /// </summary>
    public class IPDBBuilderV13
    {
        // --------------------------------------------------------
        // 内部数据结构
        // --------------------------------------------------------

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

        /// <summary>
        /// 维度字符串池，用于去重和索引化
        /// </summary>
        class DimensionPool
        {
            public Dictionary<string, ushort> Map = new Dictionary<string, ushort>();
            public List<string> List = new List<string>();
            public DimensionPool() { Map[""] = 0; List.Add(""); } // 0号索引始终为空字符串
            
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

        // --------------------------------------------------------
        // 构建主流程
        // --------------------------------------------------------

        // --------------------------------------------------------
        // 构建主流程
        // --------------------------------------------------------

        /// <summary>
        /// 构建全量数据库
        /// </summary>
        public static void Build(string sourceV4Path, string sourceV6Path, string targetDbPath)
        {
            Build(sourceV4Path, sourceV6Path, targetDbPath, null);
        }

        /// <summary>
        /// 构建特定子集数据库 (如仅国内版、仅海外版)
        /// </summary>
        /// <param name="filter">过滤器：输入为地理信息数组 [大洲, 国家, 省份...], 返回 true 保留，false 丢弃</param>
        public static void Build(string sourceV4Path, string sourceV6Path, string targetDbPath, Predicate<string[]> filter)
        {
            Console.WriteLine($"=== 开始构建 V13.0 {(filter == null ? "(全量版)" : "(定制版)")} ===");

            // 1. 初始化各类池
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
            
            // GeoID 0 预留为空
            geoStructList.Add(new GeoInfoStruct());
            geoStructMap[""] = 0;

            var recsV4 = new List<InputRecordV4>();
            var recsV6 = new List<InputRecordV6>();

            // 2. 读取源数据 (带过滤)
            Console.WriteLine("正在读取源数据...");
            if (File.Exists(sourceV4Path)) ReadRecords(sourceV4Path, false, geoStructMap, geoStructList, poolContinent, poolCountry, poolProv, poolCity, poolDistrict, poolIsp, poolCode, poolEnName, recsV4, null, filter);
            if (File.Exists(sourceV6Path)) ReadRecords(sourceV6Path, true, geoStructMap, geoStructList, poolContinent, poolCountry, poolProv, poolCity, poolDistrict, poolIsp, poolCode, poolEnName, null, recsV6, filter);
            else {
                 // Dummy logic if files missing...
                 if (recsV6.Count == 0 && filter == null) {
                     // Add dummy only if fully building and no v6 data found, preventing empty crashes?
                     // Actually logic is fine without it, but let's keep dummy if needed for testing.
                     // recsV6.Add... (Skip for clean subset build)
                 }
            }

            // 3. 自适应 GeoID 大小 (如果小于 65535 使用 2字节，否则 3字节)
            int geoIdSize = geoStructList.Count <= 65535 ? 2 : 3;
            Console.WriteLine($"自适应 GeoID 大小: {geoIdSize} bytes");

            // 4. 处理 IPv4 数据
            // 排序 -> 填充空洞 -> 合并相邻同地域记录 -> 生成 Eytzinger 块
            recsV4.Sort((a,b)=>a.StartIP.CompareTo(b.StartIP));
            recsV4 = FillGapsV4(recsV4);
            recsV4 = MergeV4(recsV4);
            Console.WriteLine($"生成 IPv4 Eytzinger 块...");
            var v4Blocks = SplitAndEytzingerV4(recsV4, geoIdSize);

            // 5. 处理 IPv6 数据
            // 排序 -> 合并 -> 按 /64 切分 -> 前缀压缩存储
            Console.WriteLine($"执行 IPv6 压缩...");
            recsV6.Sort((a,b)=>a.StartIP.CompareTo(b.StartIP));
            recsV6 = MergeV6(recsV6); 
            recsV6 = SplitV6AtPrefix(recsV6);
            var v6Data = CompressIPv6(recsV6, geoIdSize);

            // 6. 写入目标文件
            using (var fs = new FileStream(targetDbPath, FileMode.Create))
            using (var writer = new BinaryWriter(fs))
            {
                 // A. Global Header (96 bytes)
                 writer.Write(Encoding.ASCII.GetBytes("QZ13"));
                 writer.Write((uint)20260128); // 版本号 date format
                 writer.Write((uint)geoStructList.Count);
                 writer.Write((uint)0); // CRC32 (预留)
                 
                 byte flags = 0; 
                 if (recsV4.Count > 0) flags |= 1;
                 if (recsV6.Count > 0) flags |= 2;
                 if (geoIdSize == 3) flags |= 4;
                 writer.Write(flags);
                 writer.Write((byte)geoIdSize);
                 writer.Write(new byte[10]); // Padding (对齐)

                 // 记录 offsets 位置以便后续回填
                 long posOffsets = fs.Position;
                 for(int i=0;i<5;i++) writer.Write((ulong)0);
                 
                 writer.Write((uint)recsV4.Count);
                 writer.Write((uint)recsV6.Count);
                 writer.Write(new byte[96 - fs.Position]); // 填充至 96 字节

                 // B. 写入结构化地理信息区 (Geo Structs)
                 long startGeo = fs.Position;
                 foreach (var g in geoStructList) {
                    writer.Write(g.ContinentIdx); writer.Write(g.CountryIdx);
                    writer.Write(g.ProvinceIdx); writer.Write(g.CityIdx);
                    writer.Write(g.DistrictIdx); writer.Write(g.IspIdx);
                    writer.Write(g.CodeIdx); writer.Write(g.EnNameIdx);
                    writer.Write(g.Lng); writer.Write(g.Lat);
                 }

                 // C. 写入字符串池 (Pools)
                 long startPools = fs.Position;
                 WritePool(writer, poolContinent.List); WritePool(writer, poolCountry.List);
                 WritePool(writer, poolProv.List); WritePool(writer, poolCity.List);
                 WritePool(writer, poolDistrict.List); WritePool(writer, poolIsp.List);
                 WritePool(writer, poolCode.List); WritePool(writer, poolEnName.List);

                 // D. 写入 IPv4 索引与数据 (Index & Data)
                 long startV4Idx = fs.Position;
                 long startV4Data = 0;
                 
                 // 先写索引占位符
                 long idxStartPos = fs.Position;
                 for(int i=0;i<65537;i++) writer.Write((uint)0);
                 
                 startV4Data = fs.Position;
                 uint[] indexArr = new uint[65537];
                 
                 // 写入每个 /16 块的 Eytzinger 数据
                 for(int i=0; i<65536; i++)
                 {
                     indexArr[i] = (uint)(fs.Position - startV4Data); // 相对偏移
                     if (v4Blocks.ContainsKey(i)) writer.Write(v4Blocks[i]);
                 }
                 indexArr[65536] = (uint)(fs.Position - startV4Data);
                 
                 // 回填索引区
                 long endDataPos = fs.Position;
                 fs.Position = idxStartPos;
                 foreach(var v in indexArr) writer.Write(v);
                 fs.Position = endDataPos;

                 // E. 写入 IPv6 数据 (Compressed)
                 long startV6Data = fs.Position;
                 if (v6Data.Length > 0) writer.Write(v6Data);

                 // F. 回填文件头部偏移量
                 fs.Position = posOffsets;
                 writer.Write((ulong)startGeo);
                 writer.Write((ulong)startPools);
                 writer.Write((ulong)startV4Idx);
                 writer.Write((ulong)startV4Data);
                 writer.Write((ulong)startV6Data);
            }

            // G. 计算并回填 CRC32
            UpdateFileCrc32(targetDbPath);
            
            Console.WriteLine("V13.0 构建完成。");
        }

        static void UpdateFileCrc32(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            // 确保 CRC 字段 (offset 12) 为 0，因为计算 CRC 时该字段本身应视为 0
            bytes[12] = 0; bytes[13] = 0; bytes[14] = 0; bytes[15] = 0;
            
            uint crc = Crc32Algorithm.Compute(bytes);
            
            // 回写 CRC 到文件
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

        // --------------------------------------------------------
        // 核心逻辑实现
        // --------------------------------------------------------

        /// <summary>
        /// 将 IPv4 记录分组并转化为 Eytzinger (BFS) 布局
        /// 布局说明：将排序数组转化为完全二叉树的层序遍历数组，使得搜索时父子节点在内存中大概率相邻。
        /// </summary>
        static Dictionary<int, byte[]> SplitAndEytzingerV4(List<InputRecordV4> recs, int geoIdSize)
        {
            var blocks = new Dictionary<int, byte[]>();
            var groups = new Dictionary<int, List<InputRecordV4>>();
            
            // 1. 按 /16 网段分组
            foreach(var r in recs)
            {
                int highStart = (int)(r.StartIP >> 16);
                int highEnd = (int)(r.EndIP >> 16);
                for (int h = highStart; h <= highEnd; h++)
                {
                    if (!groups.ContainsKey(h)) groups[h] = new List<InputRecordV4>();
                    // 裁剪范围使其完全落在当前 /16 块内
                    uint s = (h == highStart) ? r.StartIP : (uint)(h << 16);
                    uint e = (h == highEnd) ? r.EndIP : (uint)((h << 16) | 0xFFFF);
                    groups[h].Add(new InputRecordV4 { StartIP = s, EndIP = e, GeoID = r.GeoID });
                }
            }

            // 2. 对每个组生成 Eytzinger 块
            foreach(var kv in groups)
            {
                var list = kv.Value;
                int count = list.Count;
                if (count == 0) continue;

                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms))
                {
                    var arr = list.ToArray();
                    var eytzingerArr = new InputRecordV4[count + 1]; // 1-based index
                    int sortedIdx = 0;
                    
                    // 递归映射：中序遍历树结构，依次填入排序数据
                    Action<int> recurse = null;
                    recurse = (k) => {
                        if (k > count) return;
                        recurse(2*k); // 左
                        eytzingerArr[k] = arr[sortedIdx++]; // 根 (填入当前最小的有效数据)
                        recurse(2*k + 1); // 右
                    };
                    recurse(1);

                    // 写入块头 [Count]
                    w.Write((ushort)count);
                    // 写入节点 [StartIP_Low] [GeoID]
                    for(int k=1; k<=count; k++)
                    {
                        var r = eytzingerArr[k];
                        w.Write((ushort)(r.StartIP & 0xFFFF));
                        // 写入紧凑的 GeoID
                        if (geoIdSize == 2) w.Write((ushort)r.GeoID);
                        else { w.Write((byte)r.GeoID); w.Write((byte)(r.GeoID >> 8)); w.Write((byte)(r.GeoID >> 16)); }
                    }
                    blocks[kv.Key] = ms.ToArray();
                }
            }
            return blocks;
        }

        /// <summary>
        /// 压缩 IPv6 数据：两级索引 (Prefix -> Suffix Block)
        /// 关键：所有 Key (Prefix/Suffix) 必须按 Big Endian 写入，以支持 SIMD 快速比较。
        /// </summary>
        static byte[] CompressIPv6(List<InputRecordV6> recs, int geoIdSize)
        {
             if (recs.Count == 0) return new byte[0];
             
             Console.WriteLine("  [IPv6 统计] 开始分析数据分布特征...");
             long maxSuffixCount = 0;
             long totalSuffixCount = 0;
             long giantRecordCount = 0; // 跨越 > 100 个 /64 的记录
             
             // 预扫描分析 (可选，这里直接在构建中统计)
             
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
                     
                     if (blockRecs.Count > maxSuffixCount) maxSuffixCount = blockRecs.Count;
                     totalSuffixCount += blockRecs.Count;

                     using (var bms = new MemoryStream())
                     using (var bw = new BinaryWriter(bms))
                     {
                         ulong curSuffix = 0;
                         var finalItems = new List<(ulong Suffix, int GeoID)>();
                         
                         foreach(var br in blockRecs)
                         {
                             ulong s = GetSuffix64(br.StartIP);
                             ulong e = GetSuffix64(br.EndIP);
                             
                             // 统计跨度过大的情况 (仅根据后缀判断)
                             // 如果一条记录占满了整个 /64 (0 to Max), 这是一个 Full Block
                             if (s == 0 && e == ulong.MaxValue) {
                                  // 这意味着这个 /64 全覆盖。
                                  // 如果有大量连续的这种 Prefix，说明有一个巨大的父级网段被切碎了。
                             }

                             if (s > curSuffix) finalItems.Add((curSuffix, 0));
                             finalItems.Add((s, br.GeoID));
                             
                             if (e < ulong.MaxValue) curSuffix = e + 1;
                             else curSuffix = 0; 
                         }
                         
                         bw.Write((ushort)finalItems.Count);
                         foreach(var item in finalItems)
                         {
                             bw.Write(BinaryPrimitives.ReverseEndianness(item.Suffix));
                             if (geoIdSize == 2) bw.Write((ushort)item.GeoID);
                             else { bw.Write((byte)item.GeoID); bw.Write((byte)(item.GeoID >> 8)); bw.Write((byte)(item.GeoID >> 16)); }
                         }
                         
                         blocks.Add((prefix, bms.ToArray()));
                     }
                 }
                 
                 Console.WriteLine($"  [IPv6 分析] 总 Prefix 数: {blocks.Count}");
                 Console.WriteLine($"  [IPv6 分析] 平均每 Prefix 记录数: {(double)totalSuffixCount / blocks.Count:F2}");
                 Console.WriteLine($"  [IPv6 分析] 最密集 Prefix 记录数: {maxSuffixCount}");
                 
                 // 检测是否可能存在巨型网段切分导致的膨胀
                 // 如果 Blocks Count 巨大 (如 > 100万) 但记录数很少，说明非常稀疏
                 // 如果 Blocks Count 巨大 且 平均记录数接近 1，说明可能有很多单一的大网段被切分了
                 if (blocks.Count > 500000 && (double)totalSuffixCount / blocks.Count < 1.5)
                 {
                     Console.WriteLine("  [警告] 发现大量单记录 Prefix，可能存在大网段(/48, /32)被切分为大量 /64 的情况。这会显著增加文件体积。");
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

        // --------------------------------------------------------
        // 辅助方法 (Helpers)
        // --------------------------------------------------------

        static ulong GetPrefix64(BigInteger ip)
        {
            var b = ip.ToByteArray();
            var full = new byte[16];
            Array.Copy(b, full, Math.Min(b.Length, 16));
            // BigInteger.ToByteArray 是 Little Endian (低位在前)
            // IPv6 的高 64 位位于 数组的 [8..15]
            return BitConverter.ToUInt64(full, 8);
        }
        
        static ulong GetSuffix64(BigInteger ip)
        {
            var b = ip.ToByteArray();
            var full = new byte[16];
            Array.Copy(b, full, Math.Min(b.Length, 16));
            // IPv6 的低 64 位位于 数组的 [0..7]
            return BitConverter.ToUInt64(full, 0);
        }

        static List<InputRecordV4> FillGapsV4(List<InputRecordV4> list) {
            // 确保 IP 连续覆盖，无缝衔接
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
            // 合并相邻且相同 GeoID 的记录
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
                    // 处理重叠或相邻
                    if (cur.GeoID == next.GeoID && next.EndIP > cur.EndIP) cur.EndIP = next.EndIP;
                    else if (cur.GeoID != next.GeoID) { merged.Add(cur); cur = next; }
                } else { merged.Add(cur); cur = next; }
            }
            merged.Add(cur);
            return merged;
        }

        static List<InputRecordV6> SplitV6AtPrefix(List<InputRecordV6> list) {
             // 确保记录不跨越 /64 前缀边界，以便索引
             var res = new List<InputRecordV6>();
             BigInteger mask64 = (BigInteger.One << 64) - 1; 

             foreach(var r in list) {
                 BigInteger s = r.StartIP;
                 BigInteger e = r.EndIP;
                 
                 // 安全检查：如果跨度过大 (例如超过 65536 个 /64 块)，可能是配置错误的大网段
                 // 42亿次循环会导致构建器挂死。
                 BigInteger range = e - s + 1;
                 BigInteger blockCount = range >> 64;
                 if (blockCount > 10000)
                 {
                     Console.WriteLine($"[警告] 忽略过大的 IPv6 记录: {s} - {e} (跨越 {blockCount} 个 /64 网段)。这通常是错误的配置(如 /32)。");
                     continue; 
                 }

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
                      for (int gi = 0; gi < gParts.Length; gi++)
                          gParts[gi] = gParts[gi].Replace("\x7c", "|");
                      
                      // 应用过滤器: 如果记录不满足条件，则直接跳过
                      // 跳过的记录将在后续 FillGaps 中自动填充为 GeoID=0 (空洞)
                      // 这样可以极致压缩非目标区域的空间
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
            // 写入格式: [Offset Table] [Data Blob]
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
