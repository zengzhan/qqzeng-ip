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
             return addrispArr[BitConverter.ToUInt16(dataArr, offset + (suffix << 1))];
         }
         else
         {
             return "不存在";
         }


     }




 }

    /*
    （调用例子）：    
   string result = PhoneSearch5.Instance.Query("号段|号码");
   --> result="省份|城市|区号|邮编|行政区划代码|运营商"

    查询 qqzeng-phone-5.0.dat 882万 ->0.289秒       每秒3051.9031141868513万次
    查询 qqzeng-phone-5.0.dat 1134万 ->0.36秒       每秒3150万次
    查询 qqzeng-phone-5.0.dat 1290万 ->0.424秒      每秒3042.4528301886794万次
    查询 qqzeng-phone-5.0.dat 1536万 ->0.49秒       每秒3134.6938775510203万次
    查询 qqzeng-phone-5.0.dat 322万 ->0.105秒       每秒3066.666666666667万次
    查询 qqzeng-phone-5.0.dat 362万 ->0.119秒       每秒3042.016806722689万次
    查询 qqzeng-phone-5.0.dat 888万 ->0.296秒       每秒3000万次
    查询 qqzeng-phone-5.0.dat 982万 ->0.314秒       每秒3127.388535031847万次
    查询 qqzeng-phone-5.0.dat 1212万 ->0.389秒      每秒3115.681233933162万次
    查询 qqzeng-phone-5.0.dat 1288万 ->0.429秒      每秒3002.3310023310023万次
    查询 qqzeng-phone-5.0.dat 1264万 ->0.402秒      每秒3144.278606965174万次
    查询 qqzeng-phone-5.0.dat 470万 ->0.152秒       每秒3092.105263157895万次
    查询 qqzeng-phone-5.0.dat 1078万 ->0.353秒      每秒3053.8243626062326万次
    查询 qqzeng-phone-5.0.dat 1346万 ->0.432秒      每秒3115.740740740741万次
    */
