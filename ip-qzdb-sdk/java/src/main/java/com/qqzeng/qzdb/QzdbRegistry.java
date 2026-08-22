package com.qqzeng.qzdb;

import java.io.File;
import java.util.Map;
import java.util.Queue;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ConcurrentLinkedQueue;

/**
 * 便利管理层 (QzdbRegistry)
 * <p>
 * 按"名字"管理多个具名 QzdbReader 实例。可独立实例化（推荐用于 DI/单测），亦提供显式的 Global 静态方法（用于简单 CLI）。
 */
public class QzdbRegistry {

    private static final QzdbRegistry GLOBAL_INSTANCE = new QzdbRegistry();
    private static final int QUARANTINE_CAPACITY = 8;

    private final Map<String, QzdbReader> registryMap = new ConcurrentHashMap<>();
    private final Queue<QzdbReader> quarantine = new ConcurrentLinkedQueue<>();

    public QzdbRegistry() {
    }

    private void retire(QzdbReader old) {
        if (old == null) return;
        quarantine.add(old);
        while (quarantine.size() > QUARANTINE_CAPACITY) {
            QzdbReader evicted = quarantine.poll();
            if (evicted != null) {
                try {
                    evicted.close();
                } catch (Exception ignored) {
                }
            }
        }
    }

    /**
     * 注册具名 Reader 实例 (根据文件路径)
     */
    public void register(String name, String path) throws QzdbException {
        if (name == null || name.isEmpty() || path == null || path.isEmpty()) {
            throw new QzdbException(ErrorCode.INVALID_PARAM, "Name and path must not be empty");
        }
        QzdbReader reader = new QzdbReader.Builder(new File(path)).build();
        QzdbReader old = registryMap.put(name, reader);
        retire(old);
    }

    /**
     * 注册具名 Reader 实例 (根据内存字节 Buffer)
     */
    public void registerBuffer(String name, byte[] buffer) throws QzdbException {
        if (name == null || name.isEmpty() || buffer == null) {
            throw new QzdbException(ErrorCode.INVALID_PARAM, "Name and buffer must not be empty");
        }
        QzdbReader reader = new QzdbReader.Builder(buffer).build();
        QzdbReader old = registryMap.put(name, reader);
        retire(old);
    }

    /**
     * 根据注册名称获取 Reader 实例
     */
    public QzdbReader get(String name) {
        if (name == null) return null;
        return registryMap.get(name);
    }

    /**
     * 取消注册并关闭对应的 Reader 实例
     */
    public void unregister(String name) {
        if (name == null) return;
        QzdbReader removed = registryMap.remove(name);
        retire(removed);
    }

    /**
     * 清空所有注册的 Reader 实例
     */
    public void clear() {
        for (QzdbReader reader : registryMap.values()) {
            try {
                reader.close();
            } catch (Exception ignored) {
            }
        }
        registryMap.clear();
        QzdbReader q;
        while ((q = quarantine.poll()) != null) {
            try {
                q.close();
            } catch (Exception ignored) {
            }
        }
    }

    // =========================================================================
    // 进程级默认 Global 静态 API (带 Global 后缀显式区分)
    // =========================================================================

    public static void registerGlobal(String name, String path) throws QzdbException {
        GLOBAL_INSTANCE.register(name, path);
    }

    public static void registerGlobalBuffer(String name, byte[] buffer) throws QzdbException {
        GLOBAL_INSTANCE.registerBuffer(name, buffer);
    }

    public static QzdbReader getGlobal(String name) {
        return GLOBAL_INSTANCE.get(name);
    }

    public static void unregisterGlobal(String name) {
        GLOBAL_INSTANCE.unregister(name);
    }

    public static void clearGlobal() {
        GLOBAL_INSTANCE.clear();
    }
}
