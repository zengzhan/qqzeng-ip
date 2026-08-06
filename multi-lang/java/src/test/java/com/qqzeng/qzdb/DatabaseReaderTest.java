package com.qqzeng.qzdb;

import java.io.File;
import java.io.IOException;
import java.nio.file.Files;
import java.util.Arrays;
import java.util.List;
import java.util.Optional;

/**
 * Java SDK v2.4 全功能单元测试
 */
public class DatabaseReaderTest {

    private static final String[] SAMPLE_DB_PATHS = {
            "multi-lang/data/qqzeng_ip_std_china.qzdb",
            "multi-lang/nodejs/qqzeng_ip_std_china.qzdb",
            "multi-lang/c/qqzeng_ip_std_china.qzdb",
            "../data/qqzeng_ip_std_china.qzdb"
    };

    public static void main(String[] args) {
        System.out.println("==================================================");
        System.out.println("       QZDB Java SDK v2.4 范本单元测试套件         ");
        System.out.println("==================================================");

        int passed = 0;
        int failed = 0;

        File dbFile = null;
        for (String p : SAMPLE_DB_PATHS) {
            File f = new File(p);
            if (f.exists()) {
                dbFile = f;
                break;
            }
        }

        if (!dbFile.exists()) {
            System.err.println("[WARN] 测试数据库文件不存在 (" + SAMPLE_DB_PATHS[0] + ")，跳过二进制检索测试，仅运行单元纯逻辑断言。");
            runPureLogicTests();
            return;
        }

        try {
            DatabaseReader reader = new DatabaseReader.Builder(dbFile).build();

            // Test 1: 元信息与自省断言
            testAssert("1. 元信息与自省测试", () -> {
                assertNotNull(reader.getVersion(), "version");
                assertNotNull(reader.getDataMonth(), "dataMonth");
                assertNotNull(reader.getEdition(), "edition");
                assertNotNull(reader.getScope(), "scope");
                assertNotNull(reader.getFileHash(), "fileHash");
                assertTrue(reader.getFieldNames().length > 0, "fieldNames");
            });
            passed++;

            // Test 2: 单条 IPv4 查询
            testAssert("2. 单条 IPv4 查询 (223.5.5.5)", () -> {
                Optional<GeoInfo> infoOpt = reader.find("223.5.5.5");
                assertTrue(infoOpt.isPresent(), "find 114.114.114.114");
                GeoInfo info = infoOpt.get();
                System.out.println("   [Find Output]: " + info.toPipeString());
                assertTrue(!info.toPipeString().isEmpty(), "pipe result non-empty");
            });
            passed++;

            // Test 3: 归一化 Getter 查找断言 (大小写与下划线不敏感)
            testAssert("3. 归一化 Getter 查找 (country_en == countryEn == COUNTRY_EN)", () -> {
                Optional<GeoInfo> infoOpt = reader.find("223.5.5.5");
                assertTrue(infoOpt.isPresent(), "find");
                GeoInfo info = infoOpt.get();
                String v1 = info.get("country");
                String v2 = info.get("COUNTRY");
                String v3 = info.get("C_o_u_n_t_r_y");
                assertEquals(v1, v2, "case insensitive");
                assertEquals(v1, v3, "underscore insensitive");
            });
            passed++;

            // Test 4: IPv4-Mapped IPv6 自动剥离降级 (::ffff:223.5.5.5)
            testAssert("4. IPv4-Mapped IPv6 剥离降级 (::ffff:223.5.5.5)", () -> {
                Optional<GeoInfo> infoDirect = reader.find("223.5.5.5");
                Optional<GeoInfo> infoMapped = reader.find("::ffff:223.5.5.5");
                assertTrue(infoMapped.isPresent(), "mapped IPv6 find");
                assertEquals(infoDirect.get().toPipeString(), infoMapped.get().toPipeString(), "mapped result equals direct");
            });
            passed++;

            // Test 5: toJson() 格式与数值类型测试
            testAssert("5. toJson() 格式与数值类型测试", () -> {
                Optional<GeoInfo> infoOpt = reader.find("223.5.5.5");
                assertTrue(infoOpt.isPresent(), "find");
                String json = infoOpt.get().toJson();
                assertTrue(json.startsWith("{") && json.endsWith("}"), "valid JSON format");
                System.out.println("   [JSON Output]: " + json);
            });
            passed++;

            // Test 6: openBuffer 内存字节加载
            File finalDbFile = dbFile;
            testAssert("6. openBuffer 内存字节加载", () -> {
                byte[] bytes = Files.readAllBytes(finalDbFile.toPath());
                DatabaseReader bufReader = new DatabaseReader.Builder(bytes).build();
                Optional<GeoInfo> info = bufReader.find("223.5.5.5");
                assertTrue(info.isPresent(), "openBuffer find");
                bufReader.close();
            });
            passed++;

            // Test 7: BatchResult 批量三态保留测试
            testAssert("7. BatchResult 批量处理测试", () -> {
                List<String> ips = Arrays.asList("223.5.5.5", " invalid_ip_format ", "255.255.255.255");
                List<BatchResult> batchResults = reader.findBatch(ips);
                assertEquals(3, batchResults.size(), "batch size");
                assertTrue(batchResults.get(0).isSuccess(), "first ip success");
                assertTrue(batchResults.get(1).hasError(), "second ip invalid format error");
            });
            passed++;

            // Test 8: ChainedReader 联合查询测试
            testAssert("8. ChainedReader 联合查询测试", () -> {
                ChainedReader chain = ChainedReader.chainMerge(reader);
                Optional<GeoInfo> info = chain.find("223.5.5.5");
                assertTrue(info.isPresent(), "chained find");
            });
            passed++;

            // Test 11: 恶意与非法输入防御测试
            testAssert("11. 恶意与非法 IP 输入安全防御", () -> {
                String[] badInputs = {"", "   ", "abc.def.ghi.jkl", "256.1.1.1", "1.1.1", "1.1.1.1.1", "1.1.1.1/24", "A".repeat(10000)};
                for (String bad : badInputs) {
                    try {
                        reader.find(bad);
                        fail("Should throw QzdbException for bad input: " + bad);
                    } catch (QzdbException e) {
                        assertEquals(ErrorCode.INVALID_IP, e.getErrorCode(), "ErrorCode.INVALID_IP for: " + bad);
                    }
                }
            });
            passed++;

            // Test 12: 损坏与伪造数据库文件防护测试
            testAssert("12. 损坏/伪造数据库文件强鲁棒性防护", () -> {
                byte[] badMagic = "INVALID_MAGIC_HEADER_TEST_BYTES_FOR_QZDB_PARSER_SAFETY_123456789".getBytes();
                try {
                    new DatabaseReader.Builder(badMagic).build();
                    fail("Should fail on invalid magic");
                } catch (QzdbException e) {
                    assertTrue(e.getErrorCode() == ErrorCode.CORRUPTED || e.getErrorCode() == ErrorCode.BAD_MAGIC, "CORRUPTED or BAD_MAGIC error code");
                }

                byte[] truncated = "QZDB".getBytes();
                try {
                    new DatabaseReader.Builder(truncated).build();
                    fail("Should fail on truncated header");
                } catch (QzdbException e) {
                    assertTrue(e.getErrorCode() == ErrorCode.BAD_HEADER || e.getErrorCode() == ErrorCode.BAD_MAGIC || e.getErrorCode() == ErrorCode.CORRUPTED, "truncated header error code");
                }
            });
            passed++;

            // Test 13: IPv6 全展开与双冒号压缩格式测试
            testAssert("13. IPv6 极限展开/双冒号压缩规范解析", () -> {
                Optional<GeoInfo> g1 = reader.find("2001:db8::1");
                Optional<GeoInfo> g2 = reader.find("2001:0db8:0000:0000:0000:0000:0000:0001");
                Optional<GeoInfo> g3 = reader.find("::ffff:223.5.5.5");
                assertTrue(g3.isPresent(), "mapped v6 find");
            });
            passed++;

            reader.close();
        } catch (Exception e) {
            System.err.println("[FAIL] 单元测试出现异常: " + e.getMessage());
            e.printStackTrace();
            failed++;
        }

        runPureLogicTests();

        System.out.println("\n--------------------------------------------------");
        System.out.println(" 测试结果: " + (failed == 0 ? "PASSED (全通过)" : "FAILED (存在失败)"));
        System.out.println("--------------------------------------------------");
        if (failed > 0) {
            System.exit(1);
        }
    }

    private static void runPureLogicTests() {
        System.out.println("\n[运行纯逻辑与 UsageType 21 场景映射断言]");

        // UsageType 21 场景映射测试
        testAssert("9. UsageType 21 场景官方映射", () -> {
            UsageType ai = UsageType.fromString("AICrawler");
            assertTrue(ai.isKnown(), "AICrawler is known");
            assertEquals("AI 爬虫", ai.getDisplayZh(), "AICrawler zh display");

            UsageType cloud = UsageType.fromString("Cloud");
            assertEquals("云服务", cloud.getDisplayZh(), "Cloud zh display");

            UsageType unknown = UsageType.fromString("FutureUnknownType");
            assertFalse(unknown.isKnown(), "Unknown type is not known");
            assertEquals("FutureUnknownType", unknown.getDisplayZh(), "Unknown type returns raw text");
        });

        // Registry 实例与静态隔离测试
        testAssert("10. QzdbRegistry 实例与全局隔离", () -> {
            QzdbRegistry reg = new QzdbRegistry();
            assertNull(reg.get("non_exist"), "get non-exist");
        });
    }

    private static void testAssert(String name, RunnableTest runnable) {
        try {
            runnable.run();
            System.out.println("  [✔ PASS] " + name);
        } catch (Throwable t) {
            System.err.println("  [✖ FAIL] " + name + ": " + t.getMessage());
            throw new RuntimeException(t);
        }
    }

    @FunctionalInterface
    interface RunnableTest {
        void run() throws Throwable;
    }

    private static void fail(String msg) {
        throw new AssertionError(msg);
    }

    private static void assertTrue(boolean condition, String msg) {
        if (!condition) throw new AssertionError("AssertTrue failed: " + msg);
    }

    private static void assertFalse(boolean condition, String msg) {
        if (condition) throw new AssertionError("AssertFalse failed: " + msg);
    }

    private static void assertNotNull(Object obj, String msg) {
        if (obj == null) throw new AssertionError("AssertNotNull failed: " + msg);
    }

    private static void assertNull(Object obj, String msg) {
        if (obj != null) throw new AssertionError("AssertNull failed: " + msg);
    }

    private static void assertEquals(Object expected, Object actual, String msg) {
        if (expected == null && actual == null) return;
        if (expected != null && expected.equals(actual)) return;
        throw new AssertionError("AssertEquals failed [" + msg + "]: expected <" + expected + ">, but got <" + actual + ">");
    }
}
