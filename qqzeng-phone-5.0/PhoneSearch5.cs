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
    查询 qqzeng-phone-5.0.dat 9488万 ->2.075秒      每秒4572.530120481927万次
    查询 qqzeng-phone-5.0.dat 11614万 ->2.517秒     每秒4614.223281684545万次
    查询 qqzeng-phone-5.0.dat 11416万 ->2.466秒     每秒4629.359286293592万次
    查询 qqzeng-phone-5.0.dat 6300万 ->1.382秒      每秒4558.610709117222万次
    查询 qqzeng-phone-5.0.dat 7146万 ->1.537秒      每秒4649.316851008458万次
    查询 qqzeng-phone-5.0.dat 7358万 ->1.596秒      每秒4610.275689223057万次
    查询 qqzeng-phone-5.0.dat 11182万 ->2.444秒     每秒4575.2864157119475万次
    查询 qqzeng-phone-5.0.dat 11198万 ->2.435秒     每秒4598.767967145791万次
    查询 qqzeng-phone-5.0.dat 11736万 ->2.537秒     每秒4625.936145053212万次
    查询 qqzeng-phone-5.0.dat 7444万 ->1.645秒      每秒4525.227963525836万次
    查询 qqzeng-phone-5.0.dat 7878万 ->1.729秒      每秒4556.390977443609万次
    查询 qqzeng-phone-5.0.dat 10108万 ->2.204秒     每秒4586.206896551724万次
    查询 qqzeng-phone-5.0.dat 10552万 ->2.284秒     每秒4619.964973730298万次
    查询 qqzeng-phone-5.0.dat 7824万 ->1.711秒      每秒4572.764465225015万次
    查询 qqzeng-phone-5.0.dat 11712万 ->2.565秒     每秒4566.081871345029万次
    */
}

