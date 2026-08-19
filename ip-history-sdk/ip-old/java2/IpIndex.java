package qqzeng.ip;

import java.io.IOException;
import java.io.RandomAccessFile;

public class IpIndex {
	private long startIp = 0L;
	private long ipPos = 0L;

	public long getStartIp() {
		return startIp;
	}

	public long getIpPos() {
		return ipPos;
	}

	public IpIndex(RandomAccessFile ipFile, long pos) {
		startIp = IpIO.readLong4(ipFile, pos);
		ipPos = IpIO.readLong3(ipFile, pos + 4);
		if (-1 == startIp || -1 == ipPos) {
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