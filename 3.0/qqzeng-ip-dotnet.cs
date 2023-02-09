using System;
using System.IO;
using System.Text;
using System.Threading;

namespace qqzeng_ip_dat
{
   

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
            // cur==-1 相当找不到数据
            return cur>-1? addrArr[cur]: "||||||||||";
        }


       




        // 二分逼近 O(logN)
        private long BinarySearch(long low, long high, long k)
        {
            long M = -1, mid = 0;
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
