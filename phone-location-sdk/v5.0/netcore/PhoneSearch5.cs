    internal class PhoneSearch5
    {
        private static readonly Lazy<PhoneSearch5> lazy = new Lazy<PhoneSearch5>(() => new PhoneSearch5());
        public static PhoneSearch5 Instance { get { return lazy.Value; } }
        private PhoneSearch5()
        {
            LoadDat();

        }

        private byte[] indexData;
        private int[] prefixData;
        private string[] addrispStrings;

        public void LoadDat()
        {

            var filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"qqzeng-phone-5.0.dat");
            var bytes = File.ReadAllBytes(filePath);
            Span<byte> span = bytes;
            uint[] header = new uint[5];
            for (int i = 0; i < 5; i++)
            {
                header[i] = BitConverter.ToUInt32(span.Slice(i * 4, 4));
            }
            addrispStrings = Encoding.UTF8.GetString(span.Slice((int)header[3], (int)header[4])).Split('&');
            int startIndex = (int)(header[3] + header[4]);
            prefixData = new int[200];
            for (var m = 0; m < header[2]; m++)
            {
                int dataIndex = 5 * m + startIndex;
                prefixData[span[dataIndex]] = BitConverter.ToInt32(span.Slice(dataIndex + 1, 4));
            }
            indexData = span[(int)(startIndex + 5 * header[2])..].ToArray();
        }





        public string Query(ReadOnlySpan<char> phone)
        {
            int prefix = int.Parse(phone[..3]);
            int suffix = int.Parse(phone.Slice(3, 4));
            int offset = prefix < 0xC8 ? prefixData[prefix] : 0;
            if (offset > 0)
            {
                var index = BitConverter.ToUInt16(indexData, offset + (suffix << 1));
                if (index > 0)
                {
                    return addrispStrings[index];
                }
            }

            return "";//不存在
        }




    }

    /*
    （调用例子）：    
   string result = PhoneSearch5.Instance.Query("号段|号码");
   --> result="省份|城市|区号|邮编|行政区划代码|运营商" 
    */
}

