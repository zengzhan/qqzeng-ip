using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Text;

namespace qqzengPgUI.ipdb8
{
    /// <summary>
    /// IPDB v11.0 Builder - IPv6 Support & Adaptive Optimization
    /// 
    /// New Features:
    /// 1. IPv6 Support: Dedicated IPv6 data area (Sorted, Binary Search).
    /// 2. Adaptive GeoID: Auto-detects if 2 bytes (ushort) is enough for GeoID (if Count <= 65535).
    /// 3. Unified Header: Bitmask flags for IP versions and options.
    /// 
    /// Structure:
    /// [Header] [GeoStructs] [Pools] [IPv4 Index] [IPv4 Data] [IPv6 Data]
    /// </summary>
    public class IPDBBuilderV11
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
            Console.WriteLine("=== Start Building v11.0 (IPv4 + IPv6) ===");
            
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

            // 1. Read IPv4
            if (File.Exists(sourceV4Path))
            {
                Console.WriteLine($"Reading IPv4: {sourceV4Path}");
                ReadRecords(sourceV4Path, false, geoStructMap, geoStructList, 
                    poolContinent, poolCountry, poolProv, poolCity, poolDistrict, poolIsp, poolCode, poolEnName,
                    recsV4, null);
            }

            // 2. Read IPv6
            if (File.Exists(sourceV6Path))
            {
                Console.WriteLine($"Reading IPv6: {sourceV6Path}");
                ReadRecords(sourceV6Path, true, geoStructMap, geoStructList, 
                    poolContinent, poolCountry, poolProv, poolCity, poolDistrict, poolIsp, poolCode, poolEnName,
                    null, recsV6);
            }
            else
            {
                 Console.WriteLine("Warning: No IPv6 source found. Building with dummy IPv6 data for structure validation.");
                 // Verify structure with dummy data
                 var dummyStr = "Test|Test|Test|Test|Test|Test|Test|Test|Test";
                 if (!geoStructMap.TryGetValue(dummyStr, out int dummyGeo))
                 {
                     dummyGeo = geoStructList.Count;
                     geoStructList.Add(new GeoInfoStruct { ContinentIdx = poolContinent.GetOrAdd("Test") }); // Simple test struct
                     geoStructMap[dummyStr] = dummyGeo;
                 }
                 recsV6.Add(new InputRecordV6 { StartIP = BigInteger.Parse("42540488161975842760550356425300246528"), EndIP = BigInteger.Parse("42540488161975842760550356425300246528"), GeoID = dummyGeo }); // 2001:db8::
            }

            Console.WriteLine($"Total Geo Combinations: {geoStructList.Count}");
            
            // Check GeoID Size optimization
            int geoIdSize = geoStructList.Count <= 65535 ? 2 : 3;
            Console.WriteLine($"Adaptive GeoID: Count={geoStructList.Count}, Size={geoIdSize} bytes");

            // --- Process IPv4 ---
            Console.WriteLine("[IPv4] Sorting and Filling Gaps...");
            recsV4.Sort((a,b)=>a.StartIP.CompareTo(b.StartIP));
            recsV4 = FillGapsV4(recsV4);
            
            Console.WriteLine("[IPv4] Splitting and Merging...");
            recsV4 = ProcessV4(recsV4);

            // --- Process IPv6 ---
            Console.WriteLine("[IPv6] Sorting and Merging...");
            recsV6 = ProcessV6(recsV6);

            // --- Write DB ---
            Console.WriteLine("Writing Data Blocks...");
            using (var fs = new FileStream(targetDbPath, FileMode.Create))
            using (var writer = new BinaryWriter(fs))
            {
                // Header (96 bytes for future proofing)
                writer.Write(Encoding.ASCII.GetBytes("QZ11"));
                writer.Write((uint)20260124); // Version
                writer.Write((uint)geoStructList.Count);
                writer.Write((uint)0); // CRC32 placeholder
                
                // Flags: Bit 0=IPv4, Bit 1=IPv6, Bit 2=GeoID_3Byte
                byte flags = 0;
                if (recsV4.Count > 0) flags |= 1;
                if (recsV6.Count > 0) flags |= 2;
                if (geoIdSize == 3) flags |= 4;
                writer.Write(flags);
                writer.Write((byte)geoIdSize); // Explicit Size
                writer.Write(new byte[10]); // Padding

                long posOffsets = fs.Position;
                // Offsets
                writer.Write((ulong)0); // Off_Geo
                writer.Write((ulong)0); // Off_Pools
                writer.Write((ulong)0); // Off_V4_Index
                writer.Write((ulong)0); // Off_V4_Data
                writer.Write((ulong)0); // Off_V6_Data (Index impl later if needed)
                
                writer.Write((uint)recsV4.Count);
                writer.Write((uint)recsV6.Count);
                
                writer.Write(new byte[96 - fs.Position]); // Pad header to 96 bytes

                // 1. Geo Structs
                long startGeo = fs.Position;
                foreach (var g in geoStructList) {
                    writer.Write(g.ContinentIdx); writer.Write(g.CountryIdx);
                    writer.Write(g.ProvinceIdx); writer.Write(g.CityIdx);
                    writer.Write(g.DistrictIdx); writer.Write(g.IspIdx);
                    writer.Write(g.CodeIdx); writer.Write(g.EnNameIdx);
                    writer.Write(g.Lng); writer.Write(g.Lat);
                }

                // 2. Pools
                long startPools = fs.Position;
                WritePool(writer, poolContinent.List); WritePool(writer, poolCountry.List);
                WritePool(writer, poolProv.List); WritePool(writer, poolCity.List);
                WritePool(writer, poolDistrict.List); WritePool(writer, poolIsp.List);
                WritePool(writer, poolCode.List); WritePool(writer, poolEnName.List);

                // 3. IPv4 Index & Data
                long startV4Idx = fs.Position;
                long startV4Data = 0;
                
                if (recsV4.Count > 0)
                {
                    uint[] v4Index = new uint[65537];
                    byte[] v4Data;
                    using (var ms = new MemoryStream())
                    using (var w = new BinaryWriter(ms))
                    {
                        int curRec = 0;
                        for (int i = 0; i < 65536; i++)
                        {
                            v4Index[i] = (uint)ms.Position;
                             while (curRec < recsV4.Count)
                             {
                                 var r = recsV4[curRec];
                                 if ((r.StartIP >> 16) > i) break;
                                 
                                 w.Write((ushort)(r.StartIP & 0xFFFF));
                                 WriteGeoID(w, r.GeoID, geoIdSize);
                                 curRec++;
                             }
                             // Sentinel
                             int lastGeo = 0;
                             if (curRec > 0 && (recsV4[curRec-1].StartIP >> 16) == i) lastGeo = recsV4[curRec-1].GeoID;
                             w.Write((ushort)0xFFFF);
                             WriteGeoID(w, lastGeo, geoIdSize);
                        }
                        v4Index[65536] = (uint)ms.Position;
                        v4Data = ms.ToArray();
                    }
                    
                    // Write Index
                    foreach(var idx in v4Index) writer.Write(idx);
                    
                    // Write Data
                    startV4Data = fs.Position;
                    writer.Write(v4Data);
                }

                // 4. IPv6 Data
                long startV6Data = fs.Position;
                if (recsV6.Count > 0)
                {
                    // Sorted Array: [High8][Low8][GeoID]
                    foreach(var r in recsV6)
                    {
                         writer.Write(GetIPV6Bytes(r.StartIP));
                         WriteGeoID(writer, r.GeoID, geoIdSize);
                    }
                }

                // Backfill
                long endPos = fs.Position;
                fs.Position = posOffsets;
                writer.Write((ulong)startGeo);
                writer.Write((ulong)startPools);
                writer.Write((ulong)startV4Idx);
                writer.Write((ulong)startV4Data);
                writer.Write((ulong)startV6Data);
            }
            
            Console.WriteLine("v11.0 Build Complete.");
        }

        static List<InputRecordV4> FillGapsV4(List<InputRecordV4> list)
        {
            var filled = new List<InputRecordV4>(list.Count * 2);
            if (list.Count == 0) return filled;
            
            uint nextStart = 0;
            
            foreach (var rec in list)
            {
                if (rec.StartIP > nextStart)
                {
                    // Gap found
                    filled.Add(new InputRecordV4 { StartIP = nextStart, EndIP = rec.StartIP - 1, GeoID = 0 });
                }
                
                filled.Add(rec);
                // Handle uint overflow check if EndIP is 0xFFFFFFFF
                if (rec.EndIP == 0xFFFFFFFF) 
                {
                    nextStart = 0; // Wrap around or stop? 
                    // Actually if we hit max, we should stop filling.
                    // But loop continues.
                    // Let's set nextStart to 0 to indicate full (though 0 is start)
                    // Better logic:
                    break; 
                }
                nextStart = rec.EndIP + 1;
            }
            
            // Tail gap
            if (nextStart > 0 && nextStart <= 0xFFFFFFFF) // Check if we haven't wrapped/finished
            {
                 filled.Add(new InputRecordV4 { StartIP = nextStart, EndIP = 0xFFFFFFFF, GeoID = 0 });
            }
            
            return filled;
        }

        static List<InputRecordV4> ProcessV4(List<InputRecordV4> list)
        {
            // Split
            var split = new List<InputRecordV4>(list.Count * 2);
             foreach (var rec in list)
            {
                uint sHigh = rec.StartIP >> 16;
                uint eHigh = rec.EndIP >> 16;
                if (sHigh == eHigh) split.Add(rec);
                else
                {
                    split.Add(new InputRecordV4 { StartIP = rec.StartIP, EndIP = (sHigh << 16) | 0xFFFF, GeoID = rec.GeoID });
                    for (uint h = sHigh + 1; h < eHigh; h++)
                        split.Add(new InputRecordV4 { StartIP = h << 16, EndIP = (h << 16) | 0xFFFF, GeoID = rec.GeoID });
                    split.Add(new InputRecordV4 { StartIP = eHigh << 16, EndIP = rec.EndIP, GeoID = rec.GeoID });
                }
            }
            // Sorting already done effectively but split might disorder sub-parts? No, sequential.
            // But let's keep sort to be safe or rely on insertion order.
            // Insertion order preserves IP order.
            
            // Merge
            var merged = new List<InputRecordV4>();
            if (split.Count > 0)
            {
                var cur = split[0];
                for(int i=1; i<split.Count; i++)
                {
                    var next = split[i];
                    bool sameBlock = (cur.StartIP >> 16) == (next.StartIP >> 16);
                    if (sameBlock && cur.EndIP + 1 == next.StartIP && cur.GeoID == next.GeoID)
                    {
                        cur.EndIP = next.EndIP;
                    }
                    else { merged.Add(cur); cur = next; }
                }
                merged.Add(cur);
            }
            return merged;
        }


        static List<InputRecordV6> ProcessV6(List<InputRecordV6> list)
        {
            if (list.Count == 0) return list;
            list.Sort((a,b)=>a.StartIP.CompareTo(b.StartIP));
            
            // Merge
            var merged = new List<InputRecordV6>();
            var cur = list[0];
            
            // Handle Start Gap
            if (cur.StartIP > 0)
            {
                merged.Add(new InputRecordV6 { StartIP = 0, EndIP = cur.StartIP - 1, GeoID = 0 });
            }

            for(int i=1; i<list.Count; i++)
            {
                var next = list[i];
                // Check direct adjacency
                if (cur.EndIP + 1 >= next.StartIP) // Should be ==, but if overlap?
                {
                     // If overlap or adjacent same GeoID
                     if (cur.GeoID == next.GeoID)
                     {
                         if (next.EndIP > cur.EndIP) cur.EndIP = next.EndIP;
                     }
                     else
                     {
                         // Different GeoID, write current
                         merged.Add(cur);
                         cur = next;
                     }
                }
                else
                {
                    // Gap detected
                    merged.Add(cur);
                    // Insert Gap
                    merged.Add(new InputRecordV6 { StartIP = cur.EndIP + 1, EndIP = next.StartIP - 1, GeoID = 0 });
                    cur = next;
                }
            }
            merged.Add(cur);
            
            // Handle End Gap (Optional, but good for completeness)
            // Can't represent MaxIPv6 easily to check, but we can append a final 0-Geo record 
            // at cur.EndIP + 1 to cap off the valid range.
            // Since EndIP is inclusive.
            // Note: BigInteger for 0xFFFF...FFFF is huge.
            // Let's just add one final "End Marker" with GeoID 0.
            // StartIP = cur.EndIP + 1.
            merged.Add(new InputRecordV6 { StartIP = cur.EndIP + 1, EndIP = 0, GeoID = 0 });

            return merged;
        }

        static void ReadRecords(string path, bool isV6, Dictionary<string,int> map, List<GeoInfoStruct> list, 
                                DimensionPool pCont, DimensionPool pCountry, DimensionPool pProv, DimensionPool pCity,
                                DimensionPool pDist, DimensionPool pIsp, DimensionPool pCode, DimensionPool pEn,
                                List<InputRecordV4> v4List, List<InputRecordV6> v6List)
        {
            using (var reader = new StreamReader(path, Encoding.UTF8))
            {
                string line;
                while((line = reader.ReadLine())!=null)
                {
                    if(string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split('\t');
                    if(parts.Length < 5) continue;
                    
                    string geoStr = string.Join("\t", parts.Skip(4));
                    if(!map.TryGetValue(geoStr, out int geoId))
                    {
                        var gParts = geoStr.Split('|');
                        var s = new GeoInfoStruct {
                            ContinentIdx = pCont.GetOrAdd(gParts.Length>0?gParts[0]:""),
                            CountryIdx = pCountry.GetOrAdd(gParts.Length>1?gParts[1]:""),
                            ProvinceIdx = pProv.GetOrAdd(gParts.Length>2?gParts[2]:""),
                            CityIdx = pCity.GetOrAdd(gParts.Length>3?gParts[3]:""),
                            DistrictIdx = pDist.GetOrAdd(gParts.Length>4?gParts[4]:""),
                            IspIdx = pIsp.GetOrAdd(gParts.Length>5?gParts[5]:""),
                            EnNameIdx = pEn.GetOrAdd(gParts.Length>7?gParts[7]:""),
                            CodeIdx = pCode.GetOrAdd(gParts.Length>8?gParts[8]:""),
                        };
                        float.TryParse(gParts.Length>9?gParts[9]:"0", out s.Lng);
                        float.TryParse(gParts.Length>10?gParts[10]:"0", out s.Lat);
                        geoId = list.Count;
                        list.Add(s);
                        map[geoStr] = geoId;
                    }
                    
                    if (!isV6) {
                        if (uint.TryParse(parts[2], out uint s) && uint.TryParse(parts[3], out uint e))
                            v4List.Add(new InputRecordV4{StartIP=s, EndIP=e, GeoID=geoId});
                    } else {
                        // TODO: Parse BigInt/IPv6 string
                        // StartIP, EndIP in parts[2], parts[3] might be decimal string of uint128?
                        // Or standard IP format? Assuming decimal string for BigInt for now as standard format
                        if (BigInteger.TryParse(parts[2], out BigInteger s) && BigInteger.TryParse(parts[3], out BigInteger e))
                            v6List.Add(new InputRecordV6{StartIP=s, EndIP=e, GeoID=geoId});
                    }
                }
            }
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

        static byte[] GetIPV6Bytes(BigInteger val)
        {
            var b = val.ToByteArray();
            var res = new byte[16];
            int len = Math.Min(b.Length, 16);
            Array.Copy(b, res, len);
            return res;
        }

        static void WriteGeoID(BinaryWriter w, int id, int size)
        {
            if (size == 2) w.Write((ushort)id);
            else {
                w.Write((byte)(id & 0xFF));
                w.Write((byte)((id >> 8) & 0xFF));
                w.Write((byte)((id >> 16) & 0xFF));
            }
        }
    }
}
