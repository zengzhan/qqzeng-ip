using System;
using System.IO;
using System.Text;

namespace qqzengIp
{
    /// <summary>
    /// qqzeng-ip 6.0 (.db) IP 数据库解析类 (Safe/High-Performance, 零依赖)
    ///
    /// 文件结构 (UTF-8):
    ///   文件头    4 字节: 节点数量 nodeCount (uint32 小端)
    ///   一级索引  65536 条 x 3 字节 (大端 int24): IPv4 前 16 位 -> 节点号或叶子
    ///   节点区    nodeCount x 6 字节 (每节点 2 个子指针, 各 3 字节大端 int24)
    ///   字符串区  其余部分, '\t' 分隔的 UTF-8 结果串
    ///   int24 最高位 (0x800000) 置位 = 叶子, 低 22 位为字符串下标;
    ///   否则为节点区跳转下标。查询消耗 IP 后 16 位 (MSB first)。
    ///
    /// 兼容旧版 API:
    ///   IpDbSearch.Instance.Find("1.2.3.4") / Find(uint ipInt)
    /// 新增:
    ///   new IpDbSearch(dbPath) 多实例 / FromPath(path) / FromBytes(bytes)
    ///
    /// 相比历史版本的修复与优化:
    ///   1. FastParseIp 严格化: 旧版接受 "1..2.3"/"1.2.3." 等残缺 IP;
    ///      现按统一严格规则校验 (每段 1~3 位且 <=255, 恰好 4 段)。
    ///   2. 加载期完整校验 (fail-closed): 尺寸/头字段/全部一级索引与节点跳转
    ///      合法性/叶子下标范围, 非法文件抛 InvalidOperationException。
    ///   3. 逐查询 int24 拼装改为 int[] 预解析 (一级索引 + 节点区);
    ///      静态字段改为实例字段 (旧版实例方法读写静态字段, 多实例互相污染)。
    ///   4. 路径探测补充仓库 dada/ 目录。
    /// </summary>
    public sealed class IpDbSearch
    {
        private const int IndexStart = 0x30004;
        private const int FirstCount = 65536;
        private const int EndMask = 0x800000;
        private const int ComplMask = 0x7FFFFF;
        private const string DbFileName = "qqzeng-ip-6.0-global.db";

        private static readonly Lazy<IpDbSearch> _lazy = new Lazy<IpDbSearch>(CreateDefault);

        private readonly int[] _first;    // 一级索引 (预解析 int24 值)
        private readonly int[] _nodes;    // nodeCount*2 个子指针 (预解析)
        private readonly string[] _geoispArr;
        private readonly int _nodeCount;

        /// <summary>单例 (自动探测默认路径, 与旧版行为兼容)。</summary>
        public static IpDbSearch Instance => _lazy.Value;

        private static IpDbSearch CreateDefault()
        {
            string dbPath = FindDbPath();
            if (string.IsNullOrEmpty(dbPath))
            {
                throw new InvalidOperationException($"Fatal: Cannot find '{DbFileName}'");
            }
            return new IpDbSearch(dbPath);
        }

        /// <summary>新增: 指定路径构造 (多实例)。</summary>
        public IpDbSearch(string dbPath)
        {
            if (string.IsNullOrEmpty(dbPath)) throw new ArgumentNullException(nameof(dbPath));
            byte[] data;
            try
            {
                data = File.ReadAllBytes(dbPath);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to read '{dbPath}'", ex);
            }
            int[] first;
            int[] nodes;
            string[] geoispArr;
            int nodeCount;
            Parse(data, out first, out nodes, out geoispArr, out nodeCount);
            _first = first;
            _nodes = nodes;
            _geoispArr = geoispArr;
            _nodeCount = nodeCount;
        }

        private IpDbSearch(byte[] data)
        {
            int[] first;
            int[] nodes;
            string[] geoispArr;
            int nodeCount;
            Parse(data, out first, out nodes, out geoispArr, out nodeCount);
            _first = first;
            _nodes = nodes;
            _geoispArr = geoispArr;
            _nodeCount = nodeCount;
        }

        /// <summary>新增: 指定路径构造 (显式工厂)。</summary>
        public static IpDbSearch FromPath(string dbPath) => new IpDbSearch(dbPath);

        /// <summary>新增: 从内存字节流构造。</summary>
        public static IpDbSearch FromBytes(byte[] data) => new IpDbSearch(data);

        /// <summary>解析与完整校验 (fail-closed)。</summary>
        private static void Parse(byte[] data, out int[] first, out int[] nodes,
                                  out string[] geoispArr, out int nodeCount)
        {
            first = null;
            nodes = null;
            geoispArr = null;
            nodeCount = 0;

            if (data == null || data.Length < IndexStart)
            {
                throw new InvalidOperationException("Invalid database file size");
            }
            nodeCount = data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24);
            if (nodeCount <= 0)
            {
                throw new InvalidOperationException("Invalid node count");
            }
            long nodeAreaEnd = (long)IndexStart + (long)nodeCount * 6L;
            if (nodeAreaEnd > (long)data.Length)
            {
                throw new InvalidOperationException("Node area exceeds file size");
            }
            int sa = (int)nodeAreaEnd;
            int stringCount = 1;
            for (int i = sa; i < data.Length; i++)
            {
                if (data[i] == '\t') stringCount++;
            }

            first = new int[FirstCount];
            for (int i = 0; i < FirstCount; i++)
            {
                int o = 4 + i * 3;
                int v = (data[o] << 16) | (data[o + 1] << 8) | data[o + 2];
                if (!EntryOK(v, nodeCount, stringCount))
                {
                    throw new InvalidOperationException("Bad first-level index entry");
                }
                first[i] = v;
            }

            nodes = new int[nodeCount * 2];
            for (int n = 0, o = IndexStart; n < nodeCount; n++, o += 6)
            {
                int a = (data[o] << 16) | (data[o + 1] << 8) | data[o + 2];
                int b = (data[o + 3] << 16) | (data[o + 4] << 8) | data[o + 5];
                if (!EntryOK(a, nodeCount, stringCount) || !EntryOK(b, nodeCount, stringCount))
                {
                    throw new InvalidOperationException("Bad node jump");
                }
                nodes[n * 2] = a;
                nodes[n * 2 + 1] = b;
            }

            geoispArr = Encoding.UTF8.GetString(data, sa, data.Length - sa).Split('\t');
        }

        private static bool EntryOK(int v, int nodeCount, int stringCount)
        {
            if ((v & EndMask) != 0)
            {
                return (v & ComplMask) < stringCount;
            }
            return v >= 0 && v < nodeCount;
        }

        /// <summary>查询 IP 字符串, 未命中/非法返回 ""。</summary>
        public string Find(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return "";
            if (!FastParseIp(ip, out uint ipInt)) return "";
            return Find(ipInt);
        }

        /// <summary>整数查询 (uint32)。</summary>
        public string Find(uint ipInt)
        {
            int record = _first[ipInt >> 16];
            int suffix = (int)(ipInt & 0xFFFF);
            int hops = 0;
            // 后缀 16 位 -> 最多 16 跳; 超限说明文件非法 (fail-closed)
            while ((record & EndMask) == 0)
            {
                if (hops >= 16) return "";
                hops++;
                record = _nodes[record * 2 + ((suffix >> 15) & 1)];
                suffix <<= 1;
            }
            int index = record & ComplMask;
            var arr = _geoispArr;
            if ((uint)index < (uint)arr.Length)
            {
                return arr[index];
            }
            return "";
        }

        public int NodeCount => _nodeCount;

        public int StringCount => _geoispArr.Length;

        /// <summary>严格 IPv4 解析: 每段 1~3 位数字且 <=255, 恰好 4 段。</summary>
        public static bool FastParseIp(string ip, out uint result)
        {
            result = 0;
            uint val = 0;
            int shift = 24;
            int digits = 0;
            int len = ip.Length;

            for (int i = 0; i < len; i++)
            {
                char c = ip[i];
                if (c >= '0' && c <= '9')
                {
                    val = val * 10 + (uint)(c - '0');
                    if (val > 255) return false;
                    digits++;
                    if (digits > 3) return false; // 拒绝 "0008.8.8.8" 这类超长前导零
                }
                else if (c == '.')
                {
                    if (digits == 0 || shift == 0) return false; // 空段或第 5 段
                    result |= val << shift;
                    val = 0;
                    digits = 0;
                    shift -= 8;
                }
                else
                {
                    return false;
                }
            }
            if (digits == 0 || shift != 0) return false;
            result |= val;
            return true;
        }

        /// <summary>探测默认数据库路径 (当前目录/AppDomain 目录/data/dada)。</summary>
        public static string FindDbPath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string cwd = Directory.GetCurrentDirectory();
            string[] roots = { cwd, baseDir };
            foreach (string root in roots)
            {
                string[] attempts =
                {
                    Path.Combine(root, DbFileName),
                    Path.Combine(root, "data", DbFileName),
                    Path.Combine(root, "../data", DbFileName),
                    Path.Combine(root, "../../data", DbFileName),
                    Path.Combine(root, "dada/全球旗舰版-202610", DbFileName),
                    Path.Combine(root, "../dada/全球旗舰版-202610", DbFileName),
                    Path.Combine(root, "../../dada/全球旗舰版-202610", DbFileName),
                    Path.Combine(root, "../../../dada/全球旗舰版-202610", DbFileName),
                };
                foreach (string p in attempts)
                {
                    if (File.Exists(p)) return p;
                }
            }
            return null;
        }
    }
}
