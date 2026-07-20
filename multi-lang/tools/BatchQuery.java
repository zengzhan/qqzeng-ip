package qzdb;

import qzdb.QzdbSearcher;
import qzdb.IpLocation;

import java.io.*;
import java.math.BigInteger;
import java.nio.file.*;

/**
 * Batch IP query runner for Java.
 * Usage: java -cp <build> BatchQuery <db_path> <v4_test> <v4_out> <v6_test> <v6_out>
 */
public class BatchQuery {
    public static void main(String[] args) throws Exception {
        if (args.length < 4) {
            System.err.println("Usage: BatchQuery <db_path> <v4_test> <v4_out> <v6_test> <v6_out>");
            System.exit(1);
        }

        String dbPath = args[0];
        String v4Test = args[1];
        String v4Out = args[2];
        String v6Test = args[3];
        String v6Out = args[4];

        QzdbSearcher searcher = QzdbSearcher.getInstance();
        searcher.load(dbPath);

        // V4
        if (Files.exists(Paths.get(v4Test))) {
            var lines = Files.readAllLines(Paths.get(v4Test));
            var results = new String[lines.size()];
            for (int i = 0; i < lines.size(); i++) {
                String line = lines.get(i).trim();
                if (line.isEmpty()) { results[i] = line + "|"; continue; }
                int ip = (int) Long.parseUnsignedLong(line);
                IpLocation info = searcher.findUint(ip);
                String pipeStr = (info == null) ? "" : info.toPipeString();
                results[i] = line + "|" + pipeStr;
            }
            Files.writeString(Paths.get(v4Out), String.join("\n", results) + "\n");
            System.err.println("  Java V4: " + results.length + " queries");
        }

        // V6
        if (Files.exists(Paths.get(v6Test))) {
            var lines6 = Files.readAllLines(Paths.get(v6Test));
            var results6 = new String[lines6.size()];
            for (int i = 0; i < lines6.size(); i++) {
                String line = lines6.get(i).trim();
                if (line.isEmpty()) { results6[i] = line + "|"; continue; }
                String[] parts = line.split(":");
                if (parts.length != 2) { results6[i] = line + "|"; continue; }
                BigInteger high = new BigInteger(parts[0]);
                BigInteger low = new BigInteger(parts[1]);
                BigInteger ipInt = high.shiftLeft(64).or(low);
                IpLocation info = searcher.findV6Uint(ipInt);
                String pipeStr = (info == null) ? "" : info.toPipeString();
                results6[i] = line + "|" + pipeStr;
            }
            Files.writeString(Paths.get(v6Out), String.join("\n", results6) + "\n");
            System.err.println("  Java V6: " + results6.length + " queries");
        }

        System.err.println("  Java DONE");
    }
}
