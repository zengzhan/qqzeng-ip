package qqzeng.ip;

import java.io.IOException;
import java.io.RandomAccessFile;

public class IpHeader {
	private long ipBegin = 0L;
	private long ipEnd = 0L;

	public long getIpBegin() {
		return ipBegin;
	}

	public long getIpEnd() {
		return ipEnd;
	}

	public IpHeader(RandomAccessFile ipFile) {
		ipBegin = IpIO.readLong4(ipFile, 0);
		ipEnd = IpIO.readLong4(ipFile, 4);
		if (-1 == ipBegin || -1 == ipEnd) {
			System.out.println("IP地址信息文件格式有错误");
			try {
				ipFile.close();
			} catch (IOException e) {
				e.printStackTrace();
				ipFile = null;
			}
			System.exit(1);
		}
	}

}