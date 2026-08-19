package com.qqzeng.qzdb;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Optional;

/**
 * 复合联合查询类 (ChainedReader)
 * <p>
 * 支持将多个 QzdbReader 组合（例如“国内精华版 + 全球旗舰版”），提供 Fallback 备性退避与 Merge 字段自动拼接。
 */
public class ChainedReader {

    public enum Mode {
        /**
         * Fallback 备性模式：按链顺序依次查找，首个命中结果即返回
         */
        FALLBACK,

        /**
         * Merge 拼接模式 (默认先注册者优先)：依次查询所有 Reader，将字段自动合并拼接为单个 GeoInfo
         */
        MERGE,

        /**
         * Merge 拼接模式 (后注册者覆盖)：后注册库的非空字段值覆盖先注册库
         */
        MERGE_OVERRIDE
    }

    private final List<QzdbReader> readers;
    private final Mode mode;

    private ChainedReader(List<QzdbReader> readers, Mode mode) {
        if (readers == null || readers.isEmpty()) {
            throw new QzdbException(ErrorCode.INVALID_PARAM, "ChainedReader requires at least one QzdbReader");
        }
        this.readers = Collections.unmodifiableList(new ArrayList<>(readers));
        this.mode = mode;
    }

    public static ChainedReader chain(QzdbReader... readers) {
        return new ChainedReader(Arrays.asList(readers), Mode.FALLBACK);
    }

    public static ChainedReader chainMerge(QzdbReader... readers) {
        return new ChainedReader(Arrays.asList(readers), Mode.MERGE);
    }

    public static ChainedReader chainMergeOverride(QzdbReader... readers) {
        return new ChainedReader(Arrays.asList(readers), Mode.MERGE_OVERRIDE);
    }

    // =========================================================================
    // 查询 API 矩阵
    // =========================================================================

    public Optional<GeoInfo> find(String ipStr) {
        if (mode == Mode.FALLBACK) {
            for (QzdbReader reader : readers) {
                try {
                    Optional<GeoInfo> res = reader.find(ipStr);
                    if (res.isPresent()) {
                        return res;
                    }
                } catch (QzdbException e) {
                    if (e.getErrorCode() == ErrorCode.INVALID_IP) {
                        throw e; // 输入格式错误立终止
                    }
                }
            }
            return Optional.empty();
        } else {
            // MERGE / MERGE_OVERRIDE 模式
            Map<String, String> mergedMap = new LinkedHashMap<>();

            for (QzdbReader reader : readers) {
                try {
                    Optional<GeoInfo> res = reader.find(ipStr);
                    if (res.isPresent()) {
                        GeoInfo info = res.get();
                        String[] fields = info.fieldNames();
                        String[] vals = info.values();

                        for (int i = 0; i < fields.length; i++) {
                            String f = fields[i];
                            String v = (i < vals.length && vals[i] != null) ? vals[i] : "";

                            if (mode == Mode.MERGE) {
                                // 先注册者优先：先注册库的非空值不被覆盖；
                                // 先注册库该字段缺失/为空时，才用后面库的值补上（规范 §9.1）
                                mergedMap.merge(f, v, (old, cur) -> old.isEmpty() ? cur : old);
                            } else {
                                // 后注册者覆盖：后注册库的非空值覆盖先注册库
                                if (!v.isEmpty() || !mergedMap.containsKey(f)) {
                                    mergedMap.put(f, v);
                                }
                            }
                        }
                    }
                } catch (QzdbException e) {
                    if (e.getErrorCode() == ErrorCode.INVALID_IP) {
                        throw e;
                    }
                }
            }

            if (mergedMap.isEmpty()) {
                return Optional.empty();
            }

            String[] fieldNames = mergedMap.keySet().toArray(new String[0]);
            String[] values = mergedMap.values().toArray(new String[0]);
            return Optional.of(new GeoInfo(fieldNames, values));
        }
    }

    public Optional<GeoInfo> findUint(int ipInt) {
        if (mode == Mode.FALLBACK) {
            for (QzdbReader reader : readers) {
                Optional<GeoInfo> res = reader.findUint(ipInt);
                if (res.isPresent()) return res;
            }
            return Optional.empty();
        } else {
            return find(String.format("%d.%d.%d.%d",
                    (ipInt >>> 24) & 0xFF, (ipInt >>> 16) & 0xFF,
                    (ipInt >>> 8) & 0xFF, ipInt & 0xFF));
        }
    }

    public Optional<GeoInfo> findBytes(byte[] ip16) {
        if (mode == Mode.FALLBACK) {
            for (QzdbReader reader : readers) {
                Optional<GeoInfo> res = reader.findBytes(ip16);
                if (res.isPresent()) return res;
            }
            return Optional.empty();
        } else {
            try {
                java.net.InetAddress addr = java.net.InetAddress.getByAddress(ip16);
                return find(addr.getHostAddress());
            } catch (Exception e) {
                throw new QzdbException(ErrorCode.INVALID_IP, "Invalid IP byte array", e);
            }
        }
    }

    public Optional<GeoInfo> findFields(String ipStr, String[] fields) {
        Optional<GeoInfo> full = find(ipStr);
        if (full.isEmpty() || fields == null || fields.length == 0) {
            return full;
        }

        GeoInfo fullInfo = full.get();
        String[] values = new String[fields.length];
        for (int i = 0; i < fields.length; i++) {
            values[i] = fullInfo.get(fields[i]);
        }
        return Optional.of(new GeoInfo(fields, values));
    }

    public List<BatchResult> findBatch(List<String> ips) {
        if (ips == null) return Collections.emptyList();
        List<BatchResult> results = new ArrayList<>(ips.size());
        for (String ip : ips) {
            try {
                Optional<GeoInfo> info = find(ip);
                results.add(new BatchResult(ip, info, null));
            } catch (QzdbException e) {
                results.add(new BatchResult(ip, Optional.empty(), e));
            }
        }
        return results;
    }

    public List<BatchResult> findBatchFields(List<String> ips, String[] fields) {
        if (ips == null) return Collections.emptyList();
        List<BatchResult> results = new ArrayList<>(ips.size());
        for (String ip : ips) {
            try {
                Optional<GeoInfo> info = findFields(ip, fields);
                results.add(new BatchResult(ip, info, null));
            } catch (QzdbException e) {
                results.add(new BatchResult(ip, Optional.empty(), e));
            }
        }
        return results;
    }

    public java.util.stream.Stream<BatchResult> findStream(java.util.stream.Stream<String> ips) {
        if (ips == null) return java.util.stream.Stream.empty();
        return ips.map(ip -> {
            try {
                Optional<GeoInfo> info = find(ip);
                return new BatchResult(ip, info, null);
            } catch (QzdbException e) {
                return new BatchResult(ip, Optional.empty(), e);
            }
        });
    }

    // =========================================================================
    // 元信息聚合 API
    // =========================================================================

    public String[] editions() {
        return readers.stream().map(QzdbReader::getEdition).toArray(String[]::new);
    }

    public String[] scopes() {
        return readers.stream().map(QzdbReader::getScope).toArray(String[]::new);
    }

    public String[] dataMonths() {
        return readers.stream().map(QzdbReader::getDataMonth).toArray(String[]::new);
    }

    public List<QzdbReader> readers() {
        return readers;
    }
}
