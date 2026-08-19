import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.*;

// 手机号码归属地查询 java 解析 3.0 内存版

public class PhoneSearchBest {

	private int[][] phone2D;
	private String[] addrArr;
	private String[] ispArr;

	private static PhoneSearchBest instance = null;

	private PhoneSearchBest() {
		Path path = Paths.get(System.getProperty("user.dir"), "qqzeng-phone-3.0.dat");

		byte[] data = null;
		try {
			data = Files.readAllBytes(path);
		} catch (IOException e) {
			e.printStackTrace();
		}

		long PrefNum = BytesToInt(data, 0);
		// long PhoneNum = BytesToInt(data, 4);
		long addrLen = BytesToInt(data, 8);
		long ispLen = BytesToInt(data, 12);
		// long ver = BytesToInt(data, 16);

		int headLen = 20;
		int startIndex = (int) (headLen + addrLen + ispLen);

		// 内容数组
		String addrStr = new String(Arrays.copyOfRange(data, headLen, headLen + (int) addrLen), StandardCharsets.UTF_8);
		addrArr = addrStr.split("&");

		// 运营商数组
		String ispStr = new String(Arrays.copyOfRange(data, headLen + (int) addrLen, startIndex),
				StandardCharsets.UTF_8);
		ispArr = ispStr.split("&");

		phone2D = new int[200][10000];
		for (int m = 0; m < PrefNum; m++) {
			int i = m * 7 + startIndex;
			int pref = data[i] & 0xFF;
			int index = (int) BytesToInt(data, i + 1);
			int length = BytesToShort(data, i + 5);

			for (int n = 0; n < length; n++) {
				int p = (int) (startIndex + PrefNum * 7 + (n + index) * 4);
				int suff = BytesToShort(data, p);
				int addrispIndex = BytesToShort(data, p + 2);
				phone2D[pref][suff] = addrispIndex;
			}

		}

	}

	public static synchronized PhoneSearchBest getInstance() {
		if (instance == null)
			instance = new PhoneSearchBest();
		return instance;
	}

	public String Get(String phone) {

		int prefix = Integer.valueOf(phone.substring(0, 3));
		int suffix = Integer.valueOf(phone.substring(3, 7));

		int addrispIndex = phone2D[prefix][suffix];
		if (addrispIndex == 0) {
			return "";
		}
		return addrArr[addrispIndex / 100] + "|" + ispArr[addrispIndex % 100];

	}

	private long BytesToInt(byte[] buf, int offset) {
		return (buf[offset] & 0xFFL) | ((buf[offset + 1] & 0xFFL) << 8) | ((buf[offset + 2] & 0xFFL) << 16)
				| ((buf[offset + 3] & 0xFFL) << 24);
	}

	private int BytesToShort(byte[] buf, int offset) {
		return (buf[offset] & 0xFF) | ((buf[offset + 1] & 0xFF) << 8);
	}

	public static void main(String[] args) {

		PhoneSearchBest finder = PhoneSearchBest.getInstance();
		String phone = "1933574";
		String result = finder.Get(phone);
		System.out.println(result);

		/*
		 * 山西|临汾|041000|0357|141000|电信
		 */

	}

}
