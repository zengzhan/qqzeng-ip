 internal class PhoneSearch5
 {
     private static readonly Lazy<PhoneSearch5> lazy = new Lazy<PhoneSearch5>(() => new PhoneSearch5());
     public static PhoneSearch5 Instance { get { return lazy.Value; } }
     private PhoneSearch5()
     {
         LoadDat();

     }


     private byte[] dataArr;
     private int[] prefArr;
     private string[] addrispArr;


     /// <summary>
     /// 初始化二进制dat数据
     /// </summary>
     /// <param name="dataPath"></param>
     /// 


     public void LoadDat()
     {

         var dataFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"qqzeng-phone-5.0.dat");

         var dataBytes = File.ReadAllBytes(dataFilePath);

         uint[] uintHead = new uint[5];
         for (int i = 0; i < 5; i++)
         {
             uintHead[i] = BitConverter.ToUInt32(dataBytes, i * 4);
         }

         addrispArr = Encoding.UTF8.GetString(dataBytes, (int)uintHead[3], (int)uintHead[4]).Split('&');

         int startIndex = (int)(uintHead[3] + uintHead[4]);

         prefArr = new int[200];
         for (var m = 0; m < uintHead[2]; m++)
         {
             int dataIndex = m * 5 + startIndex;
             prefArr[dataBytes[dataIndex]] = (int)BitConverter.ToUInt32(dataBytes, dataIndex + 1);
         }
         dataArr = dataBytes.Skip(startIndex + (int)uintHead[2] * 5 - 1).ToArray();

     }





     public string Query(string phone)
     {
        var prefix = Convert.ToInt32(phone.Substring(0, 3));
        var suffix = Convert.ToInt32(phone.Substring(3, 4));
        var offset = prefix < 0xC8 ? prefArr[prefix] : 0;
        if (offset > 0)
        {
            var index = BitConverter.ToUInt16(dataArr, offset + (suffix << 1));
            if (index > 0)
            {
                return addrispArr[index];
            }
        
        }
        
        return "不存在";


     }




 }

    /*
    （调用例子）：    
   string result = PhoneSearch5.Instance.Query("号段|号码");
   --> result="省份|城市|区号|邮编|行政区划代码|运营商"
   
    */
