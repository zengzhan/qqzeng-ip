//! 元信息探针（Rust）：输出与 tools/meta_probe_node.js 同构的 JSON。
//!
//! serde_json 只是 dev-dependency，这里手写最小 JSON 序列化，避免为一个
//! 诊断工具给发布产物引入运行时依赖。

use qzdb::QzdbReader;

fn esc(s: &str) -> String {
    let mut out = String::with_capacity(s.len() + 2);
    for c in s.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            c if (c as u32) < 0x20 => out.push_str(&format!("\\u{:04x}", c as u32)),
            c => out.push(c),
        }
    }
    out
}

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let mut parts: Vec<String> = Vec::with_capacity(args.len());

    for f in &args {
        let r = match QzdbReader::from_file(f) {
            Ok(r) => r,
            Err(e) => {
                eprintln!("open {} failed: {}", f, e);
                std::process::exit(1);
            }
        };
        let base = std::path::Path::new(f)
            .file_name()
            .map(|s| s.to_string_lossy().into_owned())
            .unwrap_or_else(|| f.clone());
        let names: Vec<String> =
            r.get_field_names().iter().map(|n| format!("\"{}\"", esc(n))).collect();
        parts.push(format!(
            concat!(
                "{{\"file\":\"{}\",\"lang\":\"rust\",\"edition\":\"{}\",",
                "\"edition_source\":\"{}\",\"version_mask\":{},",
                "\"field_names_source\":\"{}\",\"field_names\":[{}],",
                "\"group_count\":{},\"pool_count\":{},\"data_month\":\"{}\"}}"
            ),
            esc(&base),
            esc(&r.get_edition()),
            esc(&r.get_edition_source()),
            r.get_version_mask(),
            esc(&r.get_field_names_source()),
            names.join(","),
            r.get_group_count(),
            r.get_pool_count(),
            esc(&r.get_data_month()),
        ));
        r.close();
    }

    println!("[{}]", parts.join(","));
}
