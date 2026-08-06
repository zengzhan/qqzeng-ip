package com.qqzeng.qzdb;

import java.io.File;
import java.util.Optional;

/**
 * 命令行一键验证入口点 (适应 v2.4 新规范 API)
 */
public class Main {
    public static void main(String[] args) {
        if (args.length < 2) {
            System.err.println("Usage: java -cp build com.qqzeng.qzdb.Main <dbPath> <ip>");
            System.exit(1);
        }

        String dbPath = args[0];
        String ip = args[1];

        try {
            QzdbReader reader = new QzdbReader.Builder(new File(dbPath)).build();

            // 如果传了第 3 个参数，只输出特定格式
            if (args.length >= 3 && "pipe".equalsIgnoreCase(args[2])) {
                System.out.print(reader.findStr(ip));
                return;
            }

            Optional<GeoInfo> infoOpt = reader.find(ip);
            if (infoOpt.isPresent()) {
                GeoInfo info = infoOpt.get();
                System.out.println(info.toPipeString());
            } else {
                System.out.println("Not Found");
            }

            reader.close();
        } catch (QzdbException e) {
            System.err.println("QZDB Error: " + e.getMessage());
            System.exit(2);
        }
    }
}
