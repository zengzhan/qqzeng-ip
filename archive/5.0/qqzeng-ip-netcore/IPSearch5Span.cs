using System;
using System.IO;
using System.Text;


namespace app_qqzeng_dat
{

    //性能 每秒 超千万  纳秒级  5.0 版本 比4.0 文件更小  2023-06-02
    public class IPSearch5Span
    {
        private static readonly Lazy<IPSearch5Span> lazy = new(() => new IPSearch5Span());
        public static IPSearch5Span Instance { get { return lazy.Value; } }
        private IPSearch5Span() { LoadDat(); }

        private static uint[] prefArr;
        private static UInt16[] startArr;
        private static UInt16[] indexArr;
        private static string[] geoispArr;

        private void LoadDat()
        {
            string datPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"qqzeng-ip-5.0-ultimate.dat");
            byte[] data = File.ReadAllBytes(datPath);

            Span<byte> span = data;
            uint recordSize = BitConverter.ToUInt32(span[..4]);

            int m = 65536;
            prefArr = new uint[m * 2];
            int i = 4;
            for (int k = 0; k < m; k++)
            {
                prefArr[k << 1] = BitConverter.ToUInt32(span.Slice(i, 4));
                prefArr[(k << 1) + 1] = BitConverter.ToUInt32(span.Slice(i + 4, 4));
                i += 8;
            }

            startArr = new UInt16[recordSize];
            indexArr = new UInt16[recordSize];
            int p = 4 + m * 8;
            for (int j = 0; j < recordSize; j++)
            {
                startArr[j] = BitConverter.ToUInt16(span.Slice(p, 2));
                indexArr[j] = BitConverter.ToUInt16(span.Slice(2 + p, 2));
                p += 4;
            }

            var offset = 4 + m * 8 + recordSize * 4;
            geoispArr = Encoding.UTF8.GetString(data, (int)offset, (int)(data.Length - offset)).Split('\t');
        }


        public string Find(string ip)
        {
            var suffix = IpToInt(ip, out ushort prefix);
            long low = prefArr[prefix << 1], high = prefArr[(prefix << 1) + 1];
            var cur = low == high ? low : BinarySearchLastIndex(low, high, suffix);
            return geoispArr[indexArr[cur]];
        }

        private ushort IpToInt(string ipString, out ushort prefix)
        {
            int ip = 0;
            int partValue = 0;
            int shift = 24;

            for (int i = 0; i < ipString.Length; i++)
            {
                char ch = ipString[i];

                if (ch == '.')
                {
                    ip |= partValue << shift;
                    partValue = 0;
                    shift -= 8;
                }
                else
                {
                    int digit = ch - '0';
                    partValue = partValue * 10 + digit;
                }
            }

            ip |= partValue << shift;

            prefix = (ushort)(ip >> 16);
            return (ushort)(ip & 0xFFFF);
        }

        private long BinarySearchLastIndex(long left, long right, long target)
        {
            while (left <= right)
            {
                long mid = left + (right - left) / 2;

                if (startArr[mid] <= target)
                    left = mid + 1;
                else
                    right = mid - 1;
            }

            return right;
        }




    }

    /*
    （调用例子）：
    string result = IPSearch5Span.Instance.Find("1.2.3.4");
   --> result="亚洲|中国|香港|九龙|油尖旺|新世界电讯|810200|Hong Kong|HK|114.17495|22.327115"
    */
}
