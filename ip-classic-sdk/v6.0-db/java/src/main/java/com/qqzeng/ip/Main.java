package com.qqzeng.ip;

/**
 * qqzeng-ip 6.0 (.db) Java 版冒烟示例
 * 用法: java -cp classes com.qqzeng.ip.Main [db_path] [ip ...]
 */
public final class Main {    private Main() {
    }

    public static void main(String[] args) {
        IpDbSearch db;
        if (args.length > 0) {
            db = new IpDbSearch(args[0]);
        } else {
            db = IpDbSearch.getInstance();
        }
        System.out.printf("数据库加载完成: nodes=%d, strings=%d%n", db.nodeCount(), db.stringCount());

        String[] demo = {"1.10.11.12", "8.8.8.8", "58.213.198.68", "255.255.255.255", "1..2.3"};
        for (String ip : demo) {
            System.out.println(ip + " -> " + db.find(ip));
        }
        for (int i = 1; i < args.length; i++) {
            System.out.println(args[i] + " -> " + db.find(args[i]));
        }

        // 快速压测
        long seed = 123456789L;
        long checksum = 0;
        long t0 = System.nanoTime();
        for (int i = 0; i < 1_000_000; i++) {
            seed = seed * 48271L % 2147483647L;
            checksum += db.findUint((int) seed).length();
        }
        double ms = (System.nanoTime() - t0) / 1e6;
        System.out.printf("bench: 1000000 queries, %.0f qps (checksum %d)%n",
                1_000_000 / (ms / 1000), checksum);
    }
}
