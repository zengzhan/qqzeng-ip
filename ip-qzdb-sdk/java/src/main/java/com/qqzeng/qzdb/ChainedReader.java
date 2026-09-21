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
public final class ChainedReader {

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
        this.readers = List.copyOf(readers);
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
    //
    // 三个单条查询入口（find / findUint / findBytes）共用同一套「按模式遍历 reader」的
    // 内核 resolve()，差别只在传入的单库查询函数。此前 MERGE / MERGE_OVERRIDE 模式下，
    // findUint 会先 String.format 成点分文本、findBytes 会先经 InetAddress 转成文本，
    // 再交给每个库重新解析一遍；现在直接调用各库的原生入口，省掉格式化与二次解析。
    // =========================================================================

    /** 单个 reader 上的一次查询。 */
    @FunctionalInterface
    private interface Lookup {
        Optional<GeoInfo> apply(QzdbReader reader);
    }

    private Optional<GeoInfo> resolve(Lookup lookup) {
        return switch (mode) {
            case FALLBACK -> fallback(lookup);
            case MERGE, MERGE_OVERRIDE -> merge(lookup);
        };
    }

    /** FALLBACK：首个命中即返回。输入格式错误立即终止，其余加载/内部错误跳过该库。 */
    private Optional<GeoInfo> fallback(Lookup lookup) {
        for (QzdbReader reader : readers) {
            try {
                Optional<GeoInfo> res = lookup.apply(reader);
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
    }

    /** MERGE / MERGE_OVERRIDE：逐库字段级合并（字段序为首次出现序）。 */
    private Optional<GeoInfo> merge(Lookup lookup) {
        Map<String, String> mergedMap = new LinkedHashMap<>();

        for (QzdbReader reader : readers) {
            try {
                Optional<GeoInfo> res = lookup.apply(reader);
                if (res.isPresent()) {
                    GeoInfo info = res.get();
                    // 只读遍历，无需拷贝（公共 API 的 clone 语义仍由 fieldNames()/values() 保持）
                    String[] fields = info.fieldNamesRaw();
                    String[] vals = info.valuesRaw();

                    for (int i = 0; i < fields.length; i++) {
                        String f = fields[i];
                        String v = (i < vals.length && vals[i] != null) ? vals[i] : "";

                        if (mode == Mode.MERGE) {
                            // 先注册者优先：先注册库的非空值不被覆盖；
                            // 先注册库该字段缺失/为空时，才用后面库的值补上（规范 §9.1）
                            mergedMap.merge(f, v, (old, cur) -> old.isEmpty() ? cur : old);
                        } else if (!v.isEmpty() || !mergedMap.containsKey(f)) {
                            // 后注册者覆盖：后注册库的非空值覆盖先注册库
                            mergedMap.put(f, v);
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

    public Optional<GeoInfo> find(String ipStr) {
        return resolve(reader -> reader.find(ipStr));
    }

    public Optional<GeoInfo> findUint(int ipInt) {
        return resolve(reader -> reader.findUint(ipInt));
    }

    public Optional<GeoInfo> findBytes(byte[] ip16) {
        return resolve(reader -> reader.findBytes(ip16));
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
