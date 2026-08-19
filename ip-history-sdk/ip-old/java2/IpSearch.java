package qqzeng.ip;

import java.io.IOException;
import java.io.RandomAccessFile;
import java.sql.SQLException;

public class IpSearch {
	private final static String IP_FILE = getIpFilePath();
	public static final int IP_RECORD_LENGTH = 7;
	private static IpSearch instance = null;
	private RandomAccessFile ipFile = null;

	public RandomAccessFile getIpFile() {
		return ipFile;
	}

	public static String getIpFilePath() {
		try {
			return IpSearch.class.getClassLoader().getResource("qqzeng-ip.dat")
					.getPath();
		} catch (Exception e) {
			System.out.println("没有找到qqzeng-ip.dat文件");
			e.printStackTrace();
			return null;
		}
	}

	public IpSearch() {
		try {
			if (null == IP_FILE)
				System.exit(1);
			ipFile = new RandomAccessFile(IP_FILE, "r");
		} catch (IOException e) {
			System.err.println("无法打开" + IP_FILE + "文件");
		}
	}

	public void closeIpFile() {
		try {
			if (ipFile != null) {
				ipFile.close();
			}
		} catch (IOException e) {
			e.printStackTrace();
		} finally {
			if (null != ipFile)
				ipFile = null;
		}
	}

	public synchronized static IpSearch getInstance() {
		if (null == instance)
			instance = new IpSearch();
		return instance;
	}

	public IpEntity find(String ip) {
		long ipValue = IpUtils.ipToLong(ip);
		IpHeader header = new IpHeader(ipFile);
		long first = header.getIpBegin();
		int left = 0;
		int right = (int) ((header.getIpEnd() - first) / IP_RECORD_LENGTH);
		int middle = 0;
		IpIndex middleIndex = null;
		// 二分查找
		while (left <= right) {
			// 无符号右移，防止溢出
			middle = (left + right) >>> 1;
			middleIndex = new IpIndex(ipFile, first + middle
					* IP_RECORD_LENGTH);
			if (ipValue > middleIndex.getStartIp())
				left = middle + 1;
			else if (ipValue < middleIndex.getStartIp())
				right = middle - 1;
			else
				return new IpEntity(ipFile, middleIndex.getStartIp(),
						middleIndex.getIpPos());
		}
		// 找不到精确的，取在范围内的
		middleIndex = new IpIndex(ipFile, first + right * IP_RECORD_LENGTH);
		IpEntity record = new IpEntity(ipFile, middleIndex.getStartIp(),
				middleIndex.getIpPos());
		if (ipValue >= record.getBeginIP() && ipValue <= record.getEndIP()) {
			return record;
		} else {
			// 找不到相应的记录
			return new IpEntity(0L, ipValue);
		}
	}

	public static void main(String[] args) throws SQLException {
		String ip = "1.197.224.9";

		IpSearch finder = IpSearch.getInstance();
		IpEntity record = finder.find(ip);

		System.out.println(ip);
		System.out.println(record.getCountry());
		System.out.println(record.getArea());

/*		1.197.224.9
		中国
		河南|周口|商水|电信|411623|亚洲|China|CN|114.60604|33.53912 */

		finder.closeIpFile();
	}
}