 // 6.0版本 性能 最快 超过之前所有版本  2023-06-06
    public class IpDbSearch
    {
        private static readonly Lazy<IpDbSearch> lazy = new Lazy<IpDbSearch>(() => new IpDbSearch());
        public static IpDbSearch Instance { get { return lazy.Value; } }

        private static byte[] data;
        private static string[] geoispArr;
        private static int nodeCount;
        private static readonly int maxValue = 16777215;
        private static readonly int endMask = 0x800000;
        private static readonly int inverseMask = ~endMask; 

        static IpDbSearch()
        {
            LoadDb();
        }

        private static void LoadDb()
        {
            string datPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"qqzeng-ip-6.0-ultimate.db");
            data = File.ReadAllBytes(datPath);
            nodeCount = BitConverter.ToInt32(data, 0);

            var offset = 4 + 65536 * 3 + nodeCount * 6;

            geoispArr = Encoding.UTF8.GetString(data, offset, data.Length - offset).Split('\t');


        }

        public string Find(string ip)
        {
          
            var suffix = IpToInt(ip, out ushort prefix);
            var record = ReadPref(prefix);
            while ((record & endMask) != endMask)
            {
                int bit = (suffix >> 15) & 1;
                record = ReadNode(record, bit);
                suffix <<= 1;
            }

            //全球旗舰版
             return geoispArr[record & inverseMask];

            //国内精华版  国外拓展版 
            // return record == maxValue ? "||||||||||" : geoispArr[record & inverseMask];

        }


        private int ReadNode(int nodeNumber, int bit)
        {
            var offset = 196612 + nodeNumber * 6 + bit * 3;
            return data[offset] << 16 | data[offset + 1] << 8 | data[offset + 2];
        }

        private int ReadPref(int prefix)
        {
            var offset = 4 + prefix * 3;
            return data[offset] << 16 | data[offset + 1] << 8 | data[offset + 2];
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

    }
