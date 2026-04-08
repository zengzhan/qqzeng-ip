//名称：手机号码归属地查询 dat高效率查询
//压缩：原版txt为18M,生成这种dat结构为2.88M 
//性能：每秒解析200万+号段或者号码,简洁高效 
//环境：CPU i7-7700K +内存16GB
//创建：qqzeng-ip 于 2017-5-21

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
namespace qqzeng_phone_dat
{
     public class PhoneSearch
    {
       
        private Dictionary<uint, PrefixIndex> prefixDict;
        private byte[] indexBuffer;
        private byte[] data;
        long firstPhoneOffset;//索引区第一条流位置
        long lastPhoneOffset;//索引区最后一条流位置
        long prefixStartOffset;//前缀区第一条的流位置
        long prefixEndOffset;//前缀区最后一条的流位置
        long phoneCount;       //号段段数量
        long prefixCount;  //前缀数量

        /// <summary>
        /// 初始化二进制dat数据
        /// </summary>
        /// <param name="dataPath"></param>
        public PhoneSearch(string dataPath)
        {
            using (FileStream fs = new FileStream(dataPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                data = new byte[fs.Length];
                fs.Read(data, 0, data.Length);
            }

            firstPhoneOffset = BytesToLong(data[0], data[1], data[2], data[3]);
            lastPhoneOffset = BytesToLong(data[4], data[5], data[6], data[7]);
            prefixStartOffset = BytesToLong(data[8], data[9], data[10], data[11]);
            prefixEndOffset = BytesToLong(data[12], data[13], data[14], data[15]);

          

            phoneCount = (lastPhoneOffset - firstPhoneOffset) / 8 + 1; //索引区块每组 8字节          
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

        public static uint PhoneToInt(string phone,out uint prefix)
        {           
            prefix = Convert.ToUInt32(phone.Substring(0,3));
            return Convert.ToUInt32(phone.Substring(0, 7)); ;
        }

        /// <summary>
        /// 号段查询
        /// </summary>
        /// <param name="phone">7位或者11位</param>
        /// <returns></returns>
        public string Query(string phone)
        {
            uint phone_prefix_value;
            uint intPhone = PhoneToInt(phone, out phone_prefix_value);
            uint high = 0;
            uint low = 0;
          
            uint local_offset = 0;
            uint local_length = 0;

           
            if (prefixDict.ContainsKey(phone_prefix_value))
            {
                low = (uint)prefixDict[phone_prefix_value].start_index;
                high = (uint)prefixDict[phone_prefix_value].end_index;
            }
            else
            {
                return "";
            }

            if (low == high)
            {
                GetIndex(low, out local_offset, out local_length);
                return GetLocal(local_offset, local_length);
            }
            else
            {
                int my_index = BinarySearch(low, high, intPhone);
                if (my_index!=-1)
                {
                    GetIndex((uint)my_index, out local_offset, out local_length);
                    return GetLocal(local_offset, local_length);
                }
                else
                {
                    return "";
                }
            }
            
          
           

        }
        /// <summary>
        /// 二分算法
        /// </summary>
        public int BinarySearch(uint low, uint high, uint key)
        {
            uint mid = (low + high) / 2;
            if (low > high)
                return -1;
            else
            {
                uint phoneNum = GetIntPhone(mid);
                if (phoneNum == key)
                    return (int)mid;
                else if (phoneNum > key)
                    return BinarySearch( low, mid - 1, key);
                else
                    return BinarySearch(mid + 1, high, key);
            }
        }



        /// <summary>
        /// 在索引区解析
        /// </summary>
        /// <param name="left">ip第left个索引</param>
        /// <param name="startip">返回开始ip的数值</param>
        /// <param name="endip">返回结束ip的数值</param>
        /// <param name="local_offset">返回地址信息的流位置</param>
        /// <param name="local_length">返回地址信息的流长度</param>
        private void GetIndex(uint left, out uint local_offset, out uint local_length)
        {
            long left_offset = firstPhoneOffset + (left * 8);
            local_offset = (uint)data[4 + left_offset] + (((uint)data[5 + left_offset]) << 8) + (((uint)data[6 + left_offset]) << 16);
            local_length = (uint)data[7 + left_offset];
        }
     

        /// <summary>
        /// 返回归属地信息
        /// </summary>
        /// <param name="local_offset">地址信息的流位置</param>
        /// <param name="local_length">地址信息的流长度</param>
        /// <returns></returns>
        private string GetLocal(uint local_offset, uint local_length)
        {
            byte[] buf = new byte[local_length];
            Array.Copy(data, local_offset, buf, 0, local_length);
             return Encoding.UTF8.GetString(buf, 0, (int)local_length); 
          
           // return Encoding.GetEncoding("GB2312").GetString(buf, 0, (int)local_length);

        }

        private uint GetIntPhone(uint left)
        {
            long left_offset = firstPhoneOffset + (left * 8);
            return BytesToLong(data[0 + left_offset], data[1 + left_offset], data[2 + left_offset], data[3 + left_offset]);

        }


        /// <summary>
        /// 字节转整形 小节序 
        /// </summary>     
        private uint BytesToLong(byte a, byte b, byte c, byte d)
        {
            return ((uint)a << 0) | ((uint)b << 8) | ((uint)c << 16) | ((uint)d << 24);
        }

    /*
    （调用例子）：
    PhoneSearch finder = new PhoneSearch("qqzeng-phone.dat");
    string result = finder.Query("号段或者号码");
   --> result="省份|城市|运营商|区号|邮编|行政区划代码"
    */
}
