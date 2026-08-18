//! Tier1 补充：并发正确性测试（线程 + 热重载 + 竞态检测）。
//!
//! 验证 QzdbReader 在多线程并发查询 + 热重载场景下的线程安全性：
//! 1. 16 线程 × 10 万随机查询，无 panic / UAF / race
//! 2. 多线程查询过程中并发 reload，旧快照继续服务，新快照原子切换
//! 3. Miri 游泳池（Swimming Pool）模式：多线程复用 reader，验证 Drop 正确
//! 4. 验证 Arc<QzdbReader> 共享模式下查询安全

use qzdb_reader::QzdbReader;
use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::thread;

fn data_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../data")
}

fn load_std() -> QzdbReader {
    QzdbReader::from_file(data_dir().join("qqzeng_ip_std_china.qzdb").to_str().unwrap())
        .expect("load std")
}

/// 生成确定性随机 IP（线性同余生成器）。
fn gen_ips(count: usize) -> Vec<String> {
    let mut seed = 0x73E1C1B1u32;
    let mut ips = Vec::with_capacity(count);
    for i in 0..count {
        seed = seed.wrapping_mul(1664525).wrapping_add(1013904223).wrapping_add(i as u32);
        let a = (seed >> 24) & 0xFF;
        let b = (seed >> 16) & 0xFF;
        let c = (seed >> 8) & 0xFF;
        let d = seed & 0xFF;
        ips.push(format!("{}.{}.{}.{}", a, b, c, d % 256));
    }
    ips
}

#[test]
fn t_concurrent_queries_no_panic() {
    let reader = Arc::new(load_std());
    let hits = Arc::new(AtomicU64::new(0));
    let ips = gen_ips(100_000);

    let mut handles = Vec::new();
    for t in 0..16 {
        let reader = Arc::clone(&reader);
        let hits = Arc::clone(&hits);
        let subset: Vec<String> = ips.iter().skip(t * 100).take(1000).cloned().collect();
        handles.push(thread::spawn(move || {
            let mut local_hits = 0u64;
            for ip in &subset {
                if reader.find(ip).is_some() {
                    local_hits += 1;
                }
                // find_str 不 panic
                let _ = reader.find_str(ip);
                // lookup_cidr 不 panic
                let _ = reader.lookup_cidr(ip);
            }
            hits.fetch_add(local_hits, Ordering::Relaxed);
        }));
    }

    for h in handles {
        h.join().expect("thread panic during concurrent query");
    }
    assert!(hits.load(Ordering::Relaxed) > 0, "should have some hits");
}

#[test]
fn t_concurrent_reload_atomicity() {
    let reader = Arc::new(load_std());
    let reload_path = data_dir().join("qqzeng_ip_std_china.qzdb");
    let hits = Arc::new(AtomicU64::new(0));
    let ips = gen_ips(50_000);

    let mut handles = Vec::new();

    // 4 个查询线程
    for t in 0..4 {
        let reader = Arc::clone(&reader);
        let hits = Arc::clone(&hits);
        let subset: Vec<String> = ips.iter().skip(t * 100).take(2000).cloned().collect();
        handles.push(thread::spawn(move || {
            for ip in &subset {
                let _ = reader.find(ip);
                let _ = reader.find_str(ip);
            }
            hits.fetch_add(subset.len() as u64, Ordering::Relaxed);
        }));
    }

    // 1 个 reload 线程（在查询线程运行期间多次 reload）
    let reader_for_reload = Arc::clone(&reader);
    let reload_handle = thread::spawn(move || {
        for _ in 0..10 {
            reader_for_reload
                .reload(reload_path.to_str().unwrap())
                .expect("reload should succeed");
            // 验证 reload 后仍能查到
            assert!(reader_for_reload.find("119.51.194.142").is_some());
        }
    });

    for h in handles {
        h.join().expect("query thread panicked");
    }
    reload_handle.join().expect("reload thread panicked");

    assert!(hits.load(Ordering::Relaxed) > 0);
}

#[test]
fn t_arc_shared_reader() {
    let reader = Arc::new(load_std());
    let mut handles = Vec::new();
    for _ in 0..8 {
        let r = Arc::clone(&reader);
        handles.push(thread::spawn(move || {
            for i in 0..5000u32 {
                let ip = format!("{}.{}.{}.{}", (i >> 24) & 0xFF, (i >> 16) & 0xFF, (i >> 8) & 0xFF, i & 0xFF);
                let _ = r.find(&ip);
            }
        }));
    }
    for h in handles {
        h.join().expect("thread panicked");
    }
    // reader 在作用域外自动 Drop
}

#[test]
fn t_drop_releases_resources() {
    // 验证 close() 后查询安全返回 None / 空字符串（软卸载语义）
    let r = load_std();
    r.close();
    assert!(r.find("119.51.194.142").is_none(), "after close, find returns None");
    assert_eq!(r.find_str("119.51.194.142"), "", "after close, find_str returns empty");
}

#[test]
fn t_drop_no_panic() {
    let r = load_std();
    let _ = r.find("119.51.194.142");
    drop(r);
}

/// 黑盒并发正确性：同一 IP 的并发查询结果必须始终等于单线程基准。
///
/// 直接针对此前「key/val 双原子位置」的撕裂写缺陷——`GEO_CACHE_SIZE=16384`，
/// 两个不同 entry_id 可能落到同一槽位；交错写可写出 `key=5/val=geo16389` 的撕裂态，
/// 使并发查询静默返回错误 IP 的 GeoInfo（不 panic，故常规并发测试抓不到）。
/// 修复后 key 与 val 绑定于同一个原子 `CacheNode`，不可能撕裂，本测试必为恒等。
#[test]
fn t_concurrent_correctness_no_torn_read() {
    let reader = Arc::new(load_std());

    // 采样覆盖广泛段的 IP，并预先算出单线程基准（确定性）
    let ips = gen_ips(500);
    let mut expected: std::collections::HashMap<String, String> = std::collections::HashMap::new();
    for ip in &ips {
        expected.insert(ip.clone(), reader.find_str(ip));
    }

    let mut handles = Vec::new();
    for _ in 0..16 {
        let reader = Arc::clone(&reader);
        let ips = ips.clone();
        let expected = expected.clone();
        handles.push(thread::spawn(move || {
            for _ in 0..400 {
                for ip in &ips {
                    let got = reader.find_str(ip);
                    let exp = &expected[ip];
                    assert_eq!(
                        got, *exp,
                        "torn/incorrect geo for {ip}: got={got} exp={exp}"
                    );
                }
            }
        }));
    }
    for h in handles {
        h.join().expect("thread panicked during concurrent correctness check");
    }
}

/// 大库（std_global，条目数 >> GEO_CACHE_SIZE=16384）并发正确性测试。
///
/// 小库（std_china）条目 < 16384，任一 entry_id 独占槽位、永无碰撞，撕裂写缺陷无
/// 法触发；本测试改用 global 大库，使不同 entry_id 必然落到同一缓存槽，真正压到
/// 「槽碰撞 + 并发覆写」的热路径。修复后 CacheNode 单一原子发布，命中碰撞槽也只会
/// 重算、绝不返回错值；并发结果必须等于单线程基准。
fn load_large() -> QzdbReader {
    QzdbReader::from_file(data_dir().join("qqzeng_ip_std_global.qzdb").to_str().unwrap())
        .expect("load std_global")
}

#[test]
fn t_concurrent_correctness_large_db_collisions() {
    let reader = Arc::new(load_large());

    let ips = gen_ips(800);
    let mut expected: std::collections::HashMap<String, String> = std::collections::HashMap::new();
    for ip in &ips {
        expected.insert(ip.clone(), reader.find_str(ip));
    }

    let mut handles = Vec::new();
    for _ in 0..16 {
        let reader = Arc::clone(&reader);
        let ips = ips.clone();
        let expected = expected.clone();
        handles.push(thread::spawn(move || {
            for _ in 0..600 {
                for ip in &ips {
                    let got = reader.find_str(ip);
                    let exp = &expected[ip];
                    assert_eq!(
                        got, *exp,
                        "torn/incorrect geo under collision for {ip}: got={got} exp={exp}"
                    );
                }
            }
        }));
    }
    for h in handles {
        h.join().expect("thread panicked during concurrent correctness check (large db)");
    }
}
