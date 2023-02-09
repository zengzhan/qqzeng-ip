public class IPSearch3Span
    {
        private static readonly Lazy<IPSearch3Span> lazy = new(() => new IPSearch3Span());
        public static IPSearch3Span Instance { get { return lazy.Value; } }
        private IPSearch3Span() { LoadDat(); }

        private static uint[,] prefMap = new uint[256, 2];
        private static uint[] endArr;
        private static string[] addrArr;
        private void LoadDat()
        {
            string datPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"qqzeng-ip-ultimate.dat");
            byte[] data = File.ReadAllBytes(datPath);
            Span<byte> span = data;
            for (int k = 0; k < 256; k++)
            {
                int i = k * 8 + 4;
                uint startIndex = ToInt32(span[i..(i + 4)]);
                uint endIndex = ToInt32(span[(i + 4)..(i + 8)]);
                prefMap[k, 0] = startIndex; prefMap[k, 1] = endIndex;
            }
            uint RecordSize = ToInt32(span[0..4]);
            endArr = new uint[RecordSize];
            addrArr = new string[RecordSize];
            int endArrIndex = 0;
            int addrArrIndex = 0;
            int p = 2052;
            for (int i = 0; i < RecordSize; i++, p += 8)
            {
                uint endipnum = ToInt32(span[p..(p + 4)]);
                uint offset = ToInt24(span[(4 + p)..(7 + p)]);
                int length = span[7 + p];

                endArr[endArrIndex++] = endipnum;
                addrArr[addrArrIndex++] = Encoding.UTF8.GetString(data, (int)offset, length);
            }

        }



        public string Query(string ip)
        {
            long val = IpToInt(ip, out long prefix);
            long low = prefMap[prefix, 0], high = prefMap[prefix, 1];
            long cur = low == high ? low : BinarySearch(low, high, val);
            return addrArr[cur];
        }

        private long IpToInt(string ipString, out long prefix)
        {
            int end = ipString.Length;
            long[] parts = new long[4];
            int dotCount = 0;
            int current = 0;
            long currentValue = 0;

            for (; current < end; current++)
            {
                char ch = ipString[current];

                if (ch >= '0' && ch <= '9')
                {
                    currentValue = currentValue * 10 + (ch - '0');
                }
                else if (ch == '.')
                {
                    parts[dotCount++] = currentValue;
                    currentValue = 0;
                }
                else
                {
                    break;
                }
            }

            parts[dotCount] = currentValue;
            prefix = parts[0];
            return (parts[0] << 24) | ((parts[1] & 0xff) << 16) | ((parts[2] & 0xff) << 8) | (parts[3] & 0xff);
        }


        // 二分逼近 O(logN)  
        private long BinarySearch(long low, long high, long k)
        {
            long M = 0;
            while (low <= high)
            {
                long mid = low + ((high - low) >> 1);
                if (endArr[mid] >= k)
                {
                    M = mid;
                    high = mid - 1;
                }
                else
                {
                    low = mid + 1;
                }
            }
            return M;
        }


        //小端序
        private uint ToInt32(Span<byte> span)
        {
            return (uint)span[0] | (uint)span[1] << 8 | (uint)span[2] << 16 | (uint)span[3] << 24;
        }

        private uint ToInt24(Span<byte> span)
        {
            return (uint)span[0] | (uint)span[1] << 8 | (uint)span[2] << 16;
        }



    }

    /*
    （调用例子）：   
    string result = IPSearch3Span.Instance.Query("1.2.3.4");
   --> result="亚洲|中国|香港|九龙|油尖旺|新世界电讯|810200|Hong Kong|HK|114.17495|22.327115"
    */
