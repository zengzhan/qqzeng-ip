using System.Text;

namespace qqzeng_phone_dat
{

      /*
     ------------- qqzeng-phone-4.0.dat ----------------

     编码：UTF8  字节序：Little-Endian  

     压缩：原版txt为30M,生成dat结构为1.09M,3.0为1.95M,2.0为3.4M

     性能：每秒解析1500w+

     对比：相比其他dat更简洁更高效

     创建：qqzeng-phone-4.0 于 2024-02-25

     ------------- qqzeng-phone-4.0.dat ----------------

    */

  
    internal class PhoneSearch4Binary
    {
        private static readonly Lazy<PhoneSearch4Binary> lazy = new Lazy<PhoneSearch4Binary>(() => new PhoneSearch4Binary());
        public static PhoneSearch4Binary Instance { get { return lazy.Value; } }
        private PhoneSearch4Binary()
        {
            LoadDat();

        }

        private Dictionary<string, int> prefDict=new Dictionary<string, int>();
        private byte[] data;
        private string[] addrArr;
        private string[] ispArr;

        /// <summary>
        /// 初始化二进制dat数据
        /// </summary>
        /// <param name="dataPath"></param>
        /// 


        private void LoadDat()
        {

            var datPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"qqzeng-phone-4.0.dat");

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


            for (var m = 0; m < PrefSize; m++)
            {
                int i = m * 5 + startIndex;
                int pref = data[i];
                int index = (int)BitConverter.ToUInt32(data, i + 1);
                prefDict[pref.ToString()] = index;
            }

        }





        public string Query(string phone)
        {
            var prefix = phone.Substring(0, 3);
            var suffix = Convert.ToInt32(phone.Substring(3, 4));
            var addrispIndex = 0;
          
            if (prefDict.TryGetValue(prefix, out int start))
            {
                int p = start + suffix * 2;
                addrispIndex = BitConverter.ToUInt16(data, p);
            }

            if (addrispIndex == 0)
            {
                return "不存在";
            }
            return addrArr[addrispIndex >> 5] + "|" + ispArr[addrispIndex & 0x001F];
        }




    }

    /*
    （调用例子）：    
   string result = PhoneSearch4Binary.Instance.Query("号段|号码");
   --> result="省份|城市|区号|邮编|行政区划代码|运营商"
    */
}

