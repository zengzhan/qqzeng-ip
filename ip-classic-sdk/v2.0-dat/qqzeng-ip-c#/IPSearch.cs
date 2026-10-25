using System;
using System.IO;

namespace qqzeng_ip_dat
{
    /// <summary>
    /// qqzeng-ip 2.0 (.dat) 查询类(兼容旧版 new IPSearch(path) + Query 用法)。
    /// 内部复用不可变 DatEngine,线程安全。
    /// </summary>
    public class IPSearch
    {
        private readonly DatEngine _engine;

        /// <summary>兼容旧版: 指定 .dat 文件路径。</summary>
        public IPSearch(string dataPath)
        {
            if (string.IsNullOrEmpty(dataPath) || !File.Exists(dataPath))
                throw new FileNotFoundException("cannot find database: " + dataPath);
            _engine = new DatEngine(File.ReadAllBytes(dataPath));
        }

        /// <summary>新增: 从内存字节流加载。</summary>
        public IPSearch(byte[] bytes)
        {
            _engine = new DatEngine(bytes);
        }

        /// <summary>新增: 默认路径自动探测。</summary>
        public IPSearch()
        {
            _engine = new DatEngine(File.ReadAllBytes(FindDbPath()));
        }

        private static string FindDbPath()
        {
            string[] names = { "qqzeng-ip-utf8.dat", "qqzeng-ip.dat", "qqzeng-ip-china-utf8.dat", "qqzeng-ip-global-utf8.dat" };
            string[] prefixes = { "", "dada/国内精华版-202610/", "dada/全球旗舰版-202610/",
                "../dada/国内精华版-202610/", "../dada/全球旗舰版-202610/",
                "../../dada/国内精华版-202610/", "../../dada/全球旗舰版-202610/" };
            foreach (string p in prefixes)
                foreach (string n in names)
                {
                    string path = p + n;
                    if (File.Exists(path)) return path;
                }
            throw new FileNotFoundException("cannot find any qqzeng-ip .dat");
        }

        /// <summary>IP 查询: 未命中/非法 IP 返回 ""。</summary>
        public string Query(string ip)
        {
            uint v;
            if (!DatEngine.FastParseIp(ip, out v)) return "";
            return _engine.Find(v);
        }

        /// <summary>整数查询。</summary>
        public string QueryUint(uint ipInt)
        {
            return _engine.Find(ipInt);
        }

        /// <summary>兼容旧版静态方法: 严格 IPv4 解析(旧版八进制/十六进制容错已移除)。</summary>
        public static uint IpToInt(string ip, out uint prefix)
        {
            uint v;
            if (!DatEngine.FastParseIp(ip, out v))
                throw new FormatException("invalid ipv4: " + ip);
            prefix = (v >> 24) & 0xFFu;
            return v;
        }

        /// <summary>兼容旧版: 整数转 IP 字符串。</summary>
        public static string IntToIP(uint ip)
        {
            return string.Format("{0}.{1}.{2}.{3}",
                (ip >> 24) & 0xFFu, (ip >> 16) & 0xFFu, (ip >> 8) & 0xFFu, ip & 0xFFu);
        }
    }

    /// <summary>PrefixIndex 兼容占位(旧版公开类,现已内置为数组结构)。</summary>
    public class PrefixIndex
    {
        public uint prefix { get; set; }
        public long start_index { get; set; }
        public long end_index { get; set; }
    }
}
