 //性能 每秒 超千万  纳秒级  4.0 版本  
    public class IPSearch4Span
    {
        private static readonly Lazy<IPSearch4Span> lazy = new(() => new IPSearch4Span());
        public static IPSearch4Span Instance { get { return lazy.Value; } }
        private IPSearch4Span() { LoadDat(); }

        private static uint[] prefArr;
        private static uint[] startArr;
        private static uint[] indexArr;
        private static string[] geoispArr;

        private void LoadDat()
        {
            string datPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"qqzeng-ip-4.0-65535.dat");
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

            startArr = new uint[recordSize];
            indexArr = new uint[recordSize];
            int p = 4 + m * 8;
            for (int j = 0; j < recordSize; j++)
            {
                startArr[j] = BitConverter.ToUInt32(span.Slice(p, 4));
                indexArr[j] = BitConverter.ToUInt16(span.Slice(4 + p, 2));
                p += 6;
            }

            var offset = 4 + m * 8 + recordSize * 6;
            geoispArr = Encoding.UTF8.GetString(data, (int)offset, (int)(data.Length - offset)).Split('\t');
        }


        public string Find(string ip)
        {
            long val = IpToInt(ip, out long prefix);
            long low = prefArr[prefix << 1], high = prefArr[(prefix << 1) + 1];
            long cur = low == high ? low : BinarySearchLastIndex(low, high, val);
            return geoispArr[indexArr[cur]];
        }
        private long IpToInt(string ipString, out long prefix)
        {
            long ipInt = 0;
            long currentPart = 0;
            int dotCount = 0;

            foreach (char ch in ipString)
            {
                if (ch >= '0' && ch <= '9')
                {
                    currentPart = currentPart * 10 + (ch - '0');
                }
                else if (ch == '.')
                {
                    ipInt = (ipInt << 8) + currentPart;
                    currentPart = 0;
                    dotCount++;
                }
                else
                {
                    break;
                }
            }

            ipInt = (ipInt << 8) + currentPart;
            prefix = (ipInt >> 16) & 0xFFFF;
            return ipInt;
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
    string result = IPSearch4Span.Instance.Query("1.2.3.4");
   --> result="亚洲|中国|香港|九龙|油尖旺|新世界电讯|810200|Hong Kong|HK|114.17495|22.327115"
    */
