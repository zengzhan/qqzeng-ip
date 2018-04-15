//名称：手机号码归属地查询 dat高效率查询  内存优化版
//压缩：原版txt为22M,生成这种dat结构为2.66M 
//性能：每秒解析300万+号段或者号码,简洁高效 
//环境：CPU i7-7700K +内存16GB
//创建：qqzeng-ip 于 2018-4-5


using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace qqzeng_phone_dat
{

    public class PhoneSearchFast
    {
        private static readonly Lazy<PhoneSearchFast> lazy = new Lazy<PhoneSearchFast>(() => new PhoneSearchFast());
        public static PhoneSearchFast Instance { get { return lazy.Value; } }
        private PhoneSearchFast()
        {
            LoadDat();
            Watch();
        }

        private string datPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"qqzeng-phone.dat");
        private DateTime lastRead = DateTime.MinValue;
        private long[,] prefmap = new long[200, 2];//  000-199


        private long[,] phonemap;

        private byte[] data;

        private long[] phoneArr;
        private string[] addrArr;
        private string[] ispArr;

        /// <summary>
        /// 初始化二进制dat数据
        /// </summary>
        /// <param name="dataPath"></param>
        /// 


        private void LoadDat()
        {
            data = File.ReadAllBytes(datPath);

            long PrefSize = BytesToLong(data[0], data[1], data[2], data[3]);
            long RecordSize = BytesToLong(data[4], data[5], data[6], data[7]);

            long descLength = BytesToLong(data[8], data[9], data[10], data[11]);
            long ispLength = BytesToLong(data[12], data[13], data[14], data[15]);

            //内容数组
            int descOffset = (int)(16 + PrefSize * 9 + RecordSize * 7);
            string descString = Encoding.UTF8.GetString(data, descOffset, (int)descLength);
            addrArr = descString.Split('&');

            //运营商数组
            int ispOffset = (int)(16 + PrefSize * 9 + RecordSize * 7 + descLength);
            string ispString = Encoding.UTF8.GetString(data, ispOffset, (int)ispLength);
            ispArr = ispString.Split('&');



            //前缀区
            int m = 0;
            for (var k = 0; k < PrefSize; k++)
            {
                int i = k * 9 + 16;
                int n = data[i];
                prefmap[n, 0] = BytesToLong(data[i + 1], data[i + 2], data[i + 3], data[i + 4]);
                prefmap[n, 1] = BytesToLong(data[i + 5], data[i + 6], data[i + 7], data[i + 8]);
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

            //索引区
            phoneArr = new long[RecordSize];
            phonemap = new long[RecordSize, 2];
            for (int i = 0; i < RecordSize; i++)
            {
                long p = 16 + PrefSize * 9 + (i * 7);
                phoneArr[i] = BytesToLong(data[p], data[1 + p], data[2 + p], data[3 + p]);
                phonemap[i, 0] = data[4 + p] + ((data[5 + p]) << 8);
                phonemap[i, 1] = data[6 + p];
            }



        }
        private void Watch()
        {
            FileInfo fi = new FileInfo(datPath);
            FileSystemWatcher watcher = new FileSystemWatcher(fi.DirectoryName)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.LastWrite,
                Filter = "qqzeng-phone.dat",
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
        /// 号段查询
        /// </summary>
        /// <param name="phone">7位或者11位</param>
        /// <returns></returns>
        public string Query(string phone)
        {
            long pref;
            long val = PhoneToInt(phone, out pref);
            long low = prefmap[pref, 0], high = prefmap[pref, 1];
            if (high == 0)
            {
                return "";
            }
            long cur = low == high ? low : BinarySearch(low, high, val);
            if (cur != -1)
            {

                return addrArr[phonemap[cur, 0]] + "|" + ispArr[phonemap[cur, 1]];
            }
            else
            {
                return "";
            }






        }
        /// <summary>
        /// 二分算法
        /// </summary>
        private int BinarySearch(long low, long high, long key)
        {
            if (low > high)
                return -1;
            else
            {
                long mid = (low + high) / 2;
                long phoneNum = phoneArr[mid];
                if (phoneNum == key)
                    return (int)mid;
                else if (phoneNum > key)
                    return BinarySearch(low, mid - 1, key);
                else
                    return BinarySearch(mid + 1, high, key);
            }
        }



        private long PhoneToInt(string phone, out long prefix)
        {
            //最高性能
            char ch;
            long currentValue = 0;
            long prefval = 0;
            unsafe
            {
                fixed (char* name = phone)
                {
                    for (int current = 0; current < 7; current++)
                    {
                        ch = name[current];
                        int digitValue = ch - '0';
                        currentValue = (currentValue * 10) + digitValue;
                        if (current == 2)
                        {
                            prefval = currentValue;
                        }
                    }
                }
                prefix = prefval;
                return currentValue;
            }


            //prefix = Convert.ToUInt32(phone.Substring(0,3));
            //return Convert.ToUInt32(phone.Substring(0, 7)); ;
        }



        /// <summary>
        /// 字节转整形 小节序 
        /// </summary>     
        private uint BytesToLong(byte a, byte b, byte c, byte d)
        {
            return (uint)(a | (b << 8) | (c << 16) | (d << 24));
        }



    }

    /*
    （调用例子）：    
    string result = PhoneSearchFast.Instance.Query("号段|号码");
   --> result="省份|城市|区号|邮编|行政区划代码|运营商"
    */
}
