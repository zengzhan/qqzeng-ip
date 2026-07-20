/**
 * QzdbSearcher - Java SDK calling example
 *
 * Usage: java Main.java
 * Place qqzeng_ip_std_china.qzdb in the same directory or specify the path.
 */

import qzdb.QzdbSearcher;
import qzdb.IpLocation;
import java.io.File;

public class Main {
    static String findDb() {
        String[] candidates = {
            "qqzeng_ip_std_china.qzdb",
            "../data/qqzeng_ip_std_china.qzdb",
            "data/qqzeng_ip_std_china.qzdb",
        };
        for (String c : candidates) {
            if (new File(c).exists()) return c;
        }
        return null;
    }

    public static void main(String[] args) throws Exception {
        String dbPath = findDb();
        if (dbPath == null) {
            System.out.println("Database file not found");
            return;
        }

        QzdbSearcher searcher = QzdbSearcher.getInstance();
        searcher.load(dbPath);

        System.out.println("Version code: " + searcher.getVersionCode()
            + ", pools: " + searcher.getPoolCount());
        String[] fields = searcher.getFieldNames();
        System.out.print("Fields (" + fields.length + "):");
        for (String f : fields) System.out.print(" " + f);
        System.out.println("\n");

        // Query sample V4 IPs
        for (String ip : new String[]{"114.114.114.114", "223.5.5.5", "8.8.8.8"}) {
            String result = searcher.findStr(ip);
            System.out.println("find(\"" + ip + "\") => " + (result != null ? result : "(null)"));
        }

        // Query a V6 IP
        String result = searcher.findStr("2408:8000:9000::1");
        System.out.println("find(\"2408:8000:9000::1\") => " + (result != null ? result : "(null)"));

        // Get structured fields
        System.out.println("\n--- Structured fields for 114.114.114.114 ---");
        IpLocation loc = searcher.find("114.114.114.114");
        if (loc != null) {
            String[] vals = loc.getValues();
            for (int i = 0; i < fields.length; i++) {
                System.out.println("  " + fields[i] + ": " + vals[i]);
            }
        }
        System.out.println("TEST_PASS");
    }
}
