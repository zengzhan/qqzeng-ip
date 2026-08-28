//! Batch IP query runner for Rust (cross-language verification).
//!
//! Usage: batch_rust <database_path> <v4_test> <v4_output> <v6_test> <v6_output>
//!
//! 接口必须与 C / Go / Java / C# / Node / PHP / Python 的 runner 完全一致
//! （见 tools/cross_verify.py：`[runner, qzdb, v4_test, v4_out, v6_test, v6_out]`）：
//!   - V4 输入：每行一个十进制 u32
//!   - V6 输入：每行 `high:low`，两段均为十进制 u64（高/低各 64 位，大端拼接）
//!   - 输出：`<原始行>|<pipe 串>`，未命中则 pipe 串为空
//!
//! 历史坑：本文件旧版实现的是 stdin 逐行接口，与上述文件式约定不兼容，
//! 导致 cross_verify.py 调用时进程挂起等待 stdin 而被杀（SIGKILL/137），
//! Rust 实际从未参与过跨语言对拍。改动此文件时务必保持参数约定不变。

use std::env;
use std::fs;
use std::io::Write;
use std::process;

use qzdb::QzdbReader;

/// `high:low`（十进制）→ 16 字节大端 IPv6 地址
fn parse_v6_key(line: &str) -> Option<[u8; 16]> {
    let (h, l) = line.split_once(':')?;
    let high: u64 = h.trim().parse().ok()?;
    let low: u64 = l.trim().parse().ok()?;

    let mut addr = [0u8; 16];
    addr[..8].copy_from_slice(&high.to_be_bytes());
    addr[8..].copy_from_slice(&low.to_be_bytes());
    Some(addr)
}

fn process_file(reader: &QzdbReader, test_path: &str, out_path: &str, is_v6: bool) -> usize {
    let Ok(content) = fs::read_to_string(test_path) else {
        return 0;
    };

    let mut results: Vec<String> = Vec::new();
    for raw in content.lines() {
        let line = raw.trim();
        if line.is_empty() {
            continue;
        }

        let pipe = if is_v6 {
            parse_v6_key(line)
                .and_then(|addr| reader.find_bytes(&addr))
                .map(|g| g.to_pipe())
                .unwrap_or_default()
        } else {
            line.parse::<u32>()
                .ok()
                .and_then(|ip| reader.find_uint(ip))
                .map(|g| g.to_pipe())
                .unwrap_or_default()
        };

        results.push(format!("{line}|{pipe}"));
    }

    let mut body = results.join("\n");
    body.push('\n');
    if let Ok(mut f) = fs::File::create(out_path) {
        let _ = f.write_all(body.as_bytes());
    }
    results.len()
}

fn main() {
    let args: Vec<String> = env::args().collect();
    if args.len() < 6 {
        eprintln!(
            "Usage: {} <db_path> <v4_test> <v4_out> <v6_test> <v6_out>",
            args.first().map(String::as_str).unwrap_or("batch_rust")
        );
        process::exit(1);
    }

    let reader = match QzdbReader::from_file(&args[1]) {
        Ok(r) => r,
        Err(e) => {
            eprintln!("Rust: Failed to load database: {e:?}");
            process::exit(1);
        }
    };

    let n4 = process_file(&reader, &args[2], &args[3], false);
    eprintln!("  Rust V4: {n4} queries");

    let n6 = process_file(&reader, &args[4], &args[5], true);
    eprintln!("  Rust V6: {n6} queries");

    eprintln!("  Rust DONE");
}
