import qzdb.QzdbSearcher;
import java.io.BufferedReader;
import java.io.InputStreamReader;

public class BatchMain {
    public static void main(String[] args) {
        if (args.length < 1) return;
        try {
            QzdbSearcher searcher = new QzdbSearcher();
            searcher.load(args[0]);
            BufferedReader reader = new BufferedReader(new InputStreamReader(System.in, "UTF-8"));
            String line;
            while ((line = reader.readLine()) != null) {
                line = line.trim();
                if (line.isEmpty()) continue;
                String res = searcher.findStr(line);
                System.out.println(res != null ? res : "");
            }
        } catch (Exception e) {
            e.printStackTrace();
        }
    }
}
