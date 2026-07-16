package qzdb;

public class IpLocation {
    private final String[] values;

    public IpLocation(String[] values) {
        this.values = values;
    }

    public String[] getValues() {
        return values;
    }

    public String toPipeString() {
        return String.join("|", values);
    }
}
