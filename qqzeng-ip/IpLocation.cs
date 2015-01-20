using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
namespace qqzengip
{
    //###################################################################################
    //#
    //#  最新IP地址数据库 qqzeng-ip-db.dat
    //#
    //# 2015 qqzeng.com  采用二分逼近查找 性能提升很大
    //#
    //###################################################################################
    public class IpLocation
    {
        readonly string ipBinaryFilePath = "qqzeng-ip-db.dat";
        readonly byte[] dataBuffer, indexBuffer;
        readonly uint[] index = new uint[256];
        readonly int dataLength;
        public IpLocation()
        {
            try
            {
                FileInfo file = new FileInfo(ipBinaryFilePath);
                dataBuffer = new byte[file.Length];
                using (var fin = new FileStream(file.FullName, FileMode.Open, FileAccess.Read))
                {
                    fin.Read(dataBuffer, 0, dataBuffer.Length);
                }
                //文件头   从零开始的字节偏移量 
                var offset_len = BytesToLong(dataBuffer[0], dataBuffer[1], dataBuffer[2], dataBuffer[3]); //Big Endian
                indexBuffer = new byte[offset_len];
                Array.Copy(dataBuffer, 4, indexBuffer, 0, offset_len);
                dataLength = (int)offset_len;
                for (int loop = 0; loop < 256; loop++)
                {
                    //索引 四字节  LITTLE_ENDIAN
                    index[loop] = BytesToLong(indexBuffer[loop * 4 + 3], indexBuffer[loop * 4 + 2], indexBuffer[loop * 4 + 1], indexBuffer[loop * 4]);
                }
            }
            catch { }
        }


        public string[] Find(string ip)
        {
            var ips = ip.Split('.');
            uint ip_prefix = uint.Parse(ips[0]);
            uint find_uint32 = BytesToLong(byte.Parse(ips[0]), byte.Parse(ips[1]), byte.Parse(ips[2]), byte.Parse(ips[3]));//BIG_ENDIAN
            uint max_len = 0;
            int resultOffset = -1;
            int resultLegth = -1;
            uint start = index[ip_prefix];
            if (ip_prefix != 255)
            {
                max_len = index[ip_prefix + 1];
            }
            else
            {
                max_len = index[255];
            }
            uint num = max_len - start;
            uint my_index = BinarySearch(start, max_len, find_uint32);
            start = my_index * 8 + 1024;
            resultOffset = (int)BytesToLong((byte)0, indexBuffer[start + 6], indexBuffer[start + 5], indexBuffer[start + 4]);//LITTLE_ENDIAN
            resultLegth = 0xFF & indexBuffer[start + 7];// 长度

            if (resultOffset == -1 || resultLegth == -1)
            {
                return new string[] { "N/A" };
            }
            var areaBytes = new byte[resultLegth];
            Array.Copy(dataBuffer, dataLength + resultOffset - 1024, areaBytes, 0, resultLegth);
            return Encoding.UTF8.GetString(areaBytes).Split('\t');
        }



        /// <summary>
        /// 二分逼近
        /// </summary>
        public uint BinarySearch(uint low, uint high, uint k)
        {
            uint M = 0;
            while (low <= high)
            {
                uint mid = (low + high) / 2;
                uint endipNum = GetStartIp(mid);
                if (endipNum >= k)
                {
                    M = mid; //mid有可能是解
                    high = mid - 1;
                }
                else
                    low = mid + 1;
            }
            return M;
        }

        public uint GetStartIp(uint left)
        {
            int start = (int)(1024 + left * 8);
            uint endipNum = BytesToLong(indexBuffer[start + 0], indexBuffer[start + 1], indexBuffer[start + 2], indexBuffer[start + 3]);//BIG_ENDIAN     
            return endipNum;
        }

        private static uint BytesToLong(byte a, byte b, byte c, byte d)
        {

            return ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | (uint)d;
        }


    }

}
