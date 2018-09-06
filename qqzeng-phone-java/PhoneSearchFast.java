import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.*;

//编码：utf-8
//性能：每秒解析300万+
//环境：CPU i7-7700K + DDR2400 16G + win10 X64 (Release)
//创建：qqzeng-ip 于 2018-09-06  

public class PhoneSearchFast {

	private long[][] prefmap = new long[200][2];//  000-199
	private long[][] phonemap;
	private long[] phoneArr;
	private String[] addrArr;
	private String[] ispArr;

	private static PhoneSearchFast instance = null;

	private PhoneSearchFast() {
		Path path = Paths.get("C:\\Users\\qqzeng-phone.dat");

byte[] data = null;
		try {
			data = Files.readAllBytes(path);
		} catch (IOException e) {
			e.printStackTrace();
		}

		long PrefSize = BytesToLong(data[0], data[1], data[2], data[3]);
		long RecordSize = BytesToLong(data[4], data[5], data[6], data[7]);

		long descLength = BytesToLong(data[8], data[9], data[10], data[11]);
		long ispLength = BytesToLong(data[12], data[13], data[14], data[15]);

		//内容数组
		int descOffset = (int)(16 + PrefSize * 9 + RecordSize * 7);
		String descString = new String(Arrays.copyOfRange(data,  descOffset,(int)(descOffset+descLength)));  
		addrArr = descString.split("&");

		//运营商数组
		int ispOffset = (int)(16 + PrefSize * 9 + RecordSize * 7 + descLength);
		String ispString = new String(Arrays.copyOfRange(data,  ispOffset,(int)(ispOffset + ispLength))); 
		ispArr = ispString.split("&");



		//前缀区
		int m = 0;
		for (int k = 0; k < PrefSize; k++)
		{
			int i = k * 9 + 16;
			int n =data[i]& 0xFF;
			prefmap[n][0] = BytesToLong(data[i + 1], data[i + 2], data[i + 3], data[i + 4]);
			prefmap[n][1] = BytesToLong(data[i + 5], data[i + 6], data[i + 7], data[i + 8]);
			if (m < n)
			{
				for (; m < n; m++)
				{
					prefmap[m][0] = 0; prefmap[m][1] = 0;
				}
				m++;
			}
			else
			{
				m++;
			}
		}

		//索引区
		phoneArr = new long[(int)RecordSize];
		phonemap = new long[(int)RecordSize][2];
		for (int i = 0; i < RecordSize; i++)
		{
			int p = 16 + (int)PrefSize * 9 + (i * 7);
			phoneArr[i] = BytesToLong(data[p], data[1 + p], data[2 + p], data[3 + p]);
			phonemap[i][0] =BytesToLong(data[4 + p],data[5 + p],(byte)0,(byte)0); 
			phonemap[i][1] =BytesToLong(data[6 + p],(byte)0,(byte)0,(byte)0); 
		}





	}

	public static synchronized PhoneSearchFast getInstance() {
		if (instance == null)
			instance = new PhoneSearchFast();
		return instance;
	}

	public String Get(String phone) {

		int pref= Integer.valueOf(phone.substring(0, 3));
		int val = Integer.valueOf(phone.substring(0, 7));
		int low = (int)prefmap[pref][0], high = (int) prefmap[pref][1];
		if (high == 0)
		{
			return "";
		}
		int cur = low == high ? low : BinarySearch(low, high, val);
		if (cur != -1)
		{

			return addrArr[(int)phonemap[cur][0]] + "|" + ispArr[(int)phonemap[cur][1]];
		}
		else
		{
			return "";
		}



	}

	private int BinarySearch(int low, int high, long k) {
		int M = 0;
		while (low <= high) {
			int mid = (low + high)  >> 1;

			long phoneNum  = phoneArr[mid];
			if (phoneNum >= k) {
				M = mid;
				if (mid == 0) {
					break; 
				}
				high = mid - 1;
			} else
				low = mid + 1;
		}
		return M;
	}

	private long BytesToLong(byte a, byte b, byte c, byte d) {
		return (a & 0xFFL) | ((b << 8) & 0xFF00L) | ((c << 16) & 0xFF0000L) | ((d << 24) & 0xFF000000L);

	}



	

	public static void main(String[] args) {

		PhoneSearchFast finder = PhoneSearchFast.getInstance();

		String phone = "1991234";
		String result = finder.Get(phone);
		System.out.println(phone);
		System.out.println(result);

		/*
		 *  河北|唐山|063000|0315|130200|电信
		 */

	}

}
