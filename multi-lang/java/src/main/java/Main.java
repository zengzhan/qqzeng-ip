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
        String customPath = args.length > 0 ? args[0] : dbPath;
        if (customPath == null || !new File(customPath).exists()) {
            System.out.println("Database file not found: " + customPath);
            return;
        }

        QzdbSearcher searcher = QzdbSearcher.getInstance();
        searcher.load(customPath);

        System.out.println("Version code: " + searcher.getVersionCode()
            + ", pools: " + searcher.getPoolCount());
        String[] fields = searcher.getFieldNames();
        System.out.print("Fields (" + fields.length + "):");
        for (String f : fields) System.out.print(" " + f);
        System.out.println("\n");

        String queryIp = args.length > 1 ? args[1] : "223.85.243.88";
        String result = searcher.findStr(queryIp);
        System.out.println("find(\"" + queryIp + "\") => " + (result != null ? result : "(null)"));

        System.out.println("\n--- Structured fields for " + queryIp + " ---");
        IpLocation loc = searcher.find(queryIp);
        if (loc != null) {
            String[] vals = loc.getValues();
            for (int i = 0; i < fields.length && i < vals.length; i++) {
                System.out.println("  " + fields[i] + ": " + vals[i]);
            }
        }
        System.out.println("TEST_PASS");
    }
}
