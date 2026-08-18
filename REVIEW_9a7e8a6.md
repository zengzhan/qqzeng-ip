# Code Review Report — 多语言 SDK 提交 `9a7e8a6`（Sisyphus，2026-08-17）

**审查对象**：`git show 9a7e8a6`（34 文件，+3903 / -3006）
**涉及面**：C / Rust 锁无关缓存重写、C 直读基址热路径、Go finalizer 生命周期、全语言解码缓存与 SENTINEL/IPv4-mapped 修正
**审查方法**：逐语言读取 diff + 对照当前源码核实（非仅读提交说明）；并对 CRC 链式实现做实证测试
**审查结论**：**2 Critical / 0 High（已并入 Critical）/ 3 Medium / 1 Low**。提交说明的"全部测试通过"只能证明单线程/低碰撞场景，**不能**覆盖并发竞态与畸形输入两条 fail-closed 主路径。

---

## 一、问题清单（按严重度）

| # | 严重度 | 语言 | 位置 | 一句话结论 |
|---|--------|------|------|-----------|
| 1 | **Critical** | Rust | `resolve_geo`，`lib.rs:1597-2620`；`CacheSlot` 定义 `lib.rs:694-720` | 锁无关缓存双原子位置更新，哈希碰撞下撕裂写 → 并发返回**错误 IP 的 GeoInfo**（静默数据损坏） |
| 2 | **Critical** | C | 段边界校验 `qzdb_reader.c:1566-1569`；直读 `get_v4_child:450-466` | `off + count*nstride` 整数溢出可绕过边界校验；`off>0` 守卫在 `off==0` 时跳过校验 → 直读基址越界读 → 畸形文件崩溃（违反 fail-closed 铁律） |
| 3 | **Medium** | Python | `find_fields`，`qzdb.py:1964` | 裸 `full._values[idx]` 无 `idx<len` 保护；字段数双源对账不一致时 `IndexError` 崩溃 |
| 4 | **Low/Medium** | Node | `findFields`，`qzdb.js:1405` | 越界 `idx` 得到 `undefined` 而非 `''`，违反"未知字段补空串"契约 |
| 5 | **Medium** | Go | `installSnapshot`/`Close`，`qzdb.go:1509-1524` | 用 `runtime.SetFinalizer` 接管 munmap，`Close()` 不再确定性释放；资源释放非确定，Windows 下文件锁滞留至 GC |
| 6 | **Low** | 全部 | 多处 | 风格/格式化瑕疵（Rust 多余空行 `lib.rs:2232`、Go `if s:=...{ return s.x }` 块多 3 空格缩进） |

> **误报排除（已核实安全，不计入问题）**：
> - Node/Python 的 `zlib.crc32`/`zlib.crc32` 链式 **seed=0** 与规范（跳过 16–20 字节视为 0）实测完全一致 → CRC 无回归。
> - C# `GeoInfo.Get`（`GeoInfo.cs:41`）自带 `idx >= _values.Length → return ""` 保护，故 C# `FindFields` 实际**安全**（旧 `fi>=fc` 守卫虽移除，由 `Get` 兜底）。
> - Go `buildSnapshotFromBytes`（`qzdb.go:1576`）走堆拷贝、无 mmap，无需 munmap → 无泄漏；`buildSnapshotFromFile` 错误路径已补 munmap。

---

## 二、详细分析与修复

### 🔴 #1 Rust 锁无关解码缓存 —— 并发撕裂写（Critical，静默数据损坏）

**根因**：槽位索引为 `(entry_id as usize) & (GEO_CACHE_SIZE - 1)`，而 `GEO_CACHE_SIZE = 1<<14 = 16384`。`max/ult` 库 entry 达 1e5 量级 → **哈希碰撞必然高频发生**。每个槽是两个独立原子位置：

```rust
struct CacheSlot { key: AtomicU32, val: ArcSwapOption<GeoInfo> }
// 写入：
slot.val.store(Some(Arc::clone(&geo)));   // 先 val
slot.key.store(entry_id, Ordering::Release); // 后 key（注释称"key 是有效性标志"）
// 读取：
if slot.key.load(Ordering::Acquire) == entry_id {
    if let Some(v) = slot.val.load_full() { return Some(v); }
}
```

两个**不同 entry 命中同一槽**（如 entry 5 与 entry 16389）时的交错：

```
T_A: val.store(geo5)              // 被抢占
T_B: val.store(geo16389); key.store(16389)
T_A: key.store(5)                 // 恢复，仅写 key
最终: val=geo16389, key=5  ← 撕裂态
读者(entry=5): key==5 命中 → 返回 geo16389  // 错值！
```

提交说明称"碰撞只重算、绝不返回错值"——**在该交错下不成立**。val 与 key 是两个独立原子，无原子多字提交，抢占窗口内即产生 `(key=5, val=geo16389)` 的撕裂态被读者接受。这不会崩溃、不报错，但返回**错误 IP 的地理信息**，是典型的"测试全绿、线上静默损坏"类缺陷。单线程测试与低碰撞测试无法暴露。

**修复（推荐：恢复 Mutex 保护的槽，正确且经实战验证）**：

```rust
struct CacheSlot {
    inner: std::sync::Mutex<CacheSlotData>,
}
struct CacheSlotData { key: u32, val: Option<Arc<GeoInfo>> }
impl CacheSlot {
    fn empty() -> Self {
        CacheSlot { inner: std::sync::Mutex::new(CacheSlotData { key: u32::MAX, val: None }) }
    }
}
fn resolve_geo(&self, entry_id: u32) -> Option<Arc<GeoInfo>> {
    if entry_id == 0 || entry_id >= self.group_entry_counts[self.group_index] { return None; }
    let slot = &self.geo_cache[(entry_id as usize) & (GEO_CACHE_SIZE - 1)];
    if let Some(v) = { let g = slot.inner.lock().unwrap();
        if g.key == entry_id { g.val.clone() } else { None } } {
        return Some(v);
    }
    let geo = self.build_geo(entry_id);
    { let mut g = slot.inner.lock().unwrap(); g.key = entry_id; g.val = Some(Arc::clone(&geo)); }
    Some(geo)
}
```

> 若后续确有锁争用瓶颈，再上"单原子 `AtomicU64`(key<<32|gen) + 侧表 val + 双检(seqlock)"的正确无锁方案；**当前这个无锁实现必须回退**。

---

### 🔴 #2 C 段边界校验 —— 整数溢出 + 跳过校验（Critical，畸形文件崩溃）

**根因（两处共存）**：

```c
// qzdb_reader.c:1566-1569（节选）
if (ctx->off_v4_nodes > 0 && ctx->off_v4_nodes + (uint64_t)ctx->v4_node_count * v4_ns > ctx->data_size)
    return QZDB_ERR_BOUNDS;
```

1. **溢出绕过**：`off_v4_nodes` 由文件头 `READ_LE64(d+72)` 读取（攻击者可控）。当 `off` 取近 `UINT64_MAX` 的 hostile 值时，`off + count*v4_ns` 在 `uint64_t` 下回绕为极小值 `< data_size` → 校验"通过"。随后 `ctx->v4_nodes_base = ctx->data + off` 指向映射区之外的地址，`get_v4_child` 直读 → **SIGSEGV（DoS）/ 越界读**。
2. **`off>0` 跳过**：当 `off_v4_nodes == 0` 但 `v4_node_count` 很大（畸形）时，该校验被整体跳过，`v4_nodes_base = ctx->data`（文件头），`get_v4_child` 以 `node_idx*stride` 直读可越过 `data_size` → 越界读。

而 `get_v4_child`（`qzdb_reader.c:450-466`）**仅**校验 `node_idx >= v4_node_count`，**不**二次校验 `base + node_idx*stride` 在 `data_size` 内 —— 安全完全依赖上方校验块，故上方一旦被绕过即越界。这正是项目铁律"畸形文件只能返回结构化错误、绝不 crash/OOM"的直接违反。

**修复（溢出安全 + 不跳过）**：

```c
/* qzdb_reader.c 节选：替换 1566-1569 的 v4/v6 nodes 两行 */
#define CHECK_SEG(off, count, nstride)                                     \
    do {                                                                   \
        if ((count) > 0) {                                                 \
            if ((off) == 0 || (off) > ctx->data_size) return QZDB_ERR_BOUNDS; \
            uint64_t _need = (uint64_t)(count) * (nstride);                \
            if (_need > ctx->data_size - (off)) return QZDB_ERR_BOUNDS;    \
        }                                                                  \
    } while (0)

CHECK_SEG(ctx->off_v4_nodes, ctx->v4_node_count, v4_ns);
CHECK_SEG(ctx->off_v6_nodes, ctx->v6_node_count, v6_ns);
/* off_v4_jump / off_v6_jump / off_ip_row / off_geo_entries 等同理改为溢出安全式 */
```

要点：`off > data_size` 必先判（否则 `data_size - off` 下溢），再用减法式 `_need > data_size - off` 避免加法回绕；`count==0` 时该段本就不会被读取，无需校验。

---

### 🟡 #3 Python `find_fields` 越界崩溃（Medium）

**位置**：`qzdb.py:1964` `values[i] = full._values[idx]`（裸索引，无边界保护）。

**现象**：项目铁律存在"双字段数源对账"——`groupFieldCounts[g]` 与 `fld_count` 不一致时该组回退默认布局。一旦 `norm_idx` 给出的 `idx` 超出 `full._values` 实际长度（字段数双源不一致即可触发），抛 `IndexError`，使整个 `find_fields` 调用崩溃，而非按契约返回 `''`。注意：同文件的 `GeoInfo.Get`/`__getitem__`（`qzdb.py:323-367`）**都**有 `i < len(self._values) else ''` 保护，唯独 `find_fields` 绕过它们直接裸索引。

**修复**：

```python
for i in range(n):
    idx = norm_idx.get(_norm_key(field_names[i]))
    if idx is not None and idx < len(full._values):
        values[i] = full._values[idx]
    # 否则保持初始的 ''（已 values = [''] * n）
```

---

### 🟢 #4 Node `findFields` 契约违例（Low/Medium）

**位置**：`qzdb.js:1405` `outVals[i] = idx === undefined ? '' : full._vals[idx];`

**现象**：`idx` 是合法数字但 `>= full._vals.length` 时，`full._vals[idx]` 为 `undefined`（JS 越界读返回 `undefined`，不抛错），`outVals[i]` 得到 `undefined` 而非契约要求的 `''`；`floatFlags[i]` 同时得 `false`。即"未知/越界字段"未补空串，破坏与 Java golden 对齐的契约（C#/Python 该场景均补 `''`）。

**修复**：

```js
const v = (idx === undefined || idx >= full._vals.length) ? '' : full._vals[idx];
outVals[i] = v;
floatFlags[i] = (idx === undefined || idx >= full._vals.length) ? false : !!floatAll[idx];
```

---

### 🟡 #5 Go finalizer 生命周期（Medium）

**位置**：`qzdb.go` `installSnapshot`（`runtime.SetFinalizer`）与 `Close()`（`r.snap.Swap(nil)`，无显式释放）。

**现象**：原实现为 RCU + 引用计数（`refCount` + `releaseSnapshot`），`Close()`/`Reload()` 在最后一个读者释放后立即确定性 munmap。新实现删除引用计数，改为给 `Snapshot` 注册 finalizer，由 GC 在对象不可达后释放 mmap。两个后果：

1. **`Close()` 不再是确定性释放**：swap 为 nil 后，旧快照的 munmap 要等 GC 某次回收才发生。若进程长时间不触发 GC，mmap 区域与底层文件句柄（Windows 上 mmap 持有文件锁）持续滞留 → 重复 Open/Close 累积占用地址空间/文件锁，直至 GC。
2. 并发安全性并未如提交说明所言"16 线程安全"得到增强——它只是把确定性的引用计数换成了非确定的 GC 兜底，且未提升任何真实的并发保证。

**注**：Go GC 语义下"读者持有局部引用 → finalizer 不运行 → 数据不会被 munmap"成立，故**不存在 use-after-munmap 内存安全 bug**；本项属契约/资源释放回归，定级 Medium。

**修复（二选一）**：
- **(a) 还原引用计数**（推荐，已实战验证正确）：恢复 `refCount atomic.Int32` + `releaseSnapshot()`，`Close()`/`Reload()` 用原 RCU 逻辑。
- **(b) 若坚持 finalizer**：在 `Close()` 中显式释放并注销 finalizer，恢复确定性：

```go
func (r *QzdbReader) Close() error {
    if s := r.snap.Swap(nil); s != nil && s.release != nil {
        runtime.SetFinalizer(s, nil) // 注销，避免重复释放
        s.release()
    }
    return nil
}
```

---

### ⚪ #6 风格瑕疵（Low）

- Rust `lib.rs:2232` 多出一行空行；Go `if s := r.snapshot(); s != nil { return s.x }` 块内 `return` 多 3 空格缩进（虽不影响编译，但与 `gofmt` 不一致，建议 `gofmt -w` + `cargo fmt`）。

---

## 三、验证建议（修复后必跑）

1. **Rust 并发对拍**：在 ≥8 线程、对 1e5 量级 entry 的 `max/ult` 库跑 `find_shared` 随机压测，与单线程结果逐条比对；并加一个**定向碰撞单测**（强制两个 entry_id 落同一槽，交错写后断言读值正确）。
2. **C 畸形输入 fuzz**：用 `c/fuzz/boundary_test.c`（ASan/UBSan）+ 构造 `off_v4_nodes=0xFFFFFFFFFFFFFFF0` 与 `off_v4_nodes=0, v4_node_count=大值` 两类样本，断言返回 `QZDB_ERR_BOUNDS` 而非崩溃。
3. **Python/Node 字段数不一致**：构造 `groupFieldCounts` 与 `fld_count` 不符的数据，断言 `find_fields`/`findFields` 返回 `''` 而非崩溃/`undefined`。
4. 全语言 L2 对拍（golden_vectors.json）复跑，确认 #1/#2 修复后 8 语言仍一致。

---

## 四、总体评价

提交在**正确性修复（PHP SENTINEL 直接返回、IPv4-mapped 三入口一致、trie 边界守卫、批量三态）**方向是对的，且通过了既有测试。但性能向的两条重写（`#1` Rust 锁无关缓存、`#2` C 直读基址 + `goto fail`）**在 fail-closed 与并发正确性的核心不变量上引入了 Critical 级回归**，属于"为性能牺牲正确性"的典型陷阱——且两条都恰好落在项目铁律明确点名的高危面（C 永不淘汰/UAF、畸形文件绝不崩溃、锁无关需正确）。**建议：#1、#2 必须修复并复测后方可发布；#3–#5 应在同一发布窗口内一并修掉。**
