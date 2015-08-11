package abc.com.ip;

public class IPString {
	public final String string;
	/** length including the \0 end byte */
	public final int length;

	public IPString(final String string, final int length) {
		this.string = string;
		this.length = length;
	}
}
