using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;

namespace qqzengPgUI.ipdb8
{
    /// <summary>
    /// IPDB v10.0 Builder - 极致优化版本
    /// 
    /// 核心优化:
    /// 1. 分块索引 (Sectional Indexing): 将 IPv4 按 /16 分为 65536 个块
    /// 2. 5字节记录: 块内仅存 StartIP_Low(2B) + GeoID(3B)，利用索引隐含高16位
    /// 3. Sentinel 记录: 块末尾哨兵，消除边界检查
    /// 4. GeoID 压缩: 3字节 (支持 1600万 Geo)
    /// 
    /// 记录结构: [StartIP_Low (2B)] [GeoID (3B)] = 5 Bytes
    /// 索引结构: uint32[65537] 存储每个块的字节偏移量
    /// </summary>
    public class IPDBBuilderV10
    {
        struct GeoInfoStruct
        {
            public ushort ContinentIdx;
            public ushort CountryIdx;
            public ushort ProvinceIdx;
            public ushort CityIdx;
            public ushort DistrictIdx;
            public ushort IspIdx;
            public ushort CodeIdx;
            public ushort EnNameIdx;
            public float Lng;
            public float Lat;
        }

        struct InputRecordV4
        {
            public uint StartIP;
            public uint EndIP;
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

        public static void Build(string sourceTxtPath, string targetDbPath)
        {
            Console.WriteLine("=== 开始构建 v10.0 极致优化版数据库 ===");
            Console.WriteLine("特性: 分块索引 + 5字节记录 + GeoID(3B) + Sentinel + CRC32");

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

            var records = new List<InputRecordV4>();

            int lineCount = 0;
            using (var reader = new StreamReader(sourceTxtPath, Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split('\t');
                    if (parts.Length < 5) continue;

                    string geoStr = parts.Length > 5 ? string.Join("\t", parts.Skip(4)) : parts[4];
                    
                    if (!geoStructMap.TryGetValue(geoStr, out int geoId))
                    {
                        if (geoStructList.Count >= 0xFFFFFF)
                            throw new InvalidOperationException("GeoCount 超过 16,777,215!");

                        var gParts = geoStr.Split('|');
                        // 解析各个字段...
                        string pCont = gParts.Length > 0 ? gParts[0] : "";
                        string pCountry = gParts.Length > 1 ? gParts[1] : "";
                        string pProv = gParts.Length > 2 ? gParts[2] : "";
                        string pCity = gParts.Length > 3 ? gParts[3] : "";
                        string pDist = gParts.Length > 4 ? gParts[4] : "";
                        string pIsp = gParts.Length > 5 ? gParts[5] : "";
                        string pEnName = gParts.Length > 7 ? gParts[7] : "";
                        string pCode = gParts.Length > 8 ? gParts[8] : "";
                        float.TryParse(gParts.Length > 9 ? gParts[9] : "0", out float lng);
                        float.TryParse(gParts.Length > 10 ? gParts[10] : "0", out float lat);

                        var newStruct = new GeoInfoStruct
                        {
                            ContinentIdx = poolContinent.GetOrAdd(pCont),
                            CountryIdx = poolCountry.GetOrAdd(pCountry),
                            ProvinceIdx = poolProv.GetOrAdd(pProv),
                            CityIdx = poolCity.GetOrAdd(pCity),
                            DistrictIdx = poolDistrict.GetOrAdd(pDist),
                            IspIdx = poolIsp.GetOrAdd(pIsp),
                            EnNameIdx = poolEnName.GetOrAdd(pEnName),
                            CodeIdx = poolCode.GetOrAdd(pCode),
                            Lng = lng,
                            Lat = lat
                        };
                        geoId = geoStructList.Count;
                        geoStructList.Add(newStruct);
                        geoStructMap[geoStr] = geoId;
                    }

                    if (uint.TryParse(parts[2], out uint sInt) && uint.TryParse(parts[3], out uint eInt))
                    {
                        records.Add(new InputRecordV4 { StartIP = sInt, EndIP = eInt, GeoID = geoId });
                    }
                    lineCount++;
                    if (lineCount % 200000 == 0) Console.Write(".");
                }
            }
            Console.WriteLine($"\n读取完毕. 原始记录: {records.Count}, Geo组合: {geoStructList.Count}");

            // 1. 分裂跨 /16 的记录
            Console.WriteLine("分裂跨 /16 记录...");
            var splitRecs = new List<InputRecordV4>(records.Count * 2);
            foreach (var rec in records)
            {
                uint sHigh = rec.StartIP >> 16;
                uint eHigh = rec.EndIP >> 16;
                if (sHigh == eHigh) splitRecs.Add(rec);
                else
                {
                    splitRecs.Add(new InputRecordV4 { StartIP = rec.StartIP, EndIP = (sHigh << 16) | 0xFFFF, GeoID = rec.GeoID });
                    for (uint h = sHigh + 1; h < eHigh; h++)
                        splitRecs.Add(new InputRecordV4 { StartIP = h << 16, EndIP = (h << 16) | 0xFFFF, GeoID = rec.GeoID });
                    splitRecs.Add(new InputRecordV4 { StartIP = eHigh << 16, EndIP = rec.EndIP, GeoID = rec.GeoID });
                }
            }
            records = splitRecs;
            records.Sort((a, b) => a.StartIP.CompareTo(b.StartIP));

            // 2. 合并相邻同 GeoID 记录
            Console.WriteLine("合并相邻记录...");
            var mergedRecords = new List<InputRecordV4>(records.Count);
            if (records.Count > 0)
            {
                var current = records[0];
                for (int i = 1; i < records.Count; i++)
                {
                    var next = records[i];
                    // 必须在同一个 /16 块内才能合并
                    bool sameBlock = (current.StartIP >> 16) == (next.StartIP >> 16);
                    if (sameBlock && current.EndIP + 1 == next.StartIP && current.GeoID == next.GeoID)
                    {
                        current.EndIP = next.EndIP;
                    }
                    else
                    {
                        mergedRecords.Add(current);
                        current = next;
                    }
                }
                mergedRecords.Add(current);
            }
            records = mergedRecords;
            Console.WriteLine($"合并后记录数: {records.Count}");

            // 3. 构建数据块 (Block)
            Console.WriteLine("构建数据块...");
            uint[] index = new uint[65537]; // 65536 + 1 (Total Size)
            byte[] dataBytes;

            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                int curRecIdx = 0;
                for (int i = 0; i < 65536; i++)
                {
                    index[i] = (uint)ms.Position; // 记录当前块起始偏移

                    // 写入当前块的所有记录
                    // 块范围: [i<<16, (i<<16)|0xFFFF]
                    // 注意: records 已经分裂并排序，所以可以直接顺序读取
                    
                    uint blockEndIP = (uint)((i << 16) | 0xFFFF);
                    
                    while (curRecIdx < records.Count)
                    {
                        var rec = records[curRecIdx];
                        uint recBlock = rec.StartIP >> 16;
                        
                        if (recBlock > i) break; // 当前记录属于后续块
                        if (recBlock < i) { curRecIdx++; continue; } // 应该不会发生，除非乱序
                        
                        // 写入记录: StartIP_Low(2B) + GeoID(3B)
                        ushort lowIP = (ushort)(rec.StartIP & 0xFFFF);
                        writer.Write(lowIP); 
                        WriteUInt24(writer, (uint)rec.GeoID);

                        curRecIdx++;
                    }

                    // 写入 Sentinel (哨兵)
                    // Sentinel: StartIP = BlockEnd (0xFFFF), GeoID = Last Record's GeoID
                    // 为了简单和安全，Sentinel 的 IP 设为该块最大值 0xFFFF
                    // GeoID 实际上查询时 Sentinel 主要用于阻止越界，其 GeoID 只有在 ip >= SentinelIP 时才可能被用到。
                    // 实际上 V10 逻辑: if (next_ip > ip) return current_geoid.
                    // 如果 ip 是 0xFFFF, next_ip 是 sentinel (0xFFFF)，则不满足 >，逻辑继续?
                    // 不，Sentinel 是一条额外记录。
                    // 正确逻辑: Sentinel 的 IP 应该是 0xFFFF (或者该块结束)。
                    // 实际上，如果最后一条记录覆盖到 0xFFFF，那么下一条(Sentinel) 需要是 > 0xFFFF 吗？
                    // 5字节模式下，IP只有16位，无法表示 > 0xFFFF。
                    // 所以 Sentinel 通常作为 "该块结束标记"。
                    // 在查找时: if (val > low16) right = mid - 1; else { if (next_val > low16) found... }
                    // 如果 low16 是 0xFFFF, 只有当 val <= 0xFFFF 时才进入 else.
                    // 如果 next_val (Sentinel) 是 0xFFFF?
                    // 让我们设定 Sentinel 的 IP 为 0xFFFF，如果真实的最后一条记录也是 0xFFFF，这没问题。
                    // 关键是 Sentinel 的 GeoID 应该是什么？
                    // 应该是上一条记录的 GeoID，或者是 0? 
                    // 如果查询 192.168.255.255 (low=0xFFFF).
                    // 真实记录: [0xFE00, Geo1]. Sentinel: [0xFFFF, Geo1].
                    // 查 0xFFFF: 
                    // mid=Sentinal(0xFFFF). 0xFFFF > 0xFFFF False. Else branch.
                    // next = ?? (越界).
                    // 所以 Sentinel 只能保证 mid+1 不越界。
                    // 如果 mid 指向最后一条真实记录。 mid+1 是 Sentinel。
                    // data[mid+1] (Sentinel IP) > low16 ?
                    // 如果 low16=0xFFFF, Sentinel=0xFFFF. 0xFFFF > 0xFFFF False.
                    // 这会导致查找失败吗？
                    // 我们的查找逻辑是找 "最后一个 <= key 的元素".
                    // 稍微调整 Sentinel 策略: 
                    // 实际上 section index 模式下，通常隐含覆盖全段。
                    // 如果某段没有任何 IP? 我们写入 Sentinel (0, 0) ?
                    // 简单起见: Sentinel IP alltid 0xFFFF + ?? 不能超过16位.
                    // 也可以不依赖 Sentinel 做值比较，仅做边界 padding。
                    // 但为了 "mid+1" 安全，Sentinel 是必须的。
                    // 让我们写入: StartIP=0xFFFF, GeoID=LastGeoID.
                    // 这样即使 mid 命中最后一条，mid+1 就是 Sentinel。
                    
                    int lastGeoId = 0;
                    if (curRecIdx > 0 && (records[curRecIdx - 1].StartIP >> 16) == i)
                    {
                        lastGeoId = records[curRecIdx - 1].GeoID;
                    }
                    
                    writer.Write((ushort)0xFFFF); // Sentinel IP
                    WriteUInt24(writer, (uint)lastGeoId); // Sentinel GeoID
                }
                
                index[65536] = (uint)ms.Position; // Total Data Size
                dataBytes = ms.ToArray();
            }
            Console.WriteLine($"数据区构建完成. 大小: {dataBytes.Length / 1024.0 / 1024.0:F2} MB");

            // 4. 生成最终文件
            Console.WriteLine("写入最终文件...");
            
            byte[] geoStructBytes, poolsBytes, indexBytes;

            // Serialize GeoStruct
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                foreach (var g in geoStructList) {
                    w.Write(g.ContinentIdx); w.Write(g.CountryIdx);
                    w.Write(g.ProvinceIdx); w.Write(g.CityIdx);
                    w.Write(g.DistrictIdx); w.Write(g.IspIdx);
                    w.Write(g.CodeIdx); w.Write(g.EnNameIdx);
                    w.Write(g.Lng); w.Write(g.Lat);
                }
                geoStructBytes = ms.ToArray();
            }

            // Serialize Pools
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                WritePool(w, poolContinent.List); WritePool(w, poolCountry.List);
                WritePool(w, poolProv.List); WritePool(w, poolCity.List);
                WritePool(w, poolDistrict.List); WritePool(w, poolIsp.List);
                WritePool(w, poolCode.List); WritePool(w, poolEnName.List);
                poolsBytes = ms.ToArray();
            }

            // Serialize Index
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                foreach (var idx in index) w.Write(idx);
                indexBytes = ms.ToArray();
            }

            // CRC32
            var crc32 = new Crc32();
            crc32.Append(geoStructBytes);
            crc32.Append(poolsBytes);
            crc32.Append(indexBytes);
            crc32.Append(dataBytes);
            uint crcValue = BitConverter.ToUInt32(crc32.GetCurrentHash());

            using (var fs = new FileStream(targetDbPath, FileMode.Create))
            using (var writer = new BinaryWriter(fs))
            {
                // Header (64 bytes)
                writer.Write(Encoding.ASCII.GetBytes("QZ10"));
                writer.Write((uint)20260122);
                writer.Write((uint)geoStructList.Count);
                writer.Write(crcValue);
                
                long posOffsets = fs.Position;
                writer.Write((ulong)0); // Off_Geo
                writer.Write((ulong)0); // Off_Pools
                writer.Write((ulong)0); // Off_Index
                writer.Write((ulong)0); // Off_Data
                
                writer.Write((uint)records.Count); // Total V4 Records (approx)
                writer.Write(new byte[64 - fs.Position]); // Padding

                long startGeo = fs.Position; writer.Write(geoStructBytes);
                long startPools = fs.Position; writer.Write(poolsBytes);
                long startIndex = fs.Position; writer.Write(indexBytes);
                long startData = fs.Position; writer.Write(dataBytes);

                // Backfill offsets
                fs.Position = posOffsets;
                writer.Write((ulong)startGeo);
                writer.Write((ulong)startPools);
                writer.Write((ulong)startIndex);
                writer.Write((ulong)startData);
            }

            var info = new FileInfo(targetDbPath);
            Console.WriteLine($"v10.0 构建成功! 文件大小: {info.Length / 1024.0 / 1024.0:F2} MB");
        }

        static void WriteUInt24(BinaryWriter writer, uint value)
        {
            writer.Write((byte)(value & 0xFF));
            writer.Write((byte)((value >> 8) & 0xFF));
            writer.Write((byte)((value >> 16) & 0xFF));
        }

        static void WritePool(BinaryWriter writer, List<string> list)
        {
            writer.Write((uint)list.Count);
            var offsets = new List<uint> { 0 };
            uint cur = 0;
            var ms = new MemoryStream();
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
