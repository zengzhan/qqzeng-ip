package com.qqzeng.qzdb;

import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.io.PrintWriter;
import java.io.BufferedWriter;
import java.io.OutputStreamWriter;
import java.nio.charset.StandardCharsets;
import java.nio.file.Path;
import java.nio.file.Paths;

/**
 * 常驻流式批量查询子进程（run_batch_test_suite.py 的 Java 适配器后端）。
 *
 * stdin 每行一个 IP；stdout 每行回一个 pipe 串。必须逐行 flush：
 * stdout 到 pipe 是块缓冲，父进程按行读、不送 EOF，不 flush 就互相等死。
 */
public final class BatchStreamQuery {

    public static void main(String[] args) throws Exception {
        if (args.length < 1) {
            System.err.println("usage: BatchQuery <db.qzdb>");
            System.exit(1);
        }
        Path db = Paths.get(args[0]);
        try (QzdbReader reader = new QzdbReader.Builder(db.toFile()).build();
             BufferedReader in = new BufferedReader(
                     new InputStreamReader(System.in, StandardCharsets.UTF_8));
             PrintWriter out = new PrintWriter(
                     new BufferedWriter(new OutputStreamWriter(System.out, StandardCharsets.UTF_8), 1 << 13))) {
            String line;
            while ((line = in.readLine()) != null) {
                String ip = line.trim();
                if (ip.isEmpty()) continue;
                String pipe = reader.find(ip).map(GeoInfo::toPipeString).orElse("");
                out.print(pipe);
                out.print('\n');
                out.flush();
            }
        }
    }

    private BatchStreamQuery() {
    }
}
