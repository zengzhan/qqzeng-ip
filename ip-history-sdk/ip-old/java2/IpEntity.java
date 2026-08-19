package qqzeng.ip;

import java.io.IOException;
import java.io.RandomAccessFile;


public class IpEntity {
	private long beginIP = 0L;
	private long endIP = 0L;
	private String country = "未知国家";
	private String area = "无";
	
	private static final byte AREA_FOLLOWED = 0x01;
	private static final byte NO_AREA = 0x2;
	
	public long getBeginIP() {
		return beginIP;
	}
	public long getEndIP() {
		return endIP;
	}
	public String getCountry() {
		return country;
	}
	public String getArea() {
		return area;
	}
	
	public static String getAreaFromFile(RandomAccessFile ipFile, long pos) {
		String area = "无";
		try {
			ipFile.seek(pos);
			switch (ipFile.readByte()) {
			case AREA_FOLLOWED:
			case NO_AREA:
				pos = IpIO.readLong3(ipFile, pos + 1);
				if (pos > 0)
					area = IpIO.readString(ipFile, pos);
				break;
			default:
				area = IpIO.readString(ipFile, pos);
				break;
			}
		} catch (Exception e) {
			e.printStackTrace();
		}
		return area;
	}
	
	public IpEntity(long startIP, long endIP) {
		this.beginIP = startIP;
		this.endIP = endIP;
	}
	
	public IpEntity(RandomAccessFile ipFile, long startIP, long pos) {
		this.beginIP = startIP;
		endIP = IpIO.readLong4(ipFile, pos);
		try {
			ipFile.seek(pos + 4);
			switch (ipFile.readByte()) {
			case AREA_FOLLOWED:
				pos = IpIO.readLong3(ipFile, pos + 5);
				ipFile.seek(pos);
				switch (ipFile.readByte()) {
				case NO_AREA:
					country = IpIO.readString(ipFile, IpIO.readLong3(ipFile, pos + 1));
					pos += 4;
					break;
				default:
					country = IpIO.readString(ipFile, pos);
					pos = ipFile.getFilePointer();
					break;
				}
				break;
			case NO_AREA:
				country = IpIO.readString(ipFile, IpIO.readLong3(ipFile, pos + 5));
				pos += 8;
				break;
			default:
				country = IpIO.readString(ipFile, pos + 4);
				pos = ipFile.getFilePointer();
				break;
			}
			area = getAreaFromFile(ipFile, pos);
		} catch (IOException e) {
			e.printStackTrace();
		}
	}
}