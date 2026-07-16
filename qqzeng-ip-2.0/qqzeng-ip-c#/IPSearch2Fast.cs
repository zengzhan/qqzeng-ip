using System;
using System.IO;
using System.Text;
using System.Threading;

namespace qqzeng_ip_dat
{

    /*

    高性能IP数据库格式详解 qqzeng-ip.dat 2.0版 每秒解析900多万ip
    
    编码：UTF8  字节序：Little-Endian  

    返回多个字段信息（如：亚洲|中国|香港|九龙|油尖旺|新世界电讯|810200|Hong Kong|HK|114.17495|22.327115）
    
    ------------------------ 文件结构 2.0  -------------------------

    //文件头    16字节(4-4-4-4)
    [索引区第一条流位置][索引区最后一条流位置][前缀区第一条的流位置][前缀区最后一条的流位置] 

    //内容区    长度无限制
    [地区信息][地区信息]……唯一不重复


    //索引区    12字节(4-4-3-1)
    [起始IP][结束IP][地区流位置][流长度]


    //前缀区   9字节(1-4-4)
    [0-255][索引区start索引][索引区end索引]

    ------------------------ 文件结构 ---------------------------

    优势：索引区分为[起始IP][结束IP][地区偏移][长度],减少多级偏移跳转步骤和长度的解析,提高效率;
         根据ip第一位数字作为前缀,解析出以这个数字为前缀的第一个索引和最后一个索引,缩小查询区间,
         然后在这区间再用二分查找快速查找到对应区间,效率提高几个等级    

    压缩：原版txt为38M,生成这种dat结构为5.16M 

    性能：每秒解析900多万 (环境：CPU i7-7700K  + DDR2400 16G  + win10 X64)

    对比：相比其他dat更简洁更高效

    创建：qqzeng-ip 于 2015-08-01 
    
    优化：qqzeng-ip 于 2018-04-08 

    */

    public class IPSearch2Fast
    {
        private static readonly Lazy<IPSearch2Fast> lazy = new Lazy<IPSearch2Fast>(() => new IPSearch2Fast());
        public static IPSearch2Fast Instance { get { return lazy.Value; } }
        private IPSearch2Fast()
        {
            LoadDat();
            Watch();
        }

        private string datPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"qqzeng-ip-utf8.dat");
        private DateTime lastRead = DateTime.MinValue;

        private long[,] prefmap;
        private uint[,] ipmap;
        private string[] addrArr;
        private byte[] data;


        /// <summary>
        /// 初始化二进制 qqzeng-ip-utf8.dat 数据
        /// </summary>
        private void LoadDat()
        {
            data = File.ReadAllBytes(datPath);


            long firstStartIpOffset = BytesToLong(data[0], data[1], data[2], data[3]);//索引区第一条流位置
            long lastStartIpOffset = BytesToLong(data[4], data[5], data[6], data[7]);//索引区最后一条流位置
            long prefixStartOffset = BytesToLong(data[8], data[9], data[10], data[11]);//前缀区第一条的流位置
            long prefixEndOffset = BytesToLong(data[12], data[13], data[14], data[15]);//前缀区最后一条的流位置

            //prefixCount 不固定为256 方便以后自由定制  全球版 国内版  国外版 或者某部分 都可以

            long ipCount = (lastStartIpOffset - firstStartIpOffset) / 12 + 1; //索引区块每组 12字节     //ip段数量      
            long prefixCount = (prefixEndOffset - prefixStartOffset) / 9 + 1; //前缀区块每组 9字节 //前缀数量

            prefmap = new long[256, 2];

            //初始化前缀对应索引区区间
            byte[] indexBuffer = new byte[prefixCount * 9];
            Buffer.BlockCopy(data, (int)prefixStartOffset, indexBuffer, 0, (int)prefixCount * 9);
            int m = 0;
            for (var k = 0; k < prefixCount; k++)
            {
                int i = k * 9;
                int n = indexBuffer[i];
                prefmap[n, 0] = BytesToLong(indexBuffer[i + 1], indexBuffer[i + 2], indexBuffer[i + 3], indexBuffer[i + 4]);
                prefmap[n, 1] = BytesToLong(indexBuffer[i + 5], indexBuffer[i + 6], indexBuffer[i + 7], indexBuffer[i + 8]);
                if (m < n)
                {
                    for (; m < n; m++)
                    {
                        prefmap[m, 0] = 0; prefmap[m, 1] = 0;
                    }
                    m++;
                }
                else
                {
                    m++;
                }
            }

            //初始化 索引区间
            ipmap = new uint[ipCount, 2];
            addrArr = new string[ipCount];
            for (int i = 0; i < ipCount; i++)
            {
                long p = firstStartIpOffset + (i * 12);
                uint startip = BytesToLong(data[p], data[1 + p], data[2 + p], data[3 + p]);
                uint endip = BytesToLong(data[4 + p], data[5 + p], data[6 + p], data[7 + p]);
                int offset = data[8 + p] + ((data[9 + p]) << 8) + ((data[10 + p]) << 16);
                int length = data[11 + p];

                ipmap[i, 0] = startip;
                ipmap[i, 1] = endip;
                addrArr[i] = Encoding.UTF8.GetString(data, offset, length);
            }

        }


        private void Watch()
        {
            FileInfo fi = new FileInfo(datPath);
            FileSystemWatcher watcher = new FileSystemWatcher(fi.DirectoryName)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.LastWrite,
                Filter = "qqzeng-ip-utf8.dat",
            };

            watcher.Changed += (s, e) =>
            {

                var lastWriteTime = File.GetLastWriteTime(datPath);

                if (lastWriteTime > lastRead)
                {
                    //延时 解决 正由另一进程使用,因此该进程无法访问此文件
                    Thread.Sleep(1000);

                    LoadDat();
                    lastRead = lastWriteTime;
                }
            };
            watcher.EnableRaisingEvents = true;
        }



        /// <summary>
        /// ip快速查询方法
        /// </summary>
        /// <param name="ip">ip地址（1.4.5.6）</param>
        /// <returns>亚洲|中国|香港|九龙|油尖旺|新世界电讯|810200|Hong Kong|HK|114.17495|22.327115</returns>
        public string Query(string ip)
        {
            long val = IpToInt(ip, out long pref);
            long low = prefmap[pref, 0], high = prefmap[pref, 1];
            if (high == 0)
            {
                return "";
            }
            long cur = low == high ? low : BinarySearch(low, high, val);
            if (ipmap[cur, 0] <= val && ipmap[cur, 1] >= val)
            {
                return addrArr[cur];
            }
            else
            {
                return "";
            }

        }



        /// <summary>
        /// 二分逼近算法 O(logN)
        /// </summary>
        public long BinarySearch(long low, long high, long k)
        {
            long M = 0;
            while (low <= high)
            {
                long mid = (low + high) / 2;

                uint endipNum = ipmap[mid, 1];
                if (endipNum >= k)
                {

                    M = mid;
                    if (mid == 0)
                    {
                        break;   //防止溢出
                    }
                    high = mid - 1;
                }
                else
                    low = mid + 1;
            }
            return M;
        }




        /// <summary>
        /// 字节转整形 小节序 
        /// </summary>     
        private uint BytesToLong(byte a, byte b, byte c, byte d)
        {
            return (uint)(a | (b << 8) | (c << 16) | (d << 24));
        }



        public static long IpToInt(string ipString, out long prefix)
        {
            //最高性能
            int end = ipString.Length;
            unsafe
            {
                fixed (char* name = ipString)
                {

                    int numberBase = 10;
                    char ch;
                    long[] parts = new long[4];
                    long currentValue = 0;
                    int dotCount = 0;
                    int current = 0;
                    for (; current < end; current++)
                    {
                        ch = name[current];
                        currentValue = 0;

                        numberBase = 10;
                        if (ch == '0')
                        {
                            numberBase = 8;
                            current++;

                            if (current < end)
                            {
                                ch = name[current];
                                if (ch == 'x' || ch == 'X')
                                {
                                    numberBase = 16;
                                    current++;
                                }
                            }
                        }

                        for (; current < end; current++)
                        {
                            ch = name[current];
                            int digitValue;

                            if ((numberBase == 10 || numberBase == 16) && '0' <= ch && ch <= '9')
                            {
                                digitValue = ch - '0';
                            }
                            else if (numberBase == 8 && '0' <= ch && ch <= '7')
                            {
                                digitValue = ch - '0';
                            }
                            else if (numberBase == 16 && 'a' <= ch && ch <= 'f')
                            {
                                digitValue = ch + 10 - 'a';
                            }
                            else if (numberBase == 16 && 'A' <= ch && ch <= 'F')
                            {
                                digitValue = ch + 10 - 'A';
                            }
                            else
                            {
                                break;
                            }

                            currentValue = (currentValue * numberBase) + digitValue;

                        }

                        if (current < end && name[current] == '.')
                        {
                            parts[dotCount] = currentValue;
                            dotCount++;
                            continue;
                        }
                        break;
                    }
                    parts[dotCount] = currentValue;
                    prefix = parts[0];
                    return (parts[0] << 24) | ((parts[1] & 0xff) << 16) | ((parts[2] & 0xff) << 8) | (parts[3] & 0xff);
                }
            }

            //简洁方法
            //byte[] bytes = IPAddress.Parse(ipString).GetAddressBytes();
            //prefix = bytes[0];
            //return (uint)(bytes[3] + ((bytes[2]) << 8) + ((bytes[1]) << 16) + ((bytes[0]) << 24));
        }


    }

    /*
    （调用例子）：
    string result = IPSearch3Fast.Instance.Find("1.2.3.4");
   --> result="亚洲|中国|香港|九龙|油尖旺|新世界电讯|810200|Hong Kong|HK|114.17495|22.327115"
    */
}
