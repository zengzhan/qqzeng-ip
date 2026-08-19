package abc.com.ip;

public class IPIndex {
	public final long minIP;
	public final long maxIP;
	public final int recordOffset;

	public IPIndex(final long minIP, final long maxIP, final int recordOffset) {
		this.minIP = minIP;
		this.maxIP = maxIP;
		this.recordOffset = recordOffset;
	}
}
