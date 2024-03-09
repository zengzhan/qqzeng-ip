 internal class PhoneSearch4Best
 {
     private static readonly Lazy<PhoneSearch4Best> lazy = new Lazy<PhoneSearch4Best>(() => new PhoneSearch4Best());
     public static PhoneSearch4Best Instance { get { return lazy.Value; } }
     private PhoneSearch4Best()
     {
         LoadDat();

     }

       private byte[] data;
       private int[] prefArr;
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

           uint[] a = new uint[5];
           for (int i = 0; i < 5; i++)
           {
               a[i] = BitConverter.ToUInt32(data, i * 4);
           }

           var h = 20;
           int startIndex = (int)(h + a[2] + a[3]);
         
           addrArr = Encoding.UTF8.GetString(data, h, (int)a[2]).Split('&'); 
           ispArr = Encoding.UTF8.GetString(data, h + (int)a[2], (int)a[3]).Split('&');

           prefArr = new int[200];
           for (var m = 0; m < a[0]; m++)
           {
               int i = m * 5 + startIndex;              
               prefArr[data[i]] = (int)BitConverter.ToUInt32(data, i + 1); 
           }


       }





       public string Query(string phone)
       {
           var prefix = Convert.ToInt32(phone.Substring(0, 3));
           var suffix = Convert.ToInt32(phone.Substring(3, 4));
           var offset = prefix < 0xC8 ? prefArr[prefix] : 0;
           if (offset > 0)
           {
               var n = BitConverter.ToUInt16(data, offset + (suffix << 1));
               return addrArr[n >> 5] + "|" + ispArr[n & 0x001F];
           }
           else
           {
               return "不存在";
           }


       }




   }




 }

 /*
 （调用例子）：    
string result = PhoneSearch4Best.Instance.Query("号段|号码");
--> result="省份|城市|区号|邮编|行政区划代码|运营商"
 */
