# 优化修复与验证报告（2026-08-18）

> 针对 `REVIEW_9a7e8a6.md` 中 2 Critical / 3 Medium / 1 Low 的发现，逐项实施**正优化**并实测验证。原则：每改一项立即用对应测试实证，绝不引入负优化。

## 一、已实施并验证的修复

### ✅ Critical #2 — C 段边界校验（畸形文件崩溃 → fail-closed）
**文件**：`multi-lang/c/qzdb_reader.c`（init 段校验，~L1564）
**改动**：
- 加法回绕 `off + count*nstride > data_size` → 溢出安全减法式 `need > data_size - off`（先判 `off > data_size` 防下溢）。
- 必选段（`off_v4_jump/v4_nodes/v6_nodes/v6_jump/ip_row/geo_entries/pools`）去掉 `off>0` 门控，改为 `off==0 || off>data_size || ...` 三重兜底；`v6_*` 仅在 `v6_node_count>0` 时必选（v4-only 文件 `off_v6_*==0` 合法）。
- **保留** `off>0` 门控于可选段（`row_schema/meta/group_schema`），避免误伤合法文件。
**验证**：
- `test_main`（ASan+UBSan）：**167/167 通过**。
- 定向 `bounds_probe`：7 个必选段（含 `off==0` 与 `off=0xFFFF…F0` 溢出两种畸形）全部 `REJECTED(rc=-8 QZDB_ERR_BOUNDS)`；2 个可选段正确 `ACCEPTED`。
- `failclosed` 模糊测试（ASan，3252+ 畸形变体，头部 192 字节全域穷举 + 字节洪泛 + 尾部截断）：**无 ASAN 报错即 PASS**。

### ✅ Critical #1 — Rust 锁无关缓存撕裂写（并发静默返回错误 IP）
**文件**：`multi-lang/rust/src/lib.rs`
**改动**：把 `key: AtomicU32` + `val: ArcSwapOption` 两个独立原子位置，改为单一不可变 `CacheNode { key, val }`，经单个 `ArcSwap<CacheNode>` 原子指针发布。读者一次性原子 load 整个节点 → `key/val` 永不可能被观察到混合态。
- **保留无锁特性**（无 Mutex 回退，高效目标不退化）。
- 覆写旧节点安全（返回 `Arc`，由仍持有它的读者保命，无生命周期问题）。
**验证**：
- 全量 `cargo test -j 1`：**RC=0**，concurrency 7/7（含新增大库碰撞测试）、failclosed 7/7、golden 2/2、tier1 27/27、edge_cases 31/31、cidr_oracle 4/4 等全部绿。
- 新增 `t_concurrent_correctness_no_torn_read`（小库）+ `t_concurrent_correctness_large_db_collisions`（std_global 97MB，强制槽碰撞）：并发结果恒等于单线程基准。
- **数据准确性**：`bench_qps` parity self-check **12/12 streams 与 bench_vectors.json 逐字节一致**。
- **高效**：热路径 5–8.6M QPS（单线程），16 线程扩展到 24–57M QPS（4.7×–6.8×），p50≈125ns，`concurrency_safe(16x100k): true`。
- 诚实说明：新测试在 std_china（条目 <16384，无槽碰撞）上对回退的缺陷代码也"通过"——故它是正确性/无panic守卫，非确定性 bug 陷阱；修复正确性由「单原子指针发布」设计保证（教科书级无锁模式）。

### ✅ Medium #3 — Python `find_fields` 越界
**文件**：`multi-lang/python/qzdb.py`（~L1964）
**改动**：`full._values[idx]` 加 `idx is not None and idx < len(full._values)` 兜底，越界补 `''`（与 `GeoInfo.Get` 行为一致）。
**验证**：`test_regression` 6/0、`test_golden` 4102/0、`test_tier1` 61/0 全绿。

### ✅ Low/Medium #4 — Node `findFields` 契约
**文件**：`multi-lang/nodejs/qzdb.js`（~L1405）
**改动**：`idx === undefined || idx >= full._vals.length` 时兜底为 `''` / `false`，符合"未知字段补空串"契约。
**验证**：`regression_test.js` PASS、`test_suite.js` Tier1 379 + Tier2 0 失败、`test.js` PASS。

## 二、经核实判定「无需修改」（纠正原审查误判，避免负优化）

### Go finalizer 生命周期（原评 Medium #5）
**结论：不改。** 现有 `runtime.SetFinalizer` 接管 munmap 是**刻意设计**，被 `TestSnapshotUsableAfterCloseWhileHeld`（要求 `Close()` 后本地持有快照仍可安全查询）与 `TestCloseVsConcurrentQueriesRace`（`-race` 守护）约束。若改为 `Close()` 立即 munmap，会与并发 reader 产生 use-after-munmap 竞态，属**负优化**。
**验证**：3 项 Go 生命周期测试在 `-race` 下全 PASS（RC=0）。

## 三、验证总览

| 语言 | 修复项 | 验证手段 | 结果 |
|------|--------|----------|------|
| C | 段边界校验 | ASan test_main + 定向探针 + failclosed fuzz | ✅ 167/167 + 拒 7/接 2 + fuzz 无错 |
| Rust | 缓存撕裂写 | cargo test 全量 + 大库并发 + bench parity/QPS | ✅ RC=0, 12/12 parity, 24–57M QPS@16T |
| Python | find_fields 越界 | regression/golden/tier1 | ✅ 全绿 |
| Node | findFields 契约 | regression/suite/test | ✅ 全绿 |
| Go | （不改）finalizer | -race 生命周期测试 | ✅ 全 PASS |

## 四、结论
5 项审查发现中：**2 项 Critical 已修复并强验证；2 项 Medium/Low 已修复并验证；1 项（Go）经核实为误判、保持原状以避免负优化**。所有修复均为纯正确性/契约修复，合法文件行为零变化，解析准确性（golden/parity 对拍）与查询性能（QPS 基准）均得到实证保障。
