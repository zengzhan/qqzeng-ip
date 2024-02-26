import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Paths;
import java.util.HashMap;

//qqzeng-phone-4.0.dat 
//jdk-21.0.2

public class PhoneSearch4Binary {
    private static final PhoneSearch4Binary INSTANCE = new PhoneSearch4Binary();
    private HashMap<String, Integer> prefDict = new HashMap<>();
    private byte[] data;
    private String[] addrArr;
    private String[] ispArr;

    private PhoneSearch4Binary() {
        loadDat();
    }

    public static PhoneSearch4Binary getInstance() {
        return INSTANCE;
    }

    private void loadDat() {
        String datPath = Paths.get(System.getProperty("user.dir"), "qqzeng-phone-4.0.dat").toString();

        try {
            data = Files.readAllBytes(Paths.get(datPath));

            long prefSize = bytesToUInt(data, 0);
            long descLength = bytesToUInt(data, 8);
            long ispLength = bytesToUInt(data, 12);

            int headLength = 20;
            int startIndex = headLength + (int) descLength + (int) ispLength;

            // 内容数组
            String descString = new String(data, headLength, (int) descLength);
            addrArr = descString.split("&");

            // 运营商数组
            String ispString = new String(data, headLength + (int) descLength, (int) ispLength);
            ispArr = ispString.split("&");

            for (int m = 0; m < prefSize; m++) {
                int i = m * 5 + startIndex;
                int pref = data[i] & 0xFF;
                int index = (int) bytesToUInt(data, i + 1);
                prefDict.put(String.valueOf(pref), index);
            }
        } catch (IOException e) {
            e.printStackTrace();
        }
    }

    public String query(String phone) {
        String prefix = phone.substring(0, 3);
        int suffix = Integer.parseInt(phone.substring(3, 7));
        int addrispIndex = 0;

        if (prefDict.containsKey(prefix)) {
            int start = prefDict.get(prefix);
            int p = start + suffix * 2;
            addrispIndex = bytesToUShort(data, p);
        }

        if (addrispIndex == 0) {
            return "不存在";
        }

        return addrArr[addrispIndex >> 5] + "|" + ispArr[addrispIndex & 0x001F];
    }

    private static long bytesToUInt(byte[] bytes, int offset) {
        return ((long) (bytes[offset + 3] & 0xFF) << 24) |
               ((long) (bytes[offset + 2] & 0xFF) << 16) |
               ((long) (bytes[offset + 1] & 0xFF) << 8) |
               (bytes[offset] & 0xFF);
    }
    
    private static int bytesToUShort(byte[] bytes, int offset) {
        return ((bytes[offset + 1] & 0xFF) << 8) | (bytes[offset] & 0xFF);
    }
    
    


	public static void main(String[] args) {

          // 创建 PhoneSearch4Binary 的实例
          PhoneSearch4Binary phoneSearch = PhoneSearch4Binary.getInstance();

          // 调用 query 方法进行查询
          String result = phoneSearch.query("1933574");
  
          // 打印查询结果
          System.out.println(result);


		/*
		 *  1933574->山西|临汾|041000|0357|141000|电信
		 */

	}

}
