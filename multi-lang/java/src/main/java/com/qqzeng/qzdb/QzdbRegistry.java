package com.qqzeng.qzdb;

import java.io.File;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;

/**
 * 便利管理层 (QzdbRegistry)
 * <p>
 * 按"名字"管理多个具名 DatabaseReader 实例。可独立实例化（推荐用于 DI/单测），亦提供显式的 Global 静态方法（用于简单 CLI）。
 */
public class QzdbRegistry {

    private static final QzdbRegistry GLOBAL_INSTANCE = new QzdbRegistry();

    private final Map<String, DatabaseReader> registryMap = new ConcurrentHashMap<>();

    public QzdbRegistry() {
    }

    /**
     * 注册具名 Reader 实例 (根据文件路径)
     */
    public void register(String name, String path) throws QzdbException {
        if (name == null || name.isEmpty() || path == null || path.isEmpty()) {
            throw new QzdbException(ErrorCode.INVALID_PARAM, "Name and path must not be empty");
        }
        DatabaseReader reader = new DatabaseReader.Builder(new File(path)).build();
        DatabaseReader old = registryMap.put(name, reader);
        if (old != null) {
            old.close();
        }
    }

    /**
     * 注册具名 Reader 实例 (根据内存字节 Buffer)
     */
    public void registerBuffer(String name, byte[] buffer) throws QzdbException {
        if (name == null || name.isEmpty() || buffer == null) {
            throw new QzdbException(ErrorCode.INVALID_PARAM, "Name and buffer must not be empty");
        }
        DatabaseReader reader = new DatabaseReader.Builder(buffer).build();
        DatabaseReader old = registryMap.put(name, reader);
        if (old != null) {
            old.close();
        }
    }

    /**
     * 根据注册名称获取 Reader 实例
     */
    public DatabaseReader get(String name) {
        if (name == null) return null;
        return registryMap.get(name);
    }

    /**
     * 取消注册并关闭对应的 Reader 实例
     */
    public void unregister(String name) {
        if (name == null) return;
        DatabaseReader removed = registryMap.remove(name);
        if (removed != null) {
            removed.close();
        }
    }

    /**
     * 清空所有注册的 Reader 实例
     */
    public void clear() {
        for (DatabaseReader reader : registryMap.values()) {
            try {
                reader.close();
            } catch (Exception ignored) {
            }
        }
        registryMap.clear();
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

    public static DatabaseReader getGlobal(String name) {
        return GLOBAL_INSTANCE.get(name);
    }

    public static void unregisterGlobal(String name) {
        GLOBAL_INSTANCE.unregister(name);
    }

    public static void clearGlobal() {
        GLOBAL_INSTANCE.clear();
    }
}
