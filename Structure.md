# QZip IPDB 格式标准文档 (v14.0 Ultimate Plus - .qzdb)

本文档定义了 **QZip (QZeng IP Database)** 的 V14 终极高性能硬件优化格式（官方推荐文件后缀名：**.qzdb**，文件魔数：`QZ14`）。相比 V13 引入了 **IPv6 后缀块 Eytzinger 布局化**、**全文件大块 64 字节 Cache Line 对齐** 等低层优化，性能获得指数级提升。

## 1. 核心特性 (Key Features)

1.  **极速 IPv6 (Eytzinger Suffix)**: IPv6 后缀数据块由传统的平铺二分查找全面重构为 **Eytzinger (中序二叉树) 布局**，配合 CPU 预取，使 IPv6 查询速度提升 **220% 以上**（达到 ~80M/s - 84M/s）。
2.  **64 字节缓存行对齐 (64-Byte Cache Line Alignment)**: 
    *   文件结构中所有主要数据区（Geo Area, Pools, V4Idx, V4Data, V6Data）的起始物理偏移均对齐到 64 字节整倍数。
    *   大幅减少 CPU 从内存读取数据时跨越 Cache Line 边界的次数，将硬件开销降到最低。
3.  **极速 IPv4 (Eytzinger & Index)**: 保持 V13 的 IPv4 /16 索引配合块内 Eytzinger 检索设计，继续输出 >28M/s 的高吞吐。
4.  **数据完整性 (Data Integrity)**: 内置 **CRC32 校验**，防止磁盘损坏、网络截断或位翻转导致的隐性错误。
5.  **零 GC (Zero-Allocation)**: 查询过程全链路无堆分配，适合高并发、低延迟的云原生及网关服务。
6.  **子集构建 (Subset Building)**: 支持在构建时按需过滤 (如“仅生成中国版”)，自动压缩空洞，文件体积可减少 70%+。
7.  **热重载 (Hot Reload)**: 支持无锁原子替换数据库实例，服务升级零停机。

## 1.1 命名空间与设计理念 (Format Branding & Design Philosophy)

### A. 官方文件后缀名：`.qzdb`
为了在整个 IP 库行业建立鲜明且唯一的品牌特色，避开网上通用的垃圾/玩具型项目后缀（如 `.ezdb`、`.dat` 等），V14 正式确立 **`.qzdb`** (QZeng / QZip Database) 为官方推荐的二进制文件名后缀。
*   **Magic Number (魔数)**：文件前 4 字节固定为 `QZ14` (ASCII)。
*   **品牌寓意**：`QZ` 代表创作者品牌 `qqzeng` 和极限压缩引擎 `QZip`；`DB` 代表工业级、高可靠的数据结构。

### B. “硬件即协议”的设计理念 (Hardware-As-Protocol Philosophy)
`.qzdb` 并非为了盲目压缩体积而生，而是在**现代多核 CPU 的缓存架构**基础之上建立的一套高速检索协议：
1.  **缓存感知 (Cache-Oblivious)**：利用 Eytzinger 布局，把二分查找在跳跃时产生的内存随机寻址（Pointer-chasing）转化为局部连续寻址，让 CPU 预取器能够提前把下一层节点拉入 L1/L2 缓存。
2.  **边界对齐 (Cache-Line Align)**：将所有关键数据区的首地址强行限制为 64 字节的倍数，确保一次 CPU 内存搬运（Memory Transaction）读入的数据不跨越缓存行边界，杜绝冗余的总线周期。
3.  **无状态零拷贝 (Stateless & Zero-Allocation)**：检索代码运行期不发生任何内存分配与堆对象创建，通过 `Span` 直接在只读映射内存（mmap）中运算，保证网关级高并发时的 GC 零开销。
4.  **直接比对 (Direct Compare)**：IPv6 键采用网络序（Big Endian）存储，使检索器可直接使用无符号 64 位整数比较，无需进行主机字节序转换，大幅减少运算延迟。

## 2. 性能测试 (Benchmark)

Environment: Apple M-Series ARM64 / x64 Generic (10M IPv4 / 5M IPv6 random lookups)

| 版本 | IPv4 QPS | IPv6 QPS | 体积 (Full) | 特性 |
| :--- | :--- | :--- | :--- | :--- |
| **V14 (Ultimate Plus)** | **~28.6 M/s** | **~80.0-84.0 M/s** | **9.45 MB** | **IPv6 Eytzinger, 64B 对齐**, CRC32 |
| V13 (Ultimate) | ~31.0 M/s | ~25.9 M/s | 9.45 MB | IPv6 平铺二分, 无 64B 对齐 |
| V12 | ~30.0 M/s | ~4.5 M/s | 9.40 MB | 无 CRC32, IPv6 慢 |
| V11 | ~14.0 M/s | ~3.0 M/s | 9.60 MB | 传统二分法 |

**定制版体积对比 (Demo)**:
*   全量版 (Global): 9.45 MB
*   **中国版 (CN Only)**: **2.62 MB** (节省 72%)

## 2.1 与市面主流 IP 数据库格式对比

| 格式/属性 | MaxMind (.mmdb) | IPIP.net (.ipdb) | Ip2region (.xdb) | **QZip V14.0 (Ultimate Plus)** |
| :--- | :--- | :--- | :--- | :--- |
| **内存查询算法** | 偏移嵌套二叉树 | 偏移嵌套二叉树 | 一级向量索引 + 二级二分 | **Eytzinger (BFS) 完全二叉树** |
| **IPv4 查询性能** | ~1.0M - 3.0M QPS | ~2.0M - 5.0M QPS | ~10.0M QPS | **~28.6M QPS (快 3x - 28x)** |
| **IPv6 查询性能** | ~1.0M - 2.0M QPS | ~1.5M - 3.0M QPS | 较弱/未深度优化 | **~80.0M - 84.0M QPS (快 26x - 80x)** |
| **全量数据库体积** | 30 MB - 60 MB | 8 MB - 20 MB | 5 MB - 10 MB | **9.45 MB (体积极小)** |
| **高并发垃圾回收** | 有堆分配 (GC 压力大) | 有堆分配 (GC 压力大) | 零分配 (无 GC 压力) | **零分配 (全链路 Zero-Allocation)** |
| **Cache Line 优化** | 无对齐优化 | 无对齐优化 | 无内存对齐优化 | **主要数据区与块首地址 64 字节对齐** |

### 优势深度解析

1.  **MaxMind (.mmdb)**
    *   **劣势**：树节点尺寸不规则（如 24/28/32 位），检索时需要大量的位运算；节点分布零散，没有进行局部性优化，导致频繁的 CPU L1/L2 缓存未命中（Cache Miss）。其元数据多采用类似 JSON Map 的嵌套格式，体积膨胀严重。
    *   **对比**：QZip V14 在 QPS 上对其实现了 **10 到 80 倍的降维打击**，且体积仅为其 1/3 ~ 1/6。

2.  **IPIP.net (.ipdb)**
    *   **劣势**：继承了 MMDB 的嵌套二分查找逻辑，没有进行树结构扁平化及 Cache Line 硬件对齐。在高并发网关及高吞吐服务中，查询线程极易因为内存随机跳转（Pointer-chasing）被 CPU 锁在内存总线等待上。
    *   **对比**：QZip V14 将 IPv6 后缀块也全面重构为 Eytzinger 布局，彻底解决了多层二分查找带来的内存跳转延时，吞吐性能提升一个数量级。

3.  **Ip2region (.xdb)**
    *   **劣势**：主要为磁盘映射检索（mmap）设计，其二级块在内存中是平铺的排序数组，查询使用的是传统的二分法，无法保证最常用节点（树的前几层）被 CPU 缓存行安全预取。
    *   **对比**：QZip V14 专为全内存极限吞吐设计，采用 Eytzinger 组织块内节点，查询前 3 步完全发生在单个 L1 缓存行（64字节）内，性能高出 3 到 8 倍。

## 3. 文件结构详解 (QZ14)

文件整体分为 **Header**, **Data**, **Index** 三大部分，主要数据区的首地址保证是 64 字节的倍数。
**注意**: Header/Offsets 采用 Little Endian，但 **Index/Data Key** 采用 Big Endian (以适配 IP 网络序)。

```text
+-------------------------------------------------------+
| 1. 全局文件头 (Header)                    [96 bytes]  |
|    - Magic: "QZ14" (4B)                               |
|    - Ver:   20260527 (4B) (YYYYMMDD)                  |
|    - Count: Geo记录总数 (4B)                           |
|    - CRC32: 全文件校验和 (4B, 该字段本身视为0计算)      |
|    - GeoIdSize: 2或3 (1B)                             |
|    - Reserved: (11B)                                  |
|    - Offsets: [Geo|Pools|V4Idx|V4Data|V6Data] (8B x5) |
|    (以 64 字节边界对其填充)                             |
+-------------------------------------------------------+
| 2. 结构化地理信息区 (Geo Struct Area)      [Count * 24]  |
|    起始地址 64B 对齐。8个 ushort 索引 + Lng/Lat。        |
+-------------------------------------------------------+
| 3. 维度字典池 (Dimension Pools)            [Variable]  |
|    起始地址 64B 对齐。8个独立字符串池。格式同V13。      |
+-------------------------------------------------------+
| 4. IPv4 索引区 (/16 Index)                [256 KB]    |
|    起始地址 64B 对齐。uint32[65536]，相对偏移。       |
+-------------------------------------------------------+
| 5. IPv4 Eytzinger 数据区 (Data)           [Variable]  |
|    起始地址 64B 对齐。                                 |
|    Block: [Count(2B)] [Node 1] [Node 2] ...           |
|    Node:  [StartIP_Low(2B)] [GeoID(2/3B)]             |
+-------------------------------------------------------+
| 6. IPv6 Eytzinger 数据区 (V6 Eytzinger)    [Variable]  |
|    起始地址 64B 对齐。                                 |
|    a) Prefix Index (前缀索引 /64)                     |
|       Format: [Count] [Rec 0] [Rec 1] ...             |
|       Rec: [Prefix(8B BigEndian)] [BlockOffset(4B)]   |
|    b) Eytzinger Suffix Blocks                         |
|       Format: [Count(2B)] [Node 0] [Node 1] ...       |
|       Node: [Suffix(8B BigEndian)] [GeoID(2/3B)]      |
+-------------------------------------------------------+
```

## 4. 接入与构建指南

### C# 高性能查询 (ASP.NET Core 推荐)

建议使用 **单例模式** (Singleton) 并开启 CRC 校验。

```csharp
// Program.cs (启动时加载)
IPDBSearcherV14.Load("v14.db"); 

// Controller
var info = IPDBSearcherV14.Instance.Find("1.1.1.1");
Console.WriteLine(IPDBSearcherV14.Instance.Version); // 20260527
```

### 热重载 (Hot Reload)
当 `v14.db` 更新时，无需重启服务：
```csharp
IPDBSearcherV14.Reload("v14_new.db"); // 线程安全替换
```

### 构建定制版 (Filtered Build)
利用 `IPDBBuilderV14` 生成子集数据库：
```csharp
// 仅生成中国版
IPDBBuilderV14.Build(srcV4, srcV6, "v14_cn.db", (parts) => {
    return parts.Length > 1 && parts[1] == "中国";
});
```

## 5. 跨语言实现指南 (Polyglot Guide)

为了确保在 C++, Go, Rust, Java 等语言中实现 V14 时的正确性与高性能，请遵循以下规范。

### A. 字节序与比较 (Endianness)
*   **文件规范**: Headers/Offsets 采用 **Little Endian**；IPv6 Key 采用 **Big Endian**。
*   **比较逻辑**:
    *   **C/C++/Go**: 将 IPv6 Key 视为 `byte[8]` 数组，使用 `memcmp` 比较。
    *   **C#/.NET**: 读取为 `UInt64`，需执行 `BSWAP` (ReverseEndianness) 转换为本机整数后再比较。

### B. 缓存对齐与未对齐内存访问 (Memory Alignment)
*   文件各段偏移由 64 字节对齐，加载文件到内存时，推荐使用保证 64 字节对齐的分配函数（如 C11 的 `aligned_alloc`，或者 Rust 的 `Layout::from_size_align`）。
*   **非对齐安全指针安全 (Unaligned Memory Safety)**：
    由于压缩结构体尺寸可能为奇数（如 4/5 字节节点），直接转换为指针读取（如 C/C++ 中对未对齐地址强转 `*(uint32_t*)ptr`）在 ARM/MIPS 架构上会触发 **SIGBUS** 崩溃，在 Rust 中属于**未定义行为 (UB)**。
    *   *Rust 规范*：必须使用 `std::ptr::read_unaligned`。
    *   *Go 规范*：使用 `binary.LittleEndian.Uint32` 或按字节位移拼接。
    *   *C/C++ 规范*：使用 `std::memcpy` 拷贝数据到栈上对齐的临时变量中（现代编译器会将其优化为单条非对齐读取指令）。

### C. 推荐设计模式 (Design Patterns)

#### 1. 全局单例 (Global Singleton)
V14 Searcher 是**无状态且线程安全**的。
*   **推荐**: 应用程序启动时加载一次，全局共享。
*   **禁止**: 每次查询都 `new Searcher` 或 `open file`。

#### 2. 内存加载 (Memory Strategy)
*   **默认**: `ReadAllBytes` 全部加载到内存。目前体积仅 ~10MB。
*   **低内存环境**: 使用 **mmap** (Memory Mapped File) 减少 Private Memory 占用。

#### 3. 资源池化与零 GC (Zero-GC String Pools)
*   **GC 压力逃逸**：在 Go, Java, C# 等带垃圾回收的语言中，如果在每次查询时解码 UTF-8 字节并分配新的 String 对象，高并发下会导致严重的垃圾回收（GC）停顿。
*   **解法**：在加载时（`InitPools`）将 String Pools 里的全部字节一次性解析成宿主语言的原生字符串对象（如 Go 的 `string` 数组，Java 的 `String[]`），在查询时仅返回对象引用。在 Rust/C++ 中，可返回生命周期绑定到只读内存的 `&str` 或 `std::string_view`，实现 100% 零拷贝。

### D. IPv6 128位大整数表达与性能优化 (IPv6 128-bit Representation)
*   **痛点**：Java / JavaScript / Go 等语言没有高效的、免堆分配的原生 `uint128` 类型。使用类似 Java 的 `BigInteger` 类或 Go 的 `math/big` 会导致大量的堆分配和算术开销。
*   **解法**：在跨语言 SDK 检索循环中，统一将 IPv6 地址视为 **两个 `uint64`（High & Low）**。前缀索引直接基于 High 进行匹配，后缀 Eytzinger 数据块基于 Low 进行树上查找。这样能彻底消除对象分配，保证高吞吐。

### E. 多国语言支持扩展方案 (Multilingual Data Extension Schema)
为了使后续生成的数据库文件能兼容多国语言输出，设计应遵循以下原则以防体积暴涨或结构体不兼容：
1.  **Header 掩码字段**：在 Header 中保留一个 `LanguageMask`（如 4B），用于声明本文件中打包了哪些语言（如 `0x01: 中文`, `0x02: 英文`, `0x04: 日文`）。
2.  **独立 String Pools**：不要在定长的 `GeoInfoStruct` 中为不同语言增加多余的索引字段，而是每个语言拥有一个**独立的 String Pool**。
3.  **动态按需打包**：Builder 在构建时，根据用户订购的语言，动态生成对应语言池。Searcher 通过读取 `LanguageMask`，并根据传入的语言类型（Language Enum）计算偏移定位到对应的 String Pool，既满足了多语言扩展，又避免了单语言版用户的物理体积受损。

---
**版本历史**:
*   **v14.0 (Ultimate Plus)**: IPv6 后缀块 Eytzinger 树形组织，64字节 Cache Line 文件段首地址对齐，大幅度跃升 IPv6 查询至 80M+ QPS。
*   v13.0 (Ultimate): CRC32, Hot Reload, Safety Bounds, Subset Build.
*   v12.0: Eytzinger Layout.
*   v11.0: Sectional Index.

