using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

/*

    高性能IP数据库格式详解 qqzeng-ip.dat 2.0版

    编码:UTF8  字节序:Little-Endian
    返回多个字段信息(如:亚洲|中国|香港|九龙|油尖旺|新世界电讯|810200|Hong Kong|HK|114.17495|22.327115)

    ------------------------ 文件结构 2.0 -------------------------
    文件头    16字节(4-4-4-4)
    [索引区第一条流位置][索引区最后一条流位置][前缀区第一条的流位置][前缀区最后一条的流位置]

    内容区    长度无限制
    [地区信息][地区信息]……唯一不重复,按 (offset,len) 被索引区引用

    索引区    12字节(4-4-3-1)
    [起始IP][结束IP][地区流位置][流长度]

    前缀区    9字节(1-4-4)
    [0-255][索引区start索引][索引区end索引]
    ------------------------ 文件结构 ---------------------------

    创建: qqzeng-ip 于 2015-08-01, 优化: 2018-04-08, 重构: 2026-10

    本次重构的修复与优化:
      1. 前缀表 HashMap -> 固定 256 长度数组,缺失前缀以 -1 标记
         (修复 "0.0.0.0/8 前缀 low=high=0 被 high==0 误判未命中" 的 bug)。
      2. IpToInt 奇怪的八进制/十六进制解析重写为严格 IPv4 校验
         (拒绝 256+ / 空段 / 非数字 / IPv6 / 前后缀垃圾)。
      3. ipmap 二维数组改为平坦 uint[] 数组;字符串池去重常驻
         (全球版 137 万条记录仅 16 万个唯一字符串),查询零解码零分配。
      4. FileSystemWatcher 热加载竞态修复: 重载构造全新的不可变快照,
         通过原子引用替换发布,并发查询只会看到完整的新旧快照之一。
      5. 二分查找防溢出 (l+h)>>>1;加载时完整校验文件结构,fail-closed。
*/

namespace qqzeng_ip_dat
{
    /// <summary>
    /// qqzeng-ip 2.0 (.dat) 解析引擎(不可变快照,线程安全,可被热替换)。
    /// </summary>
    internal sealed class DatEngine
    {
        private readonly uint[] _starts;
        private readonly uint[] _ends;
        private readonly uint[] _strIdx;
        private readonly string[] _strings;
        private readonly int[] _prefLo = new int[256];
        private readonly int[] _prefHi = new int[256];

        public int RecordCount { get { return _starts == null ? 0 : _starts.Length; } }
        public int StringCount { get { return _strings == null ? 0 : _strings.Length; } }

        public DatEngine(byte[] data)
        {
            if (data == null || data.Length < 16)
                throw new InvalidDataException("database too small");
            for (int i = 0; i < 256; i++)
            {
                _prefLo[i] = -1;
                _prefHi[i] = -1;
            }

            uint firstIndex = ReadLE32(data, 0);
            uint lastIndex = ReadLE32(data, 4);
            uint prefStart = ReadLE32(data, 8);
            uint prefEnd = ReadLE32(data, 12);

            if (firstIndex < 16 || lastIndex < firstIndex || (lastIndex - firstIndex) % 12u != 0u
                || prefEnd < prefStart || prefStart < 16 || (prefEnd - prefStart) % 9u != 0u)
                throw new InvalidDataException("bad header");

            uint recordCount = (lastIndex - firstIndex) / 12u + 1u;
            uint prefixCount = (prefEnd - prefStart) / 9u + 1u;
            if (firstIndex + 12u * recordCount > (ulong)data.Length || prefStart + 9u * prefixCount > (ulong)data.Length)
                throw new InvalidDataException("header exceeds file size");

            for (uint k = 0; k < prefixCount; k++)
            {
                int o = (int)(prefStart + k * 9u);
                int n = data[o];
                uint lo = ReadLE32(data, o + 1);
                uint hi = ReadLE32(data, o + 5);
                if (lo > hi || hi >= recordCount)
                    throw new InvalidDataException("bad prefix range");
                _prefLo[n] = (int)lo;
                _prefHi[n] = (int)hi;
            }

            _starts = new uint[recordCount];
            _ends = new uint[recordCount];
            _strIdx = new uint[recordCount];

            /* 字符串池去重: key = offset<<8 | len (offset<2^24, len<2^8) */
            var seen = new Dictionary<int, int>((int)recordCount / 2 + 16);
            var strs = new string[recordCount];
            int uniq = 0;
            for (uint i = 0; i < recordCount; i++)
            {
                int o = (int)(firstIndex + i * 12u);
                uint off = (uint)(data[o + 8] | (data[o + 9] << 8) | (data[o + 10] << 16));
                int len = data[o + 11];
                if (off + (ulong)(uint)len > (ulong)data.Length)
                    throw new InvalidDataException("string region out of range");
                _starts[i] = ReadLE32(data, o);
                _ends[i] = ReadLE32(data, o + 4);
                int key = (int)((off << 8) | (uint)len);
                int id;
                if (!seen.TryGetValue(key, out id))
                {
                    id = uniq++;
                    strs[id] = Encoding.UTF8.GetString(data, (int)off, len);
                    seen[key] = id;
                }
                _strIdx[i] = (uint)id;
            }
            _strings = new string[uniq];
            Array.Copy(strs, _strings, uniq);
        }

        private static uint ReadLE32(byte[] b, int o)
        {
            return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        }

        /// <summary>严格 IPv4 解析(每段 1~3 位数字且 ≤255,恰好 4 段)。</summary>
        public static bool FastParseIp(string ip, out uint result)
        {
            result = 0;
            if (ip == null) return false;
            uint val = 0, res = 0;
            int shift = 24, digits = 0;
            for (int i = 0; i < ip.Length; i++)
            {
                char c = ip[i];
                if (c >= '0' && c <= '9')
                {
                    val = val * 10u + (uint)(c - '0');
                    if (val > 255u) return false;
                    digits++;
                    if (digits > 3) return false; /* 拒绝 "0008.8.8.8" 这类超长前导零 */
                }
                else if (c == '.')
                {
                    if (digits == 0 || shift == 0) return false; /* 空段或第 5 段 */
                    res |= val << shift;
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
            result = res | val;
            return true;
        }

        public string Find(uint val)
        {
            int p = (int)((val >> 24) & 0xFFu);
            int lo = _prefLo[p];
            if (lo < 0) return ""; /* 前缀不存在(不能以 high==0 判未命中) */
            int hi = _prefHi[p];
            uint idx;
            if (lo == hi)
            {
                idx = (uint)lo;
            }
            else
            {
                /* 二分逼近: 最小的 endip >= val */
                uint l = (uint)lo, h = (uint)hi;
                idx = 0;
                while (l <= h)
                {
                    uint mid = (l + h) >> 1;
                    if (_ends[mid] >= val)
                    {
                        idx = mid;
                        if (mid == 0) break;
                        h = mid - 1u;
                    }
                    else
                    {
                        l = mid + 1u;
                    }
                }
            }
            if (_starts[idx] <= val && _ends[idx] >= val)
                return _strings[_strIdx[idx]];
            return "";
        }
    }

    /// <summary>
    /// 高性能查询入口(兼容旧版单例 API)。
    /// 线程安全: 内部持有不可变引擎快照,热加载通过原子引用替换发布。
    /// </summary>
    public sealed class IPSearch2Fast
    {
        private static readonly Lazy<IPSearch2Fast> Lazy =
            new Lazy<IPSearch2Fast>(() => new IPSearch2Fast());

        public static IPSearch2Fast Instance { get { return Lazy.Value; } }

        private const string DefaultFileName = "qqzeng-ip-utf8.dat";

        private volatile DatEngine _engine;
        private DateTime _lastRead = DateTime.MinValue;
        private readonly string _datPath;
        private readonly bool _watch;

        /// <summary>兼容旧版: 默认路径单例(自动探测),并开启文件变更热加载。</summary>
        private IPSearch2Fast() : this(FindDbPath(), true)
        {
        }

        /// <summary>新增: 指定 .dat 路径,可同时加载国内版与全球版。</summary>
        public IPSearch2Fast(string dbPath) : this(dbPath, false)
        {
        }

        /// <summary>新增: 指定路径并选择是否监听文件热更新。</summary>
        public IPSearch2Fast(string dbPath, bool watchFile)
        {
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
                throw new FileNotFoundException("cannot find database: " + dbPath);
            _datPath = Path.GetFullPath(dbPath);
            _watch = watchFile;
            _engine = new DatEngine(File.ReadAllBytes(_datPath));
            _lastRead = File.GetLastWriteTime(_datPath);
            if (watchFile)
                Watch();
        }

        private static string FindDbPath()
        {
            string[] names = { "qqzeng-ip-utf8.dat", "qqzeng-ip.dat", "qqzeng-ip-china-utf8.dat", "qqzeng-ip-global-utf8.dat" };
            string[] prefixes = { "", "dada/国内精华版-202610/", "dada/全球旗舰版-202610/",
                "../dada/国内精华版-202610/", "../dada/全球旗舰版-202610/",
                "../../dada/国内精华版-202610/", "../../dada/全球旗舰版-202610/" };
            foreach (string p in prefixes)
            {
                foreach (string n in names)
                {
                    string path = p + n;
                    if (File.Exists(path)) return path;
                }
            }
            throw new FileNotFoundException("cannot find any qqzeng-ip .dat");
        }

        private void Watch()
        {
            var fi = new FileInfo(_datPath);
            var watcher = new FileSystemWatcher(fi.DirectoryName)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.LastWrite,
                Filter = fi.Name,
            };
            watcher.Changed += (s, e) =>
            {
                try
                {
                    var lastWrite = File.GetLastWriteTime(_datPath);
                    if (lastWrite > _lastRead)
                    {
                        Thread.Sleep(1000); /* 等待写入方释放文件 */
                        /* 构造完整新快照后原子替换,并发查询绝不看到半成品状态 */
                        var fresh = new DatEngine(File.ReadAllBytes(_datPath));
                        _engine = fresh;
                        _lastRead = lastWrite;
                    }
                }
                catch (Exception)
                {
                    /* 热加载失败保留旧快照继续服务(fail-open 到旧数据,不中断线上查询) */
                }
            };
            watcher.EnableRaisingEvents = true;
        }

        /// <summary>IP 查询: 未命中/非法 IP 返回 ""。</summary>
        public string Query(string ip)
        {
            uint v;
            if (!DatEngine.FastParseIp(ip, out v)) return "";
            return FindUint(v);
        }

        /// <summary>整数查询(跳过字符串解析,高性能场景)。</summary>
        public string FindUint(uint val)
        {
            return _engine.Find(val);
        }
    }

    /*
      (调用例子):
      string result = IPSearch2Fast.Instance.Query("1.2.3.4");
      --> result = "亚洲|中国|香港|九龙|油尖旺|新世界电讯|810200|Hong Kong|HK|114.17495|22.327115"
    */
}
