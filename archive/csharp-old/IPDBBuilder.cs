using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Buffers.Binary;

namespace qqzengPgUI.ipdb8
{
    public class IPDBBuilder
    {
        // V9 核心结构：多维 Geo 信息
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
            public int GeoID; // 指向 GeoInfoStruct 数组的下标
        }

        // 8个维度的字典池
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
            Console.WriteLine("=== 开始构建 v9.0 多维结构化数据库 ===");
            
            var poolContinent = new DimensionPool();
            var poolCountry = new DimensionPool();
            var poolProv = new DimensionPool();
            var poolCity = new DimensionPool();
            var poolDistrict = new DimensionPool();
            var poolIsp = new DimensionPool();
            var poolCode = new DimensionPool();
            var poolEnName = new DimensionPool();

            // Geo 组合去重
            // 即: 具体的 (ContID, CountryID, ... Lat, Lng) 组合ID
            var geoStructMap = new Dictionary<string, int>();
            var geoStructList = new List<GeoInfoStruct>();
            
            // 0号 Geo 为空
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

                    // 解析 GeoString: "大洋洲|澳大利亚|...|153.025|-27.470"
                    string geoStr = parts.Length > 5 ? string.Join("\t", parts.Skip(4)) : parts[4];
                    
                    if (!geoStructMap.TryGetValue(geoStr, out int geoId))
                    {
                        var gParts = geoStr.Split('|');
                        // 预处理长度，防止越界
                        string pCont = gParts.Length > 0 ? gParts[0] : "";
                        string pCountry = gParts.Length > 1 ? gParts[1] : "";
                        string pProv = gParts.Length > 2 ? gParts[2] : "";
                        string pCity = gParts.Length > 3 ? gParts[3] : "";
                        string pDist = gParts.Length > 4 ? gParts[4] : "";
                        string pIsp = gParts.Length > 5 ? gParts[5] : "";
                        // skip 6: AreaCode ?
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
                    if (lineCount % 200000 == 0) Console.WriteLine($"已读取 {lineCount} 行...");
                }
            }
            Console.WriteLine($"读取完毕。记录数: {records.Count}");
            Console.WriteLine($"唯一 Geo 组合数: {geoStructList.Count}");
            Console.WriteLine($"维度统计: 洲({poolContinent.List.Count}) 国({poolCountry.List.Count}) 省({poolProv.List.Count}) 市({poolCity.List.Count}) 区({poolDistrict.List.Count}) ISP({poolIsp.List.Count}) Code({poolCode.List.Count}) En({poolEnName.List.Count})");

            // Split Records (Same as V8)
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

            // Index
            uint[] v4Index = new uint[65536];
            int curIdx = 0;
            for (int i = 0; i < 65536; i++)
            {
                while (curIdx < records.Count && (records[curIdx].StartIP >> 16) < i) curIdx++;
                v4Index[i] = (uint)curIdx;
            }

            // WRITE
            Console.WriteLine("正在生成 v9.0 数据库文件...");
            using (var fs = new FileStream(targetDbPath, FileMode.Create))
            using (var writer = new BinaryWriter(fs))
            {
                // Simple Header: [Sig 4][Ver 4][GeoCount 4][Offset_GeoStruct 8][Offset_Pools 8][Offset_Index 8][Offset_Data 8]
                writer.Write(Encoding.ASCII.GetBytes("QZV9"));
                writer.Write((uint)20260901);
                writer.Write((uint)geoStructList.Count);
                long posGeoStruct = fs.Position; writer.Write((ulong)0);
                long posPools = fs.Position;     writer.Write((ulong)0);
                long posIndex = fs.Position;     writer.Write((ulong)0);
                long posData = fs.Position;      writer.Write((ulong)0);
                writer.Write(new byte[64 - fs.Position]);

                // 1. Geo Struct Array
                long startGeoStruct = fs.Position;
                foreach (var g in geoStructList)
                {
                    writer.Write(g.ContinentIdx);
                    writer.Write(g.CountryIdx);
                    writer.Write(g.ProvinceIdx);
                    writer.Write(g.CityIdx);
                    writer.Write(g.DistrictIdx);
                    writer.Write(g.IspIdx);
                    writer.Write(g.CodeIdx);
                    writer.Write(g.EnNameIdx);
                    writer.Write(g.Lng);
                    writer.Write(g.Lat);
                }

                // 2. String Pools
                long startPools = fs.Position;
                WritePool(writer, poolContinent.List);
                WritePool(writer, poolCountry.List);
                WritePool(writer, poolProv.List);
                WritePool(writer, poolCity.List);
                WritePool(writer, poolDistrict.List);
                WritePool(writer, poolIsp.List);
                WritePool(writer, poolCode.List);
                WritePool(writer, poolEnName.List);

                // 3. Index
                long startIndex = fs.Position;
                foreach (var idx in v4Index) writer.Write(idx);

                // 4. Data
                long startData = fs.Position;
                foreach (var rec in records)
                {
                    writer.Write(rec.StartIP);
                    writer.Write(rec.GeoID); // 4 bytes (Int32)
                }

                // Backfill
                fs.Position = posGeoStruct; writer.Write((ulong)startGeoStruct);
                fs.Position = posPools;     writer.Write((ulong)startPools);
                fs.Position = posIndex;     writer.Write((ulong)startIndex);
                fs.Position = posData;      writer.Write((ulong)startData);
            }
            Console.WriteLine("v9.0 构建完成！");
        }

        static void WritePool(BinaryWriter writer, List<string> list)
        {
            // Format: [Count 4][OffsetTable...][Blob]
            writer.Write((uint)list.Count);
            
            var offsets = new List<uint>();
            uint cur = 0;
            offsets.Add(0);
            
            var ms = new MemoryStream();
            foreach(var s in list)
            {
                var b = Encoding.UTF8.GetBytes(s);
                ms.Write(b,0,b.Length);
                cur += (uint)b.Length;
                offsets.Add(cur);
            }
            
            foreach(var off in offsets) writer.Write(off);
            ms.Position=0;
            ms.CopyTo(writer.BaseStream);
        }
    }
}
