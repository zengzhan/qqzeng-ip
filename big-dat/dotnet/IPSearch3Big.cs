using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace build_qqzeng_dat_65536
{

    public class IPSearch3Big
    {
        private static readonly Lazy<IPSearch3Big> lazy = new Lazy<IPSearch3Big>(() => new IPSearch3Big());
        public static IPSearch3Big Instance { get { return lazy.Value; } }
        private IPSearch3Big()
        {
            LoadDat();
        }

        private string datPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"qqzeng-ip-big-3.0.dat");

        private long[,] prefmap = new long[65536, 2];
        private uint[] endArr;
        private string[] addrArr;
        private byte[] data;



        /// <summary>
        /// 初始化二进制数据
        /// </summary>

        private void LoadDat()
        {
            data = File.ReadAllBytes(datPath);

            for (int k = 0; k < 65536; k++)
            {
                int i = k * 8 + 4;
                long startIndex = ReadLittleEndian32(data[i], data[i + 1], data[i + 2], data[i + 3]);
                long endIndex = ReadLittleEndian32(data[i + 4], data[i + 5], data[i + 6], data[i + 7]);
                prefmap[k, 0] = startIndex; prefmap[k, 1] = endIndex;
            }

            uint RecordSize = ReadLittleEndian32(data[0], data[1], data[2], data[3]);
            endArr = new uint[RecordSize];
            addrArr = new string[RecordSize];
            for (int i = 0; i < RecordSize; i++)
            {
                long p = 4 + 65536 * 8 + (i * 9);
                uint endipnum = ReadLittleEndian32(data[p], data[1 + p], data[2 + p], data[3 + p]);

                uint offset = ReadLittleEndian32(data[4 + p], data[5 + p], data[6 + p], data[7 + p]);
                int length = data[8 + p];

                endArr[i] = endipnum;
                addrArr[i] = Encoding.UTF8.GetString(data, (int)offset, length);
            }



        }




        /// <summary>
        /// ip快速查询方法
        /// </summary>
        /// <param name="ip">1.1.1.1</param>
        /// <returns></returns>
        public string Find(string ip)
        {
            byte[] b = IPAddress.Parse(ip).GetAddressBytes();
            int pref = b[0] * 256 + b[1];
            long val = ReadBigEndian32(b[0], b[1], b[2], b[3]);
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
                mid = (low + high) >> 1;
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
    string result = IPSearch3Big.Instance.Find("1.2.3.4");
   --> result="亚洲|中国|香港|九龙|油尖旺|新世界电讯|810200|Hong Kong|HK|114.17495|22.327115"
    */
}
