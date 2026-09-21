package com.qqzeng.qzdb;

import java.io.IOException;
import java.lang.invoke.MethodHandle;
import java.lang.invoke.MethodHandles;
import java.lang.invoke.MethodType;
import java.nio.ByteBuffer;
import java.nio.MappedByteBuffer;
import java.nio.channels.FileChannel;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;

/**
 * 只读 mmap 数据源：把“文件映射”和“确定性释放”两件事收在一处。
 * <p>
 * 背景：{@link MappedByteBuffer} 自 JDK 1.4 起就没有公开的 unmap()/close()，默认只能等
 * GC 在不确定时刻触发内部 Cleaner。过去本 SDK 靠反射 {@code sun.misc.Unsafe.invokeCleaner}
 * 主动 munmap，而 JEP 471/498 已经把 Unsafe 的内存访问方法标记为 terminally deprecated，
 * 阶段计划里明确会先告警、再默认抛异常、最后删除——届时这行代码会**静默失效**
 * （异常被吞掉 → 映射永不释放）。所以必须换成标准 API。
 * <p>
 * 标准替代是 JDK 22 起的 FFM：{@code FileChannel.map(mode, off, size, Arena)} 把映射生命周期
 * 绑定到 {@code Arena}，{@code Arena.close()} 即确定性 munmap。实测结论（见
 * docs/JAVA27_JAVA_SDK_ASSESSMENT.md §3）：
 * <ul>
 *   <li>映射文件**不属于**受限方法，无需 {@code --enable-native-access}；</li>
 *   <li>{@code Arena.close()} 之后访问派生的 ByteBuffer 抛 {@code IllegalStateException}
 *       ——是安全失败，不是 SIGSEGV。</li>
 * </ul>
 * <p>
 * <b>为什么用 MethodHandles 而不是 Multi-Release JAR：</b>本项目 CI 与手工构建都是
 * {@code javac $(find src -name '*.java')} 这种“整棵源码树一次编译”的方式，且编译基线是
 * {@code --release 17}。MRJAR 会引入 {@code src/main/java22} 第二套源码根，上述命令会把两份
 * 同名类一起编进去而直接失败，CI 与同步脚本都要改。用运行时能力探测可以在
 * **单源码树、JDK 17 字节码**的前提下吃到 JDK 22+ 的能力：22 以下走原来的
 * {@code FileChannel.map} + best-effort 释放，22 及以上走 Arena。
 * 反射只在 open/close 这两个低频路径上发生，查询热路径完全不碰。
 */
final class MmapSource implements AutoCloseable {

    /** JDK 22+ 的 FFM 路径是否可用（JDK 17~21 为 false）。 */
    static final boolean FFM_AVAILABLE;

    private static final MethodHandle ARENA_OF_SHARED;   // ()Arena
    private static final MethodHandle MAP_WITH_ARENA;    // (FileChannel,MapMode,long,long,Arena)MemorySegment
    private static final MethodHandle AS_BYTE_BUFFER;    // (MemorySegment)ByteBuffer
    private static final MethodHandle ARENA_CLOSE;       // (Arena)void

    static {
        boolean ok = false;
        MethodHandle ofShared = null, mapWithArena = null, asByteBuffer = null, arenaClose = null;
        try {
            // 显式要求 22+：java.lang.foreign 在 21 及之前是 *preview* API，
            // 虽然反射调用往往能通，但 preview 不保证跨版本稳定，也不该进生产依赖链。
            if (Runtime.version().feature() < 22) throw new IllegalStateException("JDK < 22");
            Class<?> arenaCls = Class.forName("java.lang.foreign.Arena");
            Class<?> segCls = Class.forName("java.lang.foreign.MemorySegment");
            MethodHandles.Lookup pub = MethodHandles.publicLookup();
            ofShared = pub.findStatic(arenaCls, "ofShared", MethodType.methodType(arenaCls));
            mapWithArena = pub.findVirtual(FileChannel.class, "map", MethodType.methodType(
                    segCls, FileChannel.MapMode.class, long.class, long.class, arenaCls));
            asByteBuffer = pub.findVirtual(segCls, "asByteBuffer", MethodType.methodType(ByteBuffer.class));
            arenaClose = pub.findVirtual(arenaCls, "close", MethodType.methodType(void.class));
            ok = true;
        } catch (Throwable ignored) {
            // JDK < 22：没有 java.lang.foreign，走 legacy 路径（不是错误）
            ofShared = mapWithArena = asByteBuffer = arenaClose = null;
        }
        FFM_AVAILABLE = ok;
        ARENA_OF_SHARED = ofShared;
        MAP_WITH_ARENA = mapWithArena;
        AS_BYTE_BUFFER = asByteBuffer;
        ARENA_CLOSE = arenaClose;
    }

    private final ByteBuffer buffer;
    /** JDK 22+ 时为 {@code java.lang.foreign.Arena}；legacy 路径为 null。 */
    private Object arena;
    private boolean released;

    private MmapSource(ByteBuffer buffer, Object arena) {
        this.buffer = buffer;
        this.arena = arena;
    }

    /**
     * 以只读方式映射整个文件。
     *
     * @throws IOException              文件不可读 / 映射失败
     * @throws IllegalArgumentException 文件超过 {@link Integer#MAX_VALUE}（ByteBuffer 上限）
     */
    static MmapSource open(Path path) throws IOException {
        long size = java.nio.file.Files.size(path);
        if (size > Integer.MAX_VALUE) {
            throw new IllegalArgumentException("File too large for single mapped buffer: " + size + " bytes");
        }
        if (FFM_AVAILABLE) {
            try {
                return openWithArena(path, size);
            } catch (Throwable t) {
                // FFM 意外失败（安全策略、非 HotSpot 实现等）→ 回落 legacy，不因释放机制影响可用性
            }
        }
        return openLegacy(path, size);
    }

    private static MmapSource openWithArena(Path path, long size) throws Throwable {
        Object arena = ARENA_OF_SHARED.invoke();
        ByteBuffer view;
        try (FileChannel ch = FileChannel.open(path, StandardOpenOption.READ)) {
            Object segment = MAP_WITH_ARENA.invoke(ch, FileChannel.MapMode.READ_ONLY, 0L, size, arena);
            view = (ByteBuffer) AS_BYTE_BUFFER.invoke(segment);
        } catch (Throwable t) {
            try {
                ARENA_CLOSE.invoke(arena);
            } catch (Throwable ignored) {
                // 已经失败，忽略二次异常
            }
            throw t;
        }
        return new MmapSource(view, arena);
    }

    private static MmapSource openLegacy(Path path, long size) throws IOException {
        try (FileChannel ch = FileChannel.open(path, StandardOpenOption.READ)) {
            return new MmapSource(ch.map(FileChannel.MapMode.READ_ONLY, 0, size), null);
        }
    }

    /** 映射视图；只读，生命周期与本对象一致。 */
    ByteBuffer buffer() {
        return buffer;
    }

    /** 诊断用：本实例是否走 JDK 22+ 的 Arena（false = legacy best-effort 释放）。 */
    boolean arenaBacked() {
        return arena != null;
    }

    /**
     * 确定性释放底层映射。幂等。
     * <p>
     * 调用方必须保证此刻没有任何线程正在该映射上执行查询——这正是
     * {@link QzdbReader} 用“晚一代再释放”隔离队列的原因（见 {@code retiring} 字段）。
     */
    @Override
    public void close() {
        if (released) return;   // 幂等：二次 close() 不能掉到 legacy 分支去碰已释放的缓冲
        released = true;
        Object a = arena;
        if (a != null) {
            arena = null;
            try {
                ARENA_CLOSE.invoke(a);
            } catch (Throwable ignored) {
                // Arena.close() 抛异常也不影响正确性：最坏回落到“等 GC”
            }
            return;
        }
        // JDK 17~21：没有标准 API，沿用 best-effort 的 Unsafe.invokeCleaner。
        // deny 阶段生效后这里会抛 UnsupportedOperationException —— 同样吞掉，
        // 行为退化为“等 GC”，与不使用本方法的历史行为完全一致，不会更糟。
        if (buffer instanceof MappedByteBuffer) {
            invokeCleanerBestEffort((MappedByteBuffer) buffer);
        }
    }

    private static void invokeCleanerBestEffort(MappedByteBuffer buffer) {
        try {
            Class<?> unsafeClass = Class.forName("sun.misc.Unsafe");
            java.lang.reflect.Field f = unsafeClass.getDeclaredField("theUnsafe");
            f.setAccessible(true);
            Object unsafe = f.get(null);
            java.lang.reflect.Method invokeCleaner = unsafeClass.getMethod("invokeCleaner", ByteBuffer.class);
            invokeCleaner.invoke(unsafe, buffer);
        } catch (ReflectiveOperationException | RuntimeException ignored) {
            // 回落默认行为：等 GC 的 Cleaner 兜底释放
        }
    }
}
