using System;
using System.IO;
using System.Text;
using System.Threading;

namespaceqqzeng_ip_dat
{
    /*

        高性能IP数据库格式详解 qqzeng-ip-ultimate.dat 3.0版 每秒解析1000多万ip

        编码：UTF8  字节序：Little-Endian  

        返回多个字段信息（如：亚洲|中国|香港|九龙|油尖旺|新世界电讯|810200|Hong Kong|HK|114.17495|22.327115）

        ------------------------ 文件结构 3.0 -------------------------
         //文件头  4字节
         [IP段记录]

        //前缀区   8字节(4-4)   256*8
        [索引区start第几个][索引区end第几个]      


        //索引区    8字节(4-3-1)   ip段行数*8
        [结束IP数字][地区流位置][流长度]
        

         //内容区    长度无限制
        [地区信息][地区信息]……唯一不重复

        ------------------------ 文件结构 ---------------------------

        优势：压缩形式将数据存储在内存中，通过减少将相同数据读取到内存的次数来减少I/O.
             较高的压缩率通过使用更小的内存中空间提高查询性能。
             前缀区为作为缩小查询范围,索引区和内容区长度一样,
             解析出来一次性加载到数组中,查询性能提高3-5倍！               

        压缩：原版txt为38.5M,生成dat结构为3.68M 。
             和上一版本2.0不同的是索引区去掉了[开始IP数字]4字节,节省多1-2M。
             3.0版本只适用[全球版]，条件为ip段区间连续且覆盖所有IPV4。
             2.0版本适用[全球版][国内版][国外版] 

        性能：每秒解析1000多万ip (环境：CPU i7-7700K  + DDR2400 16G  + win10 X64)
             
        创建：qqzeng-ip 于 2018-04-08

        */

    public class IPSearch3Fast
    {
        private static readonly Lazy<IPSearch3Fast> lazy = new Lazy<IPSearch3Fast>(() => new IPSearch3Fast());
        public static IPSearch3Fast Instance { get { return lazy.Value; } }
        private IPSearch3Fast()
        {
            LoadDat();
            Watch();
        }

        private string datPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"qqzeng-ip-ultimate.dat");
        private DateTime lastRead = DateTime.MinValue;

        private long[,] prefmap = new long[256, 2];
        private uint[] endArr;
        private string[] addrArr;
        private byte[] data;



        /// <summary>
        /// 初始化二进制 qqzeng-ip-ultimate.dat 数据
        /// </summary>

        private void LoadDat()
        {
            data = File.ReadAllBytes(datPath);

            for (int k = 0; k < 256; k++)
            {
                int i = k * 8 + 4;
                int prefix = k;
                long startIndex = ReadLittleEndian32(data[i], data[i + 1], data[i + 2], data[i + 3]);
                long endIndex = ReadLittleEndian32(data[i + 4], data[i + 5], data[i + 6], data[i + 7]);
                prefmap[k, 0] = startIndex; prefmap[k, 1] = endIndex;
            }

            uint RecordSize = ReadLittleEndian32(data[0], data[1], data[2], data[3]);
            endArr = new uint[RecordSize];
            addrArr = new string[RecordSize];
            for (int i = 0; i < RecordSize; i++)
            {
                long p = 2052 + (i * 8);
                uint endipnum = ReadLittleEndian32(data[p], data[1 + p], data[2 + p], data[3 + p]);

                int offset = data[4 + p] + ((data[5 + p]) << 8) + ((data[6 + p]) << 16);
                int length = data[7 + p];

                endArr[i] = endipnum;
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
                Filter = "qqzeng-ip-ultimate.dat",
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
        /// <param name="ip">1.1.1.1</param>
        /// <returns></returns>
        public string Find(string ip)
        {
            long val = IpToInt(ip, out long pref);
            long low = prefmap[pref, 0], high = prefmap[pref, 1];
            long cur = low == high ? low : BinarySearch(low, high, val);
            return addrArr[cur];
        }






        // 二分逼近 O(logN)
        private long BinarySearch(long low, long high, long k)
        {
            long M = 0, mid = 0;
            while (low <= high)
            {
                mid = (low + high) / 2;
                uint endipnum = endArr[mid];
                if (endipnum >= k)
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


       
        private long IpToInt(string ipString, out long prefix)
        {
            //高性能
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

            //简洁的 普通 

            //byte[] b = IPAddress.Parse(ip).GetAddressBytes();
            //prefix = b[0];  
            // return ReadBigEndian32(b[0], b[1], b[2], b[3]);
        }


        private uint ReadBigEndian32(byte a, byte b, byte c, byte d)
        {
            return (uint)((a << 24) | (b << 16) | (c << 8) | d);
        }


        private uint ReadLittleEndian32(byte a, byte b, byte c, byte d)
        {
            return (uint)(a | (b << 8) | (c << 16) | (d << 24));
        }





    }

    /*
    （调用例子）：   
    string result = IPSearch3Fast.Instance.Find("1.2.3.4");
   --> result="亚洲|中国|香港|九龙|油尖旺|新世界电讯|810200|Hong Kong|HK|114.17495|22.327115"
    */
}
