 internal class PhoneSearch4Best
 {
     private static readonly Lazy<PhoneSearch4Best> lazy = new Lazy<PhoneSearch4Best>(() => new PhoneSearch4Best());
     public static PhoneSearch4Best Instance { get { return lazy.Value; } }
     private PhoneSearch4Best()
     {
         LoadDat();

     }

     private int[] prefArr = new int[200];
     private byte[] data;
     private string[] addrArr;
     private string[] ispArr;

     /// <summary>
     /// 初始化二进制dat数据
     /// </summary>
     /// <param name="dataPath"></param>
     /// 


     public void LoadDat()
     {

         var datPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"qqzeng-phone-4.0.dat");

         data = File.ReadAllBytes(datPath);

         var PrefSize = BitConverter.ToUInt32(data, 0);
         var PhoneSize = BitConverter.ToUInt32(data, 4);
         var descLength = BitConverter.ToUInt32(data, 8);
         var ispLength = BitConverter.ToUInt32(data, 12);
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
             prefArr[pref] = index;
         }




     }





     public string Query(string phone)
     {
         var prefix = Convert.ToInt32(phone.Substring(0, 3));
         var suffix = Convert.ToInt32(phone.Substring(3, 4));

         if (prefix < 0xC8 && prefArr[prefix] > 0)
         {
             var addrispIndex = BitConverter.ToUInt16(data, prefArr[prefix] + (suffix << 1));
             return addrArr[addrispIndex >> 5] + "|" + ispArr[addrispIndex & 0x001F];
         }
         else
         {
             return "不存在";
         }


     }




 }

 /*
 （调用例子）：    
string result = PhoneSearch4Best.Instance.Query("号段|号码");
--> result="省份|城市|区号|邮编|行政区划代码|运营商"
 */
