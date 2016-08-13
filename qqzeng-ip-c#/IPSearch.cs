using System;
using System.Text;
using System.IO;
using System.Net;
using System.Collections.Generic;
namespace qqzeng_ip_dat
{

    /*

    高性能IP数据库格式详解 qqzeng-ip.dat
    
    编码：UTF8  字节序：Little-Endian  

    返回多个字段信息（如：亚洲|中国|香港|九龙|油尖旺|新世界电讯|810200|Hong Kong|HK|114.17495|22.327115）
    
    ------------------------ 文件结构 ---------------------------

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

    压缩：原版txt为15M,生成这种dat结构为2.45M 

    性能：解析300万ip耗时1秒

    对比：相比其他dat更简洁更高效

    创建：qqzeng-ip 于 2015-08-05

    */

    public class IPSearch
    {
        private Dictionary<uint, PrefixIndex> prefixDict;
        private byte[] indexBuffer;
        private byte[] data;
        long firstStartIpOffset;//索引区第一条流位置
        long lastStartIpOffset;//索引区最后一条流位置
        long prefixStartOffset;//前缀区第一条的流位置
        long prefixEndOffset;//前缀区最后一条的流位置
        long ipCount;       //ip段数量
        long prefixCount;  //前缀数量

        /// <summary>
        /// 初始化二进制dat数据
        /// </summary>
        /// <param name="dataPath"></param>
        public IPSearch(string dataPath)
        {
            using (FileStream fs = new FileStream(dataPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                data = new byte[fs.Length];
                fs.Read(data, 0, data.Length);
            }

            firstStartIpOffset = BytesToLong(data[0], data[1], data[2], data[3]);
            lastStartIpOffset = BytesToLong(data[4], data[5], data[6], data[7]);
            prefixStartOffset = BytesToLong(data[8], data[9], data[10], data[11]);
            prefixEndOffset = BytesToLong(data[12], data[13], data[14], data[15]);

            //prefixCount 不固定为256 方便以后自由定制 国内版  国外版 全球版 或者某部分 都可以

            ipCount = (lastStartIpOffset - firstStartIpOffset) / 12 + 1; //索引区块每组 12字节          
            prefixCount = (prefixEndOffset - prefixStartOffset) / 9 + 1; //前缀区块每组 9字节

            //初始化前缀对应索引区区间
            indexBuffer = new byte[prefixCount * 9];
            Array.Copy(data, prefixStartOffset, indexBuffer, 0, prefixCount * 9);
            prefixDict = new Dictionary<uint, PrefixIndex>();
            for (var k = 0; k < prefixCount; k++)
            {
                int i = k * 9;
                uint prefix = (uint)indexBuffer[i];
                long start_index = BytesToLong(indexBuffer[i + 1], indexBuffer[i + 2], indexBuffer[i + 3], indexBuffer[i + 4]);
                long end_index = BytesToLong(indexBuffer[i + 5], indexBuffer[i + 6], indexBuffer[i + 7], indexBuffer[i + 8]);
                prefixDict.Add(prefix, new PrefixIndex() { prefix = prefix, start_index = start_index, end_index = end_index });
            }

        }

        public static uint IpToInt(string ip,out uint prefix)
        {
            byte[] bytes = IPAddress.Parse(ip).GetAddressBytes();
            prefix = (uint)bytes[0];
            return (uint)bytes[3] + (((uint)bytes[2]) << 8) + (((uint)bytes[1]) << 16) + (((uint)bytes[0]) << 24);
        }

        public static string IntToIP(uint ip_Int)
        {
            return new IPAddress(ip_Int).ToString();
        }

        /// <summary>
        /// 根据ip查询多维字段信息
        /// </summary>
        /// <param name="ip">ip地址（123.4.5.6）</param>
        /// <returns>亚洲|中国|香港|九龙|油尖旺|新世界电讯|810200|Hong Kong|HK|114.17495|22.327115</returns>
        public string Query(string ip)
        {
            uint ip_prefix_value;
            uint intIP = IpToInt(ip,out ip_prefix_value);
            uint high = 0;
            uint low = 0;
            uint startIp = 0;
            uint endIp = 0;
            uint local_offset = 0;
            uint local_length = 0;

           
            if (prefixDict.ContainsKey(ip_prefix_value))
            {
                low = (uint)prefixDict[ip_prefix_value].start_index;
                high = (uint)prefixDict[ip_prefix_value].end_index;
            }
            else
            {
                return "";
            }

            uint my_index = low == high? low : BinarySearch(low, high, intIP);          

            GetIndex(my_index, out startIp, out endIp, out local_offset, out local_length);

            if ((startIp <= intIP) && (endIp >= intIP))
            {
                return GetLocal(local_offset, local_length);
            }
            else
            {
                return "";
            }

        }
        /// <summary>
        /// 二分逼近算法
        /// </summary>
        public uint BinarySearch(uint low, uint high, uint k)
        {
            uint M = 0;
            while (low <= high )
            {
                uint mid = (low + high) / 2;

                uint endipNum = GetEndIp(mid);
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
        /// 在索引区解析
        /// </summary>
        /// <param name="left">ip第left个索引</param>
        /// <param name="startip">返回开始ip的数值</param>
        /// <param name="endip">返回结束ip的数值</param>
        /// <param name="local_offset">返回地址信息的流位置</param>
        /// <param name="local_length">返回地址信息的流长度</param>
        private void GetIndex(uint left, out uint startip, out uint endip, out uint local_offset, out uint local_length)
        {
            long left_offset = firstStartIpOffset + (left * 12);
            startip = BytesToLong(data[left_offset], data[1 + left_offset], data[2 + left_offset],data[3 + left_offset]);
            endip = BytesToLong(data[4+left_offset], data[5 + left_offset], data[6 + left_offset], data[7 + left_offset]);
            local_offset = (uint)data[8 + left_offset] + (((uint)data[9 + left_offset]) << 8) + (((uint)data[10 + left_offset]) << 16);
            local_length = (uint)data[11 + left_offset];
        }
        /// <summary>
        /// 只获取结束ip的数值
        /// </summary>
        /// <param name="left">索引区第left个索引</param>
        /// <returns>返回结束ip的数值</returns>
        private uint GetEndIp(uint left)
        {
            long left_offset = firstStartIpOffset + (left * 12);
            return BytesToLong(data[4 + left_offset], data[5 + left_offset], data[6 + left_offset], data[7 + left_offset]);

        }

        /// <summary>
        /// 返回地址信息
        /// </summary>
        /// <param name="local_offset">地址信息的流位置</param>
        /// <param name="local_length">地址信息的流长度</param>
        /// <returns></returns>
        private string GetLocal(uint local_offset, uint local_length)
        {
            byte[] buf = new byte[local_length];
            Array.Copy(data, local_offset, buf, 0, local_length);
            return Encoding.UTF8.GetString(buf, 0, (int)local_length);

        }

        /// <summary>
        /// 字节转整形 小节序 
        /// </summary>
        private uint BytesToLong(byte a, byte b, byte c, byte d)
        {
            return ((uint)a << 0) | ((uint)b << 8) | ((uint)c << 16) | ((uint)d << 24);
        }
    }

    /*
    （调用例子）：
    IPSearch finder = new IPSearch("qqzeng-ip.dat");
    string result = finder.Query("1.2.3.4");
   --> result="亚洲|中国|香港|九龙|油尖旺|新世界电讯|810200|Hong Kong|HK|114.17495|22.327115"
    */
}

   public class PrefixIndex
    {
        public uint prefix { get; set; }
        public long start_index { get; set; }
        public long end_index { get; set; }
    }
