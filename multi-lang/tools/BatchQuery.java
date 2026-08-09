package com.qqzeng.qzdb;

import java.io.*;
import java.math.BigInteger;
import java.nio.file.*;

/**
 * Batch IP query runner for Java (v2.4 API).
 * Usage: java -cp <build> com.qqzeng.qzdb.BatchQuery <db_path> <v4_test> <v4_out> <v6_test> <v6_out>
 */
public class BatchQuery {
    public static void main(String[] args) throws Exception {
        if (args.length < 5) {
            System.err.println("Usage: BatchQuery <db_path> <v4_test> <v4_out> <v6_test> <v6_out>");
            System.exit(1);
        }

        String dbPath = args[0];
        String v4Test = args[1];
        String v4Out = args[2];
        String v6Test = args[3];
        String v6Out = args[4];

        QzdbReader searcher = new QzdbReader.Builder(new File(dbPath)).build();

        // V4
        if (Files.exists(Paths.get(v4Test))) {
            var lines = Files.readAllLines(Paths.get(v4Test));
            var results = new String[lines.size()];
            for (int i = 0; i < lines.size(); i++) {
                String line = lines.get(i).trim();
                if (line.isEmpty()) { results[i] = line + "|"; continue; }
                int ip = Integer.parseUnsignedInt(line);
                GeoInfo info = searcher.findUint(ip).orElse(null);
                String pipeStr = (info == null) ? "" : info.toPipeString();
                results[i] = line + "|" + pipeStr;
            }
            Files.writeString(Paths.get(v4Out), String.join("\n", results) + "\n");
            System.err.println("  Java V4: " + results.length + " queries");
        }

        // V6 (high:low DECIMAL -> 16-byte big-endian address -> findBytes)
        // NOTE: cross_verify.py emits `f'{high}:{low}'` with decimal integers；
        // 所有语言 runner 必须按十进制解析，否则 V6 结果会静默错位。
        if (Files.exists(Paths.get(v6Test))) {
            var lines6 = Files.readAllLines(Paths.get(v6Test));
            var results6 = new String[lines6.size()];
            for (int i = 0; i < lines6.size(); i++) {
                String line = lines6.get(i).trim();
                if (line.isEmpty()) { results6[i] = line + "|"; continue; }
                String[] parts = line.split(":");
                if (parts.length != 2) { results6[i] = line + "|"; continue; }
                byte[] addr = v6Bytes(parts[0].trim(), parts[1].trim());
                GeoInfo info = searcher.findBytes(addr).orElse(null);
                String pipeStr = (info == null) ? "" : info.toPipeString();
                results6[i] = line + "|" + pipeStr;
            }
            Files.writeString(Paths.get(v6Out), String.join("\n", results6) + "\n");
            System.err.println("  Java V6: " + results6.length + " queries");
        }

        System.err.println("  Java DONE");
    }

    private static byte[] v6Bytes(String highDec, String lowDec) {
        BigInteger high = new BigInteger(highDec, 10);
        BigInteger low = new BigInteger(lowDec, 10);
        BigInteger addr = high.shiftLeft(64).or(low);
        byte[] b = addr.toByteArray();
        byte[] out = new byte[16];
        if (b.length >= 16) {
            System.arraycopy(b, b.length - 16, out, 0, 16);
        } else {
            System.arraycopy(b, 0, out, 16 - b.length, b.length);
        }
        return out;
    }
}
