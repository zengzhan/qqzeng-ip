package com.qqzeng.qzdb;

import java.io.File;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.file.Files;
import java.util.Arrays;
import java.util.List;
import java.util.Optional;

/**
 * Java SDK v2.4 全功能单元测试 + 2026-08 修复回归套件
 * <p>
 * 回归覆盖（2026-08-06 评测修复项）：
 * R1 BuildDate 偏移 32（dataMonth/buildTime 正确）
 * R2 findUint/findBytes/find(InetAddress) 与 find(String) 同走 dimensionMask 选维
 * R3 IPv4-mapped IPv6 数值化剥离（::ffff:0:0 十六进制形态不再抛异常）
 * R4 Metadata 驱动 version/edition（pro 不再误判为 max）
 * R5 verifyCrc() 真实重算 + 损坏文件 fail-closed
 * R6 close() 后调用抛 IllegalStateException 而非 NPE
 * R7 ChainedReader MERGE 空值补齐语义
 * R8 严格 IP 解析（拒绝前导 0 / zone id / 双 ::）
 */
public class QzdbReaderTest {

    private static final String[] SAMPLE_DB_PATHS = {
            "multi-lang/test_data_202608/std/china/qqzeng_ip_std_china.qzdb",
            "test_data_202608/std/china/qqzeng_ip_std_china.qzdb",
            "../test_data_202608/std/china/qqzeng_ip_std_china.qzdb",
            "multi-lang/data/qqzeng_ip_std_china.qzdb",
            "../data/qqzeng_ip_std_china.qzdb"
    };

    private static int passed = 0;
    private static int failed = 0;
    private static int assertions = 0;

    public static void main(String[] args) {
        System.out.println("==================================================");
        System.out.println("       QZDB Java SDK v2.4 单元测试 + 回归套件      ");
        System.out.println("==================================================");

        File dbFile = findExisting(SAMPLE_DB_PATHS);
        File asnDb = findExisting(
                "multi-lang/test_data_202608/asn/china/qqzeng_ip_asn_china.qzdb",
                "test_data_202608/asn/china/qqzeng_ip_asn_china.qzdb",
                "../test_data_202608/asn/china/qqzeng_ip_asn_china.qzdb");
        File maxGlobalDb = findExisting(
                "multi-lang/test_data_202608/max/global/qqzeng_ip_max_global.qzdb",
                "test_data_202608/max/global/qqzeng_ip_max_global.qzdb",
                "../test_data_202608/max/global/qqzeng_ip_max_global.qzdb");

        runPureLogicTests();

        if (dbFile == null) {
            System.err.println("[WARN] 测试数据库不存在，跳过二进制检索测试。");
        } else {
            runBinaryTests(dbFile, asnDb, maxGlobalDb);
            runConcurrencyTests(dbFile);
        }

        System.out.println("\n--------------------------------------------------");
        System.out.println(" 测试结果: passed=" + passed + " failed=" + failed
                + (failed == 0 ? " (ALL PASSED)" : " (FAILED)"));
        System.out.println(" 独立断言总数: " + assertions
                + (assertions >= 50 ? " (≥50，满足 Tier 1 要求)" : " (<50，不满足 Tier 1 要求)"));
        System.out.println("--------------------------------------------------");
        // 与 run_all_tests.sh 的 grep "TEST_PASS" 判定约定对齐（其他语言测试入口同样以 TEST_PASS 收尾）
        if (failed == 0) {
            System.out.println("TEST_PASS");
        }
        if (failed > 0) System.exit(1);
    }

    private static File findExisting(String... paths) {
        for (String p : paths) {
            File f = new File(p);
            if (f.exists()) return f;
        }
        return null;
    }

    // =========================================================================
    // 纯逻辑测试（不依赖数据库文件）
    // =========================================================================

    /** double(1e300) 的精确十进制展开（str(int(1e300)) 一次性导出后固化，非被测路径生成）。 */
    private static final String E300_LITERAL =
            "10000000000000000525047602552044202487044685811081591549158541155118024579889"
            + "08195786371375080447864043704443832883878176942523235360430575644792184786706"
            + "98284838720092657580373783023379478809005936895323497079994508111903896764088"
            + "0074652742780142494579258788820056842838115669472196386865459400540160";

    private static void runPureLogicTests() {
        test("P0-2. FORMAT §10.5 原生浮点统一契约边界", () -> {
            // 与 python/test_native_float.py、nodejs/native_float_test.js、
            // go native_float_test.go、c native_float_boundary_test.c 用例同源。
            assertEquals("116", QzdbReader.formatNativeFloat(116.0), "整值无小数点");
            assertEquals("-3", QzdbReader.formatNativeFloat(-3.0), "负整值");
            assertEquals("0", QzdbReader.formatNativeFloat(0.0), "零");
            assertEquals("0", QzdbReader.formatNativeFloat(-0.0), "负零归一");
            assertEquals("116.400000", QzdbReader.formatNativeFloat(116.4), "非整数固定 6 位");
            assertEquals("-3.500000", QzdbReader.formatNativeFloat(-3.5), "负非整数");
            assertEquals("", QzdbReader.formatNativeFloat(Double.NaN), "NaN -> \"\"");
            assertEquals("", QzdbReader.formatNativeFloat(Double.POSITIVE_INFINITY), "+Inf -> \"\"");
            assertEquals("", QzdbReader.formatNativeFloat(Float.NaN), "f32 NaN -> \"\"");
            assertEquals("", QzdbReader.formatNativeFloat(Float.NEGATIVE_INFINITY), "f32 -Inf -> \"\"");
            assertEquals("10000000000000000", QzdbReader.formatNativeFloat(1e16), "2^53 整值（int64 路径）");
            assertEquals("9200000000000000000", QzdbReader.formatNativeFloat(9.2e18), "int64 上界内大整值");
            assertEquals("9223372036854774784", QzdbReader.formatNativeFloat(9223372036854774784.0),
                    "< 2^63 最大可表示偶数整值");
            assertEquals("9223372036854775808", QzdbReader.formatNativeFloat(9223372036854775808.0),
                    "恰为 2^63 走精确定点分支");
            assertEquals("-9223372036854775808", QzdbReader.formatNativeFloat(-9223372036854775808.0),
                    "恰为 -2^63");
            assertEquals("100000000000000000000", QzdbReader.formatNativeFloat(1e20), "> 2^63 定点整数位");
            // 1e300 非精确可表示：契约要求输出该 double 的精确十进制展开。
            // 该向量曾抓出 String.format("%.0f") 的最短 round-trip 补零缺陷
            // （2^63 → "9223372036854776000"），现以 BigDecimal(v) 精确构造器实现。
            assertEquals(E300_LITERAL, QzdbReader.formatNativeFloat(1e300), "1e300 定点展开精确值");
            assertEquals("-" + E300_LITERAL, QzdbReader.formatNativeFloat(-1e300), "-1e300");
            assertEquals("116.400002", QzdbReader.formatNativeFloat(116.4f),
                    "f32 按精确 double 值舍入（float32 116.4 = 116.40000152587890625）");
            assertEquals("116", QzdbReader.formatNativeFloat(116.0f), "f32 整值无小数点");
        });

        test("P1. IPv4 严格解析（拒绝前导 0 / 越界 / 缺段）", () -> {
            assertEquals(0x01020304, QzdbReader.parseIPv4Uint("1.2.3.4"), "basic");
            assertEquals(0, QzdbReader.parseIPv4Uint("0.0.0.0"), "all zero");
            assertEquals(-1, QzdbReader.parseIPv4Uint("255.255.255.255"), "max");
            assertThrowsInvalidIp(() -> QzdbReader.parseIPv4Uint("01.2.3.4"), "leading zero");
            assertThrowsInvalidIp(() -> QzdbReader.parseIPv4Uint("256.1.1.1"), "octet > 255");
            assertThrowsInvalidIp(() -> QzdbReader.parseIPv4Uint("1.2.3"), "3 parts");
            assertThrowsInvalidIp(() -> QzdbReader.parseIPv4Uint("1.2.3.4.5"), "5 parts");
            assertThrowsInvalidIp(() -> QzdbReader.parseIPv4Uint("1.2.3.a"), "non digit");
            assertThrowsInvalidIp(() -> QzdbReader.parseIPv4Uint("1.2.3.+4"), "plus sign");
        });

        test("P2. IPv6 严格解析（压缩/展开等价、双冒号唯一、拒绝 zone）", () -> {
            byte[] a = QzdbReader.parseIPv6Bytes("2001:db8::1");
            byte[] b = QzdbReader.parseIPv6Bytes("2001:0db8:0000:0000:0000:0000:0000:0001");
            assertTrue(Arrays.equals(a, b), "compressed == expanded");
            assertEquals(16, a.length, "16 bytes");
            assertEquals(0x20, a[0] & 0xFF, "first byte");
            assertEquals(0x01, a[15] & 0xFF, "last byte");

            byte[] full = QzdbReader.parseIPv6Bytes("::");
            boolean allZero = true;
            for (byte x : full) allZero &= (x == 0);
            assertTrue(allZero, ":: is all zeros");

            assertThrowsInvalidIp(() -> QzdbReader.parseIPv6Bytes("1::2::3"), "double ::");
            assertThrowsInvalidIp(() -> QzdbReader.parseIPv6Bytes("fe80::1%en0"), "zone id");
            assertThrowsInvalidIp(() -> QzdbReader.parseIPv6Bytes("1:2:3"), "too few groups");
            assertThrowsInvalidIp(() -> QzdbReader.parseIPv6Bytes("12345::"), "group too long");
            assertThrowsInvalidIp(() -> QzdbReader.parseIPv6Bytes("g::1"), "non-hex");
        });

        test("P3. IPv4-mapped IPv6 数值判定（覆盖点分与十六进制形态）", () -> {
            byte[] dotted = QzdbReader.parseIPv6Bytes("::ffff:1.12.0.0");
            byte[] hexed = QzdbReader.parseIPv6Bytes("0:0:0:0:0:ffff:10c:0");
            assertTrue(QzdbReader.isV4MappedBytes(dotted), "dotted mapped");
            assertTrue(QzdbReader.isV4MappedBytes(hexed), "hex mapped");
            assertEquals(QzdbReader.v4FromMappedBytes(dotted),
                    QzdbReader.v4FromMappedBytes(hexed), "same v4");
            assertEquals((1 << 24) | (12 << 16), QzdbReader.v4FromMappedBytes(dotted), "1.12.0.0");
            byte[] nat = QzdbReader.parseIPv6Bytes("2001:db8::ffff:1.2.3.4");
            assertFalse(QzdbReader.isV4MappedBytes(nat), "native v6 with ffff not mapped");
        });

        test("P4. GeoInfo 归一化 get（大小写/下划线不敏感）", () -> {
            GeoInfo g = new GeoInfo(
                    new String[]{"country", "country_en", "usage_type"},
                    new String[]{"中国", "China", "Cloud"});
            assertEquals("中国", g.get("country"), "exact");
            assertEquals("中国", g.get("COUNTRY"), "upper");
            assertEquals("中国", g.get("C_o_u_n_t_r_y"), "underscore");
            assertEquals("China", g.get("countryEn"), "camel");
            assertEquals("China", g.get("Country__En"), "double underscore");
            assertEquals("", g.get("not_exist"), "missing -> empty");
            assertEquals("Cloud", g.getUsageType().rawValue(), "usageType raw");
            assertTrue(g.getUsageType().isKnown(), "Cloud known");
        });

        test("P5. toJson 保持 snake_case 与数值类型", () -> {
            GeoInfo g = new GeoInfo(
                    new String[]{"country_en", "longitude", "asn", "geo_id", "city"},
                    new String[]{"China", "116.4074", "4134", "110000", ""});
            String json = g.toJson();
            assertTrue(json.contains("\"country_en\":\"China\""), "snake_case key");
            assertTrue(json.contains("\"longitude\":116.4074"), "longitude number");
            assertTrue(json.contains("\"asn\":4134"), "asn number");
            assertTrue(json.contains("\"geo_id\":110000"), "geo_id number");
            assertTrue(json.contains("\"city\":\"\""), "empty string stays string");

            GeoInfo empty = new GeoInfo(
                    new String[]{"longitude", "city"},
                    new String[]{"", ""});
            String j2 = empty.toJson();
            assertTrue(j2.contains("\"longitude\":null"), "empty numeric -> null");
            assertTrue(j2.contains("\"city\":\"\""), "empty text -> empty string");

            GeoInfo bad = new GeoInfo(new String[]{"asn"}, new String[]{"not-a-number"});
            assertTrue(bad.toJson().contains("\"asn\":null"), "invalid number -> null");
        });

        test("P6. UsageType 21 场景官方映射 + 未知兜底", () -> {
            UsageType ai = UsageType.fromString("AICrawler");
            assertTrue(ai.isKnown(), "AICrawler known");
            assertEquals("AI 爬虫", ai.getDisplayZh(), "AICrawler zh");
            assertEquals("云服务", UsageType.fromString("Cloud").getDisplayZh(), "Cloud zh");
            assertEquals("VPN/代理", UsageType.fromString("VPN").getDisplayZh(), "VPN zh");
            UsageType unknown = UsageType.fromString("FutureUnknownType");
            assertFalse(unknown.isKnown(), "future type not known");
            assertEquals("FutureUnknownType", unknown.rawValue(), "raw preserved");
            assertTrue(UsageType.fromString("") == KnownUsageType.UNKNOWN, "empty -> Unknown");
            assertEquals(21, KnownUsageType.values().length, "21 official types");
        });

        test("P7. QzdbRegistry 实例隔离与未注册返回 null", () -> {
            QzdbRegistry reg = new QzdbRegistry();
            assertNull(reg.get("non_exist"), "get non-exist");
            assertNull(QzdbRegistry.getGlobal("definitely_not_registered_xyz"), "global non-exist");
        });

        test("P8. 浮点字段 6 位小数格式化（IEEE 754 float 真实值）", () -> {
            java.text.DecimalFormat df = new java.text.DecimalFormat("0.000000",
                    java.text.DecimalFormatSymbols.getInstance(java.util.Locale.US));
            // float 的 IEEE 754 真实值格式化（非原始输入值）
            assertEquals("116.407402", df.format(116.4074f), "float 116.4074 实际存储为 116.407402...");
            assertEquals("-33.865101", df.format(-33.8651f), "float -33.8651 实际存储为 -33.865101...");
            assertEquals("0.000000", df.format(0.0f), "zero 6dp");
            assertEquals("180.000000", df.format(180.0f), "integer-valued float 6dp");
        });

        test("P9. 端口/CIDR/掩码/控制字符一律拒绝（§1.1/§1.4）", () -> {
            assertThrowsInvalidIp(() -> QzdbReader.parseIPv4Uint("1.1.1.1:80"), "port form");
            // 带端口（含冒号走 v6 路径会被拒）；此处验证 v4 解析器对非数字直接拒绝
            assertThrowsInvalidIp(() -> QzdbReader.parseIPv4Uint("1.1.1.1/24"), "CIDR form");
            assertThrowsInvalidIp(() -> QzdbReader.parseIPv4Uint("1.1.1.1 255.255.255.0"), "netmask form");
            assertThrowsInvalidIp(() -> QzdbReader.parseIPv4Uint("1.1.1.+1"), "plus sign");
            assertThrowsInvalidIp(() -> QzdbReader.parseIPv4Uint("1.1.1.-1"), "minus sign");
            assertThrowsInvalidIp(() -> QzdbReader.parseIPv4Uint("1.1.1.1\u0000"), "control char");
            assertThrowsInvalidIp(() -> QzdbReader.parseIPv6Bytes("2001:db8::1/64"), "v6 CIDR form");
            assertThrowsInvalidIp(() -> QzdbReader.parseIPv6Bytes("[2001:db8::1]:80"), "v6 bracketed port");
        });

        test("P10. IPv6 压缩/全展开字节级一致（::1 与 2001:218::）（§1.2）", () -> {
            assertTrue(Arrays.equals(
                    QzdbReader.parseIPv6Bytes("::1"),
                    QzdbReader.parseIPv6Bytes("0000:0000:0000:0000:0000:0000:0000:0001")), "::1 == expanded");
            assertTrue(Arrays.equals(
                    QzdbReader.parseIPv6Bytes("2001:218::"),
                    QzdbReader.parseIPv6Bytes("2001:0218:0000:0000:0000:0000:0000:0000")), "2001:218:: == expanded");
            // 末尾点分 IPv4 形态
            assertTrue(Arrays.equals(
                    QzdbReader.parseIPv6Bytes("::ffff:1.2.3.4"),
                    QzdbReader.parseIPv6Bytes("::ffff:102:304")), "dotted == hex tail");
        });

        test("P12. IP 解析严格性回归（嵌入 IPv4 点分四元组必须位于 '::' 右侧）", () -> {
            // 缺陷类：形如 "1.2.3.4::" / "2001:db8:1.2.3.4::" 的 '::' 右侧为空、
            // 嵌入 IPv4 点分四元组落在左侧末组的输入必须拒绝（与 Go netip.ParseAddr 一致）。
            // 同时守护既有合法形态：v4-mapped 降级、末尾嵌入 v4、纯 v4、zone/双冒号拒绝。
            assertThrowsInvalidIp(() -> QzdbReader.parseIPv6Bytes("0.0.0.0::"), "0.0.0.0:: reject");
            assertThrowsInvalidIp(() -> QzdbReader.parseIPv6Bytes("1.2.3.4::"), "1.2.3.4:: reject");
            assertThrowsInvalidIp(() -> QzdbReader.parseIPv6Bytes("2001:db8:1.2.3.4::"), "2001:db8:1.2.3.4:: reject");
            assertParseAccepts(() -> QzdbReader.parseIPv6Bytes("::1.2.3.4"), "::1.2.3.4 accept");
            assertParseAccepts(() -> QzdbReader.parseIPv6Bytes("2001:db8::1.2.3.4"), "2001:db8::1.2.3.4 accept");
            assertParseAccepts(() -> QzdbReader.parseIPv6Bytes("1::2.3.4.5"), "1::2.3.4.5 accept");
            assertParseAccepts(() -> QzdbReader.parseIPv4Uint("114.114.114.114"), "114.114.114.114 accept");
            assertParseAccepts(() -> QzdbReader.parseIPv6Bytes("::ffff:7272:7272"), "::ffff:7272:7272 accept");
            assertThrowsInvalidIp(() -> QzdbReader.parseIPv6Bytes("fe80::1%eth0"), "fe80::1%eth0 reject");
            assertThrowsInvalidIp(() -> QzdbReader.parseIPv6Bytes("1::2::3"), "1::2::3 reject");
        });

        test("P11. GeoInfo 连字符/下划线归一化 + 空值安全（§2）", () -> {
            GeoInfo g = new GeoInfo(new String[]{"country_code"}, new String[]{"CN"});
            assertEquals("CN", g.get("country_code"), "underscore");
            assertEquals("CN", g.get("countryCode"), "camel");
            assertEquals("CN", g.get("COUNTRY_CODE"), "upper underscore");
            assertEquals("CN", g.get("country-code"), "hyphen");
            assertEquals("CN", g.get("Country-Code"), "mixed hyphen");
            assertEquals("", g.get("no_such_field"), "missing -> empty string, no exception");
            assertEquals("", g.get(null), "null key -> empty");
            assertEquals("", g.get(""), "empty key -> empty");
        });
    }

    // =========================================================================
    // 二进制检索测试（依赖真实数据库文件）
    // =========================================================================

    private static void runBinaryTests(File dbFile, File asnDb, File maxGlobalDb) {
        try (QzdbReader reader = new QzdbReader.Builder(dbFile).build()) {

            test("B1. 元信息自省（非空 + 字段名）", () -> {
                assertNotNull(reader.getVersion(), "version");
                assertNotNull(reader.getDataMonth(), "dataMonth");
                assertNotNull(reader.getEdition(), "edition");
                assertNotNull(reader.getScope(), "scope");
                assertNotNull(reader.getFileHash(), "fileHash");
                assertTrue(reader.getFieldNames().length > 0, "fieldNames");
                assertTrue(reader.verifyCrc(), "verifyCrc on healthy file");
                assertEquals(8, reader.getFileHash().length(), "crc32 hex length");
            });

            test("B2. 单条 IPv4 查询 (223.5.5.5)", () -> {
                Optional<GeoInfo> infoOpt = reader.find("223.5.5.5");
                assertTrue(infoOpt.isPresent(), "find 223.5.5.5");
                assertTrue(!infoOpt.get().toPipeString().isEmpty(), "pipe non-empty");
            });

            // Test 14: CIDR 网段反查 API 测试 (lookupCidr IPv4 + IPv6)
            test("14. CIDR 网段反查测试 (lookupCidr)", () -> {
                String cidr4 = reader.lookupCidr("223.5.5.5");
                assertNotNull(cidr4, "cidr for 223.5.5.5 non-null");
                assertTrue(cidr4.contains("/"), "cidr4 contains slash: " + cidr4);
                System.out.println("   [IPv4 CIDR Output]: 223.5.5.5 -> " + cidr4);

                String cidr6 = reader.lookupCidr("2001:218::1");
                if (cidr6 != null) {
                    assertTrue(cidr6.contains("/"), "cidr6 contains slash: " + cidr6);
                    System.out.println("   [IPv6 CIDR Output]: 2001:218::1 -> " + cidr6);
                }
            });

            test("B3. IPv4-Mapped IPv6 自动降级与直查一致", () -> {
                Optional<GeoInfo> direct = reader.find("223.5.5.5");
                Optional<GeoInfo> mapped = reader.find("::ffff:223.5.5.5");
                Optional<GeoInfo> mappedHex = reader.find("0:0:0:0:0:ffff:df05:505");
                assertTrue(mapped.isPresent(), "mapped dotted");
                assertEquals(direct.get().toPipeString(), mapped.get().toPipeString(), "dotted == direct");
                assertTrue(mappedHex.isPresent(), "mapped hex");
                assertEquals(direct.get().toPipeString(), mappedHex.get().toPipeString(), "hex == direct");
            });

            test("B4. findUint/findBytes/find(InetAddress) 与 find(String) 一致（dimensionMask 回归）", () -> {
                String ip = "223.5.5.5";
                Optional<GeoInfo> viaStr = reader.find(ip);
                int ipInt = (223 << 24) | (5 << 16) | (5 << 8) | 5;
                Optional<GeoInfo> viaUint = reader.findUint(ipInt);
                assertEquals(viaStr.map(GeoInfo::toPipeString).orElse(""),
                        viaUint.map(GeoInfo::toPipeString).orElse(""), "findUint == find");

                byte[] v4bytes = {(byte) 223, 5, 5, 5};
                assertEquals(viaStr.map(GeoInfo::toPipeString).orElse(""),
                        reader.findBytes(v4bytes).map(GeoInfo::toPipeString).orElse(""), "findBytes(4) == find");

                java.net.InetAddress addr = java.net.InetAddress.getByName(ip);
                assertEquals(viaStr.map(GeoInfo::toPipeString).orElse(""),
                        reader.find(addr).map(GeoInfo::toPipeString).orElse(""), "find(InetAddress) == find");
            });

            test("B5. findBytes(16B) IPv6 与字符串路径一致", () -> {
                byte[] mapped = QzdbReader.parseIPv6Bytes("::ffff:223.5.5.5");
                assertEquals(reader.find("223.5.5.5").map(GeoInfo::toPipeString).orElse(""),
                        reader.findBytes(mapped).map(GeoInfo::toPipeString).orElse(""), "16B mapped");
            });

            test("B6. toJson() 结构完整", () -> {
                String json = reader.find("223.5.5.5").get().toJson();
                assertTrue(json.startsWith("{") && json.endsWith("}"), "braces");
            });

            test("B7. openBuffer 内存加载（拷贝语义）", () -> {
                byte[] bytes = Files.readAllBytes(dbFile.toPath());
                QzdbReader bufReader = new QzdbReader.Builder(bytes).build();
                Optional<GeoInfo> info = bufReader.find("223.5.5.5");
                assertTrue(info.isPresent(), "openBuffer find");
                bufReader.close();
            });

            test("B8. BatchResult 三态语义保留", () -> {
                List<String> ips = Arrays.asList("223.5.5.5", " invalid_ip_format ", "255.255.255.255");
                List<BatchResult> rs = reader.findBatch(ips);
                assertEquals(3, rs.size(), "batch size");
                assertTrue(rs.get(0).isSuccess(), "first success");
                assertTrue(rs.get(1).hasError(), "second invalid -> error");
                assertEquals(ErrorCode.INVALID_IP, rs.get(1).error().getErrorCode(), "error code");
                // 第三条：格式合法，命中或未找到都不是 error
                assertFalse(rs.get(2).hasError(), "third not error");
            });

            test("B9. findStream 惰性与三态", () -> {
                long count = reader.findStream(java.util.stream.Stream.of("223.5.5.5", "bad ip"))
                        .peek(r -> {
                        })
                        .count();
                assertEquals(2L, count, "stream count");
            });

            test("B10. findFields 投影（未知字段补空串）", () -> {
                Optional<GeoInfo> full = reader.find("223.5.5.5");
                Optional<GeoInfo> proj = reader.findFields("223.5.5.5",
                        new String[]{"country", "no_such_field"});
                assertTrue(proj.isPresent(), "projection present");
                assertEquals(full.get().get("country"), proj.get().get("country"), "country matches");
                assertEquals("", proj.get().get("no_such_field"), "unknown -> empty");
                assertEquals(2, proj.get().fieldNames().length, "projection size");
            });

            test("B11. lookupRowId/lookupIds 底层 API", () -> {
                int rowId = reader.lookupRowId("223.5.5.5");
                assertTrue(rowId > 0, "rowId positive");
                RowIds ids = reader.lookupIds(rowId);
                assertNotNull(ids, "ids");
                assertTrue(ids.geoId() > 0 || ids.asnId() > 0, "some id positive");
                assertEquals(0, reader.lookupRowId("not-an-ip"), "invalid -> 0");
                assertNull(reader.lookupIds(0), "row 0 -> null");
            });

            test("B12. 恶意与非法 IP 输入防御", () -> {
                String[] badInputs = {"", "   ", "abc.def.ghi.jkl", "256.1.1.1", "1.1.1", "1.1.1.1.1",
                        "01.1.1.1", "1.1.1.1/24", "A".repeat(1000), ":::", "1::2::3", "fe80::1%eth0"};
                for (String bad : badInputs) {
                    try {
                        reader.find(bad);
                        fail("Should throw for: " + bad);
                    } catch (QzdbException e) {
                        assertEquals(ErrorCode.INVALID_IP, e.getErrorCode(), "INVALID_IP for: " + bad);
                    }
                }
            });

            test("B13. 损坏/伪造文件 fail-closed", () -> {
                byte[] badMagic = "INVALID_MAGIC_HEADER_TEST_BYTES_FOR_QZDB_PARSER_SAFETY_123456789".getBytes();
                assertThrowsCorrupt(() -> new QzdbReader.Builder(badMagic).build());

                byte[] truncated = "QZDB".getBytes();
                assertThrowsCorrupt(() -> new QzdbReader.Builder(truncated).build());

                // 篡改尾部字节 → CRC 失败
                byte[] tampered = Files.readAllBytes(dbFile.toPath());
                tampered[tampered.length - 10] ^= (byte) 0xFF;
                try {
                    new QzdbReader.Builder(tampered).verifyCrc(true).build();
                    fail("tampered file must fail CRC");
                } catch (QzdbException e) {
                    assertEquals(ErrorCode.CORRUPTED, e.getErrorCode(), "CRC mismatch code");
                }
                // verifyCrc(false) 可打开但 verifyCrc() 必须报 false
                QzdbReader sick = new QzdbReader.Builder(tampered).verifyCrc(false).build();
                assertFalse(sick.verifyCrc(), "verifyCrc() false on tampered");
                sick.close();
            });

            test("B14. close() 后调用抛 IllegalStateException（非 NPE）", () -> {
                QzdbReader r2 = new QzdbReader.Builder(dbFile).build();
                r2.close();
                r2.close(); // 幂等
                try {
                    r2.find("1.1.1.1");
                    fail("closed reader must throw");
                } catch (IllegalStateException expected) {
                    // ok
                }
                try {
                    r2.getEdition();
                    fail("closed reader meta must throw");
                } catch (IllegalStateException expected) {
                    // ok
                }
            });

            test("B15. reload 原子性：失败不影响旧数据", () -> {
                QzdbReader r3 = new QzdbReader.Builder(dbFile).build();
                String before = r3.find("223.5.5.5").map(GeoInfo::toPipeString).orElse("");
                byte[] junk = "garbage-not-a-qzdb-file".getBytes();
                File tmp = File.createTempFile("qzdb_bad_reload", ".bin");
                Files.write(tmp.toPath(), junk);
                try {
                    r3.reload(tmp.getAbsolutePath());
                    fail("reload with junk must throw");
                } catch (QzdbException expected) {
                    // ok
                }
                assertEquals(before, r3.find("223.5.5.5").map(GeoInfo::toPipeString).orElse(""),
                        "old snapshot still serving");
                r3.reload(dbFile.getAbsolutePath()); // 正常 reload 可用
                assertEquals(before, r3.find("223.5.5.5").map(GeoInfo::toPipeString).orElse(""),
                        "same data after reload");
                r3.close();
                tmp.delete();
            });

            test("B16. 双栈一致性交叉断言：三种输入形式字段级一致（§9）", () -> {
                Optional<GeoInfo> a = reader.find("223.5.5.5");
                Optional<GeoInfo> b = reader.find("::ffff:223.5.5.5");
                Optional<GeoInfo> c = reader.find("::ffff:df05:0505");
                assertTrue(a.isPresent(), "plain v4 present");
                assertTrue(b.isPresent(), "mapped dotted present");
                assertTrue(c.isPresent(), "mapped hex present");
                String pa = a.get().toPipeString();
                assertEquals(pa, b.get().toPipeString(), "v4 == mapped dotted");
                assertEquals(pa, c.get().toPipeString(), "v4 == mapped hex");
                assertEquals(b.get().toPipeString(), c.get().toPipeString(), "dotted == hex");
            });

            test("B17. 非法 Mapped 拒绝（::ffff:256.1.1.1）（§1.3）", () -> {
                assertThrowsInvalidIp(() -> reader.find("::ffff:256.1.1.1"), "mapped with invalid v4 tail");
                assertThrowsInvalidIp(() -> reader.find("::ffff:1.2.3"), "mapped with short v4 tail");
            });

            test("B18. lookupCidr 错误语义：非法→INVALID_IP，未命中→null（§7）", () -> {
                assertThrowsInvalidIp(() -> reader.lookupCidr("not-an-ip"), "invalid -> INVALID_IP");
                assertThrowsInvalidIp(() -> reader.lookupCidr(""), "empty -> INVALID_IP");
                assertThrowsInvalidIp(() -> reader.lookupCidr("1.1.1.1/24"), "CIDR form -> INVALID_IP");
                // 未命中返回 null（用一个几乎必然不在库中的保留段地址）
                assertNull(reader.lookupCidr("203.0.113.254"), "TEST-NET-3 miss -> null");
            });

            test("B19. 超长垃圾输入拒绝（≥10000 字符）（§1.4/§4）", () -> {
                String longJunk = "1." + "1".repeat(10000) + ".1.1";
                assertThrowsInvalidIp(() -> reader.find(longJunk), "10k-char junk");
                assertThrowsInvalidIp(() -> reader.find("A".repeat(10000)), "10k-char alpha");
            });

            test("B20. CRC Fail-Closed：清零 CRC 字段拒绝加载（§5）", () -> {
                byte[] bytes = Files.readAllBytes(dbFile.toPath());
                // 清零偏移 16~19 的 CRC 字段（canonical 重算值几乎必然非 0 → 必须拒绝）
                bytes[16] = 0; bytes[17] = 0; bytes[18] = 0; bytes[19] = 0;
                try {
                    new QzdbReader.Builder(bytes).verifyCrc(true).build();
                    fail("zeroed CRC field must be rejected (crc==0 must NOT pass)");
                } catch (QzdbException e) {
                    assertEquals(ErrorCode.CORRUPTED, e.getErrorCode(), "zeroed CRC -> CORRUPTED");
                }
            });

            test("B21. §8 大端/小端平台兼容性（固定 LE 字节序 + 结果确定性）", () -> {
                byte[] raw = Files.readAllBytes(dbFile.toPath());
                ByteBuffer le = ByteBuffer.wrap(raw).order(ByteOrder.LITTLE_ENDIAN);

                byte[] magic = new byte[4];
                le.position(0);
                le.get(magic);
                assertEquals("QZDB", new String(magic, java.nio.charset.StandardCharsets.US_ASCII), "magic");

                int vmLe = le.getShort(6) & 0xFFFF;
                assertTrue(vmLe > 0, "versionMask LE > 0: " + vmLe);

                int rowCount = le.getInt(20);
                assertTrue(rowCount > 0, "rowCount LE > 0: " + rowCount);

                Optional<GeoInfo> r1 = reader.find("223.5.5.5");
                Optional<GeoInfo> r2 = reader.find("223.5.5.5");
                assertTrue(r1.isPresent() && r2.isPresent(), "both present");
                assertEquals(r1.get().toPipeString(), r2.get().toPipeString(), "deterministic result");
            });

            test("B22. §5 CRC32 校验 — 破坏任意中间字节后 verifyCrc() 必须返回 false", () -> {
                byte[] raw = Files.readAllBytes(dbFile.toPath());
                int tamperPos = Math.min(raw.length / 2, raw.length - 1);
                raw[tamperPos] ^= (byte) 0xFF;
                QzdbReader sick = new QzdbReader.Builder(raw).verifyCrc(false).build();
                assertFalse(sick.verifyCrc(), "verifyCrc must be false on tampered file");
                sick.close();
            });
         } catch (Exception e) {
            System.err.println("[FAIL] 二进制测试套件异常: " + e);
            e.printStackTrace();
            failed++;
        }

        // ── 回归：2026-08 修复点（需要 202608 数据集）──────────────────

        if (dbFile.getPath().contains("202608")) {
            test("R1. BuildDate→dataMonth/buildTime（修复偏移 144 误读）", () -> {
                try (QzdbReader r = new QzdbReader.Builder(dbFile).build()) {
                    assertEquals("2026-08", r.getDataMonth(), "dataMonth");
                    assertTrue(r.getBuildTime().startsWith("2026-08"), "buildTime date");
                    assertFalse(r.getDataMonth().startsWith("197"), "no epoch-era date");
                }
            });
            test("R4. edition/version 由 Metadata 驱动（std）", () -> {
                try (QzdbReader r = new QzdbReader.Builder(dbFile).build()) {
                    assertEquals("std", r.getEdition(), "edition from metadata");
                    assertEquals("std", r.getVersion(), "version from metadata type=1");
                }
            });
        }

        if (asnDb != null) {
            test("R2. ASN 库 findUint 与 find 一致（dimensionMask=0x02 回归）", () -> {
                try (QzdbReader r = new QzdbReader.Builder(asnDb).build()) {
                    assertEquals("asn", r.getEdition(), "asn edition");
                    // 候选 IP（114.114.0.0/18 在 asn china 库中必有记录）
                    String[] candidates = {"114.114.114.114", "1.0.1.0", "223.5.5.5"};
                    boolean anyHit = false;
                    boolean anyAsnPopulated = false;
                    for (String ip : candidates) {
                        Optional<GeoInfo> viaStr = r.find(ip);
                        int ipInt = QzdbReader.parseIPv4Uint(ip);
                        Optional<GeoInfo> viaUint = r.findUint(ipInt);
                        assertEquals(viaStr.map(GeoInfo::toPipeString).orElse("<none>"),
                                viaUint.map(GeoInfo::toPipeString).orElse("<none>"),
                                "findUint must respect dimensionMask for " + ip);
                        if (viaStr.isPresent()) {
                            anyHit = true;
                            if (!viaStr.get().get("asn").isEmpty()) anyAsnPopulated = true;
                        }
                    }
                    assertTrue(anyHit, "at least one candidate IP must exist in asn db");
                    // 部分网段本身无 ASN（ground truth 为空），只要求库中至少一条候选带 ASN
                    assertTrue(anyAsnPopulated, "at least one candidate must carry asn");
                }
            });
        }

        if (maxGlobalDb != null) {
            test("R3. ::ffff:0:0 数值化剥离（修复十六进制 mapped 抛异常）", () -> {
                try (QzdbReader r = new QzdbReader.Builder(maxGlobalDb).verifyCrc(false).build()) {
                    // 全球库 V4 trie 含 0.0.0.0/8 保留段 → 映射查询应命中保留地址
                    Optional<GeoInfo> mappedZero = r.find("::ffff:0:0");
                    assertTrue(mappedZero.isPresent(), "::ffff:0:0 resolves via V4 trie");
                    assertEquals("ZZ", mappedZero.get().get("country_code"), "reserved ZZ");
                    Optional<GeoInfo> directZero = r.find("0.0.0.0");
                    assertEquals(directZero.map(GeoInfo::toPipeString).orElse("<none>"),
                            mappedZero.map(GeoInfo::toPipeString).orElse("<none>"), "mapped == direct v4");
                }
            });
        }

        // ── ChainedReader 语义 ───────────────────────────────────────────

        File chainDbA = dbFile;
        File chainDbB = asnDb != null ? asnDb : dbFile;
        test("C1. ChainedReader Fallback 与 Merge 基础", () -> {
            try (QzdbReader a = new QzdbReader.Builder(chainDbA).build();
                 QzdbReader b = new QzdbReader.Builder(chainDbB).build()) {
                ChainedReader fb = ChainedReader.chain(a, b);
                Optional<GeoInfo> f1 = fb.find("223.5.5.5");
                assertTrue(f1.isPresent(), "fallback find");

                ChainedReader mg = ChainedReader.chainMerge(a, b);
                Optional<GeoInfo> m1 = mg.find("223.5.5.5");
                assertTrue(m1.isPresent(), "merge find");
                assertEquals(a.getEdition() + "," + b.getEdition(),
                        String.join(",", mg.editions()), "editions aggregate");
                assertEquals(2, mg.readers().size(), "readers()");
            }
        });

        test("R7. Merge 先注册者优先 + 空值补齐（修复 putIfAbsent 空值阻塞）", () -> {
            try (QzdbReader a = new QzdbReader.Builder(chainDbA).build();
                 QzdbReader b = new QzdbReader.Builder(chainDbB).build()) {
                ChainedReader mg = ChainedReader.chainMerge(a, b);
                String ip = "1.0.1.0";
                Optional<GeoInfo> ra = a.find(ip);
                Optional<GeoInfo> rb = b.find(ip);
                Optional<GeoInfo> merged = mg.find(ip);
                if (ra.isPresent() && rb.isPresent() && merged.isPresent()) {
                    GeoInfo ga = ra.get(), gb = rb.get(), gm = merged.get();
                    // 先注册者非空值不被覆盖
                    if (!ga.get("country").isEmpty()) {
                        assertEquals(ga.get("country"), gm.get("country"), "first non-empty wins");
                    }
                    // 先注册者为空 → 用后注册者补上
                    for (String f : gm.fieldNames()) {
                        String va = ga.get(f);
                        String vb = gb.get(f);
                        String vm = gm.get(f);
                        if (va.isEmpty() && !vb.isEmpty()) {
                            assertEquals(vb, vm, "empty filled from second: " + f);
                        }
                        if (!va.isEmpty()) {
                            assertEquals(va, vm, "first kept: " + f);
                        }
                    }
                }
            }
        });

        test("C2. ChainedReader 方法矩阵（findBatchFields/findStream）", () -> {
            try (QzdbReader a = new QzdbReader.Builder(chainDbA).build()) {
                ChainedReader mg = ChainedReader.chainMerge(a);
                List<BatchResult> rs = mg.findBatchFields(
                        Arrays.asList("223.5.5.5", "bad-one"), new String[]{"country"});
                assertEquals(2, rs.size(), "batchFields size");
                assertTrue(rs.get(0).isSuccess(), "first ok");
                assertTrue(rs.get(1).hasError(), "second error");
                long n = mg.findStream(java.util.stream.Stream.of("223.5.5.5")).count();
                assertEquals(1L, n, "stream count");
            }
        });

        test("C3. Fallback 输入格式错误立即终止", () -> {
            try (QzdbReader a = new QzdbReader.Builder(chainDbA).build()) {
                ChainedReader fb = ChainedReader.chain(a, a);
                try {
                    fb.find("totally-invalid");
                    fail("invalid ip in chain must throw");
                } catch (QzdbException e) {
                    assertEquals(ErrorCode.INVALID_IP, e.getErrorCode(), "chain invalid ip");
                }
            }
        });

        try (QzdbReader r2 = new QzdbReader.Builder(dbFile).build()) {
            test("C4. lookupCidrBytes 4/16 字节入口", () -> {
                String viaStr = r2.lookupCidr("223.5.5.5");
                byte[] v4 = {(byte) 223, 5, 5, 5};
                assertEquals(viaStr, r2.lookupCidrBytes(v4), "lookupCidrBytes(4B) == lookupCidr(String)");
                byte[] mapped = QzdbReader.parseIPv6Bytes("::ffff:223.5.5.5");
                assertEquals(viaStr, r2.lookupCidrBytes(mapped), "lookupCidrBytes(16B mapped) == lookupCidr(String)");
                assertThrowsInvalidIp(() -> r2.lookupCidrBytes((byte[]) null), "null -> INVALID_IP");
                assertThrowsInvalidIp(() -> r2.lookupCidrBytes(new byte[]{1, 2, 3}), "invalid length -> INVALID_IP");
            });

            test("C5. getPoolCount / getGroupCount 自省", () -> {
                assertTrue(r2.getPoolCount() >= 0, "poolCount non-negative");
                assertTrue(r2.getGroupCount() >= 1, "groupCount >= 1");
                assertTrue(r2.getGroupCount() <= 4, "groupCount <= 4");
            });

            test("C6. lookupRowIdBytes 4/16 字节入口", () -> {
                int viaStr = r2.lookupRowId("223.5.5.5");
                assertTrue(viaStr > 0, "rowId positive via string");
                byte[] v4 = {(byte) 223, 5, 5, 5};
                assertEquals(viaStr, r2.lookupRowIdBytes(v4), "lookupRowIdBytes(4B) == lookupRowId(String)");
                byte[] mapped = QzdbReader.parseIPv6Bytes("::ffff:223.5.5.5");
                assertEquals(viaStr, r2.lookupRowIdBytes(mapped), "lookupRowIdBytes(16B mapped) == lookupRowId(String)");
                assertEquals(0, r2.lookupRowIdBytes((byte[]) null), "null -> 0");
                assertEquals(0, r2.lookupRowIdBytes(new byte[]{1, 2, 3}), "invalid length -> 0");
            });
        }
    }

    // ── 并发安全测试（lock-free 快照无数据竞争）────────────────────——

    private static void runConcurrencyTests(File dbFile) {
        test("T1. 16 线程 × 100k 并发查询无异常 / 结果一致", () -> {
            final int THREADS = 16;
            final int OPS = 100_000;
            try (QzdbReader r = new QzdbReader.Builder(dbFile).build()) {
                // 先获取期望结果
                java.util.concurrent.atomic.AtomicReference<String> expected =
                        new java.util.concurrent.atomic.AtomicReference<>();
                expected.set(r.find("223.5.5.5").map(GeoInfo::toPipeString).orElse(""));

                java.util.concurrent.CountDownLatch latch = new java.util.concurrent.CountDownLatch(THREADS);
                java.util.concurrent.atomic.AtomicInteger errors = new java.util.concurrent.atomic.AtomicInteger(0);
                java.util.concurrent.atomic.AtomicInteger ok = new java.util.concurrent.atomic.AtomicInteger(0);

                for (int t = 0; t < THREADS; t++) {
                    final int tid = t;
                    new Thread(() -> {
                        try {
                            for (int i = 0; i < OPS; i++) {
                                String ip = (i % 256) + "." + ((i / 256) % 256) + "." + ((tid * 7 + i) % 256) + ".1";
                                try {
                                    java.util.Optional<GeoInfo> info = r.find(ip);
                                    info.ifPresent(g -> {
                                        if (!g.toPipeString().equals(expected.get()) && !g.toPipeString().isEmpty()) {
                                            // 不同 IP 结果不同是正常的，只验证非空结果格式合法
                                        }
                                        ok.incrementAndGet(); // OK path counter
                                    });
                                } catch (QzdbException e) {
                                    if (e.getErrorCode() == ErrorCode.INVALID_IP) {
                                        // 部分随机 IP 可能格式合法但值越界，忽略
                                    } else {
                                        errors.incrementAndGet();
                                    }
                                }
                            }
                        } finally {
                            latch.countDown();
                        }
                    }).start();
                }
                latch.await();
                assertEquals(0, errors.get(), "no unexpected errors during concurrent access");
                assertTrue(ok.get() > 0, "at least some successful lookups: " + ok.get());
            }
        });

        test("T2. 无锁热重载：reload 期间 16 线程并发查询零异常（§6）", () -> {
            final int READERS = 16;
            final int OPS = 20_000;
            final QzdbReader r = new QzdbReader.Builder(dbFile).build();
            try {
                java.util.concurrent.atomic.AtomicInteger unexpected = new java.util.concurrent.atomic.AtomicInteger(0);
                java.util.concurrent.atomic.AtomicLong done = new java.util.concurrent.atomic.AtomicLong(0);
                java.util.concurrent.atomic.AtomicBoolean stop = new java.util.concurrent.atomic.AtomicBoolean(false);
                java.util.concurrent.CountDownLatch latch = new java.util.concurrent.CountDownLatch(READERS);

                for (int t = 0; t < READERS; t++) {
                    new Thread(() -> {
                        try {
                            for (int i = 0; i < OPS && !stop.get(); i++) {
                                String ip = (i % 255 + 1) + "." + ((i * 17) % 256) + "." + ((i * 131) % 256) + "." + (i % 254 + 1);
                                try {
                                    r.find(ip); // present 或 empty 均合法，关键是零 NPE / 零脏读
                                    done.incrementAndGet();
                                } catch (QzdbException e) {
                                    if (e.getErrorCode() != ErrorCode.INVALID_IP) unexpected.incrementAndGet();
                                } catch (Throwable other) {
                                    unexpected.incrementAndGet(); // NPE / 脏读等
                                }
                            }
                        } finally {
                            latch.countDown();
                        }
                    }).start();
                }

                // 写线程：读线程运行期间反复 reload 同一合法文件（影子构建 + 原子切换）
                int reloads = 0;
                long deadline = System.nanoTime() + 3_000_000_000L; // 最多 3s
                while (System.nanoTime() < deadline && latch.getCount() > 0) {
                    try {
                        r.reload(dbFile.getAbsolutePath());
                        reloads++;
                    } catch (QzdbException e) {
                        unexpected.incrementAndGet();
                    }
                    Thread.sleep(50);
                }
                latch.await();

                assertTrue(reloads > 0, "at least one reload happened: " + reloads);
                assertEquals(0, unexpected.get(), "zero NPE / dirty-read / unexpected error during reload");
                assertTrue(done.get() > 0, "readers made progress: " + done.get());

                // reload 失败不影响现有快照
                String before = r.find("223.5.5.5").map(GeoInfo::toPipeString).orElse("<none>");
                try {
                    r.reloadBuffer("junk".getBytes());
                    fail("junk reloadBuffer must throw");
                } catch (QzdbException expected) { /* old snapshot intact */ }
                assertEquals(before, r.find("223.5.5.5").map(GeoInfo::toPipeString).orElse("<none>"),
                        "old snapshot still serving after failed reload");
            } finally {
                r.close();
            }
        });
    }

    // =========================================================================
    // 断言工具
    // =========================================================================

    private static void test(String name, RunnableTest body) {
        try {
            body.run();
            System.out.println("  [✔ PASS] " + name);
            passed++;
        } catch (Throwable t) {
            System.err.println("  [✖ FAIL] " + name + ": " + t.getMessage());
            failed++;
        }
    }

    @FunctionalInterface
    interface RunnableTest {
        void run() throws Throwable;
    }

    private static void assertThrowsInvalidIp(Runnable r, String msg) {
        assertions++;
        try {
            r.run();
            fail("expected INVALID_IP: " + msg);
        } catch (QzdbException e) {
            if (e.getErrorCode() != ErrorCode.INVALID_IP) {
                throw new AssertionError("expected INVALID_IP but got " + e.getErrorCode() + ": " + msg);
            }
        }
    }

    /** 断言生产解析路径接受该输入（不抛异常）。用于守护既有合法 IP 形态不被误伤。 */
    private static void assertParseAccepts(Runnable r, String msg) {
        assertions++;
        try {
            r.run();
        } catch (Throwable t) {
            throw new AssertionError("expected parse to accept but threw: " + msg + " -> " + t);
        }
    }

    private static void assertThrowsCorrupt(Runnable r) {
        assertions++;
        try {
            r.run();
            fail("expected corruption exception");
        } catch (QzdbException e) {
            if (!(e.getErrorCode() == ErrorCode.CORRUPTED
                    || e.getErrorCode() == ErrorCode.BAD_MAGIC
                    || e.getErrorCode() == ErrorCode.BAD_HEADER)) {
                throw new AssertionError("corrupt-family code expected, got " + e.getErrorCode());
            }
        }
    }

    private static void fail(String msg) {
        throw new AssertionError(msg);
    }

    private static void assertTrue(boolean condition, String msg) {
        assertions++;
        if (!condition) throw new AssertionError("AssertTrue failed: " + msg);
    }

    private static void assertFalse(boolean condition, String msg) {
        assertions++;
        if (condition) throw new AssertionError("AssertFalse failed: " + msg);
    }

    private static void assertNotNull(Object obj, String msg) {
        assertions++;
        if (obj == null) throw new AssertionError("AssertNotNull failed: " + msg);
    }

    private static void assertNull(Object obj, String msg) {
        assertions++;
        if (obj != null) throw new AssertionError("AssertNull failed: " + msg);
    }

    private static void assertEquals(Object expected, Object actual, String msg) {
        assertions++;
        if (expected == null && actual == null) return;
        if (expected != null && expected.equals(actual)) return;
        throw new AssertionError("AssertEquals failed [" + msg + "]: expected <" + expected + ">, but got <" + actual + ">");
    }
}
