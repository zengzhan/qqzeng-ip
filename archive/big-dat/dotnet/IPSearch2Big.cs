using System;
using System.Text;
using System.IO;
using System.Net;
using System.Collections.Generic;
namespace build_qqzeng_dat_65536
{
    public class IPSearch2Big
    {
        private Dictionary<int, PrefixIndex> prefDict;

        private byte[] data;
        private long prefCount;


        /// <summary>
        /// 初始化二进制dat数据
        /// </summary>
        /// <param name="dataPath"></param>
        public IPSearch2Big(string dataPath)
        {
            using (FileStream fs = new FileStream(dataPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                data = new byte[fs.Length];
                fs.Read(data, 0, data.Length);
            }

            prefCount = Bytes2Long(data, 0, 4); //ip段数量
            long ipCount = Bytes2Long(data, 4, 4);//前缀数量
            long ver = Bytes2Long(data, 8, 4);



            //初始化前缀对应索引区区间

            prefDict = new Dictionary<int, PrefixIndex>();
            for (var k = 0; k < prefCount; k++)
            {
                int i = k * 10 + 12;
                int prefix = (int)Bytes2Long(data, i, 2);
                long start_index = Bytes2Long(data, i + 2, 4);
                long end_index = Bytes2Long(data, i + 6, 4);
                prefDict.Add(prefix, new PrefixIndex() { prefix = prefix, start_index = start_index, end_index = end_index });
            }

        }



        /// <summary>
        /// 根据ip查询多维字段信息
        /// </summary>
        /// <param name="ip">ip地址（123.4.5.6）</param>
        /// <returns>亚洲|中国|香港|九龙|油尖旺|新世界电讯|810200|Hong Kong|HK|114.17495|22.327115</returns>
        public string Query(string ip)
        {
            int pref;
            uint intIP = IpToInt(ip, out pref);
            long high, low;
            if (prefDict.ContainsKey(pref))
            {
                low = prefDict[pref].start_index;
                high = prefDict[pref].end_index;
            }
            else
            {
                return "";
            }

            long index = low == high ? low : BinarySearch(low, high, intIP);

            long offset = 12 + prefCount * 10 + index * 13;
            long startIp = Bytes2Long(data, offset, 4);
            long endIp = Bytes2Long(data, 4 + offset, 4);

            if ((startIp <= intIP) && (endIp >= intIP))
            {
                long local_offset = Bytes2Long(data, 8 + offset, 4);
                long local_length = data[12 + offset];
                return Encoding.UTF8.GetString(data, (int)local_offset, (int)local_length);
            }
            else
            {
                return "";
            }

        }





        /// <summary>
        /// 二分逼近算法
        /// </summary>
        private long BinarySearch(long low, long high, long k)
        {
            long M = 0;
            while (low <= high)
            {
                long mid = (low + high) / 2;

                long endipNum = GetEndIp(mid);
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
        /// 只获取结束ip的数值
        /// </summary>
        /// <param name="left">索引区第left个索引</param>
        /// <returns>返回结束ip的数值</returns>
        private long GetEndIp(long left)
        {
            long offset = 12 + prefCount * 10 + (left * 13);
            return Bytes2Long(data, 4 + offset, 4);

        }


        private uint IpToInt(string ip, out int prefix)
        {
            byte[] bytes = IPAddress.Parse(ip).GetAddressBytes();
            prefix = ((int)bytes[0] << 8) + (int)bytes[1];
            return (uint)bytes[3] + (((uint)bytes[2]) << 8) + (((uint)bytes[1]) << 16) + (((uint)bytes[0]) << 24);
        }




        private long Bytes2Long(byte[] buffer, long offset, int count)
        {
            long r = 0;

            for (int i = 0; i < count; i++)
            {
                r |= (long)buffer[offset + i] << i * 8;
            }

            return r;
        }


    }

    /*
    （调用例子）：
    IPSearch2Big finder = new IPSearch2Big("qqzeng-ip-big-2.0.dat");
    string result = finder.Query("1.2.3.4");
   --> result="亚洲|中国|香港|九龙|油尖旺|新世界电讯|810200|Hong Kong|HK|114.17495|22.327115"
    */
}

