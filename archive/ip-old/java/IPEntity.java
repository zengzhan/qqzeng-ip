package abc.com.ip;

public class IPEntity {
	private final String ip;
	private String country = "";
	private String subInfo = "";

	public IPEntity(final String ip) {
		this.ip = ip;
	}

	public String getIp() {
		return ip;
	}

	public String getCountry() {
		return country;
	}

	public String getSubInfo() {
		return subInfo;
	}

	public void setCountry(final String info) {
		this.country = info;
	}

	public void setSubInfo(final String info) {
		this.subInfo = info;
	}

	@Override
	public String toString() {
		return new StringBuilder(country).append(subInfo).toString();
	}

}
