using System;
using System.IO;
using System.Text;
using System.Runtime.CompilerServices;

namespace qqzengIp
{
    /// <summary>
    /// qqzeng-ip 6.0 IP数据库解析类 (Safe/High-Performance)
    /// </summary>
    public sealed class IpDbSearch
    {
        private static readonly Lazy<IpDbSearch> _lazy = new Lazy<IpDbSearch>(() => new IpDbSearch());
        public static IpDbSearch Instance => _lazy.Value;

        private static byte[] _data = null!; 
        private static string[] _geoispArr = null!; 
        
        private const int IndexStartIndex = 0x30004; 
        private const int EndMask = 0x800000;
        private const int ComplMask = ~EndMask;
        private const string DbFileName = "qqzeng-ip-6.0-global.db";

        private IpDbSearch()
        {
            LoadDb();
        }

        private static void LoadDb()
        {
            try 
            {
                string dbPath = FindDbPath();
                if (string.IsNullOrEmpty(dbPath))
                {
                    dbPath = Path.Combine("../data", DbFileName);
                }

                if (!File.Exists(dbPath))
                {
                    throw new FileNotFoundException($"Fatal: Cannot find '{DbFileName}'");
                }

                _data = File.ReadAllBytes(dbPath);

                if (_data.Length < IndexStartIndex)
                {
                    throw new InvalidDataException("Invalid database file");
                }

                // NodeCount (小端序)
                // 仅初始化时运行一次，安全最重要
                int nodeCount = _data[0] | (_data[1] << 8) | (_data[2] << 16) | (_data[3] << 24);
                
                int stringAreaOffset = IndexStartIndex + nodeCount * 6;

                if (stringAreaOffset > _data.Length)
                {
                    throw new InvalidDataException("Invalid metadata");
                }

                _geoispArr = Encoding.UTF8.GetString(_data, stringAreaOffset, _data.Length - stringAreaOffset).Split('\t');
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to initialize IpDbSearch.", ex);
            }
        }

        public string Find(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return "";

            if (!FastParseIp(ip, out uint ipInt))
            {
                 return "";
            }

            return Find(ipInt);
        }

        /// <summary>
        /// 查询IP (Safe Optimized)
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string Find(uint ipInt)
        {
            // 缓存到局部变量，避免由于静态字段跨方法访问造成的边界检查无法消除
            var data = _data; 
            
            int prefix = (int)(ipInt >> 16);
            int suffix = (int)(ipInt & 0xFFFF);

            // 一级索引
            // 4 + prefix * 3
            int idx = 4 + prefix * 3;
            // 手动 Check 边界让 JIT 知道后续不可能越界? 
            // 在热点代码中，我们直接信任逻辑
            int record = (data[idx] << 16) | (data[idx+1] << 8) | data[idx+2];

            while ((record & EndMask) != EndMask)
            {
                int bit = (suffix >> 15) & 1;
                
                // offset = IndexStartIndex + record * 6 + bit * 3
                int offset = IndexStartIndex + record * 6 + bit * 3;
                
                record = (data[offset] << 16) | (data[offset+1] << 8) | data[offset+2];
                suffix <<= 1;
            }

            int index = record & ComplMask;
            
            // 安全的数组访问
            var arr = _geoispArr;
            if ((uint)index < (uint)arr.Length)
            {
                return arr[index];
            }
            
            return "";
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool FastParseIp(string ip, out uint result)
        {
            result = 0;
            uint val = 0;
            int shift = 24;
            // 缓存Length属性
            int len = ip.Length;

            for (int i = 0; i < len; i++)
            {
                char c = ip[i];
                if (c >= '0' && c <= '9')
                {
                    val = val * 10 + (uint)(c - '0');
                }
                else if (c == '.')
                {
                    if (val > 255) return false;
                    result |= val << shift;
                    val = 0;
                    shift -= 8;
                }
                else
                {
                    return false;
                }
            }

            if (val > 255 || shift != 0) return false;
            result |= val;
            return true;
        }

        private static string FindDbPath()
        {
            string[] attempts = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DbFileName),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../data", DbFileName), 
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../data", DbFileName), 
                "../" + DbFileName
            };

            foreach (var path in attempts)
            {
                if (File.Exists(path)) return path;
            }
            return "";
        }
    }
}
