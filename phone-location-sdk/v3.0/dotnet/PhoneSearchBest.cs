    internal class PhoneSearchBest
    {
        private static readonly Lazy<PhoneSearchBest> lazy = new Lazy<PhoneSearchBest>(() => new PhoneSearchBest());
        public static PhoneSearchBest Instance { get { return lazy.Value; } }
        private PhoneSearchBest()
        {
            LoadDat();

        }

        private byte[] data;
        private long[,] phone2D;
        private string[] addrArr;
        private string[] ispArr;

        /// <summary>
        /// 初始化二进制dat数据
        /// </summary>
        /// <param name="dataPath"></param>
        /// 


        private void LoadDat()
        {

            var datPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"qqzeng-phone-3.0.dat");

            data = File.ReadAllBytes(datPath);

            var PrefSize = BitConverter.ToUInt32(data, 0);

            var descLength = BitConverter.ToUInt32(data, 8);
            var ispLength = BitConverter.ToUInt32(data, 12);

            var PhoneSize = BitConverter.ToUInt32(data, 4);
            var verNum = BitConverter.ToUInt32(data, 16);

            var headLength = 20;
            int startIndex = (int)(headLength + descLength + ispLength);

            //内容数组        
            string descString = Encoding.UTF8.GetString(data, headLength, (int)descLength);
            addrArr = descString.Split('&');

            //运营商数组        
            string ispString = Encoding.UTF8.GetString(data, headLength + (int)descLength, (int)ispLength);
            ispArr = ispString.Split('&');


            phone2D = new long[200, 10000];
            for (var m = 0; m < PrefSize; m++)
            {
                int i = m * 7 + startIndex;
                int pref = data[i];
                int index = (int)BitConverter.ToUInt32(data, i + 1);
                int length = BitConverter.ToUInt16(data, i + 5);

                for (int n = 0; n < length; n++)
                {
                    int p = (int)(startIndex + PrefSize * 7 + (n + index) * 4);
                    var suff = BitConverter.ToUInt16(data, p);
                    var addrispIndex = BitConverter.ToUInt16(data, p + 2);
                    phone2D[pref, suff] = addrispIndex;
                }

            }



        }





        public string Query(string phone)
        {
            var prefix = Convert.ToInt32(phone.Substring(0, 3));//前缀
            var suffix = Convert.ToInt32(phone.Substring(3, 4));//后缀
            var addrispIndex = phone2D[prefix, suffix];
            if (addrispIndex == 0)
            {
                return "";
            }
            return addrArr[addrispIndex / 100] + "|" + ispArr[addrispIndex % 100];

        }




    }

    /*
    （调用例子）：    
   string result = PhoneSearchBest.Instance.Query("号段|号码");
   --> result="省份|城市|区号|邮编|行政区划代码|运营商"
    */
